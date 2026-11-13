using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>混音笔记（以前叫插件链）：文件夹分层、标签、每一步的首选和备选插件。</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _chainSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private List<PluginChain> _chains = [];
    private List<NoteFolder> _noteFolders = [];
    private List<NoteNode> _noteRoots = [];
    private readonly HashSet<long> _collapsedNoteFolders = [];
    private PluginChain? _shownChain;
    private NoteFolder? _shownFolder;
    private ObservableCollection<StepViewModel> _chainSteps = [];
    private List<ChainStep> _hiddenChainSteps = [];
    private bool _loadingChain;
    private bool _buildingNoteTree;

    private void InitializeInsights()
    {
        _chainSaveTimer.Tick += (_, _) => FlushChain();
    }

    // ---------- 左边的文件夹树 ----------

    /// <summary>重新读取笔记和文件夹。可以指定要选中的笔记或文件夹，否则保持原来的选择。</summary>
    private void LoadChains(long? selectChain = null, long? selectFolder = null)
    {
        FlushChain();
        if (selectChain == null && selectFolder == null)
        {
            selectChain = _shownChain?.Id;
            selectFolder = _shownChain == null ? _shownFolder?.Id : null;
        }
        _shownChain = null;
        _shownFolder = null;
        _chains = _database.LoadChains();
        _noteFolders = _database.LoadNoteFolders();
        ReloadRoutes();
        CountChains.Text = _chains.Count > 0 ? _chains.Count.ToString() : "";
        BuildNoteTree(selectChain, selectFolder);
    }

    private void BuildNoteTree(long? selectChain, long? selectFolder)
    {
        var search = NoteTreeSearch.Text.Trim().ToLowerInvariant();
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var plugins = _plugins.ToDictionary(p => p.Row.Key, p => p.Row.Name);
        bool Matches(PluginChain chain)
        {
            if (words.Length == 0)
                return true;
            var text = string.Join(' ', [chain.Name, chain.Note, .. chain.TagList,
                .. chain.Steps.SelectMany(s => s.AllPicks).Select(p => plugins.GetValueOrDefault(p.PluginKey) ?? p.PluginName)]).ToLowerInvariant();
            return words.All(text.Contains);
        }

        NoteNode? selected = null;
        List<NoteNode> Children(long? parent)
        {
            var nodes = new List<NoteNode>();
            foreach (var folder in _noteFolders.Where(f => f.ParentId == parent))
            {
                var children = Children(folder.Id);
                var count = MixNotes.Subtree(folder.Id, _noteFolders) is var ids ? _chains.Count(c => c.FolderId is { } f && ids.Contains(f) && Matches(c)) : 0;
                if (words.Length > 0 && count == 0)
                    continue;
                var node = NoteNode.ForFolder(folder, children, count);
                node.IsExpanded = words.Length > 0 || !_collapsedNoteFolders.Contains(folder.Id);
                if (folder.Id == selectFolder)
                    selected = node;
                nodes.Add(node);
            }
            foreach (var chain in _chains.Where(c => c.FolderId == parent && Matches(c)))
            {
                var node = NoteNode.ForChain(chain);
                if (chain.Id == selectChain)
                    selected = node;
                nodes.Add(node);
            }
            return nodes;
        }

        _noteRoots = Children(null);
        ApplyMultiMode();
        _buildingNoteTree = true;
        NoteTree.ItemsSource = _noteRoots;
        _buildingNoteTree = false;
        if (selected != null)
        {
            // 展开到选中的那一项，否则 TreeView 不会生成它，选中也看不见。
            foreach (var ancestor in Ancestors(selected))
                ancestor.IsExpanded = true;
            selected.IsSelected = true;
        }
        ShowNoteSelection();
    }

    private IEnumerable<NoteNode> Ancestors(NoteNode target)
    {
        List<NoteNode>? Path(IEnumerable<NoteNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, target))
                    return [];
                if (Path(node.Children) is { } below)
                {
                    below.Insert(0, node);
                    return below;
                }
            }
            return null;
        }
        return Path(_noteRoots) ?? [];
    }

    private NoteNode? SelectedNode => NoteTree.SelectedItem as NoteNode;

    private void NoteTree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_buildingNoteTree)
            ShowNoteSelection();
    }

    private void NoteTreeSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_projectUiReady)
            return;
        FlushChain();
        BuildNoteTree(_shownChain?.Id, _shownChain == null ? _shownFolder?.Id : null);
    }

    /// <summary>折叠状态在重建树时保留。</summary>
    private void RememberExpansion()
    {
        void Walk(IEnumerable<NoteNode> nodes)
        {
            foreach (var node in nodes.Where(n => n.IsFolder))
            {
                if (node.IsExpanded)
                    _collapsedNoteFolders.Remove(node.Id);
                else
                    _collapsedNoteFolders.Add(node.Id);
                Walk(node.Children);
            }
        }
        if (NoteTreeSearch.Text.Trim().Length == 0)
            Walk(_noteRoots);
    }

    /// <summary>新建的笔记和文件夹放在哪里：选中的文件夹，或选中笔记所在的文件夹，都没有就放最外层。</summary>
    private long? TargetFolder => SelectedNode switch
    {
        { IsFolder: true } node => node.Id,
        { IsFolder: false } node => _chains.FirstOrDefault(c => c.Id == node.Id)?.FolderId,
        _ => null,
    };

    private string FolderPathText(long? folderId)
    {
        var path = MixNotes.PathOf(folderId, _noteFolders);
        return path.Count == 0 ? "最外层" : string.Join(" › ", path);
    }

    // ---------- 中间：文件夹或笔记 ----------

    private void ShowChainDetail() => ShowNoteSelection();

    private void ShowNoteSelection()
    {
        if (!_projectUiReady)
            return;
        // 切换前先保存，等待中的内容属于正在显示的那条。
        FlushChain();
        var node = SelectedNode;
        _shownChain = node is { IsFolder: false } ? _chains.FirstOrDefault(c => c.Id == node.Id) : null;
        _shownFolder = node is { IsFolder: true } ? _noteFolders.FirstOrDefault(f => f.Id == node.Id) : null;
        ChainDetail.Visibility = _shownChain != null ? Visibility.Visible : Visibility.Collapsed;
        FolderDetail.Visibility = _shownFolder != null ? Visibility.Visible : Visibility.Collapsed;
        ChainEmpty.Visibility = _shownChain == null && _shownFolder == null ? Visibility.Visible : Visibility.Collapsed;
        ChainEmptyText.Text = _chains.Count == 0 && _noteFolders.Count == 0
            ? "在左下角新建第一条笔记，比如“人声总线”；也可以先建文件夹，比如混音师的名字"
            : NoteTreeSearch.Text.Trim().Length > 0 && _noteRoots.Count == 0 ? "没有符合搜索的笔记" : "在左边选一条笔记或一个文件夹";
        if (_shownFolder != null)
            ShowFolderDetail(_shownFolder);
        if (_shownChain != null)
            ShowChainContent(_shownChain);
        ShowChainCandidates();
        ShowBatchPanel();
    }

    private void ShowFolderDetail(NoteFolder folder)
    {
        var ids = MixNotes.Subtree(folder.Id, _noteFolders);
        var notes = _chains.Where(c => c.FolderId is { } f && ids.Contains(f)).ToList();
        var subfolders = ids.Count - 1;
        FolderTitle.Text = folder.Name;
        FolderMeta.Text = $"{FolderPathText(folder.Id)}  ·  {notes.Count} 条笔记" + (subfolders > 0 ? $"，{subfolders} 个子文件夹" : "");
        FolderNotes.ItemsSource = notes
            .OrderBy(c => FolderPathText(c.FolderId), StringComparer.CurrentCulture)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new NoteSummary(c, c.FolderId == folder.Id ? "" : FolderPathText(c.FolderId)))
            .ToList();
        ShowFolderMap(folder, notes);
    }

    private void FolderNotes_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderNotes.SelectedItem is NoteSummary summary)
            LoadChains(selectChain: summary.Chain.Id);
    }

    private void ShowChainContent(PluginChain chain)
    {
        var rows = _database.LoadPlugins();
        var wishes = WishStatuses();
        _hiddenChainSteps = [];
        _chainSteps = [];
        foreach (var step in chain.Steps)
        {
            var primary = MakePick(step.Primary, rows, wishes);
            // 改判成音源的插件不在这个软件里显示，但不能因此丢掉这一步。
            if (primary.State == ChainStepState.Instrument)
            {
                _hiddenChainSteps.Add(step);
                continue;
            }
            _chainSteps.Add(new StepViewModel(primary, step.Backups.Select(a => MakePick(a, rows, wishes)), ScheduleChainSave));
        }
        Renumber();

        _loadingChain = true;
        ChainTitle.Text = chain.Name;
        ChainNote.Text = chain.Note;
        _loadingChain = false;
        ChainItems.ItemsSource = _chainSteps;
        RebuildChainTags();
        UpdateChainMeta();
        ShowRoutePanel(chain);
    }

    private PickViewModel MakePick(ChainPick pick, IEnumerable<PluginRow> rows, IReadOnlyDictionary<string, WishStatus> wishes)
    {
        var (state, plugin) = ChainReport.Resolve(pick, rows);
        return new PickViewModel(pick, state, plugin, plugin == null ? null : _report.Usage.GetValueOrDefault(plugin.Key), ScheduleChainSave)
        {
            Wish = wishes.TryGetValue(Normalizer.Name(pick.PluginName), out var status) ? status : null,
            Remembered = state != ChainStepState.Missing ? null
                : plugin is { } row && TrashMemory.IsRemembered(row) ? row
                : TrashMemory.Find(pick.PluginName, RememberedMatcher),
        };
    }

    /// <summary>心愿单里每个名字的状态；同名的既有想买又有已购时按“想买”算。</summary>
    private Dictionary<string, WishStatus> WishStatuses() => _database.LoadWishes()
        .GroupBy(w => Normalizer.Name(w.Name))
        .Where(g => g.Key.Length > 0)
        .ToDictionary(g => g.Key, g => g.Min(w => w.Status));

    private IEnumerable<PickViewModel> AllPicks => _chainSteps.SelectMany(s => s.Alternates.Prepend(s.Primary));

    private void WishPick_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is PickViewModel pick)
            AddPicksToWishlist([pick]);
    }

    private void WishMissing_Click(object sender, RoutedEventArgs e) => AddPicksToWishlist(AllPicks.Where(p => p.CanWish).ToList());

    /// <summary>把笔记里没安装的插件加入心愿单，心愿单里已有同名的跳过。</summary>
    private void AddPicksToWishlist(IReadOnlyList<PickViewModel> picks)
    {
        if (_shownChain == null || picks.Count == 0)
            return;
        FlushItems();
        var existing = WishStatuses();
        var pending = picks.Where(p => p.CanWish).Select(p => p.Name).Where(n => !existing.ContainsKey(Normalizer.Name(n))).ToList();
        if (!ConfirmRememberedWishes(pending))
            return;
        var added = new List<string>();
        try
        {
            foreach (var pick in picks.Where(p => p.CanWish))
            {
                var key = Normalizer.Name(pick.Name);
                if (key.Length == 0 || existing.ContainsKey(key))
                    continue;
                _database.AddWish(new WishItem(0, pick.Name, GuessVendor(pick.Name), Reason: $"混音笔记“{_shownChain.Name}”里用到"));
                existing[key] = WishStatus.Wished;
                added.Add(pick.Name);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }

        if (added.Count > 0)
            LoadItems();
        foreach (var pick in AllPicks)
            pick.Wish = existing.TryGetValue(Normalizer.Name(pick.Name), out var status) ? status : null;
        UpdateChainMeta();
        StatusText.Text = added.Count switch
        {
            0 => "这些插件已经在心愿单里了",
            1 => $"已把 {added[0]} 加入心愿单",
            _ => $"已把 {added.Count} 款未安装的插件加入心愿单：{string.Join("、", added)}",
        };
    }

    private PickViewModel MakePick(PluginViewModel plugin) =>
        new(new ChainPick(plugin.Row.Key, plugin.Name), ChainStepState.Installed, plugin.Row, plugin.Usage, ScheduleChainSave);

    private void Renumber()
    {
        for (var i = 0; i < _chainSteps.Count; i++)
            _chainSteps[i].Number = i + 1;
    }

    private void UpdateChainMeta()
    {
        if (_shownChain == null)
            return;
        var picks = _chainSteps.SelectMany(s => s.Alternates.Prepend(s.Primary)).ToList();
        var missing = _chainSteps.Count(s => s.Primary.State == ChainStepState.Missing);
        var hidden = _chainSteps.Count(s => s.Primary.State == ChainStepState.Blacklisted);
        var alternates = _chainSteps.Sum(s => s.Alternates.Count);
        var parts = new List<string> { FolderPathText(_shownChain.FolderId) };
        parts.Add(_chainSteps.Count == 0 ? "还没有插件" : $"{_chainSteps.Count} 步，按从上到下的顺序");
        if (alternates > 0)
            parts.Add($"{alternates} 个备选");
        if (missing > 0)
            parts.Add($"首选里 {missing} 款没安装");
        if (hidden > 0)
            parts.Add($"{hidden} 款在垃圾桶");
        if (_shownChain.Source.Length > 0)
            parts.Add("导入自 " + _shownChain.Source);
        ChainMeta.Text = string.Join("  ·  ", parts);
        ChainItemsEmpty.Visibility = _chainSteps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var wishable = AllPicks.Where(p => p.CanWish).Select(p => Normalizer.Name(p.Name)).Distinct().Count();
        ChainWishMissingButton.Visibility = wishable > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChainWishMissingText.Text = wishable > 1 ? $"未安装的 {wishable} 款加入心愿单" : "未安装的加入心愿单";
    }

    // ---------- 标签 ----------

    private void RebuildChainTags()
    {
        if (_shownChain == null)
            return;
        var active = _shownChain.TagList;
        var chipStyle = (Style)FindResource("TagChip");
        ChainTagChips.Children.Clear();
        var all = MixNotes.PresetTags.Concat(active).Concat(_chains.SelectMany(c => c.TagList))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var tag in all)
        {
            var chip = new ToggleButton
            {
                Content = tag,
                Tag = tag,
                Style = chipStyle,
                IsChecked = active.Contains(tag, StringComparer.OrdinalIgnoreCase),
            };
            AutomationProperties.SetAutomationId(chip, "NoteTag_" + tag);
            chip.Checked += ChainTag_Changed;
            chip.Unchecked += ChainTag_Changed;
            ChainTagChips.Children.Add(chip);
        }
    }

    private void ChainTag_Changed(object sender, RoutedEventArgs e)
    {
        if (_shownChain == null)
            return;
        var chip = (ToggleButton)sender;
        var tag = (string)chip.Tag;
        var tags = _shownChain.TagList.Where(t => !t.Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (chip.IsChecked == true)
            tags.Add(tag);
        SetShownChainTags(tags);
    }

    private void AddChainTag_Click(object sender, RoutedEventArgs e) => AddChainTag();

    private void NewChainTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        AddChainTag();
        e.Handled = true;
    }

    private void AddChainTag()
    {
        if (_shownChain == null)
            return;
        var added = SplitTags(NewChainTagBox.Text);
        if (added.Count == 0)
            return;
        NewChainTagBox.Text = "";
        SetShownChainTags(_shownChain.TagList.Concat(added).ToList());
        RebuildChainTags();
    }

    private void SetShownChainTags(List<string> tags)
    {
        if (_shownChain == null)
            return;
        try
        {
            _database.SetChainTags(_shownChain.Id, tags);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }
        ReplaceChain(_shownChain with { Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        StatusText.Text = $"已保存 {_shownChain.Name} 的标签";
    }

    /// <summary>更新内存里的这条笔记，不重建树，避免正在编辑的内容失去焦点。</summary>
    private void ReplaceChain(PluginChain chain)
    {
        var at = _chains.FindIndex(c => c.Id == chain.Id);
        if (at >= 0)
            _chains[at] = chain;
        if (_shownChain?.Id == chain.Id)
            _shownChain = chain;
        FindNode(_noteRoots, false, chain.Id)?.Update(chain);
    }

    private static NoteNode? FindNode(IEnumerable<NoteNode> nodes, bool folder, long id)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder == folder && node.Id == id)
                return node;
            if (FindNode(node.Children, folder, id) is { } found)
                return found;
        }
        return null;
    }

    // ---------- 右边：可添加的插件 ----------

    /// <summary>没有搜索时按工程使用次数给建议。同一款插件可以出现多次，所以不排除已加的。</summary>
    private void ShowChainCandidates()
    {
        if (!_projectUiReady)
            return;
        ChainCandidateHint.Text = "点 + 加成新的一步；先在中间点选某一步，再点 ⇄ 可以加为它的备选";
        var search = ChainSearchBox.Text.Trim().ToLowerInvariant();
        if (search.Length == 0)
        {
            ChainCandidateLabel.Text = "按工程使用次数建议";
            var suggested = ProjectReport.SuggestForList(_plugins.Select(p => p.Row), _report.Usage, new HashSet<string>());
            ChainCandidates.ItemsSource = suggested.Select(s => new FavoritePluginViewModel(s.Plugin, s.Usage)).ToList();
            ChainCandidatesEmpty.Text = "扫描工程后，这里会列出你用得最多的插件。也可以直接搜索";
            ChainCandidatesEmpty.Visibility = suggested.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        ChainCandidateLabel.Text = "搜索结果";
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _plugins
            .Where(p => !p.IsBlacklisted && !p.Row.IsMissing && words.All(p.SearchText.Contains))
            .OrderByDescending(p => p.Usage?.Projects ?? 0)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(p => new FavoritePluginViewModel(p.Row, p.Usage))
            .ToList();
        ChainCandidates.ItemsSource = matches;
        ChainCandidatesEmpty.Text = "没有匹配的插件";
        ChainCandidatesEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChainSearch_Changed(object sender, TextChangedEventArgs e) => ShowChainCandidates();

    // ---------- 保存 ----------

    private void ChainNote_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loadingChain)
            ScheduleChainSave();
    }

    private void ScheduleChainSave()
    {
        _chainSaveTimer.Stop();
        _chainSaveTimer.Start();
    }

    /// <summary>有等待中的修改才保存。</summary>
    private void FlushChain()
    {
        var pending = _chainSaveTimer.IsEnabled;
        _chainSaveTimer.Stop();
        if (pending)
            SaveShownChain();
    }

    private bool SaveShownChain()
    {
        if (_shownChain == null)
            return false;
        var steps = _chainSteps.Select(s => s.ToStep()).Concat(_hiddenChainSteps).ToList();
        var note = ChainNote.Text;
        try
        {
            _database.SaveChain(_shownChain.Id, note, steps);
            RecordUsage(() => _database.RecordAction());
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return false;
        }
        ReplaceChain(_shownChain with { Note = note, Steps = steps });
        StatusText.Text = $"已保存 {_shownChain.Name}";
        return true;
    }

    private void CommitChainSteps()
    {
        _chainSaveTimer.Stop();
        Renumber();
        SaveShownChain();
        UpdateChainMeta();
    }

    // ---------- 新建、重命名、移动、删除 ----------

    private void NewChainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        AddChain();
        e.Handled = true;
    }

    private void AddChain_Click(object sender, RoutedEventArgs e) => AddChain();

    private void AddChain()
    {
        var folder = TargetFolder;
        try
        {
            RememberExpansion();
            var chain = _database.AddChain(NewChainBox.Text, folder);
            NewChainBox.Text = "";
            if (folder is { } id)
                _collapsedNoteFolders.Remove(id);
            NoteTreeSearch.Text = "";
            LoadChains(selectChain: chain.Id);
            StatusText.Text = $"已在“{FolderPathText(folder)}”新建笔记“{chain.Name}”";
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var parent = TargetFolder;
        try
        {
            RememberExpansion();
            var folder = _database.AddNoteFolder(parent, NewChainBox.Text);
            NewChainBox.Text = "";
            if (parent is { } id)
                _collapsedNoteFolders.Remove(id);
            NoteTreeSearch.Text = "";
            LoadChains(selectFolder: folder.Id);
            StatusText.Text = $"已在“{FolderPathText(parent)}”新建文件夹“{folder.Name}”";
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private void RenameChain_Click(object sender, RoutedEventArgs e)
    {
        if (_shownChain is not { } chain)
            return;
        var name = Dialogs.Prompt(this, "重命名笔记", "新的名称：", "保存", chain.Name);
        if (name != null)
            RenameNote(chain.Id, name);
    }

    /// <summary>重命名笔记，保持展开和选中。名字为空或重复时在状态栏提示。</summary>
    private void RenameNote(long chainId, string name)
    {
        try
        {
            FlushChain();
            RememberExpansion();
            _database.RenameChain(chainId, name);
            LoadChains(selectChain: chainId);
            StatusText.Text = $"已改名为“{name.Trim()}”";
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_shownFolder is not { } folder)
            return;
        var name = Dialogs.Prompt(this, "重命名文件夹", "新的名称：", "保存", folder.Name);
        if (name != null)
            RenameFolder(folder.Id, name);
    }

    private void RenameFolder(long folderId, string name)
    {
        try
        {
            FlushChain();
            RememberExpansion();
            _database.RenameNoteFolder(folderId, name);
            LoadChains(selectFolder: folderId);
            StatusText.Text = $"文件夹已改名为“{name.Trim()}”";
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    /// <summary>选择目标文件夹。exclude 里的文件夹（自己和子文件夹）不能选。返回 false 表示取消。</summary>
    private bool PickFolder(string title, long? current, IReadOnlySet<long> exclude, out long? target)
    {
        target = null;
        var options = new List<(string Text, long? Id)> { ("最外层", null) };
        options.AddRange(_noteFolders
            .Where(f => !exclude.Contains(f.Id))
            .Select(f => (FolderPathText(f.Id), (long?)f.Id))
            .OrderBy(o => o.Item1, StringComparer.CurrentCulture));
        var index = Dialogs.PickOne(this, title, "移到哪个文件夹？也可以直接把左边的条目拖到文件夹上。",
            options.Select(o => o.Text).ToList(), "移动", Math.Max(0, options.FindIndex(o => o.Id == current)));
        if (index == null)
            return false;
        target = options[index.Value].Id;
        return true;
    }

    private void MoveChain_Click(object sender, RoutedEventArgs e)
    {
        if (_shownChain is not { } chain || !PickFolder($"移动“{chain.Name}”", chain.FolderId, new HashSet<long>(), out var target))
            return;
        MoveNote(chain.Id, target);
    }

    private void MoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_shownFolder is not { } folder || !PickFolder($"移动文件夹“{folder.Name}”", folder.ParentId, MixNotes.Subtree(folder.Id, _noteFolders), out var target))
            return;
        MoveFolder(folder.Id, target);
    }

    private void MoveNote(long chainId, long? folder)
    {
        try
        {
            FlushChain();
            RememberExpansion();
            _database.MoveChain(chainId, folder);
            if (folder is { } id)
                _collapsedNoteFolders.Remove(id);
            LoadChains(selectChain: chainId);
            StatusText.Text = $"已移到“{FolderPathText(folder)}”";
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private void MoveFolder(long folderId, long? parent)
    {
        try
        {
            FlushChain();
            RememberExpansion();
            _database.MoveNoteFolder(folderId, parent);
            if (parent is { } id)
                _collapsedNoteFolders.Remove(id);
            LoadChains(selectFolder: folderId);
            StatusText.Text = $"已移到“{FolderPathText(parent)}”";
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private void DeleteChain_Click(object sender, RoutedEventArgs e)
    {
        if (_shownChain is not { } chain)
            return;
        if (!Dialogs.Confirm(this, "删除笔记", $"删除“{chain.Name}”？里面的步骤、备选和思路会一起删除，插件不受影响。", "删除", danger: true))
            return;
        // 取消等待中的保存，否则会往已删除的笔记写。
        _chainSaveTimer.Stop();
        RememberExpansion();
        _database.DeleteChain(chain.Id);
        _shownChain = null;
        LoadChains(selectFolder: chain.FolderId);
        StatusText.Text = $"已删除笔记“{chain.Name}”";
    }

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_shownFolder is not { } folder)
            return;
        var ids = MixNotes.Subtree(folder.Id, _noteFolders);
        var notes = _chains.Count(c => c.FolderId == folder.Id);
        var subfolders = _noteFolders.Count(f => f.ParentId == folder.Id);
        // 连子文件夹里的也算，勾选“同时删除笔记”时删的是这些。
        var allNotes = _chains.Count(c => c.FolderId is { } f && ids.Contains(f));
        var parent = FolderPathText(folder.ParentId);
        var withNotes = false;
        bool confirmed;
        if (allNotes == 0)
        {
            confirmed = Dialogs.Confirm(this, "删除文件夹", subfolders == 0
                ? $"删除空文件夹“{folder.Name}”？"
                : $"删除文件夹“{folder.Name}”？里面的 {subfolders} 个子文件夹会移到“{parent}”。", "删除文件夹", danger: true);
        }
        else
        {
            var option = ids.Count > 1
                ? $"同时删除笔记：这个文件夹和子文件夹里的 {allNotes} 条笔记、{ids.Count - 1} 个子文件夹一起删除，不能恢复"
                : $"同时删除笔记：里面的 {allNotes} 条笔记一起删除，不能恢复";
            var inside = string.Join("和", new[] { notes > 0 ? $"{notes} 条笔记" : "", subfolders > 0 ? $"{subfolders} 个子文件夹" : "" }.Where(s => s.Length > 0));
            confirmed = Dialogs.Confirm(this, "删除文件夹",
                $"删除文件夹“{folder.Name}”？里面的 {inside}会移到“{parent}”，不会删除任何笔记。",
                "删除文件夹", option, ref withNotes);
        }
        if (!confirmed)
            return;
        if (withNotes)
        {
            // 取消等待中的保存，否则会往已删除的笔记写。
            _chainSaveTimer.Stop();
            _shownChain = null;
        }
        else
            FlushChain();
        RememberExpansion();
        foreach (var id in ids)
            _collapsedNoteFolders.Remove(id);
        if (withNotes)
        {
            var deleted = _database.DeleteNoteFolderWithNotes(folder.Id);
            LoadChains(selectFolder: folder.ParentId);
            StatusText.Text = $"已删除文件夹“{folder.Name}”和里面的 {deleted} 条笔记";
            return;
        }
        _database.DeleteNoteFolder(folder.Id);
        LoadChains(selectFolder: folder.ParentId);
        StatusText.Text = $"已删除文件夹“{folder.Name}”" + (notes + subfolders > 0 ? $"，里面的内容移到了“{parent}”" : "");
    }

    // ---------- 拖动到文件夹 ----------

    private Point _dragStart;
    private NoteNode? _dragNode;

    private void NoteTree_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(NoteTree);
        _dragNode = null;
        if (HandleMarkClick(e))
        {
            // 这次点击只用来多选，不切换显示的内容。
            e.Handled = true;
            return;
        }
        _dragNode = NodeAt(e.OriginalSource);
    }

    private void NoteTree_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode == null || IsInside<TextBox>(e.OriginalSource))
            return;
        var delta = e.GetPosition(NoteTree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var node = _dragNode;
        _dragNode = null;
        // 拖的是多选里的一项时，选中的一起移动，所以松开时不清空、不取消。
        _clearMarksOnUp = false;
        _toggleOnUp = null;
        DragDrop.DoDragDrop(NoteTree, new DataObject(typeof(NoteNode), node), DragDropEffects.Move);
    }

    private void NoteTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropTarget(e, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void NoteTree_Drop(object sender, DragEventArgs e)
    {
        if (!DropTarget(e, out var target) || e.Data.GetData(typeof(NoteNode)) is not NoteNode dragged)
            return;
        if (dragged.IsMarked)
            MoveBatch(CurrentBatch(), target);
        else if (dragged.IsFolder)
            MoveFolder(dragged.Id, target);
        else
            MoveNote(dragged.Id, target);
        e.Handled = true;
    }

    /// <summary>落在文件夹上就放进这个文件夹，落在笔记上放进它所在的文件夹，落在空白处放到最外层。</summary>
    private bool DropTarget(DragEventArgs e, out long? folder)
    {
        folder = null;
        if (e.Data.GetData(typeof(NoteNode)) is not NoteNode dragged)
            return false;
        var over = NodeAt(e.OriginalSource);
        folder = over switch
        {
            { IsFolder: true } node => node.Id,
            { IsFolder: false } node => _chains.FirstOrDefault(c => c.Id == node.Id)?.FolderId,
            _ => null,
        };
        // 多选一起拖：不能放进选中的文件夹自己里面。
        if (dragged.IsMarked)
            return !(folder is { } inside && CurrentBatch().Inside.Contains(inside));
        if (dragged.IsFolder)
        {
            var current = _noteFolders.FirstOrDefault(f => f.Id == dragged.Id)?.ParentId;
            return folder != current && !(folder is { } f && MixNotes.Subtree(dragged.Id, _noteFolders).Contains(f));
        }
        return folder != _chains.FirstOrDefault(c => c.Id == dragged.Id)?.FolderId;
    }

    private static NoteNode? NodeAt(object source)
    {
        for (var node = source as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TreeViewItem { DataContext: NoteNode item })
                return item;
        }
        return null;
    }

    // ---------- 步骤和备选 ----------

    private StepViewModel? SelectedStep => ChainItems.SelectedItem as StepViewModel;

    private void ChainItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedStep is { } step)
            ChainCandidateHint.Text = $"点 + 加成新的一步；点 ⇄ 加为第 {step.Number} 步（{step.Name}）的备选";
        else
            ChainCandidateHint.Text = "点 + 加成新的一步；先在中间点选某一步，再点 ⇄ 可以加为它的备选";
    }

    private void AddToChain_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string key || _shownChain == null)
            return;
        var plugin = _plugins.FirstOrDefault(p => p.Row.Key == key);
        if (plugin == null)
            return;
        var added = new StepViewModel(MakePick(plugin), [], ScheduleChainSave);
        _chainSteps.Add(added);
        CommitChainSteps();
        ChainItems.ScrollIntoView(added);
    }

    private void AddAlternate_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string key || _shownChain == null)
            return;
        if (SelectedStep is not { } step)
        {
            StatusText.Text = "先在中间点选要加备选的那一步";
            return;
        }
        var plugin = _plugins.FirstOrDefault(p => p.Row.Key == key);
        if (plugin == null)
            return;
        step.AddAlternate(MakePick(plugin));
        step.IsExpanded = true;
        CommitChainSteps();
        StatusText.Text = $"已把 {plugin.Name} 加为第 {step.Number} 步的备选";
    }

    private void RemoveChainStep_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not StepViewModel step)
            return;
        _chainSteps.Remove(step);
        CommitChainSteps();
    }

    private void MoveChainStep_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not StepViewModel step)
            return;
        var at = _chainSteps.IndexOf(step);
        var target = at + ((string)button.CommandParameter == "up" ? -1 : 1);
        if (at < 0 || target < 0 || target >= _chainSteps.Count)
            return;
        _chainSteps.Move(at, target);
        CommitChainSteps();
    }

    private StepViewModel? StepOf(PickViewModel alternate) => _chainSteps.FirstOrDefault(s => s.Alternates.Contains(alternate));

    private void PromoteAlternate_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not PickViewModel alternate || StepOf(alternate) is not { } step)
            return;
        step.Promote(alternate);
        CommitChainSteps();
        StatusText.Text = $"第 {step.Number} 步的首选改成了 {alternate.Name}";
    }

    private void MoveAlternate_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not PickViewModel alternate || StepOf(alternate) is not { } step)
            return;
        if (step.MoveAlternate(alternate, (string)button.CommandParameter == "up" ? -1 : 1))
            CommitChainSteps();
    }

    private void CollapseAlternates_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is StepViewModel step)
            step.IsExpanded = false;
    }

    private void RemoveAlternate_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not PickViewModel alternate || StepOf(alternate) is not { } step)
            return;
        step.RemoveAlternate(alternate);
        CommitChainSteps();
    }

    private void ChainItems_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 在备注框里双击是选词，不是查看详情。
        if (IsInside<TextBox>(e.OriginalSource) || IsInside<ButtonBase>(e.OriginalSource))
            return;
        var pick = (e.OriginalSource as FrameworkElement)?.DataContext switch
        {
            PickViewModel alternate => alternate,
            StepViewModel step => step.Primary,
            _ => null,
        };
        if (pick?.PluginKey is { } key)
            ShowPluginInLibrary(key);
    }
}

