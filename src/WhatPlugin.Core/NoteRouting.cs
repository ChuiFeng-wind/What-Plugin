namespace WhatPlugin.Core;

/// <summary>笔记详情里路由区的内容：类型、输出到、发送、侧链和接入的。</summary>
public sealed record NoteRoutePanel(
    ChannelRole Role,
    IReadOnlyList<(string Text, long? Id)> Outputs,
    int OutputIndex,
    bool OutputEnabled,
    IReadOnlyList<PluginChain> Sends,
    IReadOnlyList<(PluginChain Target, string Plugin)> Sidechains,
    string Incoming,
    string IncomingToolTip);

/// <summary>混音笔记的路由（Windows 版和跨平台版共用）。只是记录，不会改 DAW 工程。</summary>
public static class NoteRouting
{
    public static readonly IReadOnlyList<(ChannelRole Role, string Text)> RoleOptions =
    [
        (ChannelRole.Track, "轨道"),
        (ChannelRole.Group, "编组 / 总线"),
        (ChannelRole.Effect, "效果轨"),
        (ChannelRole.Master, "主输出"),
    ];

    public const string SidechainHint = "把这条通道的信号送进别的通道上某个插件的侧链输入，例如人声压混响上的压缩";
    public const string Saved = "路由已保存";

    /// <summary>同一个文件夹里的其他笔记，编组、效果轨、主输出排在前面。</summary>
    public static List<PluginChain> Candidates(PluginChain chain, IReadOnlyList<PluginChain> chains) => chains
        .Where(c => c.Id != chain.Id && c.FolderId == chain.FolderId)
        .OrderBy(c => c.Role switch { ChannelRole.Group => 0, ChannelRole.Effect => 1, ChannelRole.Master => 2, _ => 3 })
        .ThenBy(c => c.Name, StringComparer.CurrentCulture)
        .ToList();

    public static string Label(PluginChain chain) =>
        chain.Role == ChannelRole.Track ? chain.Name : $"{chain.Name}（{TrackRouting.RoleText(chain.Role)}）";

    public static string RoleChanged(PluginChain chain, ChannelRole role) => $"“{chain.Name}”标为{TrackRouting.RoleText(role)}";

    public static NoteRoutePanel Panel(PluginChain chain, IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteRoute> routes, IReadOnlyList<NoteFolder> folders)
    {
        var mine = routes.Where(r => r.From == chain.Id).ToList();
        var output = mine.FirstOrDefault(r => r.Kind == RouteKind.Output)?.To;
        var outputs = new List<(string Text, long? Id)> { ("主输出", null) };
        var candidates = Candidates(chain, chains);
        if (output is { } current && candidates.All(c => c.Id != current) && chains.FirstOrDefault(c => c.Id == current) is { } elsewhere)
            candidates.Insert(0, elsewhere);
        foreach (var target in candidates.Where(c => c.Role != ChannelRole.Effect || c.Id == output))
            outputs.Add((Label(target) + (target.FolderId != chain.FolderId ? $" · {NoteListing.FolderPath(target.FolderId, folders)}" : ""), target.Id));
        var index = Math.Max(0, outputs.FindIndex(o => o.Id is { } id && id == output));

        var sends = mine.Where(r => r.Kind == RouteKind.Send)
            .Select(r => chains.FirstOrDefault(c => c.Id == r.To)).OfType<PluginChain>().ToList();
        var sidechains = mine.Where(r => r.Kind == RouteKind.Sidechain)
            .Select(r => (Target: chains.FirstOrDefault(c => c.Id == r.To), r.Plugin))
            .Where(s => s.Target != null).Select(s => (s.Target!, s.Plugin)).ToList();

        static string KindText(NoteRoute r, bool always) => r.Kind switch
        {
            RouteKind.Send => "（发送）",
            RouteKind.Sidechain => $"（侧链 · {r.Plugin}）",
            _ => always ? "（输出）" : "",
        };
        var incoming = routes.Where(r => r.To == chain.Id)
            .Select(r => (Source: chains.FirstOrDefault(c => c.Id == r.From), Route: r))
            .Where(x => x.Source != null)
            .ToList();
        var text = incoming.Count == 0 ? "" :
            $"接入 {incoming.Count} 路：" + string.Join("、", incoming.Take(6).Select(x => x.Source!.Name + KindText(x.Route, false))) + (incoming.Count > 6 ? " 等" : "");
        var tip = string.Join("\n", incoming.Select(x => x.Source!.Name + KindText(x.Route, true)));
        return new NoteRoutePanel(chain.Role, outputs, index, chain.Role != ChannelRole.Master || output != null, sends, sidechains, text, tip);
    }

