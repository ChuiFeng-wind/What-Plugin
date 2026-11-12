using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>混音笔记的路由：笔记详情里的类型、输出到、发送，以及文件夹的路由图。只是记录，不会改 DAW 工程。</summary>
public partial class MainWindow
{
    private IReadOnlyList<NoteRoute> _routes = [];
    private bool _loadingRoute;
    private bool _folderShowsMap;
    private bool _folderMapHooked;

    private static readonly (ChannelRole Role, string Text)[] RoleOptions =
    [
        (ChannelRole.Track, "轨道"),
        (ChannelRole.Group, "编组 / 总线"),
        (ChannelRole.Effect, "效果轨"),
        (ChannelRole.Master, "主输出"),
    ];

    public static string RoleGlyph(ChannelRole role) => role switch
    {
        ChannelRole.Group => "",
        ChannelRole.Effect => "",
        ChannelRole.Master => "",
        _ => "",
    };

    private void ReloadRoutes() => _routes = _database.LoadRoutes();

    /// <summary>同一个文件夹里的其他笔记，编组、效果轨、主输出排在前面。</summary>
    private List<PluginChain> RouteCandidates(PluginChain chain) => _chains
        .Where(c => c.Id != chain.Id && c.FolderId == chain.FolderId)
        .OrderBy(c => c.Role switch { ChannelRole.Group => 0, ChannelRole.Effect => 1, ChannelRole.Master => 2, _ => 3 })
        .ThenBy(c => c.Name, StringComparer.CurrentCulture)
        .ToList();

    private string NoteLabel(PluginChain chain) =>
        chain.Role == ChannelRole.Track ? chain.Name : $"{chain.Name}（{TrackRouting.RoleText(chain.Role)}）";

    private void ShowRoutePanel(PluginChain chain)
    {
        _loadingRoute = true;
        if (RouteRoleBox.Items.Count == 0)
        {
            foreach (var (role, text) in RoleOptions)
                RouteRoleBox.Items.Add(new ComboBoxItem { Content = text, Tag = role });
        }
        RouteRoleBox.SelectedIndex = Array.FindIndex(RoleOptions, o => o.Role == chain.Role);

        var mine = _routes.Where(r => r.From == chain.Id).ToList();
        var output = mine.FirstOrDefault(r => r.Kind == RouteKind.Output)?.To;
        RouteOutputBox.Items.Clear();
        RouteOutputBox.Items.Add(new ComboBoxItem { Content = "主输出", Tag = null });
        var candidates = RouteCandidates(chain);
        if (output is { } current && candidates.All(c => c.Id != current) && _chains.FirstOrDefault(c => c.Id == current) is { } elsewhere)
            candidates.Insert(0, elsewhere);
        foreach (var target in candidates.Where(c => c.Role != ChannelRole.Effect || c.Id == output))
            RouteOutputBox.Items.Add(new ComboBoxItem { Content = NoteLabel(target) + (target.FolderId != chain.FolderId ? $" · {FolderPathText(target.FolderId)}" : ""), Tag = target.Id });
        RouteOutputBox.SelectedIndex = Math.Max(0, RouteOutputBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag is long id && id == output));
        RouteOutputBox.IsEnabled = chain.Role != ChannelRole.Master || output != null;

        RouteSends.Children.Clear();
        var label = new TextBlock { Text = "发送", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        label.SetResourceReference(StyleProperty, "SmallNote");
        RouteSends.Children.Add(label);
        foreach (var send in mine.Where(r => r.Kind == RouteKind.Send))
        {
            if (_chains.FirstOrDefault(c => c.Id == send.To) is { } target)
                RouteSends.Children.Add(SendChip(chain, target));
        }
        var add = new Button { Content = "+ 添加发送", Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 0, 10, 0), Height = 28 };
        add.SetResourceReference(StyleProperty, "GhostButton");
        add.Click += (_, _) => AddSend(chain);
        RouteSends.Children.Add(add);