/// <summary>左边树上的一项：文件夹或笔记。</summary>
public sealed class NoteNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsFolder { get; private init; }
    public long Id { get; private init; }
    public string Name { get; private set; } = "";
    public string Count { get; private set; } = "";
    public string ToolTip { get; private set; } = "";
    public List<NoteNode> Children { get; private init; } = [];
    public string Icon => IsFolder ? (IsExpanded ? "" : "") : Role == ChannelRole.Track ? "" : MainWindow.RoleGlyph(Role);

    public ChannelRole Role { get; private set; }
    public Brush IconBrush => ThemeManager.Resource(IsFolder ? "Accent" : "Muted");

    private bool _isEditing;
    private string _editText = "";

    /// <summary>正在树上直接改名。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
                return;
            _isEditing = value;
            if (value)
                EditText = Name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    public string EditText
    {
        get => _editText;
        set
        {
            _editText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditText)));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private bool _isMarked;

    /// <summary>多选里选中的（Ctrl、Shift 点击）。和 IsSelected 分开：IsSelected 是右边正在显示的那一项。</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value)
                return;
            _isMarked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMarked)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkTag)));
        }
    }

    /// <summary>给树节点的样式用：多选选中时是 "Marked"。</summary>
    public string? MarkTag => _isMarked ? "Marked" : null;

    private bool _showMarkBox;

    /// <summary>打开“多选”时每一行前面显示勾选框。</summary>
    public bool ShowMarkBox
    {
        get => _showMarkBox;
        set
        {
            if (_showMarkBox == value)
                return;
            _showMarkBox = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkBoxVisibility)));
        }
    }

    public Visibility MarkBoxVisibility => _showMarkBox ? Visibility.Visible : Visibility.Collapsed;

    public static NoteNode ForFolder(NoteFolder folder, List<NoteNode> children, int notes) => new()
    {
        IsFolder = true,
        Id = folder.Id,
        Name = folder.Name,
        Children = children,
        Count = notes > 0 ? notes.ToString() : "",
        ToolTip = $"{folder.Name}：{notes} 条笔记",
    };

    public static NoteNode ForChain(PluginChain chain)
    {
        var node = new NoteNode { IsFolder = false, Id = chain.Id };
        node.Update(chain);
        return node;
    }

    public void Update(PluginChain chain)
    {
        Name = chain.Name;
        Role = chain.Role;
        Count = chain.Steps.Count > 0 ? chain.Steps.Count.ToString() : "";
        ToolTip = chain.TagList.Count > 0 ? $"{chain.Name}  ·  {string.Join("、", chain.TagList)}" : chain.Name;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    }

    public override string ToString() => Name;
}