    /// <summary>可以发送到的：同文件夹里还没发送过、不是主输出的笔记。</summary>
    public static IReadOnlyList<PluginChain> SendOptions(PluginChain chain, IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteRoute> routes)
    {
        var existing = routes.Where(r => r.From == chain.Id && r.Kind == RouteKind.Send).Select(r => r.To).ToHashSet();
        return Candidates(chain, chains).Where(c => !existing.Contains(c.Id) && c.Role != ChannelRole.Master).ToList();
    }

    public const string NoSendTargets = "同一个文件夹里没有可以发送到的笔记。先新建一条，比如“混响”，类型选效果轨";
    public const string NoSidechainTargets = "同一个文件夹里没有带插件的笔记可以接侧链";
    public const string SendPrompt = "选一条同文件夹里的笔记，通常是效果轨（混响、延迟）或并行处理用的总线。";
    public const string SidechainPrompt = "选接收侧链的那条通道（例如混响效果轨），下一步再选它上面的插件（例如 Pro-C 2）。";

    /// <summary>可以接侧链的：同文件夹里带插件的笔记。</summary>
    public static IReadOnlyList<PluginChain> SidechainOptions(PluginChain chain, IReadOnlyList<PluginChain> chains) =>
        Candidates(chain, chains).Where(c => c.Steps.Count > 0).ToList();

    /// <summary>接收侧链的笔记上可以选的插件（去掉重复）。</summary>
    public static IReadOnlyList<string> SidechainPlugins(PluginChain target) =>
        target.Steps.Select(s => s.PluginName).Where(n => n.Length > 0).Distinct().ToList();

    /// <summary>改输出和发送，侧链不动。changeOutput 为 false 时保留原来的输出。</summary>
    public static void SaveRoutes(PluginDatabase database, long chainId, IReadOnlyList<NoteRoute> routes, long? output = null, bool changeOutput = false, Func<IEnumerable<long>, IEnumerable<long>>? sendsChange = null)
    {
        var mine = routes.Where(r => r.From == chainId).ToList();
        var currentOutput = mine.FirstOrDefault(r => r.Kind == RouteKind.Output)?.To;
        var sends = mine.Where(r => r.Kind == RouteKind.Send).Select(r => r.To);
        if (sendsChange != null)
            sends = sendsChange(sends);
        database.SetRoutes(chainId, changeOutput ? output : currentOutput, sends.ToList());
    }

    public static void SaveSidechains(PluginDatabase database, long chainId, IReadOnlyList<NoteRoute> routes, Func<IEnumerable<(long Target, string Plugin)>, IEnumerable<(long Target, string Plugin)>> change)
    {
        var mine = routes.Where(r => r.From == chainId).ToList();
        var output = mine.FirstOrDefault(r => r.Kind == RouteKind.Output)?.To;
        var sends = mine.Where(r => r.Kind == RouteKind.Send).Select(r => r.To).ToList();
        var sidechains = change(mine.Where(r => r.Kind == RouteKind.Sidechain).Select(r => (r.To, r.Plugin))).ToList();
        database.SetRoutes(chainId, output, sends, sidechains);
    }

    /// <summary>路由图上每条笔记的插件，以及装没装。</summary>
    public static Func<PluginChain, IReadOnlyList<(string Name, bool Installed)>> MapPlugins(IEnumerable<PluginRow> rows)
    {
        var installed = rows.Where(p => !p.IsMissing).Select(p => p.Key).ToHashSet();
        return chain => chain.Steps
            .Select(s => (s.PluginName, s.PluginKey.Length > 0 && installed.Contains(s.PluginKey)))
            .ToList();
    }
}

