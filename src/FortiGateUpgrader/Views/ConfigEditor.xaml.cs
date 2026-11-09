using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FortiGateUpgrader.Views;

/// <summary>Highlights to draw behind the text. Line numbers are 1-based; columns are 0-based.</summary>
public sealed class EditorHighlights
{
    /// <summary>Whole-line background.</summary>
    public Dictionary<int, Brush> Lines { get; } = new();

    /// <summary>Character ranges within a line (the exact text that differs).</summary>
    public List<(int Line, int Column, int Length)> Spans { get; } = new();

    public Brush? SpanBrush { get; set; }

    /// <summary>Lines above which something was deleted (Count+1 = after the last line).</summary>
    public HashSet<int> DeletionMarkers { get; } = new();

    public Brush? MarkerBrush { get; set; }
}

/// <summary>Config editor (AvalonEdit) with line numbers, highlighted find, go-to-line and diff highlights.</summary>
public partial class ConfigEditor : UserControl
{
    private static readonly Brush SearchBrush = Frozen(Color.FromArgb(0xB0, 0xFD, 0xE0, 0x47));
    private static readonly Pen CurrentMatchPen = new(Frozen(Color.FromRgb(0xEA, 0x58, 0x0C)), 1.5);

    private readonly HighlightRenderer _renderer;
    private bool _suppress;

    public ConfigEditor()
    {
        InitializeComponent();
        _renderer = new HighlightRenderer(this);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        Editor.TextArea.SelectionCornerRadius = 0;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.CurrentLineBackground = Frozen(Color.FromArgb(0x18, 0x25, 0x63, 0xEB));
        Editor.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(FindBox.Text)) UpdateFindStatus();
            if (!_suppress) UserEdited?.Invoke(this, EventArgs.Empty);
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdatePosition();
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Scrolled?.Invoke(this, EventArgs.Empty);

