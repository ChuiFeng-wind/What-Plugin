using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 混音笔记：按混音师、乐器分文件夹，记下每条轨道用了哪些插件、什么顺序、为什么这么用。
/// 这里只是记录，不会写入 DAW；从工程导入轨道时工程文件只读。
/// </summary>
public partial class NotesView : UserControl
{
    private readonly PluginDatabase _database;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly HashSet<long> _collapsed = [];
    private readonly Button _importButton;
    private List<PluginChain> _chains = [];
    private List<NoteFolder> _folders = [];
    private IReadOnlyList<NoteRoute> _routes = [];
    private List<NoteItem> _roots = [];
    private IReadOnlyList<PluginRow> _effects = [];
    private IReadOnlyDictionary<string, PluginUsage> _usage = new Dictionary<string, PluginUsage>();
    private PluginNameMatcher? _remembered;
    private PluginChain? _shownChain;
    private NoteFolder? _shownFolder;
    private List<StepItem> _steps = [];
    private List<ChainStep> _hiddenSteps = [];
    private StepItem? _selectedStep;
    private readonly Dictionary<StepItem, Border> _stepCards = [];
    private RouteMapView? _map;
    private bool _building;
    private bool _treeSearched;
    private string _builtSearch = "";
    private bool _loadingChain;
    private bool _loadingRoute;

    public event Action<string>? StatusChanged;

    /// <summary>笔记或心愿单的数量变了，侧边栏的数字要更新。</summary>
    public event Action? CountsChanged;

    /// <summary>双击一款插件：在插件库里打开它。</summary>
    public event Action<PluginRow>? OpenPlugin;

    /// <summary>对话框。测试换成按脚本回答的实现。</summary>
    public IDialogs Dialogs { get; set; }

    /// <summary>标题右边的“从工程导入轨道…”。</summary>
    public Control Actions => _importButton;

    // 设计器需要无参构造函数。
    public NotesView() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db"))) { }

    public NotesView(PluginDatabase database)
    {
        _database = database;
        InitializeComponent();
        Dialogs = new WindowDialogs(() => TopLevel.GetTopLevel(this) as Window);

        var importContent = new StackPanel { Orientation = Orientation.Horizontal };
        importContent.Children.Add(new MaterialIcon { Kind = MaterialIconKind.FileImportOutline, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        importContent.Children.Add(new TextBlock { Text = "从工程导入轨道…", VerticalAlignment = VerticalAlignment.Center });
        _importButton = new Button { Content = importContent, Classes = { "Ghost" } };
        ToolTip.SetTip(_importButton, "读取 DAW 工程，选择轨道，把轨道上的插件按顺序记成笔记。工程文件只读，不会修改");
        AutomationProperties.SetName(_importButton, "从工程导入轨道…");
        _importButton.Click += (_, _) => _ = ImportTracks();

        _saveTimer.Tick += (_, _) => Flush();
        ToolTip.SetTip(MultiToggle, $"打开后逐行勾选笔记或文件夹，一起移动、加标签或删除。不打开也可以按住 {AppPlatform.CommandKeyName} 或 Shift 点击");
        FolderListHint.Text = $"左下角新建的笔记和文件夹会放在这个文件夹里。也可以把左边的笔记或文件夹拖到另一个文件夹上；按住 {AppPlatform.CommandKeyName} 或 Shift 点击可以多选，一起移动、加标签或删除。";
        ToolTip.SetTip(FolderMapToggle, $"轨道 → 编组 → 效果轨 → 主输出。实线是输出，虚线是发送；{AppPlatform.CommandKeyName} + 滚轮缩放，按住空白处拖动");

        // TextChanged 是稍后才触发的：程序自己改的文字（例如新建后清空搜索）已经处理过，不再重建。
        TreeSearch.TextChanged += (_, _) =>
        {
            if ((TreeSearch.Text ?? "") == _builtSearch)
                return;
            Flush();
            BuildTree(_shownChain?.Id, _shownChain == null ? _shownFolder?.Id : null);
        };
        MultiToggle.IsCheckedChanged += (_, _) => ApplyMultiMode();
        NoteTree.SelectionChanged += (_, _) =>
        {
            if (!_building)
                ShowSelection();
        };
        NoteTree.AddHandler(PointerPressedEvent, TreePressed, RoutingStrategies.Tunnel);
        NoteTree.AddHandler(PointerMovedEvent, TreeMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        NoteTree.AddHandler(PointerReleasedEvent, TreeReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        NoteTree.AddHandler(KeyDownEvent, TreeKeyDown, RoutingStrategies.Tunnel);
        NoteTree.AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { DataContext: NoteItem { IsEditing: true } item })
                EndRename(item, save: true);
        }, RoutingStrategies.Bubble);
        NoteTree.ContainerPrepared += (_, args) =>
        {
            if (args.Container is TreeViewItem tree)
                tree.ContextMenu ??= TreeMenu(tree);
        };

        NewNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddChain(NewNameBox.Text ?? "");
                e.Handled = true;
            }
        };
        AddChainButton.Click += (_, _) => AddChain(NewNameBox.Text ?? "");
        AddFolderButton.Click += (_, _) => AddFolder(NewNameBox.Text ?? "");

        BatchClearButton.Click += (_, _) => ClearMarks();
        BatchMoveButton.Click += (_, _) => _ = MoveBatchAsync();
        BatchDeleteButton.Click += (_, _) => _ = DeleteBatchAsync();
        BatchTagAddButton.Click += (_, _) => _ = AddTagsAsync();
        BatchTagRemoveButton.Click += (_, _) => _ = RemoveTagsAsync();

        FolderRenameButton.Click += (_, _) => _ = RenameShownAsync();
        FolderMoveButton.Click += (_, _) => _ = MoveShownAsync();
        FolderDeleteButton.Click += (_, _) => _ = DeleteFolderAsync();
        FolderListToggle.IsCheckedChanged += (_, _) =>
        {
            if (_shownFolder != null)
                ShowFolder(_shownFolder);
        };
        FolderNotes.DoubleTapped += (_, _) =>
        {
            if (FolderNotes.SelectedItem is NoteSummaryRow row)
                Reload(selectChain: row.Chain.Id);
        };