/// <summary>从工程导入轨道生成混音笔记的步骤（两边共用）。工程文件只读。</summary>
public sealed class TrackImportPlan
{
    public const string BrowseText = "浏览其他工程文件…";
    public const string FilePickTitle = "从工程导入轨道";
    public const string FilePickPrompt = "选一个工程（只读取，不会改动工程文件）。下面是扫描过的工程，最近修改的在前。";
    public const string FolderPrompt = "导入的笔记放在哪里？和已有笔记同名时会自动加序号，不会覆盖。";
    public const string RoutingOption = "连同输出、发送和侧链接到的编组、效果轨和轨道一起导入，并按工程连好路由（文件夹里可以看路由图）";

    public static readonly string[] Extensions = [".cpr", ".npr", ".song", ".rpp", ".als"];

    private readonly Dictionary<ProjectTrack, IReadOnlyList<ChainStep>> _steps;

    public string Path { get; }
    public ProjectHost Host { get; }
    public IReadOnlyList<ProjectTrack> Tracks { get; }
    public IReadOnlyList<ProjectTrack> Candidates { get; }
    public bool HasRouting { get; }
    private readonly PluginNameMatcher _installed;

    /// <summary>
    /// 音源和宿主自带的插件不进笔记。列出还剩插件的轨道，以及接到编组、效果轨上的轨道（画路由要用）。
    /// </summary>
    public TrackImportPlan(string path, IReadOnlyList<ProjectTrack> tracks, IReadOnlyList<PluginRow> rows)
    {
        Path = path;
        Host = ProjectParser.HostOf(path) ?? throw new ArgumentException("不是支持的工程文件");
        Tracks = tracks;
        var matcher = new PluginNameMatcher(rows);
        _installed = new PluginNameMatcher(rows.Where(p => !p.IsMissing));
        _steps = new Dictionary<ProjectTrack, IReadOnlyList<ChainStep>>(ReferenceEqualityComparer.Instance);
        foreach (var t in tracks)
            _steps[t] = TrackImport.Steps(Host, t, matcher, skipStock: true);
        bool Routed(ProjectTrack t) => t.SendIds.Count > 0 || t.Sidechains.Count > 0
            || TrackRouting.Destinations(t, tracks).Any(d => d.Role != ChannelRole.Master)
            || t.Role is ChannelRole.Group or ChannelRole.Effect;
        Candidates = tracks.Where(t => _steps[t].Count > 0 || Routed(t)).ToList();
        HasRouting = tracks.Any(t => t.OutputId != null || t.SendIds.Count > 0 || t.Sidechains.Count > 0);
    }

    public string FileName => System.IO.Path.GetFileName(Path);
    public string ProjectName => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string Reading => $"正在读取“{FileName}”…";
    public string NothingFound => $"“{FileName}”里没有找到带第三方插件的轨道";

    public string Prompt =>
        $"{ProjectParser.HostName(Host)} 工程“{ProjectName}”里有 {Candidates.Count} 条通道用了插件或接了编组、效果轨。"
        + "每条勾选的通道会建一条混音笔记，插件按插槽顺序排好，之后可以给每个插件写应用思路。音源和宿主自带的插件不导入。";

    public IReadOnlyList<ChainStep> StepsOf(ProjectTrack track) => _steps[track];

    public string Line(ProjectTrack track)
    {
        var plugins = string.Join(" › ", _steps[track].Select(s => _installed.Find(s.PluginName) == null ? s.PluginName + "（未安装）" : s.PluginName));
        var role = track.Role == ChannelRole.Track ? "" : $"［{TrackRouting.RoleText(track.Role)}］";
        var routing = TrackRouting.Describe(track, Tracks);
        return $"{track.Name}{role}　{plugins}" + (routing.Length > 0 ? (plugins.Length > 0 ? "　" : "") + routing : "");
    }