/// <summary>文件夹详情里列出的一条笔记。</summary>
public sealed class NoteSummary(PluginChain chain, string path)
{
    public PluginChain Chain { get; } = chain;
    public string Name => Chain.Name;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (path.Length > 0)
                parts.Add(path);
            parts.Add(Chain.Steps.Count == 0 ? "还没有插件" : string.Join(" → ", Chain.Steps.Select(s => s.PluginName)));
            if (Chain.TagList.Count > 0)
                parts.Add(string.Join("、", Chain.TagList));
            return string.Join("  ·  ", parts);
        }
    }

    public override string ToString() => Name;
}

/// <summary>一款插件：某一步的首选或备选。改说明时通知保存，不重建列表，否则输入框会失去焦点。</summary>
public sealed class PickViewModel(ChainPick pick, ChainStepState state, PluginRow? plugin, PluginUsage? usage, Action changed) : INotifyPropertyChanged
{
    private string _note = pick.Note;
    private string _label = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChainStepState State => state;
    public string? PluginKey => plugin?.Key;
    public string Name => plugin?.Name ?? pick.PluginName;

    public string Detail
    {
        get
        {
            if (plugin == null)
                return "这款插件已不在扫描结果里";
            var vendor = plugin.Vendor.Length > 0 ? plugin.Vendor : "未知";
            return $"{vendor}  ·  {UsageLabels.Summary(usage)}";
        }
    }

