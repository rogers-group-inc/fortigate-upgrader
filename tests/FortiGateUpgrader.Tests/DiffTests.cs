using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Diff;

namespace FortiGateUpgrader.Tests;

public class DiffTests
{
    /// <summary>Rebuilds both sides from the diff; they must equal the inputs.</summary>
    private static void AssertReconstructs(string[] a, string[] b, DiffResult d)
    {
        Assert.Equal(a, d.Lines.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text));
        Assert.Equal(b, d.Lines.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text));
        Assert.Equal(Enumerable.Range(1, a.Length).Cast<int?>(), d.Lines.Where(l => l.OldLine != null).Select(l => l.OldLine));
        Assert.Equal(Enumerable.Range(1, b.Length).Cast<int?>(), d.Lines.Where(l => l.NewLine != null).Select(l => l.NewLine));
    }

    [Theory]
    [InlineData("a b c", "a b c")]
    [InlineData("", "a b")]
    [InlineData("a b", "")]
    [InlineData("a b c d", "a x c d")]
    [InlineData("a b c a b b a", "c b a b a c")]
    [InlineData("x y z", "a b c")]
    public void Diff_is_correct_and_minimal_enough(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var d = LineDiff.Compute(a, b);
        AssertReconstructs(a, b, d);
        Assert.False(d.Approximate);
    }

    [Fact]
    public void Classic_myers_example_has_five_edits()
    {
        var d = LineDiff.Compute("ABCABBA".Select(c => c.ToString()).ToArray(), "CBABAC".Select(c => c.ToString()).ToArray());
        Assert.Equal(5, d.Added + d.Removed);
    }

    [Fact]
    public void Random_inputs_reconstruct()
    {
        var rnd = new Random(42);
        for (var t = 0; t < 200; t++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => ((char)('a' + rnd.Next(4))).ToString()).ToArray();
            var b = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => ((char)('a' + rnd.Next(4))).ToString()).ToArray();
            AssertReconstructs(a, b, LineDiff.Compute(a, b));
        }
    }

    [Fact]
    public void Falls_back_when_too_different()
    {
        var a = Enumerable.Range(0, 100).Select(i => "a" + i).ToArray();
        var b = Enumerable.Range(0, 100).Select(i => "b" + i).ToArray();
        var d = LineDiff.Compute(a, b, maxEdits: 10);
        Assert.True(d.Approximate);
        AssertReconstructs(a, b, d);
    }

    [Fact]
    public void Hunks_show_changes_with_context()
    {
        var a = Enumerable.Range(1, 30).Select(i => "line" + i).ToArray();
        var b = a.ToArray();
        b[4] = "changed5";
        b[24] = "changed25";
        var rows = LineDiff.ToRows(LineDiff.Compute(a, b), context: 2);
        Assert.Equal(2, rows.Count(r => r.IsHeader));
        Assert.Equal("@@ -3,5 +3,5 @@", rows[0].Text);
        Assert.Equal(2 * (1 + 2 + 2 + 2), rows.Count); // header + 2 context + removed + added + 2 context, twice
        Assert.Equal(32, LineDiff.ToRows(LineDiff.Compute(a, b), context: null).Count); // 28 same + 2 removed + 2 added
    }

    [Fact]
    public void Indentation_can_be_ignored()
    {
        var flat = new[] { "config system interface", "edit \"wan1\"", "set vdom \"root\"", "next", "end" };
        var indented = new[] { "config system interface", "    edit \"x1\"", "        set vdom \"root\"", "    next", "end" };
        var d = LineDiff.Compute(flat, indented, ignoreIndentation: true);
        Assert.Equal(1, d.Removed);
        Assert.Equal(1, d.Added);
        Assert.Contains(d.Lines, l => l.Kind == DiffKind.Same && l.Text == "        set vdom \"root\"" && l.OldLine == 3);
        Assert.Equal(3, LineDiff.Compute(flat, indented).Removed); // everything but "config"/"end" differs by indentation
    }

    [Fact]
    public void Flat_original_vs_converted_only_shows_real_changes()
    {
        var src = Fixtures.Parse61F();
        var r = ConversionService.Convert(new ConversionRequest
        {
            Source = src, SourceModel = Fixtures.Model("61F"), TargetModel = Fixtures.Model("91G"), Mapping = Fixtures.Map61Fto91G,
        });
        var d = LineDiff.Compute(Fixtures.Config61F, FortiConfigWriter.Write(r.Output), ignoreIndentation: true);
        // Only renamed/added/removed lines differ; not every line because of indentation.
        Assert.InRange(d.Removed, 10, 40);
        Assert.Contains(d.Lines, l => l.Kind == DiffKind.Removed && l.Text == "edit \"wan1\"" && l.OldLine == 27);
    }

    [Fact]
    public void Conversion_diff_shows_renames()
    {
        var src = Fixtures.Parse61F();
        var r = ConversionService.Convert(new ConversionRequest
        {
            Source = src, SourceModel = Fixtures.Model("61F"), TargetModel = Fixtures.Model("91G"), Mapping = Fixtures.Map61Fto91G,
        });
        var d = LineDiff.Compute(FortiConfigWriter.Write(src), FortiConfigWriter.Write(r.Output));
        Assert.Contains(d.Lines, l => l.Kind == DiffKind.Removed && l.Text.Trim() == "edit \"wan1\"");
        Assert.Contains(d.Lines, l => l.Kind == DiffKind.Added && l.Text.Trim() == "edit \"x1\"");
        Assert.Contains(d.Lines, l => l.Kind == DiffKind.Added && l.Text == "#platform=FORTIGATE-91G");
    }

    [Fact]
    public void Blocks_and_inline_spans_mark_the_changed_words()
    {
        var oldText = "a\nset interface \"wan1\"\nb\nremoved line\nc\n";
        var newText = "a\n    set interface \"x1\"\nb\nc\nnew line\n";
        var d = LineDiff.Compute(oldText, newText, ignoreIndentation: true);
        var blocks = LineDiff.Blocks(d);
        Assert.Equal(3, blocks.Count);

        // Changed line: whole interface names are highlighted, indentation is skipped.
        var (o, n) = LineDiff.InlineChange(blocks[0].Removed[0], blocks[0].Added[0]);
        Assert.Equal("wan1", "set interface \"wan1\"".Substring(o!.Column, o.Length));
        Assert.Equal("x1", "    set interface \"x1\"".Substring(n!.Column, n.Length));

        // Pure deletion: the new side position is the line it sat above ("c" = new line 4).
        Assert.Empty(blocks[1].Added);
        Assert.Equal(4, blocks[1].NewStart);
        Assert.Equal(4, blocks[1].OldStart);

        // Pure addition at the end: the old side position is after the last line.
        Assert.Empty(blocks[2].Removed);
        Assert.Equal(6, blocks[2].OldStart);

        var newToOld = LineDiff.MapNewToOld(d, 5);
        Assert.Equal(1, newToOld[1]);
        Assert.Equal(3, newToOld[3]);
        Assert.Equal(5, newToOld[4]); // "c"
        var oldToNew = LineDiff.MapOldToNew(d, 5);
        Assert.Equal(4, oldToNew[5]);
    }
}