        ChainRenameButton.Click += (_, _) => _ = RenameShownAsync();
        ChainMoveButton.Click += (_, _) => _ = MoveShownAsync();
        ChainDeleteButton.Click += (_, _) => _ = DeleteChainAsync();
        WishMissingButton.Click += (_, _) => _ = AddToWishlist(AllPicks.Where(p => p.CanWish).ToList());
        ChainNote.TextChanged += (_, _) =>
        {
            // 显示笔记时填进去的文字不算修改。
            if (!_loadingChain && _shownChain != null && (ChainNote.Text ?? "") != _shownChain.Note)
                ScheduleSave();
        };
        AddTagButton.Click += (_, _) => AddCustomTag();
        NewTagBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddCustomTag();
                e.Handled = true;
            }
        };
        RoleBox.ItemsSource = NoteRouting.RoleOptions.Select(o => o.Text).ToList();
        RoleBox.SelectionChanged += (_, _) => RoleChanged();
        OutputBox.SelectionChanged += (_, _) => OutputChanged();

        CandidateSearch.TextChanged += (_, _) => ShowCandidates();
        CandidateList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is not Control { Tag: string key } button)
                return;
            if (button.Classes.Contains("AddStep"))
                AddStep(key);
            else if (button.Classes.Contains("AddAlternate"))
                AddAlternate(key);
        });

        Reload();
    }

    // ---------- 给主窗口和测试用 ----------

    public IReadOnlyList<NoteItem> Roots => _roots;

    public IEnumerable<NoteItem> AllItems => Walk(_roots);

    public NoteItem? Find(string name) => AllItems.FirstOrDefault(i => i.Name == name);

    public PluginChain? ShownChain => _shownChain;

    public NoteFolder? ShownFolder => _shownFolder;

    public IReadOnlyList<StepItem> Steps => _steps;

    public StepItem? SelectedStep
    {
        get => _selectedStep;
        set => SelectStep(value);
    }

    public IReadOnlyList<CandidateItem> Candidates => CandidateList.ItemsSource as IReadOnlyList<CandidateItem> ?? [];

    public IReadOnlyList<ToggleButton> TagButtons => TagChips.Children.OfType<ToggleButton>().ToList();

    public IReadOnlyList<NoteItem> Selected => NoteTree.SelectedItems?.OfType<NoteItem>().ToList() ?? [];

    public bool IsBatch => BatchPanel.IsVisible;

    public RouteMapView? Map => _map;

    public bool ShowsMap
    {
        get => FolderMapToggle.IsChecked == true;
        set => (value ? FolderMapToggle : FolderListToggle).IsChecked = true;
    }

    public void SetUsage(IReadOnlyDictionary<string, PluginUsage> usage)
    {
        _usage = usage;
        ShowCandidates();
    }

    /// <summary>选中一项（清掉多选）。</summary>
    public void Select(NoteItem? item)
    {
        if (item != null)
            foreach (var ancestor in Ancestors(item))
                ancestor.IsExpanded = true;
        NoteTree.SelectedItem = item;
    }

    /// <summary>多选：一起选中这些项。</summary>
    public void SelectMany(IEnumerable<NoteItem> items)
    {
        _building = true;
        NoteTree.SelectedItems.Clear();
        foreach (var item in items)
            NoteTree.SelectedItems.Add(item);
        _building = false;
        ShowSelection();
    }

    // ---------- 树 ----------

    /// <summary>重新读取笔记和文件夹。可以指定要选中的笔记或文件夹，否则保持原来的选择。</summary>
    public void Reload(long? selectChain = null, long? selectFolder = null)
    {
        Flush();
        if (selectChain == null && selectFolder == null)
        {
            selectChain = _shownChain?.Id;
            selectFolder = _shownChain == null ? _shownFolder?.Id : null;
        }
        _shownChain = null;
        _shownFolder = null;
        _chains = _database.LoadChains();
        _folders = _database.LoadNoteFolders();
        _routes = _database.LoadRoutes();
        _effects = _database.LoadEffects();
        _remembered = new PluginNameMatcher(_effects);
        BuildTree(selectChain, selectFolder);
    }

    private static IEnumerable<NoteItem> Walk(IEnumerable<NoteItem> items) => items.SelectMany(i => Walk(i.Children).Prepend(i));

    private IEnumerable<NoteItem> VisibleItems(IEnumerable<NoteItem> items) =>
        items.SelectMany(i => i.IsExpanded ? VisibleItems(i.Children).Prepend(i) : [i]);

    private List<NoteItem> Ancestors(NoteItem target)
    {
        List<NoteItem>? Path(IEnumerable<NoteItem> items)
        {
            foreach (var item in items)
            {
                if (ReferenceEquals(item, target))
                    return [];
                if (Path(item.Children) is { } below)
                {
                    below.Insert(0, item);
                    return below;
                }
            }
            return null;
        }
        return Path(_roots) ?? [];
    }

    private void BuildTree(long? selectChain, long? selectFolder)
    {
        RememberExpansion();
        var search = TreeSearch.Text ?? "";
        _builtSearch = search;
        var searching = search.Trim().Length > 0;
        var names = _effects.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Name);
        var entries = NoteListing.Tree(_chains, _folders, search, names);
        _roots = entries.Select(e => new NoteItem(e, true)).ToList();
        _treeSearched = searching;
        foreach (var folder in Walk(_roots).Where(i => i.IsFolder))
            folder.IsExpanded = searching || !_collapsed.Contains(folder.Id);
        ApplyMultiMode();

        _building = true;
        NoteTree.SelectedItems.Clear();
        NoteTree.ItemsSource = _roots;
        var selected = Walk(_roots).FirstOrDefault(i => i.IsFolder ? i.Id == selectFolder : i.Id == selectChain);
        if (selected != null)
            Select(selected);
        _building = false;
        ShowSelection();
    }

    /// <summary>折叠状态在重建树时保留（搜索时都展开，不记）。</summary>
    private void RememberExpansion()
    {
        if (_treeSearched)
            return;
        foreach (var folder in Walk(_roots).Where(i => i.IsFolder))
        {
            if (folder.IsExpanded)
                _collapsed.Remove(folder.Id);
            else
                _collapsed.Add(folder.Id);
        }
    }

    private long? TargetFolder => NoteListing.TargetFolder(Selected.Count == 1 ? Selected[0].Entry : null, _chains);

    private string FolderPath(long? id) => NoteListing.FolderPath(id, _folders);

    private void Status(string text) => StatusChanged?.Invoke(text);

    // ---------- 多选 ----------

    private bool MultiMode => MultiToggle.IsChecked == true;

    private void ApplyMultiMode()
    {
        foreach (var item in Walk(_roots))
            item.ShowMarkBox = MultiMode;
    }

    private NoteBatch CurrentBatch()
    {
        var selected = Selected.ToHashSet();
        var notes = new List<PluginChain>();
        var folders = new List<NoteFolder>();
        // 选中了文件夹又选中它里面的东西时，只算最外面的那个文件夹。
        void Collect(IEnumerable<NoteItem> items)
        {
            foreach (var item in items)
            {
                if (!selected.Contains(item))
                {
                    Collect(item.Children);
                    continue;
                }
                if (item.IsFolder)
                {
                    if (_folders.FirstOrDefault(f => f.Id == item.Id) is { } folder)
                        folders.Add(folder);
                }
                else if (_chains.FirstOrDefault(c => c.Id == item.Id) is { } chain)
                    notes.Add(chain);
            }
        }
        Collect(_roots);
        return NoteBatch.Of(folders, notes, _folders, _chains);
    }

    /// <summary>取消多选，只留正在显示的那一项。</summary>
    public void ClearMarks()
    {
        var keep = Walk(_roots).FirstOrDefault(i => _shownChain != null ? !i.IsFolder && i.Id == _shownChain.Id : _shownFolder != null && i.IsFolder && i.Id == _shownFolder.Id);
        NoteTree.SelectedItem = keep;
        if (keep == null)
            ShowSelection();
    }

    /// <summary>{Ctrl}+A：选中能看到的所有笔记。</summary>
    public void MarkAll() => SelectMany(VisibleItems(_roots).Where(i => !i.IsFolder));

    private void ShowBatch(NoteBatch batch)
    {
        BatchTitle.Text = "已选中 " + batch.Describe();
        BatchMeta.Text = batch.Hint;
        BatchList.ItemsSource = batch.Folders.Select(f => new BatchRow(true, f.Name, FolderPath(f.ParentId)))
            .Concat(batch.Notes.Select(c => new BatchRow(false, c.Name, FolderPath(c.FolderId))))
            .ToList();
        BatchTagAddButton.IsEnabled = batch.TagTargets.Count > 0;
        BatchTagRemoveButton.IsEnabled = batch.TagTargets.Any(c => c.TagList.Count > 0);
    }

    public async Task MoveBatchAsync(NoteBatch? batch = null)
    {
        batch ??= CurrentBatch();
        if (batch.IsEmpty)
            return;
        if (await PickFolder("移动 " + batch.Describe(), null, batch.Inside) is not { } target)
            return;
        MoveBatch(batch, target.Id);
    }

    private void MoveBatch(NoteBatch batch, long? target)
    {
        Flush();
        RememberExpansion();
        var done = NoteListing.MoveBatch(_database, batch, target, _folders);
        if (target is { } id)
            _collapsed.Remove(id);
        Reload(selectFolder: target);
        Status(done);
    }

    public async Task DeleteBatchAsync()
    {
        var batch = CurrentBatch();
        if (batch.IsEmpty)
            return;
        var (title, message, option) = batch.DeletePrompt();
        var withNotes = false;
        if (option == null)
        {
            if (!await Dialogs.Confirm(title, message, "删除", danger: true))
                return;
        }
        else
        {
            var (confirmed, checkedOption) = await Dialogs.ConfirmWithOption(title, message, "删除", option);
            if (!confirmed)
                return;
            withNotes = checkedOption;
        }
        // 取消等待中的保存，否则会往已删除的笔记写。
        if (_shownChain is { } shown && (batch.Notes.Any(c => c.Id == shown.Id) || withNotes && batch.Contained.Any(c => c.Id == shown.Id)))
        {
            _saveTimer.Stop();
            _shownChain = null;
        }
        else
            Flush();
        RememberExpansion();
        var done = NoteListing.DeleteBatch(_database, batch, withNotes);
        foreach (var folder in batch.Folders)
            _collapsed.Remove(folder.Id);
        _shownFolder = null;
        Reload();
        CountsChanged?.Invoke();
        Status(done);
    }

    public async Task AddTagsAsync()
    {
        var targets = CurrentBatch().TagTargets;
        if (targets.Count == 0)
            return;
        var known = NoteListing.KnownTags(_chains);
        var (picked, _) = await Dialogs.PickMany("加标签", $"给 {targets.Count} 条笔记加上勾选的标签。已经有的不会重复。",
            known.Select(t => (t, false)).ToList(), "加上");
        if (picked is not { Count: > 0 })
            return;
        var add = picked.Select(i => known[i]).ToList();
        ApplyTags(targets, tags => tags.Concat(add), $"已给 {targets.Count} 条笔记加上标签：{string.Join("、", add)}");
    }

    public async Task RemoveTagsAsync()
    {
        var targets = CurrentBatch().TagTargets;
        var present = targets.SelectMany(c => c.TagList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (present.Count == 0)
            return;
        var (picked, _) = await Dialogs.PickMany("去掉标签", $"从 {targets.Count} 条笔记上去掉勾选的标签。",
            present.Select(t => (t, false)).ToList(), "去掉");
        if (picked is not { Count: > 0 })
            return;
        var remove = picked.Select(i => present[i]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplyTags(targets, tags => tags.Where(t => !remove.Contains(t)), $"已从 {targets.Count} 条笔记上去掉标签：{string.Join("、", remove)}");
    }

    private void ApplyTags(IReadOnlyList<PluginChain> targets, Func<IEnumerable<string>, IEnumerable<string>> change, string done)
    {
        Flush();
        var selected = Selected.Select(i => (i.IsFolder, i.Id)).ToList();
        NoteListing.ChangeTags(_database, targets, change);
        Reload();
        // 改标签后保留多选，方便接着操作。
        SelectMany(Walk(_roots).Where(i => selected.Contains((i.IsFolder, i.Id))));
        Status(done);
    }

    // ---------- 拖动、右键、键盘 ----------

    private NoteItem? _pressed;
    private Point _pressPoint;
    private NoteItem? _dragging;

    private static NoteItem? ItemAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as NoteItem;

    private void TreePressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        _dragging = null;
        if (!e.GetCurrentPoint(NoteTree).Properties.IsLeftButtonPressed || (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) != null)
            return;
        var item = ItemAt(e.Source);
        _pressed = item;
        _pressPoint = e.GetPosition(NoteTree);
        // 打开“多选”后，点一行就是选中或取消（展开箭头和勾选框各管各的）。
        if (item != null && MultiMode && e.KeyModifiers == KeyModifiers.None
            && (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) == null)
        {
            if (NoteTree.SelectedItems.Contains(item))
                NoteTree.SelectedItems.Remove(item);
            else
                NoteTree.SelectedItems.Add(item);
            e.Handled = true;
        }
    }

    private void TreeMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed == null || _dragging != null || !e.GetCurrentPoint(NoteTree).Properties.IsLeftButtonPressed)
            return;
        var delta = e.GetPosition(NoteTree) - _pressPoint;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 8)
        {
            _dragging = _pressed;
            Cursor = new Cursor(StandardCursorType.DragMove);
        }
    }

    private void TreeReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _dragging;
        _dragging = null;
        _pressed = null;
        Cursor = null;
        if (dragged == null)
            return;
        var over = ItemAt(NoteTree.InputHitTest(e.GetPosition(NoteTree)));
        Drop(dragged, over);
    }

    /// <summary>
    /// 拖到 over 上：落在文件夹上放进这个文件夹，落在笔记上放进它所在的文件夹，落在空白处放到最外层。
    /// 拖的是多选里的一项时，选中的一起移动。
    /// </summary>
    public bool Drop(NoteItem dragged, NoteItem? over)
    {
        long? folder = over switch
        {
            { IsFolder: true } node => node.Id,
            { IsFolder: false } node => _chains.FirstOrDefault(c => c.Id == node.Id)?.FolderId,
            _ => null,
        };
        if (Selected.Count > 1 && Selected.Contains(dragged))
        {
            var batch = CurrentBatch();
            if (folder is { } inside && batch.Inside.Contains(inside))
                return false;
            MoveBatch(batch, folder);
            return true;
        }
        if (dragged.IsFolder)
        {
            var current = _folders.FirstOrDefault(f => f.Id == dragged.Id)?.ParentId;
            if (folder == current || folder is { } f && MixNotes.Subtree(dragged.Id, _folders).Contains(f))
                return false;
            MoveFolder(dragged.Id, folder);
            return true;
        }
        if (folder == _chains.FirstOrDefault(c => c.Id == dragged.Id)?.FolderId)
            return false;
        MoveNote(dragged.Id, folder);
        return true;
    }

    private ContextMenu TreeMenu(TreeViewItem container)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            if (container.DataContext is not NoteItem item || item.IsEditing)
            {
                e.Cancel = true;
                return;
            }
            var items = new List<Control>();
            // 在多选的一项上右键：操作针对选中的全部。
            if (Selected.Count > 1 && Selected.Contains(item))
            {
                var batch = CurrentBatch();
                items.Add(Entry($"移动 {batch.Describe()}到…", MaterialIconKind.FolderMoveOutline, () => _ = MoveBatchAsync()));
                items.Add(Entry("加标签…", MaterialIconKind.TagPlusOutline, () => _ = AddTagsAsync(), enabled: batch.TagTargets.Count > 0));
                items.Add(Entry("去掉标签…", MaterialIconKind.TagRemoveOutline, () => _ = RemoveTagsAsync(), enabled: batch.TagTargets.Any(c => c.TagList.Count > 0)));
                items.Add(new Separator());
                items.Add(Entry($"删除 {batch.Describe()}", MaterialIconKind.DeleteOutline, () => _ = DeleteBatchAsync(), "Del"));
                items.Add(new Separator());
                items.Add(Entry("取消选择", MaterialIconKind.Close, ClearMarks, "Esc"));
            }
            else
            {
                // 先选中，中间显示的就是这一项，移动和删除都针对它。
                Select(item);
                items.Add(Entry("重命名", MaterialIconKind.RenameOutline, () => BeginRename(item), "F2"));
                items.Add(Entry("移动到…", MaterialIconKind.FolderMoveOutline, () => _ = MoveShownAsync()));
                items.Add(new Separator());
                items.Add(Entry(item.IsFolder ? "删除文件夹" : "删除笔记", MaterialIconKind.DeleteOutline,
                    () => _ = item.IsFolder ? DeleteFolderAsync() : DeleteChainAsync()));
            }
            menu.ItemsSource = items;
        };
        return menu;
    }

    private static MenuItem Entry(string header, MaterialIconKind icon, Action action, string gesture = "", bool enabled = true)
    {
        var item = new MenuItem { Header = header, Icon = new MaterialIcon { Kind = icon, Width = 15, Height = 15 }, IsEnabled = enabled };
        if (gesture.Length > 0)
            item.InputGesture = KeyGesture.Parse(gesture);
        item.Click += (_, _) => action();
        return item;
    }

    private static bool CommandKey(KeyModifiers modifiers) => modifiers is KeyModifiers.Control or KeyModifiers.Meta;

    private void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox { DataContext: NoteItem editing })
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                EndRename(editing, save: e.Key == Key.Enter);
                e.Handled = true;
            }
            return;
        }
        if (e.Source is TextBox)
            return;
        if (e.Key == Key.A && CommandKey(e.KeyModifiers))
        {
            MarkAll();
            e.Handled = true;
        }
        else if (Selected.Count > 1 && e.Key is Key.Escape or Key.Delete or Key.Back)
        {
            if (e.Key == Key.Escape)
                ClearMarks();
            else
                _ = DeleteBatchAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && Selected.Count == 1)
        {
            BeginRename(Selected[0]);
            e.Handled = true;
        }
    }

    // ---------- 直接在树上改名 ----------

    public void BeginRename(NoteItem item)
    {
        foreach (var other in Walk(_roots).Where(i => i.IsEditing && !ReferenceEquals(i, item)))
            other.IsEditing = false;
        item.IsEditing = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (NoteTree.TreeContainerFromItem(item) is TreeViewItem container
                && container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("Rename")) is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>名字为空或没变就不保存。</summary>
    public void EndRename(NoteItem item, bool save)
    {
        if (!item.IsEditing)
            return;
        var name = item.EditText.Trim();
        item.IsEditing = false;
        if (!save || name.Length == 0 || name == item.Name)
        {
            if (save && name.Length == 0)
                Status("名称不能为空，没有改名");
            NoteTree.Focus();
            return;
        }
        // 保存会重建树，等焦点切换结束后再做。
        Dispatcher.UIThread.Post(() =>
        {
            if (item.IsFolder)
                RenameFolder(item.Id, name);
            else
                RenameNote(item.Id, name);
        }, DispatcherPriority.Background);
    }

    // ---------- 中间：显示选中的 ----------

    private void ShowSelection()
    {
        // 切换前先保存，等待中的内容属于正在显示的那条。
        Flush();
        var selected = Selected;
        if (selected.Count > 1)
        {
            EmptyPanel.IsVisible = FolderPanel.IsVisible = ChainPanel.IsVisible = false;
            BatchPanel.IsVisible = true;
            ShowBatch(CurrentBatch());
            return;
        }
        BatchPanel.IsVisible = false;
        var item = selected.Count == 1 ? selected[0] : null;
        _shownChain = item is { IsFolder: false } ? _chains.FirstOrDefault(c => c.Id == item.Id) : null;
        _shownFolder = item is { IsFolder: true } ? _folders.FirstOrDefault(f => f.Id == item.Id) : null;
        ChainPanel.IsVisible = _shownChain != null;
        FolderPanel.IsVisible = _shownFolder != null;
        EmptyPanel.IsVisible = _shownChain == null && _shownFolder == null;
        EmptyText.Text = NoteListing.EmptyText(_chains.Count == 0 && _folders.Count == 0, (TreeSearch.Text ?? "").Trim().Length > 0, _roots.Count == 0);
        if (_shownFolder != null)
            ShowFolder(_shownFolder);
        if (_shownChain != null)
            ShowChain(_shownChain);
        ShowCandidates();
    }

    private void ShowFolder(NoteFolder folder)
    {
        FolderTitle.Text = folder.Name;
        FolderMeta.Text = NoteListing.FolderMeta(folder, _chains, _folders);
        var map = ShowsMap;
        FolderNotes.IsVisible = !map;
        FolderListHint.IsVisible = !map;
        FolderMapHost.IsVisible = map;
        if (!map)
        {
            FolderNotes.ItemsSource = NoteListing.FolderNotes(folder, _chains, _folders);
            return;
        }
        if (_map == null)
        {
            _map = new RouteMapView();
            _map.OpenNote += id => Reload(selectChain: id);
            FolderMapHost.Child = _map;
        }
        _map.Show(NoteListing.NotesIn(folder, _chains, _folders), _routes, NoteRouting.MapPlugins(_database.LoadPlugins()));
    }

    private void ShowChain(PluginChain chain)
    {
        var rows = _database.LoadPlugins();
        var wishes = NoteWishes.Statuses(_database);
        _hiddenSteps = [];
        _steps = [];
        foreach (var step in chain.Steps)
        {
            var primary = MakePick(step.Primary, rows, wishes);
            // 改判成音源的插件不在这个软件里显示，但不能因此丢掉这一步。
            if (primary.State == ChainStepState.Instrument)
            {
                _hiddenSteps.Add(step);
                continue;
            }
            _steps.Add(new StepItem(primary, step.Backups.Select(a => MakePick(a, rows, wishes))));
        }
        _selectedStep = null;

        _loadingChain = true;
        ChainTitle.Text = chain.Name;
        ChainNote.Text = chain.Note;
        _loadingChain = false;
        BuildSteps();
        BuildTags();
        UpdateMeta();
        ShowRoutes(chain);
    }

    private PickItem MakePick(ChainPick pick, IEnumerable<PluginRow> rows, IReadOnlyDictionary<string, WishStatus> wishes)
    {
        var (state, plugin) = ChainReport.Resolve(pick, rows);
        return new PickItem(pick, state, plugin, plugin == null ? null : _usage.GetValueOrDefault(plugin.Key), ScheduleSave)
        {
            Wish = wishes.TryGetValue(Normalizer.Name(pick.PluginName), out var status) ? status : null,
            Remembered = NoteListing.Remembered(pick, state, plugin, _remembered ?? new PluginNameMatcher([])),
        };
    }

    private PickItem MakePick(PluginRow plugin) =>
        new(new ChainPick(plugin.Key, plugin.Name), ChainStepState.Installed, plugin, _usage.GetValueOrDefault(plugin.Key), ScheduleSave);

    private IEnumerable<PickItem> AllPicks => _steps.SelectMany(s => s.Alternates.Prepend(s.Primary));

    private void UpdateMeta()
    {
        if (_shownChain == null)
            return;
        var missing = _steps.Count(s => s.Primary.State == ChainStepState.Missing);
        var trashed = _steps.Count(s => s.Primary.State == ChainStepState.Blacklisted);
        var alternates = _steps.Sum(s => s.Alternates.Count);
        ChainMeta.Text = NoteListing.ChainMeta(_shownChain, _folders, _steps.Count, alternates, missing, trashed);
        StepsEmpty.IsVisible = _steps.Count == 0;
        var wishable = AllPicks.Where(p => p.CanWish).Select(p => Normalizer.Name(p.Name)).Distinct().Count();
        WishMissingButton.IsVisible = wishable > 0;
        WishMissingText.Text = NoteListing.WishMissingText(wishable);
    }

    public string MetaText => ChainMeta.Text ?? "";

    public string WishMissingLabel => WishMissingButton.IsVisible ? WishMissingText.Text ?? "" : "";

    // ---------- 标签 ----------

    private void BuildTags()
    {
        if (_shownChain == null)
            return;
        var active = _shownChain.TagList;
        TagChips.Children.Clear();
        foreach (var tag in NoteListing.KnownTags(_chains, active))
        {
            var chip = new ToggleButton { Content = tag, Tag = tag, Classes = { "Chip" }, IsChecked = active.Contains(tag, StringComparer.OrdinalIgnoreCase) };
            AutomationProperties.SetAutomationId(chip, "NoteTag_" + tag);
            chip.IsCheckedChanged += (_, _) => ToggleTag(tag, chip.IsChecked == true);
            TagChips.Children.Add(chip);
        }
    }

    public void ToggleTag(string tag, bool on)
    {
        if (_shownChain == null)
            return;
        var tags = _shownChain.TagList.Where(t => !t.Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on)
            tags.Add(tag);
        SetTags(tags);
    }

    public void AddCustomTag(string? text = null)
    {
        if (_shownChain == null)
            return;
        var added = PluginEdits.SplitTags(text ?? NewTagBox.Text ?? "");
        if (added.Count == 0)
            return;
        NewTagBox.Text = "";
        SetTags(_shownChain.TagList.Concat(added).ToList());
        BuildTags();
    }

    private void SetTags(List<string> tags)
    {
        if (_shownChain == null)
            return;
        _database.SetChainTags(_shownChain.Id, tags);
        ReplaceChain(_shownChain with { Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        Status($"已保存 {_shownChain.Name} 的标签");
    }

    /// <summary>更新内存里的这条笔记，不重建树，避免正在编辑的内容失去焦点。</summary>
    private void ReplaceChain(PluginChain chain)
    {
        var at = _chains.FindIndex(c => c.Id == chain.Id);
        if (at >= 0)
            _chains[at] = chain;
        if (_shownChain?.Id == chain.Id)
            _shownChain = chain;
        Walk(_roots).FirstOrDefault(i => !i.IsFolder && i.Id == chain.Id)?.Update(chain);
    }

    // ---------- 保存 ----------

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public bool HasPendingSave => _saveTimer.IsEnabled;

    /// <summary>有等待中的修改才保存。离开页面、切换笔记前都要调用。</summary>
    public void Flush()
    {
        var pending = _saveTimer.IsEnabled;
        _saveTimer.Stop();
        if (pending)
            SaveShown();
    }

    private void SaveShown()
    {
        if (_shownChain == null)
            return;
        var steps = _steps.Select(s => s.ToStep()).Concat(_hiddenSteps).ToList();
        var note = ChainNote.Text ?? "";
        _database.SaveChain(_shownChain.Id, note, steps);
        _database.RecordAction();
        ReplaceChain(_shownChain with { Note = note, Steps = steps });
        Status($"已保存 {_shownChain.Name}");
    }

    private void CommitSteps()
    {
        _saveTimer.Stop();
        SaveShown();
        BuildSteps();
        UpdateMeta();
    }

    // ---------- 新建、改名、移动、删除 ----------

    public void AddChain(string name)
    {
        var folder = TargetFolder;
        try
        {
            Flush();
            RememberExpansion();
            var chain = _database.AddChain(name, folder);
            NewNameBox.Text = "";
            if (folder is { } id)
                _collapsed.Remove(id);
            TreeSearch.Text = "";
            Reload(selectChain: chain.Id);
            CountsChanged?.Invoke();
            Status($"已在“{FolderPath(folder)}”新建笔记“{chain.Name}”");
        }
        catch (ArgumentException error)
        {
            Status(error.Message);
        }
    }

    public void AddFolder(string name)
    {
        var parent = TargetFolder;
        try
        {
            Flush();
            RememberExpansion();
            var folder = _database.AddNoteFolder(parent, name);
            NewNameBox.Text = "";
            if (parent is { } id)
                _collapsed.Remove(id);
            TreeSearch.Text = "";
            Reload(selectFolder: folder.Id);
            Status($"已在“{FolderPath(parent)}”新建文件夹“{folder.Name}”");
        }
        catch (ArgumentException error)
        {
            Status(error.Message);
        }
    }

    private async Task RenameShownAsync()
    {
        if (_shownChain is { } chain)
        {
            if (await Dialogs.Prompt("重命名笔记", "新的名称：", "保存", chain.Name) is { } name)
                RenameNote(chain.Id, name);
        }
        else if (_shownFolder is { } folder && await Dialogs.Prompt("重命名文件夹", "新的名称：", "保存", folder.Name) is { } name)
            RenameFolder(folder.Id, name);
    }

    public void RenameNote(long id, string name)
    {
        try
        {
            Flush();
            RememberExpansion();
            _database.RenameChain(id, name);
            Reload(selectChain: id);
            Status($"已改名为“{name.Trim()}”");
        }
        catch (ArgumentException error)
        {
            Status(error.Message);
        }
    }

    public void RenameFolder(long id, string name)
    {
        try
        {
            Flush();
            RememberExpansion();
            _database.RenameNoteFolder(id, name);
            Reload(selectFolder: id);
            Status($"文件夹已改名为“{name.Trim()}”");
        }
        catch (ArgumentException error)
        {
            Status(error.Message);
        }
    }

    /// <summary>选择目标文件夹。exclude 里的文件夹（自己和子文件夹）不能选。取消返回 null。</summary>
    private async Task<(long? Id, bool Ok)?> PickFolder(string title, long? current, IReadOnlySet<long> exclude)
    {
        var options = NoteListing.MoveTargets(_folders, exclude).ToList();
        var index = await Dialogs.PickOne(title, "移到哪个文件夹？也可以直接把左边的条目拖到文件夹上。",
            options.Select(o => o.Text).ToList(), "移动", Math.Max(0, options.FindIndex(o => o.Id == current)));
        return index == null ? null : (options[index.Value].Id, true);
    }

    public async Task MoveShownAsync()
    {
        if (_shownChain is { } chain)
        {
            if (await PickFolder($"移动“{chain.Name}”", chain.FolderId, new HashSet<long>()) is { } target)
                MoveNote(chain.Id, target.Id);
        }
        else if (_shownFolder is { } folder
            && await PickFolder($"移动文件夹“{folder.Name}”", folder.ParentId, MixNotes.Subtree(folder.Id, _folders)) is { } target)
            MoveFolder(folder.Id, target.Id);
    }

    public void MoveNote(long id, long? folder)
    {
        try
        {
            Flush();
            RememberExpansion();
            _database.MoveChain(id, folder);
            if (folder is { } f)
                _collapsed.Remove(f);
            Reload(selectChain: id);
            Status($"已移到“{FolderPath(folder)}”");
        }
        catch (ArgumentException error)
        {
            Status(error.Message);
        }
    }

    public void MoveFolder(long id, long? parent)
    {
        try
        {
            Flush();
            RememberExpansion();
            _database.MoveNoteFolder(id, parent);
            if (parent is { } p)
                _collapsed.Remove(p);
            Reload(selectFolder: id);
            Status($"已移到“{FolderPath(parent)}”");
        }
        catch (ArgumentException error)
        {
            Status(error.Message);
        }
    }

    public async Task DeleteChainAsync()
    {
        if (_shownChain is not { } chain)
            return;
        if (!await Dialogs.Confirm("删除笔记", $"删除“{chain.Name}”？里面的步骤、备选和思路会一起删除，插件不受影响。", "删除", danger: true))
            return;
        // 取消等待中的保存，否则会往已删除的笔记写。
        _saveTimer.Stop();
        RememberExpansion();
        _database.DeleteChain(chain.Id);
        _shownChain = null;
        Reload(selectFolder: chain.FolderId);
        CountsChanged?.Invoke();
        Status($"已删除笔记“{chain.Name}”");
    }

    public async Task DeleteFolderAsync()
    {
        if (_shownFolder is not { } folder)
            return;
        var ids = MixNotes.Subtree(folder.Id, _folders);
        var notes = _chains.Count(c => c.FolderId == folder.Id);
        var subfolders = _folders.Count(f => f.ParentId == folder.Id);
        var parent = FolderPath(folder.ParentId);
        var (message, option) = NoteListing.DeleteFolderPrompt(folder, _chains, _folders);
        var withNotes = false;
        if (option == null)
        {
            if (!await Dialogs.Confirm("删除文件夹", message, "删除文件夹", danger: true))
                return;
        }
        else
        {
            var (confirmed, checkedOption) = await Dialogs.ConfirmWithOption("删除文件夹", message, "删除文件夹", option);
            if (!confirmed)
                return;
            withNotes = checkedOption;
        }
        if (withNotes)
        {
            _saveTimer.Stop();
            _shownChain = null;
        }
        else
            Flush();
        RememberExpansion();
        foreach (var id in ids)
            _collapsed.Remove(id);
        if (withNotes)
        {
            var deleted = _database.DeleteNoteFolderWithNotes(folder.Id);
            Reload(selectFolder: folder.ParentId);
            CountsChanged?.Invoke();
            Status($"已删除文件夹“{folder.Name}”和里面的 {deleted} 条笔记");
            return;
        }
        _database.DeleteNoteFolder(folder.Id);
        Reload(selectFolder: folder.ParentId);
        Status($"已删除文件夹“{folder.Name}”" + (notes + subfolders > 0 ? $"，里面的内容移到了“{parent}”" : ""));
    }

    // ---------- 心愿单 ----------

    public async Task AddToWishlist(IReadOnlyList<PickItem> picks)
    {
        if (_shownChain == null || picks.Count == 0)
            return;
        var existing = NoteWishes.Statuses(_database);
        var pending = NoteWishes.Pending(picks.Where(p => p.CanWish).Select(p => p.Name), existing);
        if (NoteWishes.RememberedPrompt(pending, _effects, _database.LoadUninstallHistory()) is { } prompt
            && !await Dialogs.Confirm("以前不要过的插件", prompt, "仍然加入"))
            return;
        var added = NoteWishes.Add(_database, pending, _shownChain.Name, existing, _effects);
        foreach (var pick in AllPicks)
            pick.Wish = existing.TryGetValue(Normalizer.Name(pick.Name), out var status) ? status : null;
        BuildSteps();
        UpdateMeta();
        if (added.Count > 0)
            CountsChanged?.Invoke();
        Status(NoteWishes.Added(added));
    }

    // ---------- 路由 ----------

    private void ShowRoutes(PluginChain chain)
    {
        _loadingRoute = true;
        var panel = NoteRouting.Panel(chain, _chains, _routes, _folders);
        RoleBox.SelectedIndex = NoteRouting.RoleOptions.ToList().FindIndex(o => o.Role == chain.Role);
        OutputBox.ItemsSource = panel.Outputs.Select(o => o.Text).ToList();
        OutputBox.Tag = panel.Outputs;
        OutputBox.SelectedIndex = panel.OutputIndex;
        OutputBox.IsEnabled = panel.OutputEnabled;
        RouteIncoming.Text = panel.Incoming;
        RouteIncoming.IsVisible = panel.Incoming.Length > 0;
        ToolTip.SetTip(RouteIncoming, panel.Incoming.Length > 0 ? panel.IncomingToolTip : null);

        RouteSends.Children.Clear();
        RouteSends.Children.Add(RouteLabel("发送", null));
        foreach (var target in panel.Sends)
            RouteSends.Children.Add(RouteChip(NoteRouting.Label(target), "打开这条笔记", "AccentSoft", target.Id,
                $"删除发送到 {target.Name}", () => SaveRoutes(chain, sendsChange: sends => sends.Where(s => s != target.Id))));
        RouteSends.Children.Add(RouteAdd("+ 添加发送", () => _ = AddSendAsync(chain)));

        RouteSidechains.Children.Clear();
        RouteSidechains.Children.Add(RouteLabel("侧链", NoteRouting.SidechainHint));
        foreach (var (target, plugin) in panel.Sidechains)
            RouteSidechains.Children.Add(RouteChip($"{target.Name} · {plugin}", $"送进“{target.Name}”上 {plugin} 的侧链。点击打开这条笔记", "GoodSoft", target.Id,
                $"删除侧链到 {target.Name} {plugin}", () => SaveSidechains(chain, list => list.Where(s => !(s.Target == target.Id && s.Plugin == plugin)))));
        RouteSidechains.Children.Add(RouteAdd("+ 添加侧链", () => _ = AddSidechainAsync(chain)));
        _loadingRoute = false;
    }

    private static TextBlock RouteLabel(string text, string? tip)
    {
        var label = new TextBlock { Text = text, Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), MinWidth = 36 };
        if (tip != null)
            ToolTip.SetTip(label, tip);
        return label;
    }

    private Button RouteAdd(string text, Action action)
    {
        var add = new Button { Content = text, Classes = { "Ghost" }, Padding = new Thickness(10, 0), Height = 28, Margin = new Thickness(0, 2) };
        add.Click += (_, _) => action();
        return add;
    }

    private Border RouteChip(string text, string tip, string background, long target, string removeName, Action remove)
    {
        var open = new Button { Content = text, Classes = { "Ghost" }, Padding = new Thickness(8, 0, 4, 0), Height = 26 };
        ToolTip.SetTip(open, tip);
        open.Click += (_, _) => Reload(selectChain: target);
        var close = new Button { Classes = { "Ghost" }, Width = 22, Height = 22, Padding = new Thickness(0), Content = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 11, Height = 11 } };
        AutomationProperties.SetName(close, removeName);
        close.Click += (_, _) => remove();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(open);
        panel.Children.Add(close);
        var chip = new Border { CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 2, 6, 2), Child = panel, BorderThickness = new Thickness(1) };
        chip.Bind(Border.BackgroundProperty, this.GetResourceObservable(background));
        chip.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Line"));
        return chip;
    }

    /// <summary>路由区现在的发送和侧链（测试检查用）。</summary>
    public IReadOnlyList<string> RouteChips => RouteSends.Children.Concat(RouteSidechains.Children).OfType<Border>()
        .Select(b => ((b.Child as StackPanel)?.Children[0] as Button)?.Content as string ?? "").ToList();

    public string IncomingText => RouteIncoming.Text ?? "";

    public IReadOnlyList<string> OutputOptions => OutputBox.ItemsSource as IReadOnlyList<string> ?? [];

    public int OutputIndex
    {
        get => OutputBox.SelectedIndex;
        set => OutputBox.SelectedIndex = value;
    }

    public void SetRole(ChannelRole role) => RoleBox.SelectedIndex = NoteRouting.RoleOptions.ToList().FindIndex(o => o.Role == role);

    private void RoleChanged()
    {
        if (_loadingRoute || _shownChain is not { } chain || RoleBox.SelectedIndex < 0)
            return;
        var role = NoteRouting.RoleOptions[RoleBox.SelectedIndex].Role;
        if (role == chain.Role)
            return;
        Flush();
        _database.SetChainRole(chain.Id, role);
        RememberExpansion();
        Reload(selectChain: chain.Id);
        Status(NoteRouting.RoleChanged(chain, role));
    }

    private void OutputChanged()
    {
        if (_loadingRoute || _shownChain is not { } chain || OutputBox.Tag is not IReadOnlyList<(string Text, long? Id)> outputs || OutputBox.SelectedIndex < 0)
            return;
        SaveRoutes(chain, outputs[OutputBox.SelectedIndex].Id, changeOutput: true);
    }

    private void SaveRoutes(PluginChain chain, long? output = null, bool changeOutput = false, Func<IEnumerable<long>, IEnumerable<long>>? sendsChange = null)
    {
        NoteRouting.SaveRoutes(_database, chain.Id, _routes, output, changeOutput, sendsChange);
        _routes = _database.LoadRoutes();
        if (_shownChain?.Id == chain.Id)
            ShowRoutes(chain);
        Status(NoteRouting.Saved);
    }

    private void SaveSidechains(PluginChain chain, Func<IEnumerable<(long Target, string Plugin)>, IEnumerable<(long Target, string Plugin)>> change)
    {
        NoteRouting.SaveSidechains(_database, chain.Id, _routes, change);
        _routes = _database.LoadRoutes();
        if (_shownChain?.Id == chain.Id)
            ShowRoutes(chain);
        Status(NoteRouting.Saved);
    }

    public async Task AddSendAsync(PluginChain? chain = null)
    {
        chain ??= _shownChain;
        if (chain == null)
            return;
        var options = NoteRouting.SendOptions(chain, _chains, _routes);
        if (options.Count == 0)
        {
            Status(NoteRouting.NoSendTargets);
            return;
        }
        var index = await Dialogs.PickOne($"“{chain.Name}”发送到", NoteRouting.SendPrompt, options.Select(NoteRouting.Label).ToList(), "添加");
        if (index == null)
            return;
        var target = options[index.Value].Id;
        SaveRoutes(chain, sendsChange: sends => sends.Append(target));
    }

    public async Task AddSidechainAsync(PluginChain? chain = null)
    {
        chain ??= _shownChain;
        if (chain == null)
            return;
        var options = NoteRouting.SidechainOptions(chain, _chains);
        if (options.Count == 0)
        {
            Status(NoteRouting.NoSidechainTargets);
            return;
        }
        var index = await Dialogs.PickOne($"“{chain.Name}”的侧链送到", NoteRouting.SidechainPrompt, options.Select(NoteRouting.Label).ToList(), "下一步");
        if (index == null)
            return;
        var target = options[index.Value];
        var plugins = NoteRouting.SidechainPlugins(target);
        var pick = plugins.Count == 1 ? 0 : await Dialogs.PickOne($"“{target.Name}”上哪个插件接收侧链", "选接收侧链信号的插件。", plugins, "添加");
        if (pick == null)
            return;
        var plugin = plugins[pick.Value];
        SaveSidechains(chain, list => list.Append((target.Id, plugin)));
    }

    // ---------- 右边：可添加的插件 ----------

    private void ShowCandidates()
    {
        var hint = _selectedStep is { } step ? NoteListing.CandidateHintFor(step.Number, step.Name) : NoteListing.CandidateHint;
        CandidateHint.Text = hint;
        var search = CandidateSearch.Text ?? "";
        var items = NoteListing.Candidates(_effects, _usage, search).Select(c => new CandidateItem(c.Plugin, c.Usage)).ToList();
        CandidateLabel.Text = search.Trim().Length == 0 ? "按工程使用次数建议" : "搜索结果";
        CandidateList.ItemsSource = items;
        CandidatesEmpty.Text = search.Trim().Length == 0 ? "扫描工程后，这里会列出你用得最多的插件。也可以直接搜索" : "没有匹配的插件";
        CandidatesEmpty.IsVisible = items.Count == 0;
        CandidateList.IsEnabled = _shownChain != null;
    }

    public string CandidateHintText => CandidateHint.Text ?? "";

    public void SearchCandidates(string text) => CandidateSearch.Text = text;

    // ---------- 步骤和备选 ----------

    public void AddStep(string key)
    {
        if (_shownChain == null || _effects.FirstOrDefault(p => p.Key == key) is not { } plugin)
            return;
        _steps.Add(new StepItem(MakePick(plugin), []));
        CommitSteps();
    }

    public void AddAlternate(string key)
    {
        if (_shownChain == null || _effects.FirstOrDefault(p => p.Key == key) is not { } plugin)
            return;
        if (_selectedStep is not { } step)
        {
            Status("先在中间点选要加备选的那一步");
            return;
        }
        step.Alternates.Add(MakePick(plugin));
        step.IsExpanded = true;
        CommitSteps();
        Status($"已把 {plugin.Name} 加为第 {step.Number} 步的备选");
    }

    public void RemoveStep(StepItem step)
    {
        _steps.Remove(step);
        if (_selectedStep == step)
            _selectedStep = null;
        CommitSteps();
    }

    public void MoveStep(StepItem step, int offset)
    {
        var at = _steps.IndexOf(step);
        var target = at + offset;
        if (at < 0 || target < 0 || target >= _steps.Count)
            return;
        _steps.RemoveAt(at);
        _steps.Insert(target, step);
        CommitSteps();
    }

    private StepItem? StepOf(PickItem alternate) => _steps.FirstOrDefault(s => s.Alternates.Contains(alternate));

    public void Promote(PickItem alternate)
    {
        if (StepOf(alternate) is not { } step)
            return;
        step.Promote(alternate);
        CommitSteps();
        Status($"第 {step.Number} 步的首选改成了 {alternate.Name}");
    }

    public void MoveAlternate(PickItem alternate, int offset)
    {
        if (StepOf(alternate) is { } step && step.MoveAlternate(alternate, offset))
            CommitSteps();
    }

    public void RemoveAlternate(PickItem alternate)
    {
        if (StepOf(alternate) is not { } step)
            return;
        step.Alternates.Remove(alternate);
        CommitSteps();
    }

    /// <summary>展开或收起一步的备选。</summary>
    public void Expand(StepItem step, bool expanded)
    {
        step.IsExpanded = expanded;
        BuildSteps();
    }

    private void SelectStep(StepItem? step)
    {
        _selectedStep = step;
        foreach (var (item, card) in _stepCards)
            card.Classes.Set("Selected", item == step);
        ShowCandidates();
    }

    private void BuildSteps()
    {
        for (var i = 0; i < _steps.Count; i++)
            _steps[i].Number = i + 1;
        _stepCards.Clear();
        var panel = new StackPanel();
        foreach (var step in _steps)
        {
            var card = StepCard(step);
            _stepCards[step] = card;
            panel.Children.Add(card);
        }
        StepList.ItemsSource = null;
        StepList.Items.Clear();
        StepList.Items.Add(panel);
        if (_selectedStep != null && !_steps.Contains(_selectedStep))
            _selectedStep = null;
        SelectStep(_selectedStep);
    }

    private Button IconButton(MaterialIconKind icon, string name, Action action, bool enabled = true)
    {
        var button = new Button
        {
            Classes = { "Ghost" }, Width = 28, Height = 28, Padding = new Thickness(0), IsEnabled = enabled,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new MaterialIcon { Kind = icon, Width = 14, Height = 14 },
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private TextBlock Badge(string text, string foreground, string? background, string? tip = null)
    {
        var block = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        block.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(foreground));
        if (tip != null)
            ToolTip.SetTip(block, tip);
        return block;
    }

    private Border Pill(string text, string foreground, string background, string? tip = null)
    {
        var border = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Badge(text, foreground, null, tip) };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable(background));
        return border;
    }

    /// <summary>一款插件的名称行：名称、状态、曾差评、加入心愿单。</summary>
    private Control PickHeader(PickItem pick, string prefix)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        if (prefix.Length > 0)
            row.Children.Add(new TextBlock { Text = prefix, Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var name = new TextBlock { Text = pick.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (pick.Plugin != null)
        {
            ToolTip.SetTip(name, "双击在插件库里查看");
            name.DoubleTapped += (_, _) => OpenPlugin?.Invoke(pick.Plugin);
        }
        row.Children.Add(name);
        if (pick.HasState)
            row.Children.Add(Pill(pick.StateText, pick.IsWarning ? "Danger" : "Muted", pick.IsWarning ? "DangerSoft" : "Line"));
        if (pick.HasRemembered)
        {
            var remembered = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Badge("曾差评", "Danger", null, pick.RememberedText) };
            remembered.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Danger"));
            row.Children.Add(remembered);
        }
        if (pick.CanWish)
        {
            var wish = new Button { Content = "加入心愿单", Classes = { "Ghost" }, Padding = new Thickness(8, 0), Height = 24, FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
            wish.Click += (_, _) => _ = AddToWishlist([pick]);
            row.Children.Add(wish);
        }
        if (pick.ShowWishText)
            row.Children.Add(Pill(pick.WishText, "Accent", "AccentSoft"));
        return row;
    }

    /// <summary>名称行右边放按钮，下面的说明和输入框占满整行。</summary>
    private static DockPanel WithTools(Control header, Control tools)
    {
        var row = new DockPanel();
        DockPanel.SetDock(tools, Dock.Right);
        row.Children.Add(tools);
        row.Children.Add(header);
        return row;
    }

    private TextBox NoteBox(PickItem pick, string placeholder)
    {
        var box = new TextBox { Text = pick.Note, PlaceholderText = placeholder, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
        AutomationProperties.SetName(box, placeholder);
        box.TextChanged += (_, _) => pick.Note = box.Text ?? "";
        return box;
    }

    private Border StepCard(StepItem step)
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var alternates = new Button { Content = step.AlternatesText, Classes = { "Ghost" }, Padding = new Thickness(8, 0), Height = 28, FontSize = 12 };
        ToolTip.SetTip(alternates, "展开或收起备选插件");
        alternates.Click += (_, _) =>
        {
            step.IsExpanded = !step.IsExpanded;
            SelectStep(step);
            BuildSteps();
        };
        tools.Children.Add(alternates);
        var at = _steps.IndexOf(step);
        tools.Children.Add(IconButton(MaterialIconKind.ArrowUp, "上移", () => MoveStep(step, -1), at > 0));
        tools.Children.Add(IconButton(MaterialIconKind.ArrowDown, "下移", () => MoveStep(step, 1), at < _steps.Count - 1));
        tools.Children.Add(IconButton(MaterialIconKind.Close, "删除这一步（连同备选）", () => RemoveStep(step)));

        var number = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        number.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentSoft"));
        var numberText = new TextBlock { Text = step.Number.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, FontSize = 12 };
        numberText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Accent"));
        number.Child = numberText;

        var body = new StackPanel();
        body.Children.Add(WithTools(PickHeader(step.Primary, ""), tools));
        body.Children.Add(new TextBlock { Text = step.Primary.Detail, Classes = { "SmallNote" }, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(NoteBox(step.Primary, "应用思路：这一步为什么用它、怎么设置，选填"));

        if (step.IsExpanded)
        {
            var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            list.Children.Add(new TextBlock { Text = step.AlternatesHint, Classes = { "SmallNote" }, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var alternate in step.Alternates)
            {
                var index = step.Alternates.IndexOf(alternate);
                var altTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                var promote = new Button { Content = "设为首选", Classes = { "Ghost" }, Padding = new Thickness(8, 0), Height = 26, FontSize = 12 };
                promote.Click += (_, _) => Promote(alternate);
                altTools.Children.Add(promote);
                altTools.Children.Add(IconButton(MaterialIconKind.ArrowUp, "备选上移", () => MoveAlternate(alternate, -1), index > 0));
                altTools.Children.Add(IconButton(MaterialIconKind.ArrowDown, "备选下移", () => MoveAlternate(alternate, 1), index < step.Alternates.Count - 1));
                altTools.Children.Add(IconButton(MaterialIconKind.Close, "移除这个备选", () => RemoveAlternate(alternate)));
                var altBody = new StackPanel();
                altBody.Children.Add(WithTools(PickHeader(alternate, alternate.Label), altTools));
                altBody.Children.Add(new TextBlock { Text = alternate.Detail, Classes = { "SmallNote" }, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                altBody.Children.Add(NoteBox(alternate, "什么时候换用它，选填"));
                altBody.Margin = new Thickness(0, 4, 0, 8);
                var altCard = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Child = altBody };
                altCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Line"));
                list.Children.Add(altCard);
            }
            var collapse = new Button { Content = "收起备选 ▴", Classes = { "Ghost" }, Padding = new Thickness(8, 0), Height = 26, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
            collapse.Click += (_, _) =>
            {
                step.IsExpanded = false;
                BuildSteps();
            };
            list.Children.Add(collapse);
            body.Children.Add(list);
        }

        var row = new DockPanel();
        DockPanel.SetDock(number, Dock.Left);
        row.Children.Add(number);
        row.Children.Add(body);
        var card = new Border { Classes = { "StepCard" }, Child = row, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 8) };
        card.AddHandler(PointerPressedEvent, (_, _) => SelectStep(step), RoutingStrategies.Tunnel);
        return card;
    }

    // ---------- 从工程导入轨道 ----------

    /// <summary>先列出扫描过的工程（最近修改的在前），也可以浏览别的文件。</summary>
    private async Task<string?> PickProjectFile()
    {
        var recent = TrackImportPlan.RecentFiles(_database);
        if (recent.Count > 0)
        {
            var options = recent.Select(TrackImportPlan.FileLine).Prepend(TrackImportPlan.BrowseText).ToList();
            var index = await Dialogs.PickOne(TrackImportPlan.FilePickTitle, TrackImportPlan.FilePickPrompt, options, "下一步", selected: 1);
            if (index == null)
                return null;
            if (index > 0)
                return recent[index.Value - 1].Path;
        }
        return await Dialogs.OpenFile("选择 DAW 工程", "DAW 工程", TrackImportPlan.Extensions);
    }

    public async Task ImportTracks()
    {
        var path = await PickProjectFile();
        if (path == null)
            return;
        IReadOnlyList<ProjectTrack> tracks;
        _importButton.IsEnabled = false;
        Status($"正在读取“{Path.GetFileName(path)}”…");
        try
        {
            tracks = await Task.Run(() => ProjectParser.ParseTracks(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status($"读取失败：{error.Message}");
            return;
        }
        finally
        {
            _importButton.IsEnabled = true;
        }

        var plan = new TrackImportPlan(path, tracks, _database.LoadPlugins());
        if (plan.Candidates.Count == 0)
        {
            Status(plan.NothingFound);
            return;
        }
        var (picked, withDestinations) = await Dialogs.PickMany("选择要导入的轨道", plan.Prompt,
            plan.Candidates.Select(t => (plan.Line(t), false)).ToList(), "下一步",
            plan.HasRouting ? TrackImportPlan.RoutingOption : null, optionChecked: true);
        if (picked is not { Count: > 0 })
            return;

        var parent = TargetFolder;
        var folders = TrackImportPlan.FolderOptions(plan.ProjectName, parent, _folders);
        var choice = await Dialogs.PickOne("放到哪个文件夹", TrackImportPlan.FolderPrompt, folders.Select(o => o.Text).ToList(), "导入");
        if (choice == null)
            return;

        try
        {
            Flush();
            RememberExpansion();
            var folderId = TrackImportPlan.ResolveFolder(_database, folders[choice.Value], plan.ProjectName, parent, _folders);
            var notes = plan.Notes(picked, withDestinations);
            var result = _database.AddImportedNotes(folderId, notes);
            var created = result.Created;
            if (folderId is { } id)
                _collapsed.Remove(id);
            TreeSearch.Text = "";
            var single = created.Count == 1 && result.Reused.Count == 0;
            Reload(selectChain: single ? created[0].Id : null, selectFolder: single ? null : folderId);
            CountsChanged?.Invoke();
            Status(plan.Result(result, notes, FolderPath(folderId)));
        }
        catch (ArgumentException error)
        {
            Status(error.Message);
        }
    }
}
