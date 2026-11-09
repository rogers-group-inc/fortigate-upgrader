namespace FortiGateUpgrader.Core.Diff;

public enum DiffKind { Same, Removed, Added }

/// <summary>One line of a diff. Line numbers are 1-based; null on the side the line doesn't exist.</summary>
public sealed record DiffLine(DiffKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>A displayable row: either a diff line or a hunk header ("@@ -a,b +c,d @@").</summary>
public sealed record DiffRow(DiffKind? Kind, int? OldLine, int? NewLine, string Text)
{
    public bool IsHeader => Kind == null;
}

/// <summary>A run of consecutive removed and/or added lines.</summary>
public sealed record ChangeBlock(int OldStart, int NewStart, IReadOnlyList<DiffLine> Removed, IReadOnlyList<DiffLine> Added);

/// <summary>Changed characters within one line (Column 0-based).</summary>
public sealed record InlineSpan(int Line, int Column, int Length);

public sealed class DiffResult
{
    public required IReadOnlyList<DiffLine> Lines { get; init; }
    public int Added => Lines.Count(l => l.Kind == DiffKind.Added);
    public int Removed => Lines.Count(l => l.Kind == DiffKind.Removed);
    public bool Identical => Lines.All(l => l.Kind == DiffKind.Same);

    /// <summary>True when the files differ so much that the diff fell back to replacing a whole block.</summary>
    public bool Approximate { get; init; }
}

/// <summary>Line-based diff (Myers' O(ND) algorithm) with unified-style hunks.</summary>
public static class LineDiff
{
    public static string[] SplitLines(string text)
    {
        text = text.Replace("\r\n", "\n");
        if (text.EndsWith('\n')) text = text[..^1];
        return text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
    }

    public static DiffResult Compute(string oldText, string newText, int maxEdits = 3000, bool ignoreIndentation = false) =>
        Compute(SplitLines(oldText), SplitLines(newText), maxEdits, ignoreIndentation);

    /// <param name="ignoreIndentation">
    /// Compare lines with leading/trailing whitespace removed (flat FortiManager exports vs indented backups).
    /// Unchanged lines are then shown with the new side's text.
    /// </param>
    public static DiffResult Compute(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEdits = 3000, bool ignoreIndentation = false)
    {
        // Intern lines to ints so comparisons are cheap.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string s)
        {
            if (ignoreIndentation) s = s.Trim();
            if (!ids.TryGetValue(s, out var v)) { v = ids.Count; ids[s] = v; }
            return v;
        }
        var A = a.Select(Id).ToArray();
        var B = b.Select(Id).ToArray();

        var prefix = 0;
        while (prefix < A.Length && prefix < B.Length && A[prefix] == B[prefix]) prefix++;
        var suffix = 0;
        while (suffix < A.Length - prefix && suffix < B.Length - prefix && A[^(suffix + 1)] == B[^(suffix + 1)]) suffix++;

        var lines = new List<DiffLine>(Math.Max(a.Count, b.Count) + 16);
        for (var i = 0; i < prefix; i++) lines.Add(new DiffLine(DiffKind.Same, i + 1, i + 1, b[i]));

        var midA = A[prefix..(A.Length - suffix)];
        var midB = B[prefix..(B.Length - suffix)];
        var ops = Myers(midA, midB, maxEdits);
        var approximate = ops == null;
        if (ops == null)
        {
            ops = new List<(DiffKind, int, int)>();
            for (var i = 0; i < midA.Length; i++) ops.Add((DiffKind.Removed, i, -1));
            for (var j = 0; j < midB.Length; j++) ops.Add((DiffKind.Added, -1, j));
        }
        foreach (var (kind, i, j) in ops)
        {
            lines.Add(kind switch
            {
                DiffKind.Same => new DiffLine(kind, prefix + i + 1, prefix + j + 1, b[prefix + j]),
                DiffKind.Removed => new DiffLine(kind, prefix + i + 1, null, a[prefix + i]),
                _ => new DiffLine(kind, null, prefix + j + 1, b[prefix + j]),
            });
        }

        for (var s = suffix; s > 0; s--)
        {
            int ai = a.Count - s, bi = b.Count - s;
            lines.Add(new DiffLine(DiffKind.Same, ai + 1, bi + 1, b[bi]));
        }
        return new DiffResult { Lines = lines, Approximate = approximate };
    }

    /// <summary>Returns edit operations (kind, indexInA, indexInB), or null if more than maxEdits are needed.</summary>
    private static List<(DiffKind Kind, int I, int J)>? Myers(int[] A, int[] B, int maxEdits)
    {
        int n = A.Length, m = B.Length;
        var result = new List<(DiffKind, int, int)>();
        if (n == 0) { for (var j = 0; j < m; j++) result.Add((DiffKind.Added, -1, j)); return result; }
        if (m == 0) { for (var i = 0; i < n; i++) result.Add((DiffKind.Removed, i, -1)); return result; }

        var max = Math.Min(n + m, maxEdits);
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = false;

        for (var d = 0; d <= max && !found; d++)
        {
            // Keep only the slice of V that the backtrack for this d will read.
            var snap = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, snap, 0, snap.Length);
            trace.Add(snap);

            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && A[x] == B[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
        }
        if (!found) return null;

        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var snap = trace[d];
            int Get(int k) => snap[k + d + 1];
            var k0 = cx - cy;
            var prevK = k0 == -d || (k0 != d && Get(k0 - 1) < Get(k0 + 1)) ? k0 + 1 : k0 - 1;
            var prevX = d == 0 ? 0 : Get(prevK);
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                result.Add((DiffKind.Same, cx - 1, cy - 1));
                cx--; cy--;
            }
            if (d == 0) break;
            if (cx == prevX) result.Add((DiffKind.Added, -1, cy - 1));
            else result.Add((DiffKind.Removed, cx - 1, -1));
            cx = prevX;
            cy = prevY;
        }
        result.Reverse();
        return result;
    }

    /// <summary>
    /// Groups consecutive changed lines. <see cref="ChangeBlock.OldStart"/> / <see cref="ChangeBlock.NewStart"/> are the
    /// first line of the block on each side, or, when a side has no lines in the block, the line it would sit above.
    /// </summary>
    public static List<ChangeBlock> Blocks(DiffResult diff)
    {
        var blocks = new List<ChangeBlock>();
        int nextOld = 1, nextNew = 1;
        var lines = diff.Lines;
        for (var i = 0; i < lines.Count;)
        {
            if (lines[i].Kind == DiffKind.Same) { nextOld = lines[i].OldLine!.Value + 1; nextNew = lines[i].NewLine!.Value + 1; i++; continue; }
            var removed = new List<DiffLine>();
            var added = new List<DiffLine>();
            while (i < lines.Count && lines[i].Kind != DiffKind.Same)
            {
                (lines[i].Kind == DiffKind.Removed ? removed : added).Add(lines[i]);
                i++;
            }
            blocks.Add(new ChangeBlock(removed.FirstOrDefault()?.OldLine ?? nextOld, added.FirstOrDefault()?.NewLine ?? nextNew, removed, added));
            if (removed.Count > 0) nextOld = removed[^1].OldLine!.Value + 1;
            if (added.Count > 0) nextNew = added[^1].NewLine!.Value + 1;
        }
        return blocks;
    }

    /// <summary>
    /// The part of a changed line that actually differs, on each side, widened to whole words
    /// (so "wan1" → "x1" highlights the full names). Leading/trailing whitespace is ignored.
    /// </summary>
    public static (InlineSpan? Old, InlineSpan? New) InlineChange(DiffLine removed, DiffLine added)
    {
        var a = removed.Text;
        var b = added.Text;
        int ia = Indent(a), ib = Indent(b);
        var ta = a.Trim();
        var tb = b.Trim();
        var p = 0;
        while (p < ta.Length && p < tb.Length && ta[p] == tb[p]) p++;
        var s = 0;
        while (s < ta.Length - p && s < tb.Length - p && ta[^(s + 1)] == tb[^(s + 1)]) s++;

        // Widen to word boundaries on both sides consistently.
        while (p > 0 && IsWord(ta[p - 1])) p--;
        while (s > 0 && (IsWord(ta[^s]) || IsWord(tb[^s]))) s--;

        InlineSpan? Span(DiffLine l, int indent, string trimmed) =>
            trimmed.Length - p - s > 0 ? new InlineSpan(l.Kind == DiffKind.Removed ? l.OldLine!.Value : l.NewLine!.Value, indent + p, trimmed.Length - p - s) : null;
        return (Span(removed, ia, ta), Span(added, ib, tb));
    }

    private static int Indent(string s) { var i = 0; while (i < s.Length && char.IsWhiteSpace(s[i])) i++; return i; }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.';

    /// <summary>For each line of the new text (1-based index), the closest matching line of the old text.</summary>
    public static int[] MapNewToOld(DiffResult diff, int newLineCount)
    {
        var map = new int[newLineCount + 2];
        var lastOld = 0;
        foreach (var l in diff.Lines)
        {
            if (l.OldLine is int o) lastOld = o;
            if (l.NewLine is int n && n < map.Length) map[n] = l.OldLine ?? Math.Max(1, lastOld + 1);
        }
        return map;
    }

    /// <summary>For each line of the old text (1-based index), the closest matching line of the new text.</summary>
    public static int[] MapOldToNew(DiffResult diff, int oldLineCount)
    {
        var map = new int[oldLineCount + 2];
        var lastNew = 0;
        foreach (var l in diff.Lines)
        {
            if (l.NewLine is int n) lastNew = n;
            if (l.OldLine is int o && o < map.Length) map[o] = l.Kind == DiffKind.Same ? l.NewLine!.Value : Math.Max(1, lastNew + 1);
        }
        return map;
    }

    /// <summary>Rows for display: every line, or only changes with <paramref name="context"/> lines around them.</summary>
    public static List<DiffRow> ToRows(DiffResult diff, int? context)
    {
        var lines = diff.Lines;
        if (context == null) return lines.Select(l => new DiffRow(l.Kind, l.OldLine, l.NewLine, l.Text)).ToList();

        var rows = new List<DiffRow>();
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Same) continue;
            for (var j = Math.Max(0, i - context.Value); j <= Math.Min(lines.Count - 1, i + context.Value); j++) keep[j] = true;
        }

        var start = 0;
        while (start < lines.Count)
        {
            if (!keep[start]) { start++; continue; }
            var end = start;
            while (end + 1 < lines.Count && keep[end + 1]) end++;

            var hunk = lines.Skip(start).Take(end - start + 1).ToList();
            var oldStart = hunk.FirstOrDefault(l => l.OldLine != null)?.OldLine ?? 0;
            var newStart = hunk.FirstOrDefault(l => l.NewLine != null)?.NewLine ?? 0;
            var oldCount = hunk.Count(l => l.Kind != DiffKind.Added);
            var newCount = hunk.Count(l => l.Kind != DiffKind.Removed);
            rows.Add(new DiffRow(null, null, null, $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@"));
            rows.AddRange(hunk.Select(l => new DiffRow(l.Kind, l.OldLine, l.NewLine, l.Text)));
            start = end + 1;
        }
        return rows;
    }
}
