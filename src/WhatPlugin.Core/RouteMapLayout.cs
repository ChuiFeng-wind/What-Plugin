namespace WhatPlugin.Core;

/// <summary>路由图上的一张卡片。Note 为空表示补出来的“主输出”。</summary>
public sealed record RouteMapNode(long Id, PluginChain? Note, ChannelRole Role, int Column, IReadOnlyList<string> Sends, IReadOnlyList<string> Sidechains)
{
    public string Name => Note?.Name ?? "主输出";
}

/// <summary>
/// 文件夹路由图的排布，像思维导图一样从左往右：轨道 → 编组（可以多层）→ 效果轨 → 主输出。
/// 只算位置，画图由界面负责（Windows 版和跨平台版共用）。
/// </summary>
public sealed class RouteMapLayout
{
    /// <summary>没有指定输出的通道接到这张补出来的“主输出”卡片。</summary>
    public const long ImplicitMaster = -1;

    private readonly Dictionary<long, RouteMapNode> _nodes;

    /// <summary>从左到右的列，每列从上到下。没有接路由的笔记不画，这时为空。</summary>
    public IReadOnlyList<IReadOnlyList<RouteMapNode>> Columns { get; }

    /// <summary>要画的连线，包括补到主输出的输出线。</summary>
    public IReadOnlyList<NoteRoute> Edges { get; }

    public bool IsEmpty => Columns.Count == 0;

    private RouteMapLayout(IReadOnlyList<IReadOnlyList<RouteMapNode>> columns, IReadOnlyList<NoteRoute> edges)
    {
        Columns = columns;
        Edges = edges;
        _nodes = columns.SelectMany(c => c).ToDictionary(n => n.Id);
    }

    public RouteMapNode Node(long id) => _nodes[id];

    public int ColumnOf(long id) => _nodes[id].Column;

    public const string EmptyText = "这个文件夹里的笔记还没有路由。从工程导入轨道时勾选“连同输出和发送到的编组、效果轨一起导入”会自动连好；也可以在笔记里设置“输出到”和“发送”。";

    /// <summary>只画接了路由的笔记（编组、效果轨、主输出本身也画）；没有输出的接到“主输出”。</summary>
    public static RouteMapLayout Build(IReadOnlyList<PluginChain> notes, IReadOnlyList<NoteRoute> routes)
    {
        var byId = notes.ToDictionary(n => n.Id);
        var edges = routes.Where(r => byId.ContainsKey(r.From) && byId.ContainsKey(r.To) && r.From != r.To).ToList();
        var routed = edges.SelectMany(r => new[] { r.From, r.To }).ToHashSet();
        foreach (var note in notes.Where(n => n.Role != ChannelRole.Track))
            routed.Add(note.Id);
        var nodes = notes.Where(n => routed.Contains(n.Id)).ToList();
        if (nodes.Count == 0)
            return new RouteMapLayout([], []);

        // 没有输出连线的（主输出通道本身除外）接到主输出：文件夹里只有一条主输出笔记就接它，否则补一个“主输出”。
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
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => byId[e.To].Name).ToList());
        var sidechainNames = edges.Where(e => e.Kind == RouteKind.Sidechain)
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => $"{byId[e.To].Name}（{e.Plugin}）").ToList());

        RouteMapNode NodeOf(long id) => id == ImplicitMaster
            ? new RouteMapNode(id, null, ChannelRole.Master, column[id], [], [])
            : new RouteMapNode(id, byId[id], byId[id].Role, column[id], sendNames.GetValueOrDefault(id) ?? [], sidechainNames.GetValueOrDefault(id) ?? []);
        // 输出线最后画，压在发送线上面。
        return new RouteMapLayout(
            columns.Select(c => (IReadOnlyList<RouteMapNode>)c.Select(NodeOf).ToList()).ToList(),
            edges.OrderBy(e => e.Kind == RouteKind.Output).ToList());
    }

    /// <summary>
    /// 卡片的上边位置：第一列从上往下排；后面的列尽量和接入它的卡片对齐（像思维导图），不重叠。
    /// height 是每张卡片量出来的高度。
    /// </summary>
    public IReadOnlyDictionary<long, double> Place(Func<long, double> height, double margin, double rowGap)
    {
        var center = new Dictionary<long, double>();
        var tops = new Dictionary<long, double>();
        foreach (var col in Columns)
        {
            var bottom = margin - rowGap;
            foreach (var node in col)
            {
                var h = height(node.Id);
                var incoming = Edges.Where(e => e.To == node.Id && e.Kind != RouteKind.Sidechain && center.ContainsKey(e.From)).ToList();
                var mainIn = incoming.Where(e => e.Kind == RouteKind.Output).ToList();
                var use = mainIn.Count > 0 ? mainIn : incoming;
                var top = use.Count > 0 ? use.Average(e => center[e.From]) - h / 2 : margin;
                top = Math.Max(top, bottom + rowGap);
                tops[node.Id] = top;
                center[node.Id] = top + h / 2;
                bottom = top + h;
            }
        }
        return tops;
    }

    /// <summary>卡片上的提示：插件链、发送、侧链。</summary>
    public static string ToolTip(RouteMapNode node, IReadOnlyList<(string Name, bool Installed)> plugins) =>
        node.Note == null
            ? "没有指定输出的通道都接到这里"
            : (plugins.Count > 0 ? string.Join(" → ", plugins.Select(p => p.Name)) + "\n" : "")
              + (node.Sends.Count > 0 ? "发送 → " + string.Join("、", node.Sends) + "\n" : "")
              + (node.Sidechains.Count > 0 ? "侧链 → " + string.Join("、", node.Sidechains) + "\n" : "")
              + "点击打开这条笔记";

    private static int RoleOrder(ChannelRole role) =>
        role switch { ChannelRole.Group => 1, ChannelRole.Effect => 2, ChannelRole.Master => 3, _ => 0 };
}
