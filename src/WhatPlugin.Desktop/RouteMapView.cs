using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using WhatPlugin.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace WhatPlugin.Desktop;

/// <summary>
/// 文件夹的路由图，和 Windows 版一样从左往右：轨道 → 编组（可以多层）→ 效果轨 → 主输出。
/// 每张卡片是一条笔记和它串联的插件；实线是输出，虚线是发送（并联），绿色点线是侧链。
/// 发送和侧链平时淡一些，鼠标停在卡片上时突出。排布在 Core 的 RouteMapLayout 里算。
/// 点卡片打开笔记；Ctrl（Mac 上 Command）加滚轮缩放，按住空白处拖动平移。
/// </summary>
public sealed class RouteMapView : Panel
{
    private const double CardWidth = 200;
    private const double ColumnGap = 72;
    private const double RowGap = 14;
    private const double Margin0 = 16;
    private const double SendOpacity = 0.4;

    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly LayoutTransformControl _zoom;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Dictionary<long, Border> _cards = [];
    private readonly List<(long From, long To, double Resting, Path Line, Path Arrow)> _edges = [];
    private Point? _panStart;
    private Vector _panOffset;
    private double _scale = 1;

    public event Action<long>? OpenNote;

    public RouteMapView()
    {
        _zoom = new LayoutTransformControl { Child = _canvas };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _zoom,
        };
        _empty.Bind(TextBlock.ForegroundProperty, _empty.GetResourceObservable("Muted"));
        Children.Add(_scroll);
        Children.Add(_empty);
        AutomationProperties.SetName(this, "路由图");
        AutomationProperties.SetAutomationId(this, "RouteMap");

        _scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
                return;
            Zoom = _scale * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _canvas.PointerPressed += (_, e) =>
        {
            if (e.Source != _canvas || !e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
                return;
            _panStart = e.GetPosition(_scroll);
            _panOffset = _scroll.Offset;
            e.Pointer.Capture(_canvas);
            _canvas.Cursor = new Cursor(StandardCursorType.SizeAll);
        };
        _canvas.PointerMoved += (_, e) =>
        {
            if (_panStart is not { } start)
                return;
            var now = e.GetPosition(_scroll);
            _scroll.Offset = new Vector(_panOffset.X - (now.X - start.X), _panOffset.Y - (now.Y - start.Y));
        };
        _canvas.PointerReleased += (_, e) =>
        {
            _panStart = null;
            e.Pointer.Capture(null);
            _canvas.Cursor = null;
        };
    }

    /// <summary>缩放比例，0.4 到 2 之间。</summary>
    public double Zoom
    {
        get => _scale;
        set
        {
            _scale = Math.Clamp(value, 0.4, 2.0);
            _zoom.LayoutTransform = new ScaleTransform(_scale, _scale);
        }
    }

    public bool IsEmpty => _empty.IsVisible;

    public string EmptyText => _empty.Text ?? "";

    /// <summary>画出来的卡片（测试检查用），键是笔记 Id，补出来的主输出是 RouteMapLayout.ImplicitMaster。</summary>
    public IReadOnlyDictionary<long, Border> Cards => _cards;

    public int EdgeCount => _edges.Count;

    /// <summary>画出 notes 之间的路由。只画接了路由的笔记；没有输出的接到“主输出”。</summary>
    public void Show(IReadOnlyList<PluginChain> notes, IReadOnlyList<NoteRoute> routes, Func<PluginChain, IReadOnlyList<(string Name, bool Installed)>> plugins)
    {
        _canvas.Children.Clear();
        _cards.Clear();
        _edges.Clear();

        var layout = RouteMapLayout.Build(notes, routes);
        if (layout.IsEmpty)
        {
            _empty.Text = RouteMapLayout.EmptyText;
            _empty.IsVisible = true;
            _scroll.IsVisible = false;
            return;
        }
        _empty.IsVisible = false;
        _scroll.IsVisible = true;

        var heights = new Dictionary<long, double>();
        foreach (var node in layout.Columns.SelectMany(c => c))
        {
            var card = Card(node, node.Note == null ? [] : plugins(node.Note));
            // 卡片要先进视觉树才能拿到主题里的字体和颜色，量出来的高度才准。
            _canvas.Children.Add(card);
            card.Measure(new Size(CardWidth, double.PositiveInfinity));
            heights[node.Id] = card.DesiredSize.Height;
            _cards[node.Id] = card;
        }
        var tops = layout.Place(id => heights[id], Margin0, RowGap);
        double maxBottom = 0;
        foreach (var (id, card) in _cards)
        {
            Canvas.SetLeft(card, Margin0 + layout.ColumnOf(id) * (CardWidth + ColumnGap));
            Canvas.SetTop(card, tops[id]);
            card.ZIndex = 2;
            maxBottom = Math.Max(maxBottom, tops[id] + heights[id]);
        }
        _canvas.Width = Margin0 + layout.Columns.Count * (CardWidth + ColumnGap) - ColumnGap + Margin0;
        _canvas.Height = maxBottom + Margin0 + 30;

        foreach (var edge in layout.Edges)
            DrawEdge(edge, heights, tops, layout);

        // 比可见区域宽时先缩小到放得下（最小 0.6），之后可以 Ctrl + 滚轮再调。
        Dispatcher.UIThread.Post(() =>
        {
            var width = _scroll.Viewport.Width;
            Zoom = width > 0 && _canvas.Width > width ? Math.Max(0.6, width / _canvas.Width) : 1;
        }, DispatcherPriority.Loaded);
    }