    /// <summary>勾选的通道（可连同接到的编组、效果轨）变成要写入的笔记，路由用这次导入里的 Key 互相指向。</summary>
    public IReadOnlyList<ImportedNote> Notes(IEnumerable<int> picked, bool withDestinations)
    {
        var chosen = picked.Select(i => Candidates[i]).ToList();
        if (HasRouting && withDestinations)
            chosen = TrackRouting.WithDestinations(Tracks, chosen, master => _steps[master].Count > 0).ToList();
        var keys = chosen.Select((t, i) => (t, i)).ToDictionary<(ProjectTrack t, int i), ProjectTrack, string>(x => x.t, x => x.t.Id.Length > 0 ? x.t.Id : "#" + x.i, ReferenceEqualityComparer.Instance);
        var byId = chosen.Where(t => t.Id.Length > 0).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => keys[g.First()]);
        string? KeyOf(string? id) => id != null && byId.TryGetValue(id, out var key) ? key : null;
        return chosen.Select(t => new ImportedNote(
                keys[t], t.Name, TrackImport.Source(Path, t.Name), _steps[t], t.Role,
                KeyOf(t.OutputId), t.SendIds.Select(KeyOf).OfType<string>().ToList(),
                t.Sidechains.Select(s => (Key: KeyOf(s.TargetId), s.Plugin)).Where(s => s.Key != null).Select(s => (s.Key!, s.Plugin)).ToList()))
            .ToList();
    }

    public string Result(ImportedNotes result, IReadOnlyList<ImportedNote> notes, string folderPath)
    {
        var missing = result.Created.Sum(c => c.Steps.Count(s => _installed.Find(s.PluginName) == null));
        var routes = notes.Sum(n => (n.OutputKey != null ? 1 : 0) + (n.SendKeys?.Count ?? 0) + (n.Sidechains?.Count ?? 0));
        return $"已导入 {result.Created.Count} 条笔记到“{folderPath}”"
            + (result.Reused.Count > 0 ? $"，{result.Reused.Count} 条以前导入过，保留原内容、只更新路由" : "")
            + (routes > 0 ? $"，连好 {routes} 条路由" : "")
            + (missing > 0 ? $"，其中 {missing} 处插件未安装" : "");
    }

    /// <summary>扫描过的工程（还在、读取成功的），最近修改的在前，最多 40 个。</summary>
    public static IReadOnlyList<ProjectFileRecord> RecentFiles(PluginDatabase database) => database.LoadProjectFiles()
        .Where(p => p.Status == ProjectScanStatus.Ok && File.Exists(p.Path))
        .OrderByDescending(p => p.ModifiedUtc)
        .Take(40)
        .ToList();

    public static string FileLine(ProjectFileRecord file) =>
        $"{System.IO.Path.GetFileName(file.Path)}　{file.ModifiedUtc.ToLocalTime():yyyy-MM-dd}　{System.IO.Path.GetDirectoryName(file.Path)}";

    /// <summary>放到哪个文件夹：第一项是在当前位置新建以工程命名的文件夹，然后是最外层和已有文件夹。</summary>
    public static IReadOnlyList<(string Text, long? Id, bool Create)> FolderOptions(string projectName, long? parent, IReadOnlyList<NoteFolder> folders)
    {
        var options = new List<(string Text, long? Id, bool Create)>
        {
            ($"“{NoteListing.FolderPath(parent, folders)}”下的“{projectName}”文件夹", null, true),
            ("最外层", null, false),
        };
        options.AddRange(folders
            .Select(f => (NoteListing.FolderPath(f.Id, folders), (long?)f.Id, false))
            .OrderBy(o => o.Item1, StringComparer.CurrentCulture));
        return options;
    }

    /// <summary>选了新建时建好（同名已存在就直接用），返回文件夹 Id。</summary>
    public static long? ResolveFolder(PluginDatabase database, (string Text, long? Id, bool Create) choice, string projectName, long? parent, IReadOnlyList<NoteFolder> folders) =>
        choice.Create
            ? database.EnsureFolderPath([.. (parent is { } p ? MixNotes.PathOf(p, folders) : []), projectName])
            : choice.Id;
}
