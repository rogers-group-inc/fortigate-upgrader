using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Mapping;
using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Views;

/// <summary>A port of the new firewall (IsPort) or a switch/aggregate interface box.</summary>
public sealed record GroupNodeRef(bool IsPort, string Name);

/// <summary>
/// Draws the new firewall's front panel (top) and the configuration's switch/aggregate interfaces (boxes below),
/// and lets the user drag lines from ports to the interfaces they should belong to.
/// </summary>
public sealed class GroupMapView : Canvas
{
    private const double Cell = 64, PortW = 54, PortH = 44, RowGap = 52, Pad = 16, Header = 30, Between = 150,
        BoxW = 210, BoxH = 92, BoxGap = 26;

    private readonly Dictionary<GroupNodeRef, Rect> _nodes = new();
    private GroupState? _state;
    private GroupLink? _selectedLink;
    private GroupNodeRef? _selected;

    private GroupNodeRef? _pressed;
    private Point _pressPoint;
    private GroupNodeRef? _dragAnchor;
    private Path? _dragLine;
    private GroupNodeRef? _hover;
    private readonly List<GroupLink> _detached = new();

    public GroupMapView()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeftButtonDown += (_, _) =>
        {
            Focus();
            if (_selectedLink == null && _selected == null) return;
            _selectedLink = null;
            _selected = null;
            Rebuild();
        };
        KeyDown += OnKeyDown;
    }

    public GroupState? State
    {
        get => _state;
        set
        {
            if (_state != null) { _state.Changed -= OnChanged; _state.Mapping.Changed -= OnChanged; }
            _state = value;
            if (_state != null) { _state.Changed += OnChanged; _state.Mapping.Changed += OnChanged; }
            _selectedLink = null;
            _selected = null;
            Rebuild();
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Rebuild();

    private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static readonly Brush Green = B("#16A34A"), Red = B("#DC2626"), Amber = B("#F59E0B"), Idle = B("#9CA3AF"),
        Drag = B("#2563EB"), PanelFill = B("#F3F4F6"), PanelStroke = B("#9CA3AF"), RJ45Fill = B("#374151"),
        OpticalFill = B("#64748B"), SharedFill = B("#475569"), Muted = B("#6B7280"), BoxFill = B("#EEF2FF");

    private static double PanelWidth(ModelProfile m) => (m.Ports.Max(p => p.Column) + 1) * Cell + 2 * Pad - (Cell - PortW);
    private static double PanelHeight(ModelProfile m) => Header + (m.Ports.Max(p => p.Row) + 1) * RowGap + Pad - (RowGap - PortH);

    public void Rebuild()
    {
        Children.Clear();
        _nodes.Clear();
        if (_state == null)
        {
            Width = 900;
            Height = 300;
            var hint = new TextBlock { Text = "Open a FortiGate configuration file to begin.", FontSize = 18, Foreground = Muted };
            SetLeft(hint, 260);
            SetTop(hint, 130);
            Children.Add(hint);
            return;
        }

        var dst = _state.Target;
        var groups = _state.Groups;
        var perRow = Math.Max(1, (int)Math.Floor((Math.Max(PanelWidth(dst), 900) + BoxGap) / (BoxW + BoxGap)));
        var rows = Math.Max(1, (int)Math.Ceiling(groups.Count / (double)perRow));
        var boxesWidth = Math.Min(groups.Count, perRow) * (BoxW + BoxGap) - BoxGap;
        var width = Math.Max(PanelWidth(dst), boxesWidth) + 40;
        var boxTop = 10 + PanelHeight(dst) + Between;
        Width = width;
        Height = boxTop + rows * (BoxH + BoxGap) + Pad;

        DrawPanel(dst, (width - PanelWidth(dst)) / 2, 10);
        if (groups.Count == 0)
        {
            var none = new TextBlock { Text = "This configuration has no hardware/software switches or aggregate interfaces.", FontSize = 15, Foreground = Muted };
            SetLeft(none, 20);
            SetTop(none, boxTop);
            Children.Add(none);
        }
        for (var i = 0; i < groups.Count; i++)
        {
            var row = i / perRow;
            var inRow = Math.Min(perRow, groups.Count - row * perRow);
            var rowWidth = inRow * (BoxW + BoxGap) - BoxGap;
            var x = (width - rowWidth) / 2 + (i % perRow) * (BoxW + BoxGap);
            _nodes[new GroupNodeRef(false, groups[i].Name)] = new Rect(x, boxTop + row * (BoxH + BoxGap), BoxW, BoxH);
        }

        // Frame the boxes as the current (source) firewall: these interfaces come from its config.
        if (groups.Count > 0)
        {
            var boxes = _nodes.Where(kv => !kv.Key.IsPort).Select(kv => kv.Value).ToList();
            var left = boxes.Min(b => b.Left) - Pad;
            var frame = new Rectangle
            {
                Width = boxes.Max(b => b.Right) + Pad - left, Height = boxes.Max(b => b.Bottom) + Pad - (boxTop - Header),
                RadiusX = 6, RadiusY = 6, Fill = PanelFill, Stroke = PanelStroke,
            };
            SetLeft(frame, left);
            SetTop(frame, boxTop - Header);
            Children.Add(frame);
            var title = new TextBlock { Text = $"Current: {_state.Mapping.Source.DisplayName} – switch & aggregate interfaces", FontWeight = FontWeights.SemiBold, FontSize = 13 };
            SetLeft(title, left + Pad);
            SetTop(title, boxTop - Header + 6);
            Children.Add(title);
        }

        var hitLayer = new Canvas();
        Children.Insert(0, hitLayer);
        foreach (var (node, rect) in _nodes)
        {
            if (node.IsPort) DrawPort(node, rect);
            else DrawGroup(node, rect);
        }
        var strokeLayer = new Canvas { IsHitTestVisible = false };
        Children.Add(strokeLayer);
        foreach (var link in _state.Links) DrawLink(hitLayer, strokeLayer, link);
    }

    private void DrawPanel(ModelProfile model, double x, double y)
    {
        var rect = new Rectangle { Width = PanelWidth(model), Height = PanelHeight(model), RadiusX = 6, RadiusY = 6, Fill = PanelFill, Stroke = PanelStroke };
        SetLeft(rect, x);
        SetTop(rect, y);
        Children.Add(rect);
        var title = new TextBlock { Text = $"New: {model.DisplayName}", FontWeight = FontWeights.SemiBold, FontSize = 13 };
        SetLeft(title, x + Pad);
        SetTop(title, y + 6);
        Children.Add(title);
        foreach (var p in model.Ports)
            _nodes[new GroupNodeRef(true, p.Name)] = new Rect(x + Pad + p.Column * Cell, y + Header + p.Row * RowGap, PortW, PortH);
    }

    private string? StandaloneSource(string port) =>
        _state!.Mapping.Links.Where(l => l.Target == port).Select(l => l.Source)
            .FirstOrDefault(s => _state.Mapping.Usage.TryGetValue(s, out var u) && u.InUse && !u.OnlyGroupMember);

    private void DrawPort(GroupNodeRef node, Rect r)
    {
        var def = _state!.Target.FindPort(node.Name)!;
        var links = _state.Links.Where(l => l.Port == node.Name).ToList();
        Brush stroke = Idle;
        double thickness = 1.5;
        if (links.Count > 0)
        {
            var problem = links.Any(l => _state.ProblemOf(l) != null);
            stroke = problem ? Red : links.Any(l => _state.WarningOf(l) != null) ? Amber : Green;
            thickness = 2.5;
        }
        if (Equals(node, _hover) && _dragAnchor != null) { stroke = Drag; thickness = 3.5; }
        if (Equals(node, _selected)) { stroke = Drag; thickness = 3.5; }

        var fill = def.Media switch
        {
            PortMedia.SFP or PortMedia.SFPPlus => OpticalFill,
            PortMedia.SharedRJ45SFP or PortMedia.SharedRJ45SFPPlus => SharedFill,
            _ => RJ45Fill,
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = def.Name, Foreground = Brushes.White, FontSize = def.Name.Length > 7 ? 9.5 : 11, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = def.Label.Length > 9 ? def.Label.Split('/')[0].Trim() : def.Label, Foreground = B("#D1D5DB"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center });

        var standalone = StandaloneSource(node.Name);
        var tip = new List<string> { $"{def.Name}  (front label \"{def.Label}\")", def.MediaDescription };
        tip.Add(def.SwitchCapable switch { true => "Can be a hardware switch member", false => "Can't be a hardware switch member", _ => "Hardware switch support not confirmed" });
        if (standalone != null) tip.Add($"Receives {standalone}'s settings (step 3); it can only join a group {standalone} was in");
        tip.Add(links.Count == 0 ? "Not in a switch or aggregate. Drag it onto one below." : $"Member of {string.Join(", ", links.Select(l => l.Group))}");
        foreach (var l in links) { var p = _state.ProblemOf(l) ?? _state.WarningOf(l); if (p != null) tip.Add("⚠ " + p); }

        var border = new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(4), Background = fill,
            BorderBrush = stroke, BorderThickness = new Thickness(thickness), Child = stack, Cursor = Cursors.Hand, Tag = node,
            ToolTip = string.Join("\n", tip), Opacity = standalone != null && links.Count == 0 ? 0.55 : 1,
        };
        ToolTipService.SetInitialShowDelay(border, 300);
        border.MouseLeftButtonDown += OnNodeDown;
        border.ContextMenu = Menu(links);
        SetLeft(border, r.X);
        SetTop(border, r.Y);
        Children.Add(border);

        if (standalone != null)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Drag, Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
            SetLeft(dot, r.Right - 6);
            SetTop(dot, r.Y - 3);
            Children.Add(dot);
        }
    }

    private void DrawGroup(GroupNodeRef node, Rect r)
    {
        var g = _state!.FindGroup(node.Name)!;
        var links = _state.LinksOf(g.Name).ToList();
        var empty = links.Count == 0 && g.OtherMembers.Count == 0;
        Brush stroke = empty ? Amber : links.Any(l => _state.ProblemOf(l) != null) ? Red : Green;
        var thickness = empty ? 3.0 : 2.0;
        if (Equals(node, _hover) && _dragAnchor != null) { stroke = Drag; thickness = 3.5; }
        if (Equals(node, _selected)) { stroke = Drag; thickness = 3.5; }

        var stack = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
        stack.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = g.KindLabel + (g.Address != null ? $" · {g.Address.Split(' ')[0]}" : ""), Foreground = Muted, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock
        {
            Text = empty ? "No ports – won't be converted" : $"{links.Count} port(s)" + (g.OtherMembers.Count > 0 ? $" + {g.OtherMembers.Count} other" : ""),
            Foreground = empty ? B("#B45309") : B("#15803D"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = g.PhysicalMembers.Count == 0 ? "was: (no ports)" : "was: " + string.Join(", ", g.PhysicalMembers),
            Foreground = Muted, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var tip = new List<string> { $"{g.Name} – {g.KindLabel}" };
        if (g.Address != null) tip.Add($"Address: {g.Address}");
        tip.Add("Old members: " + (g.PhysicalMembers.Count == 0 ? "none" : string.Join(", ", g.PhysicalMembers)));
        if (g.OtherMembers.Count > 0) tip.Add("Other members (kept): " + string.Join(", ", g.OtherMembers));
        tip.Add(links.Count == 0 ? "Drag ports of the new firewall here." : "New members: " + string.Join(", ", links.Select(l => l.Port)));
        if (empty) tip.Add("With no ports it won't be converted, and anything that uses it (policies, DHCP, VLANs…) is removed.");

        var border = new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(6), Background = BoxFill,
            BorderBrush = stroke, BorderThickness = new Thickness(thickness), Child = stack, Cursor = Cursors.Hand, Tag = node,
            ToolTip = string.Join("\n", tip),
        };
        ToolTipService.SetInitialShowDelay(border, 300);
        border.MouseLeftButtonDown += OnNodeDown;
        border.ContextMenu = Menu(links);
        SetLeft(border, r.X);
        SetTop(border, r.Y);
        Children.Add(border);
    }

    private ContextMenu Menu(List<GroupLink> links)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = links.Count > 1 ? "Remove all connections" : "Remove connection", IsEnabled = links.Count > 0 };
        remove.Click += (_, _) => { foreach (var l in links) _state!.Remove(l); };
        menu.Items.Add(remove);
        return menu;
    }

    private Point Anchor(GroupNodeRef node, GroupLink? link = null)
    {
        var r = _nodes[node];
        if (node.IsPort) return new Point(r.X + r.Width / 2, r.Bottom);
        // Spread the line ends along the top of the box.
        var links = _state!.LinksOf(node.Name).ToList();
        var i = link == null ? -1 : links.IndexOf(link);
        if (i < 0) return new Point(r.X + r.Width / 2, r.Top);
        return new Point(r.X + r.Width * (i + 1) / (links.Count + 1), r.Top);
    }

    private static Geometry Curve(Point a, Point b)
    {
        var dy = Math.Max(40, Math.Abs(b.Y - a.Y) / 2) * (b.Y >= a.Y ? 1 : -1);
        var fig = new PathFigure { StartPoint = a };
        fig.Segments.Add(new BezierSegment(new Point(a.X, a.Y + dy), new Point(b.X, b.Y - dy), b, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    private void DrawLink(Canvas hitLayer, Canvas strokeLayer, GroupLink link)
    {
        var a = Anchor(new GroupNodeRef(true, link.Port));
        var b = Anchor(new GroupNodeRef(false, link.Group), link);
        var geometry = Curve(a, b);
        var problem = _state!.ProblemOf(link);
        var warning = _state.WarningOf(link);
        var brush = problem != null ? Red : warning != null ? Amber : Green;
        var selected = link == _selectedLink;

        strokeLayer.Children.Add(new Path { Data = geometry, Stroke = brush, StrokeThickness = selected ? 5 : 3, Opacity = 0.9, StrokeDashArray = selected ? new DoubleCollection { 2, 1 } : null });
        foreach (var p in new[] { a, b })
        {
            var dot = new Ellipse { Width = 9, Height = 9, Fill = brush, Stroke = Brushes.White, StrokeThickness = 1.5 };
            SetLeft(dot, p.X - 4.5);
            SetTop(dot, p.Y - 4.5);
            strokeLayer.Children.Add(dot);
        }

        var hit = new Path { Data = geometry, Stroke = Brushes.Transparent, StrokeThickness = 14, Cursor = Cursors.Hand };
        hit.ToolTip = $"{link.Port} → {link.Group}" + (problem != null ? $"\n⚠ {problem}" : warning != null ? $"\n⚠ {warning}" : "") + "\nClick to select, Delete to remove.";
        hit.MouseLeftButtonDown += (_, e) =>
        {
            _selectedLink = link;
            _selected = null;
            Focus();
            Rebuild();
            e.Handled = true;
        };
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = $"Remove {link.Port} from {link.Group}" };
        remove.Click += (_, _) => _state.Remove(link);
        menu.Items.Add(remove);
        hit.ContextMenu = menu;
        hitLayer.Children.Add(hit);
    }

    // ---------------------------------------------------------------- interaction

    private GroupNodeRef? NodeAt(Point p) => _nodes.FirstOrDefault(kv => kv.Value.Contains(p)).Key;

    private void OnNodeDown(object sender, MouseButtonEventArgs e)
    {
        if (_state == null || sender is not FrameworkElement { Tag: GroupNodeRef node }) return;
        _pressed = node;
        _pressPoint = e.GetPosition(this);
        CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_state == null) return;
        var pos = e.GetPosition(this);
        if (_pressed != null && _dragAnchor == null && (pos - _pressPoint).Length > 5) BeginDrag(_pressed);
        if (_dragAnchor == null || _dragLine == null) return;
        _dragLine.Data = Curve(Anchor(_dragAnchor), pos);

        var over = NodeAt(pos);
        if (over != null && over.IsPort == _dragAnchor.IsPort) over = null;
        if (!Equals(over, _hover))
        {
            _hover = over;
            RedrawKeepingDragLine();
        }
    }

    /// <summary>Dragging a port that is already in a group picks its line up (drop it on another group, or on empty space to remove it).</summary>
    private void BeginDrag(GroupNodeRef pressed)
    {
        _selectedLink = null;
        _detached.Clear();
        _dragAnchor = pressed;
        if (pressed.IsPort)
        {
            foreach (var l in _state!.Links.Where(l => l.Port == pressed.Name).ToList())
            {
                _detached.Add(l);
                _state.Remove(l);
            }
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
            _detached.Clear();
            var drop = NodeAt(pos);
            var anchor = _dragAnchor;
            _dragAnchor = null;
            _dragLine = null;
            _hover = null;
            if (drop != null && drop.IsPort != anchor.IsPort)
            {
                var (port, group) = anchor.IsPort ? (anchor.Name, drop.Name) : (drop.Name, anchor.Name);
                _state.Add(port, group);
            }
            else Rebuild();
        }
        else if (_pressed != null)
        {
            _selected = _pressed;
            _selectedLink = null;
            Rebuild();
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
            else if (_selected != null)
            {
                var n = _selected;
                foreach (var l in _state.Links.Where(l => n.IsPort ? l.Port == n.Name : l.Group == n.Name).ToList()) _state.Remove(l);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _dragAnchor != null)
        {
            _dragAnchor = null;
            _dragLine = null;
            _hover = null;
            _pressed = null;
            ReleaseMouseCapture();
            foreach (var l in _detached) _state.Add(l.Port, l.Group);
            _detached.Clear();
            Rebuild();
            e.Handled = true;
        }
    }
}
