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
    private const long ImplicitMaster = -1;

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

    /// <summary>画出 notes 之间的路由。只画接了路由的笔记；没有输出的接到“主输出”。</summary>
    public void Show(IReadOnlyList<PluginChain> notes, IReadOnlyList<NoteRoute> routes, Func<PluginChain, IReadOnlyList<(string Name, bool Installed)>> plugins)
    {
        _canvas.Children.Clear();
        _cards.Clear();
        _edges.Clear();

        var byId = notes.ToDictionary(n => n.Id);
        var edges = routes.Where(r => byId.ContainsKey(r.From) && byId.ContainsKey(r.To) && r.From != r.To).ToList();
        var routed = edges.SelectMany(r => new[] { r.From, r.To }).ToHashSet();
        foreach (var note in notes.Where(n => n.Role != ChannelRole.Track))
            routed.Add(note.Id);
        var nodes = notes.Where(n => routed.Contains(n.Id)).ToList();
        if (nodes.Count == 0)
        {
            _empty.Text = "这个文件夹里的笔记还没有路由。从工程导入轨道时勾选“连同输出和发送到的编组、效果轨一起导入”会自动连好；也可以在笔记里设置“输出到”和“发送”。";
            _empty.Visibility = Visibility.Visible;
            _scroll.Visibility = Visibility.Collapsed;
            return;
        }
        _empty.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;

        // 没有输出连线的（主输出通道本身除外）接到主输出：文件夹里只有一条主输出笔记就接它，否则画一个“主输出”。
        var outputs = edges.Where(e => e.Kind == RouteKind.Output).Select(e => e.From).ToHashSet();
        var masters = nodes.Where(n => n.Role == ChannelRole.Master).ToList();
        var dangling = nodes.Where(n => n.Role != ChannelRole.Master && !outputs.Contains(n.Id)).ToList();
        long? sink = null;
        if (dangling.Count > 0)
        {
            sink = masters.Count == 1 ? masters[0].Id : ImplicitMaster;
            foreach (var node in dangling)
                edges.Add(new NoteRoute(node.Id, sink.Value, RouteKind.Output));
        }
        var ids = nodes.Select(n => n.Id).ToList();
        if (sink == ImplicitMaster)
            ids.Add(ImplicitMaster);

        ChannelRole RoleOf(long id) => id == ImplicitMaster ? ChannelRole.Master : byId[id].Role;
        bool Main(long id) => RoleOf(id) is ChannelRole.Track or ChannelRole.Group;

        // 分列：轨道和编组按接入的层数往右排（编组可以套编组），效果轨统一放一列，主输出放最后一列。有环时最多迭代 n 次。
        var column = ids.ToDictionary(id => id, _ => 0);
        for (var pass = 0; pass < ids.Count; pass++)
        {
            var changed = false;
            foreach (var edge in edges.Where(e => e.Kind != RouteKind.Sidechain && Main(e.From) && Main(e.To)))
            {
                if (column[edge.To] < column[edge.From] + 1)
                {
                    column[edge.To] = column[edge.From] + 1;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }
        var mainLast = ids.Where(Main).Select(id => column[id]).DefaultIfEmpty(-1).Max();
        var effectColumn = mainLast + 1;
        var masterColumn = effectColumn + (ids.Any(id => RoleOf(id) == ChannelRole.Effect) ? 1 : 0);
        foreach (var id in ids)
        {
            if (RoleOf(id) == ChannelRole.Effect)
                column[id] = effectColumn;
            else if (RoleOf(id) == ChannelRole.Master)
                column[id] = masterColumn;
        }

        // 列内顺序：按相邻列里连着的位置来回排几遍，减少交叉。排序优先看输出（主干），没有才看发送。
        var columns = ids.GroupBy(id => column[id]).OrderBy(g => g.Key)
            .Select(g => g.OrderBy(id => RoleOrder(RoleOf(id))).ThenBy(id => id == ImplicitMaster ? "" : byId[id].Name, StringComparer.CurrentCulture).ToList())
            .ToList();
        Dictionary<long, double> Positions() => columns
            .SelectMany(c => c.Select((id, i) => (id, p: (i + 0.5) / c.Count)))
            .ToDictionary(x => x.id, x => x.p);
        double? Average(IEnumerable<NoteRoute> links, Func<NoteRoute, long> other, Dictionary<long, double> position)
        {
            var list = links.ToList();
            var main = list.Where(e => e.Kind == RouteKind.Output).ToList();
            var use = main.Count > 0 ? main : list;
            return use.Count > 0 ? use.Average(e => position[other(e)]) : null;
        }
        void Order(List<long> col, Func<long, double?> key)
        {
            var keys = col.Select((id, i) => (id, k: key(id) ?? (i + 0.5) / col.Count)).ToDictionary(x => x.id, x => x.k);
            var stable = col.ToList();
            col.Sort((a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : stable.IndexOf(a).CompareTo(stable.IndexOf(b)));
        }
        for (var sweep = 0; sweep < 3; sweep++)
        {
            for (var c = columns.Count - 2; c >= 0; c--)
            {
                var position = Positions();
                Order(columns[c], id => Average(edges.Where(e => e.From == id && e.Kind != RouteKind.Sidechain && column[e.To] > column[id]), e => e.To, position));
            }
            for (var c = 1; c < columns.Count; c++)
            {
                var position = Positions();
                Order(columns[c], id => Average(edges.Where(e => e.To == id && e.Kind != RouteKind.Sidechain && column[e.From] < column[id]), e => e.From, position));
            }
        }

        // 每条笔记的发送和侧链目标，写在卡片上（线淡的时候也能看清）。
        var sendNames = edges.Where(e => e.Kind == RouteKind.Send)
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => byId[e.To].Name).ToList());
        var sidechainNames = edges.Where(e => e.Kind == RouteKind.Sidechain)
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => $"{byId[e.To].Name}（{e.Plugin}）").ToList());

        // 放卡片：第一列从上往下排；后面的列尽量和接入它的卡片对齐（像思维导图），不重叠。
        var center = new Dictionary<long, double>();
        var heights = new Dictionary<long, double>();
        var x = Margin0;
        double maxBottom = 0;
        foreach (var col in columns)
        {
            var bottom = Margin0 - RowGap;
            foreach (var id in col)
            {
                var card = id == ImplicitMaster
                    ? Card(null, [], ChannelRole.Master, [], [])
                    : Card(byId[id], plugins(byId[id]), byId[id].Role, sendNames.GetValueOrDefault(id) ?? [], sidechainNames.GetValueOrDefault(id) ?? []);
                card.Measure(new Size(CardWidth, double.PositiveInfinity));
                var height = card.DesiredSize.Height;
                var incoming = edges.Where(e => e.To == id && e.Kind != RouteKind.Sidechain && center.ContainsKey(e.From)).ToList();
                var mainIn = incoming.Where(e => e.Kind == RouteKind.Output).ToList();
                var use = mainIn.Count > 0 ? mainIn : incoming;
                var top = use.Count > 0 ? use.Average(e => center[e.From]) - height / 2 : Margin0;
                top = Math.Max(top, bottom + RowGap);
                Canvas.SetLeft(card, x);
                Canvas.SetTop(card, top);
                Panel.SetZIndex(card, 2);
                _canvas.Children.Add(card);
                _cards[id] = card;
                heights[id] = height;
                center[id] = top + height / 2;
                bottom = top + height;
            }
            maxBottom = Math.Max(maxBottom, bottom);
            x += CardWidth + ColumnGap;
        }
        _canvas.Width = x - ColumnGap + Margin0;
        _canvas.Height = maxBottom + Margin0 + 30;

        foreach (var edge in edges.OrderBy(e => e.Kind == RouteKind.Output))
            DrawEdge(edge, heights, column);

        // 比可见区域宽时先缩小到放得下（最小 0.6），之后可以 Ctrl + 滚轮再调。
        Dispatcher.BeginInvoke(() =>
        {
            var width = _scroll.ViewportWidth;
            var fit = width > 0 && _canvas.Width > width ? Math.Max(0.6, width / _canvas.Width) : 1;
            _scale.ScaleX = _scale.ScaleY = fit;
        }, DispatcherPriority.Loaded);
    }

    private void DrawEdge(NoteRoute edge, Dictionary<long, double> heights, Dictionary<long, int> column)
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
        if (column[edge.To] > column[edge.From])
        {
            end = new Point(Canvas.GetLeft(to), toTop + heights[edge.To] / 2 + offset);
            var bend = Math.Max(30, (end.X - start.X) / 2);
            figure.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end, true));
            pointsLeft = false;
        }
        else if (column[edge.To] == column[edge.From])
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

    private static int RoleOrder(ChannelRole role) =>
        role switch { ChannelRole.Group => 1, ChannelRole.Effect => 2, ChannelRole.Master => 3, _ => 0 };

    private Border Card(PluginChain? note, IReadOnlyList<(string Name, bool Installed)> plugins, ChannelRole role, IReadOnlyList<string> sends, IReadOnlyList<string> sidechains)
    {
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
            ToolTip = note == null
                ? "没有指定输出的通道都接到这里"
                : (plugins.Count > 0 ? string.Join(" → ", plugins.Select(p => p.Name)) + "\n" : "")
                  + (sends.Count > 0 ? "发送 → " + string.Join("、", sends) + "\n" : "")
                  + (sidechains.Count > 0 ? "侧链 → " + string.Join("、", sidechains) + "\n" : "")
                  + "点击打开这条笔记",
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
        var key = note?.Id ?? ImplicitMaster;
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