        RouteSidechains.Children.Clear();
        var sideLabel = new TextBlock { Text = "侧链", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = "把这条通道的信号送进别的通道上某个插件的侧链输入，例如人声压混响上的压缩" };
        sideLabel.SetResourceReference(StyleProperty, "SmallNote");
        RouteSidechains.Children.Add(sideLabel);
        foreach (var side in mine.Where(r => r.Kind == RouteKind.Sidechain))
        {
            if (_chains.FirstOrDefault(c => c.Id == side.To) is { } target)
                RouteSidechains.Children.Add(SidechainChip(chain, target, side.Plugin));
        }
        var addSide = new Button { Content = "+ 添加侧链", Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 0, 10, 0), Height = 28 };
        addSide.SetResourceReference(StyleProperty, "GhostButton");
        addSide.Click += (_, _) => AddSidechain(chain);
        RouteSidechains.Children.Add(addSide);

        static string KindText(NoteRoute r, bool always) => r.Kind switch
        {
            RouteKind.Send => "（发送）",
            RouteKind.Sidechain => $"（侧链 · {r.Plugin}）",
            _ => always ? "（输出）" : "",
        };
        var incoming = _routes.Where(r => r.To == chain.Id)
            .Select(r => (Source: _chains.FirstOrDefault(c => c.Id == r.From), Route: r))
            .Where(x => x.Source != null)
            .ToList();
        RouteIncoming.Text = incoming.Count == 0 ? "" :
            $"接入 {incoming.Count} 路：" + string.Join("、", incoming.Take(6).Select(x => x.Source!.Name + KindText(x.Route, false))) + (incoming.Count > 6 ? " 等" : "");
        RouteIncoming.ToolTip = incoming.Count == 0 ? null : string.Join("\n", incoming.Select(x => x.Source!.Name + KindText(x.Route, true)));
        _loadingRoute = false;
    }

    private Border SendChip(PluginChain chain, PluginChain target)
    {
        var open = new Button { Content = NoteLabel(target), Padding = new Thickness(8, 0, 4, 0), Height = 26, ToolTip = "打开这条笔记" };
        open.SetResourceReference(StyleProperty, "GhostButton");
        open.Click += (_, _) => LoadChains(selectChain: target.Id);
        var remove = new Button { Content = "", FontSize = 9, Width = 22, Height = 22, ToolTip = "删除这个发送" };
        remove.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(remove, $"删除发送到 {target.Name}");
        remove.Click += (_, _) => SaveRoutes(chain, sendsChange: sends => sends.Where(s => s != target.Id));
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(open);
        panel.Children.Add(remove);
        var chip = new Border { CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 2, 6, 2), Child = panel, BorderThickness = new Thickness(1) };
        chip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        chip.SetResourceReference(Border.BorderBrushProperty, "Line");
        return chip;
    }

    private Border SidechainChip(PluginChain chain, PluginChain target, string plugin)
    {
        var open = new Button { Content = $"{target.Name} · {plugin}", Padding = new Thickness(8, 0, 4, 0), Height = 26, ToolTip = $"送进“{target.Name}”上 {plugin} 的侧链。点击打开这条笔记" };
        open.SetResourceReference(StyleProperty, "GhostButton");
        open.Click += (_, _) => LoadChains(selectChain: target.Id);
        var remove = new Button { Content = "\uE711", FontSize = 9, Width = 22, Height = 22, ToolTip = "删除这个侧链" };
        remove.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(remove, $"删除侧链到 {target.Name} {plugin}");
        remove.Click += (_, _) => SaveSidechains(chain, list => list.Where(s => !(s.Target == target.Id && s.Plugin == plugin)));
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(open);
        panel.Children.Add(remove);
        var chip = new Border { CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 2, 6, 2), Child = panel, BorderThickness = new Thickness(1) };
        chip.SetResourceReference(Border.BackgroundProperty, "GoodSoft");
        chip.SetResourceReference(Border.BorderBrushProperty, "Line");
        return chip;
    }

    /// <summary>选一条同文件夹里的笔记，再选它上面接收侧链的插件。</summary>
    private void AddSidechain(PluginChain chain)
    {
        var options = RouteCandidates(chain).Where(c => c.Steps.Count > 0).ToList();
        if (options.Count == 0)
        {
            StatusText.Text = "同一个文件夹里没有带插件的笔记可以接侧链";
            return;
        }
        var index = Dialogs.PickOne(this, $"“{chain.Name}”的侧链送到", "选接收侧链的那条通道（例如混响效果轨），下一步再选它上面的插件（例如 Pro-C 2）。",
            options.Select(NoteLabel).ToList(), "下一步");
        if (index == null)
            return;
        var target = options[index.Value];
        var plugins = target.Steps.Select(s => s.PluginName).Where(n => n.Length > 0).Distinct().ToList();
        var pick = plugins.Count == 1 ? 0 : Dialogs.PickOne(this, $"“{target.Name}”上哪个插件接收侧链", "选接收侧链信号的插件。", plugins, "添加");
        if (pick == null)
            return;
        var plugin = plugins[pick.Value];
        SaveSidechains(chain, list => list.Append((target.Id, plugin)));
    }

    private void SaveSidechains(PluginChain chain, Func<IEnumerable<(long Target, string Plugin)>, IEnumerable<(long Target, string Plugin)>> change)
    {
        var mine = _routes.Where(r => r.From == chain.Id).ToList();
        var output = mine.FirstOrDefault(r => r.Kind == RouteKind.Output)?.To;
        var sends = mine.Where(r => r.Kind == RouteKind.Send).Select(r => r.To).ToList();
        var sidechains = change(mine.Where(r => r.Kind == RouteKind.Sidechain).Select(r => (r.To, r.Plugin))).ToList();
        _database.SetRoutes(chain.Id, output, sends, sidechains);
        ReloadRoutes();
        if (_shownChain?.Id == chain.Id)
            ShowRoutePanel(chain);
        StatusText.Text = "路由已保存";
    }

    private void AddSend(PluginChain chain)
    {
        var existing = _routes.Where(r => r.From == chain.Id && r.Kind == RouteKind.Send).Select(r => r.To).ToHashSet();
        var options = RouteCandidates(chain).Where(c => !existing.Contains(c.Id) && c.Role != ChannelRole.Master).ToList();
        if (options.Count == 0)
        {
            StatusText.Text = "同一个文件夹里没有可以发送到的笔记。先新建一条，比如“混响”，类型选效果轨";
            return;
        }
        var index = Dialogs.PickOne(this, $"“{chain.Name}”发送到", "选一条同文件夹里的笔记，通常是效果轨（混响、延迟）或并行处理用的总线。",
            options.Select(NoteLabel).ToList(), "添加");
        if (index == null)
            return;
        var target = options[index.Value].Id;
        SaveRoutes(chain, sendsChange: sends => sends.Append(target));
    }

    private void RouteRole_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRoute || _shownChain is not { } chain || RouteRoleBox.SelectedItem is not ComboBoxItem { Tag: ChannelRole role } || role == chain.Role)
            return;
        _database.SetChainRole(chain.Id, role);
        RememberExpansion();
        LoadChains(selectChain: chain.Id);
        StatusText.Text = $"“{chain.Name}”标为{TrackRouting.RoleText(role)}";
    }

    private void RouteOutput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRoute || _shownChain is not { } chain || RouteOutputBox.SelectedItem is not ComboBoxItem item)
            return;
        var output = item.Tag as long?;
        SaveRoutes(chain, output: output, changeOutput: true);
    }

    /// <summary>改输出或发送后保存并刷新路由区（不重建左边的树）。</summary>
    private void SaveRoutes(PluginChain chain, long? output = null, bool changeOutput = false, Func<IEnumerable<long>, IEnumerable<long>>? sendsChange = null)
    {
        var mine = _routes.Where(r => r.From == chain.Id).ToList();
        var currentOutput = mine.FirstOrDefault(r => r.Kind == RouteKind.Output)?.To;
        var sends = mine.Where(r => r.Kind == RouteKind.Send).Select(r => r.To);
        if (sendsChange != null)
            sends = sendsChange(sends);
        _database.SetRoutes(chain.Id, changeOutput ? output : currentOutput, sends.ToList());
        ReloadRoutes();
        if (_shownChain?.Id == chain.Id)
            ShowRoutePanel(chain);
        StatusText.Text = "路由已保存";
    }

    // ---------- 文件夹的路由图 ----------

    private void FolderView_Changed(object sender, RoutedEventArgs e)
    {
        if (!_projectUiReady || FolderMapToggle == null)
            return;
        _folderShowsMap = FolderMapToggle.IsChecked == true;
        if (_shownFolder != null)
            ShowFolderDetail(_shownFolder);
    }

    private void ShowFolderMap(NoteFolder folder, IReadOnlyList<PluginChain> notes)
    {
        FolderNotes.Visibility = _folderShowsMap ? Visibility.Collapsed : Visibility.Visible;
        FolderMap.Visibility = _folderShowsMap ? Visibility.Visible : Visibility.Collapsed;
        FolderListHint.Visibility = _folderShowsMap ? Visibility.Collapsed : Visibility.Visible;
        if (!_folderShowsMap)
            return;
        if (!_folderMapHooked)
        {
            FolderMap.OpenNote += FolderMap_Open;
            _folderMapHooked = true;
        }
        var rows = _database.LoadPlugins();
        var installed = rows.Where(p => !p.IsMissing).Select(p => p.Key).ToHashSet();
        FolderMap.Show(notes, _routes, chain => chain.Steps
            .Select(s => (s.PluginName, Installed: s.PluginKey.Length > 0 && installed.Contains(s.PluginKey)))
            .ToList());
    }

    private void FolderMap_Open(long chainId) => LoadChains(selectChain: chainId);
}