    public string StateText => State switch
    {
        ChainStepState.Missing => "未安装",
        ChainStepState.Blacklisted => "在垃圾桶",
        ChainStepState.Instrument => "音源",
        _ => "",
    };

    public Visibility StateVisibility => StateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>没安装、而且以前移进垃圾桶并卸载过：提示当时的评价。</summary>
    public PluginRow? Remembered { get; init; }
    public Visibility RememberedVisibility => Remembered != null ? Visibility.Visible : Visibility.Collapsed;
    public string RememberedText => Remembered == null ? "" : "以前移进垃圾桶并卸载了。" + TrashMemory.Describe(Remembered);

    private WishStatus? _wish;

    /// <summary>心愿单里同名记录的状态，没有就是 null。</summary>
    public WishStatus? Wish
    {
        get => _wish;
        set
        {
            if (_wish == value)
                return;
            _wish = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    /// <summary>没安装、心愿单里也还没有，可以一键加入。</summary>
    public bool CanWish => State == ChainStepState.Missing && Wish == null;

    public Visibility WishButtonVisibility => CanWish ? Visibility.Visible : Visibility.Collapsed;
    public string WishText => Wish == WishStatus.Purchased ? "已购买" : "在心愿单";
    public Visibility WishTextVisibility => State == ChainStepState.Missing && Wish != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>“备选 1”这样的序号，首选没有。</summary>
    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (_note == value)
                return;
            _note = value;
            changed();
        }
    }

