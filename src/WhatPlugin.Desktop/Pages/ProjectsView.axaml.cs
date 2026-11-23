using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Material.Icons;
using Material.Icons.Avalonia;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 工程页：添加存放工程的文件夹，读出每个工程用了哪些插件、缺了哪些。
/// 工程文件只读，这里没有任何改动工程或插件文件的操作。
/// </summary>
public partial class ProjectsView : UserControl
{
    private const double NarrowWidth = 560;

    private readonly PluginDatabase _database;
    private readonly Button _cancelButton;
    private readonly Button _addFolderButton;
    private readonly Button _scanButton;
    private ProjectReportResult _report = new([], [], new Dictionary<string, PluginUsage>());
    private List<ProjectListRow> _projects = [];
    private IReadOnlyList<MissingListRow> _missing = [];
    private PluginNameMatcher? _remembered;
    private CancellationTokenSource? _scanCancel;
    private string? _folderFilter;
    private HashSet<string>? _collapsed;
    private bool? _treeVisible;
    private bool _rebuildingTree;
    private bool _narrow;

    public event Action<string>? StatusChanged;

    /// <summary>读取工程开始或结束：主窗口显示进度条。</summary>
    public event Action<bool>? ScanningChanged;

    public event Action<int, int>? ProgressChanged;

    /// <summary>工程或忽略的名称变了，缺失、闲置和插件的使用次数都要重算。由主窗口重新读取后调用 Show。</summary>
    public event Action? ReportChanged;

    /// <summary>标题右边的按钮：停止、添加文件夹、扫描工程。</summary>
    public Control Actions { get; }

    /// <summary>定位文件、打开文件夹、选文件夹。测试换成记录下来的函数，不真的打开访达或对话框。</summary>
    public Func<string, string?> Reveal { get; set; } = SystemShell.Reveal;

    public Func<string, string?> OpenFolder { get; set; } = SystemShell.OpenFolder;

    public Func<Task<IReadOnlyList<string>>>? PickFolders { get; set; }

    // 设计器需要无参构造函数。
    public ProjectsView() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db"))) { }

