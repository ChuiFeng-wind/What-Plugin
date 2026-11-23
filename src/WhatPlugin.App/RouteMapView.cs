using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>
/// 文件夹的路由图，像思维导图一样从左往右：轨道 → 编组（可以多层）→ 效果轨 → 主输出。
/// 每张卡片是一条笔记和它串联的插件；实线是输出，虚线是发送（并联），绿色点线是侧链。
/// 发送和侧链平时淡一些，鼠标停在卡片上时突出。
/// 点卡片打开笔记；Ctrl + 滚轮缩放，按住空白处拖动平移。
/// </summary>
public sealed class RouteMapView : Grid
{
    private const double CardWidth = 200;
    private const double ColumnGap = 72;
    private const double RowGap = 14;
    private const double Margin0 = 16;
    private const double SendOpacity = 0.4;

    private readonly Canvas _canvas = new();
    private readonly ScrollViewer _scroll;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Dictionary<long, Border> _cards = [];
    private readonly List<(long From, long To, double Resting, Path Line, Path Arrow)> _edges = [];
    private Point? _panStart;
    private Point _panOffset;

    public event Action<long>? OpenNote;

    public RouteMapView()
    {
        _canvas.LayoutTransform = _scale;
        _canvas.Background = Brushes.Transparent;
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _canvas,
            Focusable = false,
        };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Children.Add(_scroll);
        Children.Add(_empty);
        AutomationProperties.SetName(this, "路由图");
        AutomationProperties.SetAutomationId(this, "RouteMap");

        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            var factor = Math.Clamp(_scale.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.4, 2.0);
            _scale.ScaleX = _scale.ScaleY = factor;
            e.Handled = true;
        };
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != _canvas)
                return;
            _panStart = e.GetPosition(_scroll);
            _panOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
            _canvas.CaptureMouse();
            _canvas.Cursor = Cursors.SizeAll;
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (_panStart is not { } start)
                return;
            var now = e.GetPosition(_scroll);
            _scroll.ScrollToHorizontalOffset(_panOffset.X - (now.X - start.X));
            _scroll.ScrollToVerticalOffset(_panOffset.Y - (now.Y - start.Y));
        };
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            _panStart = null;
            _canvas.ReleaseMouseCapture();
            _canvas.Cursor = null;
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>画出 notes 之间的路由。只画接了路由的笔记；没有输出的接到“主输出”。排布在 Core 里算。</summary>
    public void Show(IReadOnlyList<PluginChain> notes, IReadOnlyList<NoteRoute> routes, Func<PluginChain, IReadOnlyList<(string Name, bool Installed)>> plugins)
    {
        _canvas.Children.Clear();
        _cards.Clear();
        _edges.Clear();

        var layout = RouteMapLayout.Build(notes, routes);
        if (layout.IsEmpty)
        {
            _empty.Text = RouteMapLayout.EmptyText;
            _empty.Visibility = Visibility.Visible;
            _scroll.Visibility = Visibility.Collapsed;
            return;
        }
        _empty.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;

        var heights = new Dictionary<long, double>();
        foreach (var node in layout.Columns.SelectMany(c => c))
        {
            var card = Card(node, node.Note == null ? [] : plugins(node.Note));
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
            Panel.SetZIndex(card, 2);
            _canvas.Children.Add(card);
            maxBottom = Math.Max(maxBottom, tops[id] + heights[id]);
        }
        _canvas.Width = Margin0 + layout.Columns.Count * (CardWidth + ColumnGap) - ColumnGap + Margin0;
        _canvas.Height = maxBottom + Margin0 + 30;

        foreach (var edge in layout.Edges)
            DrawEdge(edge, heights, layout);

        // 比可见区域宽时先缩小到放得下（最小 0.6），之后可以 Ctrl + 滚轮再调。
        Dispatcher.BeginInvoke(() =>
        {
            var width = _scroll.ViewportWidth;
            var fit = width > 0 && _canvas.Width > width ? Math.Max(0.6, width / _canvas.Width) : 1;
            _scale.ScaleX = _scale.ScaleY = fit;
        }, DispatcherPriority.Loaded);
    }

    private void DrawEdge(NoteRoute edge, Dictionary<long, double> heights, RouteMapLayout layout)
    {
        if (!_cards.TryGetValue(edge.From, out var from) || !_cards.TryGetValue(edge.To, out var to))
            return;
        var send = edge.Kind == RouteKind.Send;
        var sidechain = edge.Kind == RouteKind.Sidechain;
        var offset = send ? 7 : sidechain ? -7 : 0;
        var fromTop = Canvas.GetTop(from);
        var toTop = Canvas.GetTop(to);
        var start = new Point(Canvas.GetLeft(from) + CardWidth, fromTop + heights[edge.From] / 2 + offset);
        var figure = new PathFigure { StartPoint = start };
        Point end;
        bool pointsLeft;
        if (layout.ColumnOf(edge.To) > layout.ColumnOf(edge.From))
        {
            end = new Point(Canvas.GetLeft(to), toTop + heights[edge.To] / 2 + offset);
            var bend = Math.Max(30, (end.X - start.X) / 2);
            figure.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end, true));
            pointsLeft = false;
        }
        else if (layout.ColumnOf(edge.To) == layout.ColumnOf(edge.From))
        {
            // 同一列（例如效果轨发送到另一条效果轨）：从右边绕一个弯接到目标右边。
            end = new Point(Canvas.GetLeft(to) + CardWidth, toTop + heights[edge.To] / 2 + offset);
            var reach = Math.Min(ColumnGap - 12, 26 + Math.Abs(end.Y - start.Y) / 6);
            figure.Segments.Add(new BezierSegment(new Point(start.X + reach, start.Y), new Point(end.X + reach, end.Y), end, true));
            pointsLeft = true;
        }
        else
        {
            // 往回接：从两张卡片下面绕过去。
            end = new Point(Canvas.GetLeft(to), toTop + heights[edge.To] / 2 + offset);
            var below = Math.Max(fromTop + heights[edge.From], toTop + heights[edge.To]) + 18;
            figure.Segments.Add(new BezierSegment(new Point(start.X + 40, start.Y), new Point(start.X + 40, below), new Point(start.X, below), true));
            figure.Segments.Add(new LineSegment(new Point(end.X, below), true));
            figure.Segments.Add(new BezierSegment(new Point(end.X - 40, below), new Point(end.X - 40, end.Y), end, true));
            pointsLeft = false;
        }

        var brush = send ? "Accent" : sidechain ? "Good" : "TextDim";
        var line = new Path { Data = new PathGeometry([figure]), StrokeThickness = send || sidechain ? 1.4 : 1.8, IsHitTestVisible = false };
        line.SetResourceReference(Shape.StrokeProperty, brush);
        if (send)
            line.StrokeDashArray = [4, 3];
        if (sidechain)
        {
            line.StrokeDashArray = [0.1, 2.6];
            line.StrokeDashCap = PenLineCap.Round;
            line.StrokeThickness = 2;
        }
        var d = pointsLeft ? 7 : -7;
        var arrow = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M {end.X + d},{end.Y - 4} L {end.X},{end.Y} L {end.X + d},{end.Y + 4} Z")),
            IsHitTestVisible = false,
        };
        arrow.SetResourceReference(Shape.FillProperty, brush);
        var rest = send || sidechain ? SendOpacity : 1;
        line.Opacity = arrow.Opacity = rest;
        Panel.SetZIndex(line, send || sidechain ? 0 : 1);
        Panel.SetZIndex(arrow, send || sidechain ? 0 : 1);
        _canvas.Children.Add(line);
        _canvas.Children.Add(arrow);
        _edges.Add((edge.From, edge.To, rest, line, arrow));
    }

    private Border Card(RouteMapNode node, IReadOnlyList<(string Name, bool Installed)> plugins)
    {
        var note = node.Note;
        var role = node.Role;
        var sends = node.Sends;
        var sidechains = node.Sidechains;
        var stack = new StackPanel();
        var head = new DockPanel();
        var icon = new TextBlock { Text = note == null || role != ChannelRole.Track ? MainWindow.RoleGlyph(role) : "", FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, role == ChannelRole.Track ? "Muted" : "Accent");
        var kind = new TextBlock { Text = TrackRouting.RoleText(role), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(kind, Dock.Right);
        head.Children.Add(icon);
        head.Children.Add(kind);
        head.Children.Add(new TextBlock { Text = note?.Name ?? "主输出", FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(head);

        if (note != null)
        {
            var shown = plugins.Take(4).ToList();
            var chain = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
            chain.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            if (shown.Count == 0)
            {
                chain.Text = "没有插件";
                chain.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            }
            for (var i = 0; i < shown.Count; i++)
            {
                if (i > 0)
                    chain.Inlines.Add(new Run(" → "));
                var run = new Run(shown[i].Name);
                if (!shown[i].Installed)
                    run.SetResourceReference(TextElement.ForegroundProperty, "Danger");
                chain.Inlines.Add(run);
            }
            if (plugins.Count > shown.Count)
                chain.Inlines.Add(new Run($"  +{plugins.Count - shown.Count}"));
            stack.Children.Add(chain);
            if (sends.Count > 0)
            {
                var line = new TextBlock { Text = "发送 → " + string.Join("、", sends), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                line.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                stack.Children.Add(line);
            }
            if (sidechains.Count > 0)
            {
                var line = new TextBlock { Text = "侧链 → " + string.Join("、", sidechains), FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                line.SetResourceReference(TextBlock.ForegroundProperty, "Good");
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
            Cursor = note == null ? null : Cursors.Hand,
            ToolTip = RouteMapLayout.ToolTip(node, plugins),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, role == ChannelRole.Track ? "LineStrong" : "Accent");
        AutomationProperties.SetName(card, (note?.Name ?? "主输出") + "（" + TrackRouting.RoleText(role) + "）");
        if (note != null)
        {
            var id = note.Id;
            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenNote?.Invoke(id);
            };
        }
        var key = node.Id;
        card.MouseEnter += (_, _) => Highlight(key);
        card.MouseLeave += (_, _) => Highlight(null);
        return card;
    }

    /// <summary>鼠标停在卡片上时，只突出和它相连的线和卡片（发送线也变实）。</summary>
    private void Highlight(long? id)
    {
        var related = id == null ? null : _edges.Where(e => e.From == id || e.To == id).SelectMany(e => new[] { e.From, e.To }).Append(id.Value).ToHashSet();
        foreach (var (from, to, rest, line, arrow) in _edges)
        {
            line.Opacity = arrow.Opacity = id == null ? rest : from == id || to == id ? 1 : 0.08;
            Panel.SetZIndex(line, id != null && (from == id || to == id) ? 1 : 0);
        }
        foreach (var (cardId, card) in _cards)
            card.Opacity = related == null || related.Contains(cardId) ? 1 : 0.4;
    }
}