    /// <summary>首选和备选互换时说明跟着插件走，所以要能通知界面刷新。</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public ChainPick ToPick() => pick with { Note = _note };

    public override string ToString() => Name;
}

/// <summary>链上的一步：首选插件和按顺序排的备选。</summary>
public sealed class StepViewModel : INotifyPropertyChanged
{
    private readonly Action _changed;
    private int _number;
    private bool _isExpanded;

    public StepViewModel(PickViewModel primary, IEnumerable<PickViewModel> alternates, Action changed)
    {
        Primary = primary;
        _changed = changed;
        Alternates = new ObservableCollection<PickViewModel>(alternates);
        Alternates.CollectionChanged += (_, _) => Relabel();
        Relabel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PickViewModel Primary { get; private set; }
    public ObservableCollection<PickViewModel> Alternates { get; }
    public string Name => Primary.Name;

    public int Number
    {
        get => _number;
        set
        {
            _number = value;
            Notify(nameof(Number));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(AlternatesVisibility));
            Notify(nameof(AlternatesText));
        }
    }

    /// <summary>有备选时带个箭头，提示可以展开、收起。</summary>
    public string AlternatesText => Alternates.Count == 0 ? "+ 备选" : $"备选 {Alternates.Count} {(IsExpanded ? "▴" : "▾")}";
    public Visibility AlternatesVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string AlternatesHint => Alternates.Count == 0
        ? "还没有备选。在右边找到插件，点 ⇄ 就会加到这一步的备选里"
        : "没有首选插件时按顺序换用下面的备选";

    public void AddAlternate(PickViewModel pick) => Alternates.Add(pick);

    public void RemoveAlternate(PickViewModel pick) => Alternates.Remove(pick);

    public bool MoveAlternate(PickViewModel pick, int offset)
    {
        var at = Alternates.IndexOf(pick);
        var target = at + offset;
        if (at < 0 || target < 0 || target >= Alternates.Count)
            return false;
        Alternates.Move(at, target);
        return true;
    }

    /// <summary>备选升为首选，原来的首选放到它原来的位置。</summary>
    public void Promote(PickViewModel pick)
    {
        var at = Alternates.IndexOf(pick);
        if (at < 0)
            return;
        Alternates[at] = Primary;
        Primary = pick;
        pick.Label = "";
        Relabel();
        Notify(nameof(Primary));
        Notify(nameof(Name));
    }

    private void Relabel()
    {
        for (var i = 0; i < Alternates.Count; i++)
            Alternates[i].Label = $"备选 {i + 1}";
        Notify(nameof(AlternatesText));
        Notify(nameof(AlternatesHint));
    }

    public ChainStep ToStep() => ChainStep.From(Primary.ToPick(), Alternates.Select(a => a.ToPick()));

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}