    private void DrawEdge(NoteRoute edge, Dictionary<long, double> heights, IReadOnlyDictionary<long, double> tops, RouteMapLayout layout)
    {
        if (!_cards.TryGetValue(edge.From, out var from) || !_cards.TryGetValue(edge.To, out var to))
            return;
        var send = edge.Kind == RouteKind.Send;
        var sidechain = edge.Kind == RouteKind.Sidechain;
        var offset = send ? 7 : sidechain ? -7 : 0;
        var fromTop = tops[edge.From];
        var toTop = tops[edge.To];
        var fromLeft = Canvas.GetLeft(from);
        var toLeft = Canvas.GetLeft(to);
        var start = new Point(fromLeft + CardWidth, fromTop + heights[edge.From] / 2 + offset);
        var figure = new PathFigure { StartPoint = start, IsClosed = false, Segments = [] };
        Point end;
        bool pointsLeft;
        if (layout.ColumnOf(edge.To) > layout.ColumnOf(edge.From))
        {
            end = new Point(toLeft, toTop + heights[edge.To] / 2 + offset);
            var bend = Math.Max(30, (end.X - start.X) / 2);
            figure.Segments.Add(new BezierSegment { Point1 = new Point(start.X + bend, start.Y), Point2 = new Point(end.X - bend, end.Y), Point3 = end });
            pointsLeft = false;
        }
        else if (layout.ColumnOf(edge.To) == layout.ColumnOf(edge.From))
        {
            // 同一列（例如效果轨发送到另一条效果轨）：从右边绕一个弯接到目标右边。
            end = new Point(toLeft + CardWidth, toTop + heights[edge.To] / 2 + offset);
            var reach = Math.Min(ColumnGap - 12, 26 + Math.Abs(end.Y - start.Y) / 6);
            figure.Segments.Add(new BezierSegment { Point1 = new Point(start.X + reach, start.Y), Point2 = new Point(end.X + reach, end.Y), Point3 = end });
            pointsLeft = true;
        }
        else
        {
            // 往回接：从两张卡片下面绕过去。
            end = new Point(toLeft, toTop + heights[edge.To] / 2 + offset);
            var below = Math.Max(fromTop + heights[edge.From], toTop + heights[edge.To]) + 18;
            figure.Segments.Add(new BezierSegment { Point1 = new Point(start.X + 40, start.Y), Point2 = new Point(start.X + 40, below), Point3 = new Point(start.X, below) });
            figure.Segments.Add(new LineSegment { Point = new Point(end.X, below) });
            figure.Segments.Add(new BezierSegment { Point1 = new Point(end.X - 40, below), Point2 = new Point(end.X - 40, end.Y), Point3 = end });
            pointsLeft = false;
        }

        var brush = send ? "Accent" : sidechain ? "Good" : "TextDim";
        var line = new Path { Data = new PathGeometry { Figures = [figure] }, StrokeThickness = send || sidechain ? 1.4 : 1.8, IsHitTestVisible = false };
        line.Bind(Shape.StrokeProperty, line.GetResourceObservable(brush));
        if (send)
            line.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        if (sidechain)
        {
            line.StrokeDashArray = new AvaloniaList<double> { 0.1, 2.6 };
            line.StrokeLineCap = PenLineCap.Round;
            line.StrokeThickness = 2;
        }
        var d = pointsLeft ? 7 : -7;
        var arrow = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M {end.X + d},{end.Y - 4} L {end.X},{end.Y} L {end.X + d},{end.Y + 4} Z")),
            IsHitTestVisible = false,
        };
        arrow.Bind(Shape.FillProperty, arrow.GetResourceObservable(brush));
        var rest = send || sidechain ? SendOpacity : 1;
        line.Opacity = arrow.Opacity = rest;
        line.ZIndex = arrow.ZIndex = send || sidechain ? 0 : 1;
        _canvas.Children.Add(line);
        _canvas.Children.Add(arrow);
        _edges.Add((edge.From, edge.To, rest, line, arrow));
    }

    public static MaterialIconKind RoleIcon(ChannelRole role) => role switch
    {
        ChannelRole.Group => MaterialIconKind.CallMerge,
        ChannelRole.Effect => MaterialIconKind.Waveform,
        ChannelRole.Master => MaterialIconKind.VolumeHigh,
        _ => MaterialIconKind.MusicNote,
    };

    private Border Card(RouteMapNode node, IReadOnlyList<(string Name, bool Installed)> plugins)
    {
        var note = node.Note;
        var role = node.Role;
        var stack = new StackPanel();
        var head = new DockPanel();
        if (note == null || role != ChannelRole.Track)
        {
            var icon = new MaterialIcon { Kind = RoleIcon(role), Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("Accent"));
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(icon);
        }
        var kind = new TextBlock { Text = TrackRouting.RoleText(role), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        kind.Bind(TextBlock.ForegroundProperty, kind.GetResourceObservable("Muted"));
        DockPanel.SetDock(kind, Dock.Right);
        head.Children.Add(kind);
        head.Children.Add(new TextBlock { Text = node.Name, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(head);

        if (note != null)
        {
            var shown = plugins.Take(4).ToList();
            var chain = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Inlines = [] };
            chain.Bind(TextBlock.ForegroundProperty, chain.GetResourceObservable(shown.Count == 0 ? "Muted" : "TextDim"));
            if (shown.Count == 0)
                chain.Text = "没有插件";
            for (var i = 0; i < shown.Count; i++)
            {
                if (i > 0)
                    chain.Inlines.Add(new Run(" → "));
                var run = new Run(shown[i].Name);
                if (!shown[i].Installed)
                    run.Bind(TextElement.ForegroundProperty, chain.GetResourceObservable("Danger"));
                chain.Inlines.Add(run);
            }
            if (plugins.Count > shown.Count)
                chain.Inlines.Add(new Run($"  +{plugins.Count - shown.Count}"));
            stack.Children.Add(chain);
            if (node.Sends.Count > 0)
            {
                var line = new TextBlock { Text = "发送 → " + string.Join("、", node.Sends), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable("Accent"));
                stack.Children.Add(line);
            }
            if (node.Sidechains.Count > 0)
            {
                var line = new TextBlock { Text = "侧链 → " + string.Join("、", node.Sidechains), FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable("Good"));
                stack.Children.Add(line);
            }
        }

        var card = new Border
        {
            Width = CardWidth,
            Padding = new Thickness(12, 9, 12, 10),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(role == ChannelRole.Track ? 1 : 1.5),
            Child = stack,
            Cursor = note == null ? null : new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(card, RouteMapLayout.ToolTip(node, plugins));
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("Surface"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable(role == ChannelRole.Track ? "LineStrong" : "Accent"));
        AutomationProperties.SetName(card, node.Name + "（" + TrackRouting.RoleText(role) + "）");
        if (note != null)
        {
            var id = note.Id;
            card.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left)
                    return;
                e.Handled = true;
                OpenNote?.Invoke(id);
            };
        }
        var key = node.Id;
        card.PointerEntered += (_, _) => Highlight(key);
        card.PointerExited += (_, _) => Highlight(null);
        return card;
    }

    /// <summary>鼠标停在卡片上时，只突出和它相连的线和卡片（发送线也变实）。</summary>
    public void Highlight(long? id)
    {
        var related = id == null ? null : _edges.Where(e => e.From == id || e.To == id).SelectMany(e => new[] { e.From, e.To }).Append(id.Value).ToHashSet();
        foreach (var (from, to, rest, line, arrow) in _edges)
        {
            line.Opacity = arrow.Opacity = id == null ? rest : from == id || to == id ? 1 : 0.08;
            line.ZIndex = id != null && (from == id || to == id) ? 1 : 0;
        }
        foreach (var (cardId, card) in _cards)
            card.Opacity = related == null || related.Contains(cardId) ? 1 : 0.4;
    }
}