    public ProjectsView(PluginDatabase database)
    {
        _database = database;
        InitializeComponent();
        PickFolders = PickWithDialog;

        _cancelButton = new Button { Content = "停止", IsVisible = false, Margin = new Thickness(0, 0, 8, 0) };
        _addFolderButton = new Button { Content = "添加文件夹", Classes = { "Ghost" }, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(_addFolderButton, "选择存放工程的文件夹，子文件夹也会一起读取");
        var scanContent = new StackPanel { Orientation = Orientation.Horizontal };
        scanContent.Children.Add(new MaterialIcon { Kind = MaterialIconKind.Refresh, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        scanContent.Children.Add(new TextBlock { Text = "扫描工程", VerticalAlignment = VerticalAlignment.Center });
        _scanButton = new Button { Content = scanContent, Classes = { "Primary" } };
        ToolTip.SetTip(_scanButton, "只读取新增或有变化的工程");
        Avalonia.Automation.AutomationProperties.SetName(_scanButton, "扫描工程");
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_cancelButton);
        actions.Children.Add(_addFolderButton);
        actions.Children.Add(_scanButton);
        Actions = actions;

        _cancelButton.Click += (_, _) => CancelScan();
        _addFolderButton.Click += async (_, _) =>
        {
            if (PickFolders != null && await PickFolders() is { Count: > 0 } folders)
                await AddFolders(folders);
        };
        _scanButton.Click += (_, _) => _ = RunScanAsync();
        FolderList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Control { Classes: var classes } button && classes.Contains("RemoveFolder") && button.Tag is string folder)
                _ = RemoveFolder(folder);
        });
        DetailLines.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Control { Classes: var classes } button && classes.Contains("IgnoreLine") && button.Tag is string name)
                Ignore(name);
        });

        SearchBox.TextChanged += (_, _) => FilterChanged();
        foreach (var segment in new[] { SegmentAll, SegmentMissing, SegmentSummary })
            segment.IsCheckedChanged += (_, _) =>
            {
                if (segment.IsChecked == true)
                    FilterChanged();
            };
        TreeToggle.Click += (_, _) => ToggleTree();
        ClearIgnoredButton.Click += (_, _) =>
        {
            _database.ClearIgnoredProjectNames();
            ReportChanged?.Invoke();
            StatusChanged?.Invoke(ProjectListing.IgnoredCleared);
        };
        FolderTree.SelectionChanged += (_, _) => FolderSelected();
        ProjectList.SelectionChanged += (_, _) => ShowDetail();
        MissingList.SelectionChanged += (_, _) => ShowDetail();
        RevealButton.Content = OperatingSystem.IsMacOS() ? "在访达中显示" : "在文件夹中显示";
        RevealButton.Click += (_, _) => RevealSelected();
        IgnoreMissingButton.Click += (_, _) =>
        {
            if (MissingList.SelectedItem is MissingListRow row)
                Ignore(row.Name);
        };
        ProjectList.SizeChanged += (_, e) => SetNarrow(e.NewSize.Width < NarrowWidth);
        ProjectList.ItemTemplate = RowTemplate();
        FolderTree.ContainerPrepared += (_, args) =>
        {
            if (args.Container is TreeViewItem { DataContext: ProjectFolderItem { Path: { } path } } item)
            {
                var open = new MenuItem { Header = "打开所在文件夹" };
                open.Click += (_, _) => OpenTreeFolder(path);
                item.ContextMenu = new ContextMenu { ItemsSource = new[] { open } };
            }
        };
        LoadFolders();
    }

    // ---------- 给主窗口和测试用 ----------

    public IReadOnlyList<string> Folders => FolderList.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>列表里现在显示的工程（经过搜索、筛选和文件夹）。</summary>
    public IReadOnlyList<ProjectListRow> Rows => ProjectList.ItemsSource as IReadOnlyList<ProjectListRow> ?? [];

    public IReadOnlyList<MissingListRow> MissingRows => MissingList.ItemsSource as IReadOnlyList<MissingListRow> ?? [];

    public ProjectListRow? SelectedProject
    {
        get => ProjectList.SelectedItem as ProjectListRow;
        set => ProjectList.SelectedItem = value;
    }

    public MissingListRow? SelectedMissing
    {
        get => MissingList.SelectedItem as MissingListRow;
        set => MissingList.SelectedItem = value;
    }

    public IReadOnlyList<ProjectFolderItem> TreeItems => FolderTree.ItemsSource as IReadOnlyList<ProjectFolderItem> ?? [];

    public ProjectFolderItem? SelectedFolder
    {
        get => FolderTree.SelectedItem as ProjectFolderItem;
        set => FolderTree.SelectedItem = value;
    }

    public bool IsScanning => _scanCancel != null;

    public string Status => ProjectListing.Status(_projects.Count, _projects.Count(p => p.MissingCount > 0), _missing.Count);

    // ---------- 工程文件夹和扫描 ----------

    private async Task<IReadOnlyList<string>> PickWithDialog()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return [];
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择存放工程的文件夹", AllowMultiple = true });
        return picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    private void LoadFolders()
    {
        var folders = _database.LoadProjectFolders();
        FolderList.ItemsSource = folders;
        FolderList.IsVisible = folders.Count > 0;
        _scanButton.IsEnabled = folders.Count > 0 && _scanCancel == null;
    }

    /// <summary>添加文件夹后马上读取。</summary>
    public Task AddFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
            _database.AddProjectFolder(folder);
        LoadFolders();
        return RunScanAsync();
    }

    /// <summary>只从软件里忘掉这个文件夹的统计，工程文件不动。</summary>
    public Task RemoveFolder(string folder)
    {
        if (_scanCancel != null)
            return Task.CompletedTask;
        _database.RemoveProjectFolder(folder);
        LoadFolders();
        return RunScanAsync();
    }

    public void CancelScan()
    {
        _scanCancel?.Cancel();
        _cancelButton.IsEnabled = false;
        StatusChanged?.Invoke("正在停止…");
    }

    public async Task RunScanAsync()
    {
        if (_scanCancel != null)
            return;
        _scanCancel = new CancellationTokenSource();
        SetScanning(true);
        var progress = new Progress<ProjectScanProgress>(p =>
        {
            if (_scanCancel == null)
                return;
            ProgressChanged?.Invoke(p.Done, p.Total);
            if (p.Current != null)
                StatusChanged?.Invoke($"正在读取工程 {p.Done}/{p.Total}：{Path.GetFileName(p.Current)}");
        });

        string message;
        try
        {
            var token = _scanCancel.Token;
            var summary = await Task.Run(() => ProjectScanner.Scan(_database, progress, token), CancellationToken.None);
            ReportChanged?.Invoke();
            message = ProjectListing.DescribeScan(summary, _projects.Count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            message = "读取工程出错：" + error.Message;
        }
        finally
        {
            _scanCancel.Dispose();
            _scanCancel = null;
            SetScanning(false);
        }
        StatusChanged?.Invoke(message);
    }

    private void SetScanning(bool scanning)
    {
        _cancelButton.IsVisible = scanning;
        _cancelButton.IsEnabled = scanning;
        _addFolderButton.IsEnabled = !scanning;
        FolderList.IsEnabled = !scanning;
        _scanButton.IsEnabled = !scanning && _database.LoadProjectFolders().Count > 0;
        ScanningChanged?.Invoke(scanning);
    }

    // ---------- 列表 ----------

    /// <summary>工程、垃圾桶或安装情况变了以后由主窗口调用。保持选中的工程。</summary>
    public void Show(ProjectReportResult report)
    {
        var selectedProject = SelectedProject?.Entry.Key;
        var selectedMissing = SelectedMissing?.Name;
        _report = report;
        _projects = report.Projects.Select(p => new ProjectListRow(p)).ToList();
        _remembered = new PluginNameMatcher(_database.LoadEffects());
        _missing = ProjectListing.MissingRows(report, _remembered);

        SegmentMissing.Content = $"有缺失（{_projects.Count(p => p.MissingCount > 0)}）";
        SegmentSummary.Content = $"缺失汇总（{_missing.Count}）";
        ClearIgnoredButton.IsVisible = _database.LoadIgnoredProjectNames().Count > 0;
        LoadFolders();
        Refilter(selectedProject, selectedMissing);
    }

    private bool ShowingSummary => SegmentSummary.IsChecked == true;

    private bool MatchesFilters(ProjectListRow row) =>
        (SegmentMissing.IsChecked != true || row.MissingCount > 0) && ProjectListing.Matches(SearchBox.Text ?? "", row.SearchText);

    private void FilterChanged() => Refilter(SelectedProject?.Entry.Key, SelectedMissing?.Name);

    private void Refilter(string? selectedProject, string? selectedMissing)
    {
        RebuildTree();
        var rows = _projects.Where(p => MatchesFilters(p) && (_folderFilter == null || ProjectFolderTree.Contains(_folderFilter, p.Folder))).ToList();
        ProjectList.ItemsSource = rows;
        ProjectList.SelectedItem = rows.FirstOrDefault(p => p.Entry.Key == selectedProject);
        var missing = _missing.Where(m => ProjectListing.Matches(SearchBox.Text ?? "", m.SearchText)).ToList();
        MissingList.ItemsSource = missing;
        MissingList.SelectedItem = missing.FirstOrDefault(m => m.Name == selectedMissing);

        var summary = ShowingSummary;
        ProjectList.IsVisible = ProjectHeader.IsVisible = !summary;
        MissingList.IsVisible = MissingHeader.IsVisible = summary;
        var shown = summary ? missing.Count : rows.Count;
        EmptyPanel.IsVisible = shown == 0;
        EmptyText.Text = ProjectListing.EmptyText(_database.LoadProjectFolders().Count == 0, _projects.Count, summary,
            _missing.Count, SegmentMissing.IsChecked == true, _projects.Any(p => p.MissingCount > 0), _folderFilter != null);
        ShowDetail();
    }

    /// <summary>窗口较窄时先收起宿主、版本、效果器三列，保证工程名看得见。</summary>
    private void SetNarrow(bool narrow)
    {
        if (narrow == _narrow)
            return;
        _narrow = narrow;
        ProjectHeader.ColumnDefinitions = ColumnDefinitions.Parse(Columns);
        HostHeader.IsVisible = VersionsHeader.IsVisible = EffectsHeader.IsVisible = !narrow;
        ProjectList.ItemTemplate = RowTemplate();
    }

    private string Columns => _narrow ? "*,0,96,0,0,68" : "2.4*,76,96,44,56,68";

    private FuncDataTemplate<ProjectListRow> RowTemplate() => new((row, _) =>
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(Columns), Height = 52, Background = Brushes.Transparent };
        if (row == null)
            return grid;
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) };
        name.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        var folder = new TextBlock { Text = row.Folder, FontSize = 12, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        folder.Bind(TextBlock.ForegroundProperty, folder.GetResourceObservable("Muted"));
        ToolTip.SetTip(folder, row.Folder);
        name.Children.Add(folder);
        grid.Children.Add(name);
        if (!_narrow)
        {
            AddCell(grid, 1, row.HostText);
            AddCell(grid, 3, row.Versions.ToString());
            AddCell(grid, 4, row.EffectCount.ToString());
        }
        AddCell(grid, 2, row.ModifiedText);
        var missing = AddCell(grid, 5, row.MissingText);
        missing.Bind(TextBlock.ForegroundProperty, missing.GetResourceObservable(row.Warn ? "Danger" : "Good"));

        var reveal = new MenuItem { Header = "打开所在文件夹" };
        reveal.Click += (_, _) => RevealProject(row.Entry.Path);
        grid.ContextMenu = new ContextMenu { ItemsSource = new[] { reveal } };
        return grid;
    });

    private static TextBlock AddCell(Grid grid, int column, string text)
    {
        var cell = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 6, 0) };
        cell.Bind(TextBlock.ForegroundProperty, cell.GetResourceObservable("TextDim"));
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
        return cell;
    }

    // ---------- 详情 ----------

    private void ShowDetail()
    {
        var project = ShowingSummary ? null : SelectedProject;
        var missing = ShowingSummary ? SelectedMissing : null;
        DetailEmpty.IsVisible = project == null && missing == null;
        ProjectDetail.IsVisible = project != null;
        MissingDetail.IsVisible = missing != null;

        if (project != null)
        {
            var detail = ProjectListing.Detail(project.Entry, _remembered ??= new PluginNameMatcher(_database.LoadEffects()));
            DetailTitle.Text = detail.Title;
            DetailMeta.Text = detail.Meta;
            DetailPath.Text = detail.Path;
            DetailError.IsVisible = detail.Error != null;
            DetailError.Text = detail.Error ?? "";
            DetailLines.ItemsSource = detail.Lines;
            DetailNotes.Text = detail.Notes;
            DetailNotes.IsVisible = detail.Notes.Length > 0;
        }
        if (missing != null)
        {
            MissingTitle.Text = missing.Name;
            MissingMeta.Text = ProjectListing.MissingMeta(missing);
            MissingProjects.ItemsSource = ProjectListing.MissingProjects(missing);
        }
    }

    public IReadOnlyList<ProjectLineRow> DetailRows => DetailLines.ItemsSource as IReadOnlyList<ProjectLineRow> ?? [];

    public string DetailNoteText => DetailNotes.IsVisible ? DetailNotes.Text ?? "" : "";

    private void RevealSelected()
    {
        if (SelectedProject is { } project)
            RevealProject(project.Entry.Path);
    }

    private void RevealProject(string path)
    {
        if (SystemShell.RevealProject(path, Reveal, OpenFolder) is { } message)
            StatusChanged?.Invoke(message);
    }

    /// <summary>把一个名称设为不算缺失（例如宿主自带的插件）。</summary>
    public void Ignore(string name)
    {
        _database.IgnoreProjectName(name);
        ReportChanged?.Invoke();
        StatusChanged?.Invoke(ProjectListing.Ignored(name));
    }

    // ---------- 左侧文件夹树 ----------

    private bool TreeVisible => _treeVisible ??= _database.GetSetting(ProjectFolderTree.VisibleSetting) != "0";

    private HashSet<string> Collapsed => _collapsed ??= LoadCollapsed();

    private HashSet<string> LoadCollapsed()
    {
        try
        {
            var saved = _database.GetSetting(ProjectFolderTree.CollapsedSetting);
            var paths = saved == null ? null : JsonSerializer.Deserialize<string[]>(saved);
            return new HashSet<string>(paths ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>树上的数量跟随搜索和“有缺失”筛选，但不受当前选中的文件夹影响。</summary>
    private void RebuildTree()
    {
        var matched = _projects.Where(MatchesFilters).ToList();
        var nodes = ProjectFolderTree.Build(_database.LoadProjectFolders(), matched.Select(p => p.Entry));
        var searching = (SearchBox.Text ?? "").Trim().Length > 0;

        _rebuildingTree = true;
        var all = new ProjectFolderItem(null, "全部工程", matched.Count, matched.Count(p => p.MissingCount > 0), [], true, OnToggled);
        var items = new List<ProjectFolderItem> { all };
        items.AddRange(nodes.Select(n => ToItem(n, searching)));
        FolderTree.ItemsSource = items;
        // 搜索后选中的文件夹里可能已经没有结果，这时回到全部工程，免得列表看起来是空的。
        var selected = _folderFilter == null ? null : Find(items, _folderFilter);
        if (selected == null)
            _folderFilter = null;
        FolderTree.SelectedItem = selected ?? all;
        _rebuildingTree = false;

        var hasProjects = _projects.Count > 0;
        TreeCard.IsVisible = hasProjects && TreeVisible && !ShowingSummary;
        TreeToggle.IsVisible = hasProjects && !ShowingSummary;
        TreeToggle.Content = TreeVisible ? "隐藏文件夹" : "显示文件夹";
    }

    private ProjectFolderItem ToItem(ProjectFolderNode node, bool forceExpanded) =>
        new(node.Path, node.Name, node.ProjectCount, node.MissingProjects,
            node.Children.Select(c => ToItem(c, forceExpanded)).ToList(),
            forceExpanded || !Collapsed.Contains(node.Path), OnToggled);

    /// <summary>找到并展开上级，否则选中的文件夹藏在折叠的层级里，看不出列表为什么变少了。</summary>
    private static ProjectFolderItem? Find(IEnumerable<ProjectFolderItem> items, string path)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                return item;
            if (Find(item.Children, path) is { } child)
            {
                item.IsExpanded = true;
                return child;
            }
        }
        return null;
    }

    private void OnToggled(ProjectFolderItem folder)
    {
        // 搜索时的展开是临时的，不能覆盖用户自己的折叠状态。
        if (_rebuildingTree || folder.Path == null || (SearchBox.Text ?? "").Trim().Length > 0)
            return;
        if (folder.IsExpanded)
            Collapsed.Remove(folder.Path);
        else
            Collapsed.Add(folder.Path);
        _database.SetSetting(ProjectFolderTree.CollapsedSetting, JsonSerializer.Serialize(Collapsed.Order(StringComparer.OrdinalIgnoreCase)));
    }

    private void FolderSelected()
    {
        if (_rebuildingTree || FolderTree.SelectedItem is not ProjectFolderItem folder || folder.Path == _folderFilter)
            return;
        _folderFilter = folder.Path;
        FilterChanged();
    }

    public void ToggleTree()
    {
        _treeVisible = !TreeVisible;
        _database.SetSetting(ProjectFolderTree.VisibleSetting, _treeVisible == true ? "1" : "0");
        // 隐藏后列表必须显示全部工程，否则用户看不到是什么在过滤。
        if (_treeVisible == false)
            _folderFilter = null;
        FilterChanged();
    }

    /// <summary>树上右键“打开所在文件夹”。</summary>
    internal void OpenTreeFolder(string path)
    {
        if (OpenFolder(path) is { } message)
            StatusChanged?.Invoke(message);
    }
}

/// <summary>文件夹树的一项。Path 为空表示“全部工程”。</summary>
public sealed class ProjectFolderItem(
    string? path,
    string name,
    int count,
    int missing,
    IReadOnlyList<ProjectFolderItem> children,
    bool expanded,
    Action<ProjectFolderItem> toggled) : INotifyPropertyChanged
{
    private bool _isExpanded = expanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Path { get; } = path;
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string MissingText { get; } = missing > 0 ? $"缺 {missing}" : "";
    public bool HasMissing { get; } = missing > 0;
    public string ToolTip { get; } = path ?? "所有已添加文件夹里的工程";
    public IReadOnlyList<ProjectFolderItem> Children { get; } = children;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            toggled(this);
        }
    }
}
