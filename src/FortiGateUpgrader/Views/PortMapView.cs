using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FortiGateUpgrader.Core.Mapping;
using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Views;

public enum PanelSide { Source, Target }

public sealed record PortRef(PanelSide Side, string Name);

/// <summary>
/// Draws the source firewall (top) and target firewall (bottom) as front panels and lets the user
/// drag connection lines between ports. Green = valid 1:1, red = conflict, amber outline = in-use port not mapped.
/// </summary>
public sealed class PortMapView : Canvas
{
    private const double Cell = 64, PortW = 54, PortH = 44, RowGap = 52, Pad = 16, Header = 30, Between = 170;

    private readonly Dictionary<PortRef, Rect> _ports = new();
    private MappingState? _state;
    private PortLink? _selectedLink;

    // Drag state
    private PortRef? _pressed;
    private Point _pressPoint;
    private PortRef? _dragAnchor;
    private Path? _dragLine;
    private PortRef? _hover;
    private readonly List<PortLink> _detached = new();

    public PortMapView()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeftButtonDown += (_, _) =>
        {
            // Only reached when the background (not a port or line) is clicked.
            Focus();
            if (_selectedLink == null && SelectedPort == null) return;
            _selectedLink = null;
            SelectedPort = null;
            Rebuild();
            PortSelected?.Invoke(this, null);
        };
        KeyDown += OnKeyDown;
    }

    public event EventHandler<PortRef?>? PortSelected;

    public PortRef? SelectedPort { get; private set; }

    public MappingState? State
    {
        get => _state;
        set
        {
            if (_state != null) _state.Changed -= OnStateChanged;
            _state = value;
            if (_state != null) _state.Changed += OnStateChanged;
            _selectedLink = null;
            SelectedPort = null;
            Rebuild();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Rebuild();

    // ---------------------------------------------------------------- colours

    private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static readonly Brush Green = B("#16A34A"), Red = B("#DC2626"), Amber = B("#F59E0B"), Idle = B("#9CA3AF"),
        Drag = B("#2563EB"), PanelFill = B("#F3F4F6"), PanelStroke = B("#9CA3AF"), RJ45Fill = B("#374151"),
        OpticalFill = B("#64748B"), SharedFill = B("#475569"), Muted = B("#6B7280"), Selected = B("#2563EB"), Purple = B("#7C3AED");

    // ---------------------------------------------------------------- layout

    private static double PanelWidth(ModelProfile m) => (m.Ports.Max(p => p.Column) + 1) * Cell + 2 * Pad - (Cell - PortW);
    private static double PanelHeight(ModelProfile m) => Header + (m.Ports.Max(p => p.Row) + 1) * RowGap + Pad - (RowGap - PortH);

    public void Rebuild()
    {
        Children.Clear();
        _ports.Clear();
        if (_state == null)
        {
            Width = 900;
            Height = 400;
            var hint = new TextBlock { Text = "Open a FortiGate configuration file to begin.", FontSize = 18, Foreground = Muted };
            SetLeft(hint, 260);
            SetTop(hint, 180);
            Children.Add(hint);
            return;
        }

        var src = _state.Source;
        var dst = _state.Target;
        var width = Math.Max(PanelWidth(src), PanelWidth(dst)) + 40;
        var srcTop = 10.0;
        var dstTop = srcTop + PanelHeight(src) + Between;
        Width = width;
        Height = dstTop + PanelHeight(dst) + 10;

        DrawPanel(src, PanelSide.Source, (width - PanelWidth(src)) / 2, srcTop);
        DrawPanel(dst, PanelSide.Target, (width - PanelWidth(dst)) / 2, dstTop);

        // Clickable (invisible) line paths go under the ports so ports stay clickable;
        // the visible strokes go on top so lines to the second row of ports are not hidden.
        var hitLayer = new Canvas();
        Children.Insert(0, hitLayer);
        foreach (var (port, rect) in _ports) DrawPort(port, rect);
        var strokeLayer = new Canvas { IsHitTestVisible = false };
        Children.Add(strokeLayer);
        foreach (var link in _state.Links) DrawLink(hitLayer, strokeLayer, link);
    }

    private void DrawPanel(ModelProfile model, PanelSide side, double x, double y)
    {
        var rect = new Rectangle
        {
            Width = PanelWidth(model), Height = PanelHeight(model), RadiusX = 6, RadiusY = 6,
            Fill = PanelFill, Stroke = PanelStroke, StrokeThickness = 1,
        };
        SetLeft(rect, x);
        SetTop(rect, y);
        Children.Add(rect);

        var title = new TextBlock
        {
            Text = $"{(side == PanelSide.Source ? "Current" : "New")}: {model.DisplayName}",
            FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Brushes.Black,
        };
        if (!model.Verified)
            title.Inlines.Add(new System.Windows.Documents.Run("   (port names from docs – unverified)") { Foreground = Amber, FontWeight = FontWeights.Normal, FontSize = 11 });
        SetLeft(title, x + Pad);
        SetTop(title, y + 6);
        Children.Add(title);

        foreach (var p in model.Ports)
        {
            var px = x + Pad + p.Column * Cell;
            var py = y + Header + p.Row * RowGap;
            _ports[new PortRef(side, p.Name)] = new Rect(px, py, PortW, PortH);
        }
    }

    private void DrawPort(PortRef port, Rect r)
    {
        var model = port.Side == PanelSide.Source ? _state!.Source : _state!.Target;
        var def = model.FindPort(port.Name)!;
        var inUse = port.Side == PanelSide.Source && _state.Usage.TryGetValue(port.Name, out var u) && u.InUse;

        Brush stroke = Idle;
        double thickness = 1.5;
        var opacity = 1.0;
        if (port.Side == PanelSide.Source)
        {
            switch (_state.StatusOfSource(port.Name))
            {
                case SourcePortStatus.Mapped: stroke = Green; thickness = 2.5; break;
                case SourcePortStatus.Conflict: stroke = Red; thickness = 2.5; break;
                case SourcePortStatus.UnmappedInUse: stroke = Amber; thickness = 3.5; break;
                case SourcePortStatus.UnmappedUnused: opacity = 0.75; break;
                case SourcePortStatus.GroupMember: stroke = Purple; thickness = 2.5; break;
            }
        }
        else
        {
            if (_state.IsTargetConflicted(port.Name)) { stroke = Red; thickness = 2.5; }
            else if (_state.Links.Any(l => l.Target == port.Name)) { stroke = Green; thickness = 2.5; }
        }
        if (Equals(port, _hover) && _dragAnchor != null) { stroke = Drag; thickness = 3.5; }
        if (Equals(port, SelectedPort)) { stroke = Selected; thickness = 3.5; }

        var fill = def.Media switch
        {
            PortMedia.SFP or PortMedia.SFPPlus => OpticalFill,
            PortMedia.SharedRJ45SFP or PortMedia.SharedRJ45SFPPlus => SharedFill,
            _ => RJ45Fill,
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = def.Name, Foreground = Brushes.White, FontSize = def.Name.Length > 7 ? 9.5 : 11,
            FontWeight = inUse ? FontWeights.Bold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = def.Label.Length > 9 ? def.Label.Split('/')[0].Trim() : def.Label,
            Foreground = B("#D1D5DB"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center,
        });

        var border = new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(4),
            Background = fill, BorderBrush = stroke, BorderThickness = new Thickness(thickness),
            Opacity = opacity, Child = stack, Cursor = Cursors.Hand, Tag = port,
            ToolTip = BuildTooltip(port, def),
        };
        ToolTipService.SetInitialShowDelay(border, 300);
        border.MouseLeftButtonDown += OnPortDown;
        border.ContextMenu = BuildContextMenu(port);
        SetLeft(border, r.X);
        SetTop(border, r.Y);
        Children.Add(border);

        if (inUse)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Drag, Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false, ToolTip = "In use" };
            SetLeft(dot, r.Right - 6);
            SetTop(dot, r.Y - 3);
            Children.Add(dot);
        }
    }

    private string BuildTooltip(PortRef port, PortDefinition def)
    {
        var lines = new List<string> { $"{def.Name}  (front label \"{def.Label}\")", def.MediaDescription };
        if (port.Side == PanelSide.Source && _state!.Usage.TryGetValue(def.Name, out var u))
        {
            lines.Add("");
            lines.Add(u.Summary);
            lines.Add("");
            lines.Add(_state.StatusOfSource(def.Name) switch
            {
                SourcePortStatus.UnmappedInUse => "Not mapped – it won't be converted, and its settings and anything that depends on it will be removed. Drag it to a port to keep them.",
                SourcePortStatus.UnmappedUnused => "Not mapped – unused, won't be converted.",
                SourcePortStatus.GroupMember => $"Only a member of {string.Join(", ", u.Groups)}. You don't need to map it: choose that group's new ports in step 4 (Aggregates & switches). Map it only to carry its port settings (speed etc.).",
                _ => "Drag to change where it goes.",
            });
        }
        else if (port.Side == PanelSide.Target)
        {
            var from = _state!.Links.Where(l => l.Target == def.Name).Select(l => l.Source).ToList();
            lines.Add(from.Count == 0 ? "Free – will get default settings." : $"Receives: {string.Join(", ", from)}");
        }
        return string.Join("\n", lines);
    }

    private ContextMenu BuildContextMenu(PortRef port)
    {
        var menu = new ContextMenu();
        var links = _state!.Links.Where(l => port.Side == PanelSide.Source ? l.Source == port.Name : l.Target == port.Name).ToList();
        var remove = new MenuItem { Header = "Remove connection", IsEnabled = links.Count > 0 };
        remove.Click += (_, _) => { foreach (var l in links) _state.Remove(l); };
        menu.Items.Add(remove);
        return menu;
    }

    private Point Anchor(PortRef port)
    {
        var r = _ports[port];
        return port.Side == PanelSide.Source ? new Point(r.X + r.Width / 2, r.Bottom) : new Point(r.X + r.Width / 2, r.Top);
    }

    private static Geometry Curve(Point a, Point b, bool fromTop)
    {
        var dy = Math.Max(40, Math.Abs(b.Y - a.Y) / 2);
        var sign = fromTop ? 1 : -1;
        var fig = new PathFigure { StartPoint = a };
        fig.Segments.Add(new BezierSegment(new Point(a.X, a.Y + sign * dy), new Point(b.X, b.Y - sign * dy), b, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    private void DrawLink(Canvas hitLayer, Canvas strokeLayer, PortLink link)
    {
        var a = Anchor(new PortRef(PanelSide.Source, link.Source));
        var b = Anchor(new PortRef(PanelSide.Target, link.Target));
        var geometry = Curve(a, b, true);
        var conflict = _state!.StatusOf(link) == LinkStatus.Conflict;
        var selected = link == _selectedLink;
        var brush = conflict ? Red : Green;

        var visible = new Path
        {
            Data = geometry, Stroke = brush, StrokeThickness = selected ? 5 : 3, Opacity = 0.9,
            StrokeDashArray = selected ? new DoubleCollection { 2, 1 } : null,
        };
        strokeLayer.Children.Add(visible);
        foreach (var p in new[] { a, b })
        {
            var dot = new Ellipse { Width = 9, Height = 9, Fill = brush, Stroke = Brushes.White, StrokeThickness = 1.5 };
            SetLeft(dot, p.X - 4.5);
            SetTop(dot, p.Y - 4.5);
            strokeLayer.Children.Add(dot);
        }

        var hit = new Path { Data = geometry, Stroke = Brushes.Transparent, StrokeThickness = 14, Cursor = Cursors.Hand, Tag = link };
        hit.ToolTip = $"{link.Source} → {link.Target}{(conflict ? "  (conflict)" : "")}\nClick to select, Delete to remove.";
        hit.MouseLeftButtonDown += (_, e) =>
        {
            _selectedLink = link;
            SelectedPort = null;
            Focus();
            Rebuild();
            e.Handled = true;
        };
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = $"Remove {link.Source} → {link.Target}" };
        remove.Click += (_, _) => _state.Remove(link);
        menu.Items.Add(remove);
        hit.ContextMenu = menu;
        hitLayer.Children.Add(hit);
    }

    // ---------------------------------------------------------------- interaction

    private PortRef? PortAt(Point p) => _ports.FirstOrDefault(kv => kv.Value.Contains(p)).Key;

    private void OnPortDown(object sender, MouseButtonEventArgs e)
    {
        if (_state == null || sender is not FrameworkElement { Tag: PortRef port }) return;
        _pressed = port;
        _pressPoint = e.GetPosition(this);
        CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_state == null) return;
        var pos = e.GetPosition(this);

        if (_pressed != null && _dragAnchor == null && (pos - _pressPoint).Length > 5)
            BeginDrag(_pressed);

        if (_dragAnchor == null || _dragLine == null) return;
        _dragLine.Data = Curve(Anchor(_dragAnchor), pos, _dragAnchor.Side == PanelSide.Source);

        var over = PortAt(pos);
        if (over != null && over.Side == _dragAnchor.Side) over = null;
        if (!Equals(over, _hover))
        {
            _hover = over;
            RedrawKeepingDragLine();
        }
    }

    /// <summary>
    /// Pressing a connected port and dragging picks the existing line up by that end
    /// (source port: the line is re-pointed from the source; target port: the line's target end moves).
    /// Pressing a free port starts a new line.
    /// </summary>
    private void BeginDrag(PortRef pressed)
    {
        _selectedLink = null;
        _detached.Clear();
        if (pressed.Side == PanelSide.Source)
        {
            _dragAnchor = pressed;
            foreach (var l in _state!.Links.Where(l => l.Source == pressed.Name).ToList())
            {
                _detached.Add(l);
                _state.Remove(l);
            }
        }
        else
        {
            var existing = _state!.Links.LastOrDefault(l => l.Target == pressed.Name);
            if (existing != null)
            {
                _detached.Add(existing);
                _state.Remove(existing);
                _dragAnchor = new PortRef(PanelSide.Source, existing.Source);
            }
            else _dragAnchor = pressed;
        }
        RedrawKeepingDragLine();
    }

    private void RedrawKeepingDragLine()
    {
        var data = _dragLine?.Data;
        Rebuild();
        _dragLine = new Path { Stroke = Drag, StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false, Data = data };
        Children.Add(_dragLine);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_state == null) return;
        var pos = e.GetPosition(this);
        ReleaseMouseCapture();

        if (_dragAnchor != null)
        {
            _detached.Clear(); // dropping on empty space deletes the line
            var drop = PortAt(pos);
            if (drop != null && drop.Side != _dragAnchor.Side)
            {
                var (s, t) = _dragAnchor.Side == PanelSide.Source ? (_dragAnchor.Name, drop.Name) : (drop.Name, _dragAnchor.Name);
                _dragAnchor = null;
                _dragLine = null;
                _hover = null;
                _state.Add(s, t); // triggers Rebuild
            }
            else
            {
                _dragAnchor = null;
                _dragLine = null;
                _hover = null;
                Rebuild();
            }
        }
        else if (_pressed != null)
        {
            // Plain click: select the port.
            SelectedPort = _pressed;
            _selectedLink = null;
            Rebuild();
            PortSelected?.Invoke(this, SelectedPort);
        }
        _pressed = null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_state == null) return;
        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedLink != null)
            {
                var l = _selectedLink;
                _selectedLink = null;
                _state.Remove(l);
            }
            else if (SelectedPort != null)
            {
                var p = SelectedPort;
                foreach (var l in _state.Links.Where(l => p.Side == PanelSide.Source ? l.Source == p.Name : l.Target == p.Name).ToList())
                    _state.Remove(l);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _dragAnchor != null)
        {
            // Cancel the drag and put back any line that was picked up.
            _dragAnchor = null;
            _dragLine = null;
            _hover = null;
            _pressed = null;
            ReleaseMouseCapture();
            foreach (var l in _detached) _state.Add(l.Source, l.Target);
            _detached.Clear();
            Rebuild();
            e.Handled = true;
        }
    }
}