        InputBindings.Add(new KeyBinding(new RelayCommand(() => { FindBox.Focus(); FindBox.SelectAll(); }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => { LineBox.Focus(); LineBox.SelectAll(); }), Key.G, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Find(true)), Key.F3, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Find(false)), Key.F3, ModifierKeys.Shift));
        IsReadOnly = true;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Raised when the user changes the text (not when <see cref="SetText"/> is called).</summary>
    public event EventHandler? UserEdited;

    /// <summary>Raised when the view scrolls.</summary>
    public event EventHandler? Scrolled;

    public string Text => Editor.Document.Text;

    public bool IsReadOnly
    {
        get => Editor.IsReadOnly;
        set
        {
            Editor.IsReadOnly = value;
            Editor.Background = value ? Brushes.WhiteSmoke : Brushes.White;
        }
    }

    internal string? SearchTerm => string.IsNullOrEmpty(FindBox.Text) ? null : FindBox.Text;
    internal EditorHighlights? Highlights { get; private set; }

    /// <summary>Replaces the text without raising <see cref="UserEdited"/>; keeps the scroll position if possible.</summary>
    public void SetText(string text)
    {
        if (text == Editor.Document.Text) return;
        var offset = Editor.TextArea.TextView.ScrollOffset;
        _suppress = true;
        Editor.Document.Text = text;
        Editor.Document.UndoStack.ClearAll();
        _suppress = false;
        Editor.ScrollToVerticalOffset(offset.Y);
        Editor.ScrollToHorizontalOffset(offset.X);
        UpdatePosition();
    }

    public void SetHighlights(EditorHighlights? highlights)
    {
        Highlights = highlights;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    public int LineCount => Editor.Document.LineCount;

    public int CaretLine => Editor.TextArea.Caret.Line;

    /// <summary>The first line visible at the top of the view (1-based).</summary>
    public int TopLine
    {
        get
        {
            var tv = Editor.TextArea.TextView;
            return tv.GetDocumentLineByVisualTop(tv.ScrollOffset.Y)?.LineNumber ?? 1;
        }
    }

    /// <summary>Scrolls so <paramref name="line"/> is at the top, without moving the caret.</summary>
    public void ScrollLineToTop(int line)
    {
        line = Math.Clamp(line, 1, Math.Max(1, Editor.Document.LineCount));
        Editor.ScrollToVerticalOffset(Editor.TextArea.TextView.GetVisualTopByDocumentLine(line));
    }

    public void GoToLine(int oneBased, bool select = true)
    {
        if (Editor.Document.LineCount == 0) return;
        var line = Editor.Document.GetLineByNumber(Math.Clamp(oneBased, 1, Editor.Document.LineCount));
        Editor.TextArea.Caret.Offset = line.Offset;
        if (select) Editor.Select(line.Offset, line.Length);
        // Put the line about a third of the way down so the context above it is visible.
        var tv = Editor.TextArea.TextView;
        Editor.ScrollToVerticalOffset(Math.Max(0, tv.GetVisualTopByDocumentLine(line.LineNumber) - Editor.ViewportHeight / 3));
        if (select) Editor.TextArea.Focus();
    }

    private void UpdatePosition()
    {
        var c = Editor.TextArea.Caret;
        Position.Text = $"Ln {c.Line}, Col {c.Column}  ·  {Editor.Document.LineCount:N0} lines";
    }

    // ------------------------------------------------------------------ find

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFindStatus();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { FindBox.Clear(); Editor.TextArea.Focus(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        Find(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => Find(true);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => Find(false);

    private void UpdateFindStatus()
    {
        var term = FindBox.Text;
        if (string.IsNullOrEmpty(term)) { FindStatus.Text = ""; return; }
        var n = CountMatches(Editor.Document.Text, term);
        FindStatus.Text = n == 0 ? "Not found" : $"{n:N0} match{(n == 1 ? "" : "es")}";
    }

    private void Find(bool forward)
    {
        var term = FindBox.Text;
        var text = Editor.Document.Text;
        if (string.IsNullOrEmpty(term) || text.Length == 0) return;

        int idx;
        if (forward)
        {
            // Start just after the current match (or at the caret) so repeated searches advance.
            var from = Editor.SelectionLength > 0 ? Editor.SelectionStart + 1 : Editor.CaretOffset;
            idx = text.IndexOf(term, Math.Min(from, text.Length), StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var from = (Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset) - 1;
            idx = from < 0 ? -1 : text.LastIndexOf(term, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = text.LastIndexOf(term, StringComparison.OrdinalIgnoreCase);
        }
        UpdateFindStatus();
        if (idx < 0) return;

        Editor.Select(idx, term.Length);
        Editor.TextArea.Caret.Offset = idx + term.Length;
        var loc = Editor.Document.GetLocation(idx);
        var tv = Editor.TextArea.TextView;
        var top = tv.GetVisualTopByDocumentLine(loc.Line);
        if (top < tv.ScrollOffset.Y || top > tv.ScrollOffset.Y + Editor.ViewportHeight - 2 * tv.DefaultLineHeight)
            Editor.ScrollToVerticalOffset(Math.Max(0, top - Editor.ViewportHeight / 3));
        Editor.ScrollTo(loc.Line, loc.Column);

        var ordinal = CountMatches(text[..idx], term) + 1;
        FindStatus.Text = $"{ordinal:N0} of {CountMatches(text, term):N0}";
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private static int CountMatches(string text, string term)
    {
        var n = 0;
        for (var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(term, i + term.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private void LineBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(LineBox.Text.Trim(), out var n)) GoToLine(n);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Draws diff line backgrounds, changed-text spans and all find matches for the visible lines.</summary>
    private sealed class HighlightRenderer : IBackgroundRenderer
    {
        private readonly ConfigEditor _owner;
        public HighlightRenderer(ConfigEditor owner) => _owner = owner;

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            var doc = textView.Document;
            var h = _owner.Highlights;
            var width = Math.Max(textView.ActualWidth, ((System.Windows.Controls.Primitives.IScrollInfo)textView).ExtentWidth) + textView.HorizontalOffset;

            foreach (var vl in textView.VisualLines)
            {
                var line = vl.FirstDocumentLine;
                var y = vl.VisualTop - textView.VerticalOffset;
                if (h != null && h.Lines.TryGetValue(line.LineNumber, out var brush))
                    dc.DrawRectangle(brush, null, new Rect(0, y, width, vl.Height));
                if (h?.MarkerBrush != null)
                {
                    if (h.DeletionMarkers.Contains(line.LineNumber))
                        dc.DrawRectangle(h.MarkerBrush, null, new Rect(0, y - 1, width, 2.5));
                    if (line.LineNumber == doc.LineCount && h.DeletionMarkers.Contains(doc.LineCount + 1))
                        dc.DrawRectangle(h.MarkerBrush, null, new Rect(0, y + vl.Height - 1.5, width, 2.5));
                }
            }

            var first = textView.VisualLines[0].FirstDocumentLine.LineNumber;
            var last = textView.VisualLines[^1].LastDocumentLine.LineNumber;

            if (h?.SpanBrush != null && h.Spans.Count > 0)
            {
                var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
                var any = false;
                foreach (var (ln, col, len) in h.Spans)
                {
                    if (ln < first || ln > last || ln > doc.LineCount) continue;
                    var dl = doc.GetLineByNumber(ln);
                    var start = dl.Offset + Math.Min(col, dl.Length);
                    var end = dl.Offset + Math.Min(col + len, dl.Length);
                    if (end <= start) continue;
                    builder.AddSegment(textView, new TextSegment { StartOffset = start, EndOffset = end });
                    any = true;
                }
                if (any && builder.CreateGeometry() is { } g) dc.DrawGeometry(h.SpanBrush, null, g);
            }

            var term = _owner.SearchTerm;
            if (term != null)
            {
                var builder = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
                var any = false;
                for (var ln = first; ln <= last; ln++)
                {
                    var dl = doc.GetLineByNumber(ln);
                    var text = doc.GetText(dl);
                    for (var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(term, i + term.Length, StringComparison.OrdinalIgnoreCase))
                    {
                        builder.AddSegment(textView, new TextSegment { StartOffset = dl.Offset + i, Length = term.Length });
                        any = true;
                    }
                }
                if (any && builder.CreateGeometry() is { } g) dc.DrawGeometry(SearchBrush, null, g);

                // Outline the current match (the selection) so it stands out from the others.
                var sel = _owner.Editor.TextArea.Selection;
                if (!sel.IsEmpty && string.Equals(sel.GetText(), term, StringComparison.OrdinalIgnoreCase))
                {
                    var cb = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
                    cb.AddSegment(textView, new TextSegment { StartOffset = _owner.Editor.SelectionStart, Length = _owner.Editor.SelectionLength });
                    if (cb.CreateGeometry() is { } cg) dc.DrawGeometry(null, CurrentMatchPen, cg);
                }
            }
        }
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
