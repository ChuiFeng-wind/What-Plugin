using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>主窗口：左侧导航加右侧页面。各页面在后续阶段从 Windows 版逐个移植过来。</summary>
public partial class MainWindow : Window, IScanHost
{
    /// <summary>一个导航项。Key 和 Windows 版一致，保存的导航顺序两边通用。</summary>
    public sealed record NavDefinition(string Key, string Label, MaterialIconKind Icon, string Subtitle, int Stage);

    /// <summary>默认顺序，和 Windows 版的左侧导航一样。“健康检查”上面有一条分隔线。</summary>
    public static readonly IReadOnlyList<NavDefinition> Pages =
    [
        new("Plugins", "插件库", MaterialIconKind.Bookshelf, "按功能、厂家和格式整理已安装的插件", 2),
        new("Blacklist", "垃圾桶", MaterialIconKind.DeleteOutline, "不想在插件库里看到的插件。文件不会被移动或卸载", 2),
        new("Chains", "混音笔记", MaterialIconKind.LinkVariant, "按混音师、乐器分文件夹，记下每条轨道用了哪些插件、什么顺序、为什么这么用。这里只是记录，不会写入 DAW", 5),
        new("Accounts", "已购账号", MaterialIconKind.KeyOutline, "买过的插件和厂家账号，密码加密保存在本机", 5),
        new("Wishes", "心愿单", MaterialIconKind.HeartOutline, "想买的插件和打折提醒", 5),
        new("Health", "健康检查", MaterialIconKind.HeartPulse, "扫描失败、重复安装和 DAW 屏蔽的插件", 3),
        new("Idle", "闲置报告", MaterialIconKind.ClockOutline, "已安装但长期没进工程的效果器，只统计已扫描的工程", 4),
        new("Projects", "工程", MaterialIconKind.FolderOutline, "支持 Cubase、Nuendo、Studio One、REAPER、Ableton Live。只读取工程文件，不修改工程，也不加载插件", 4),
    ];

    public static readonly NavDefinition SettingsPage =
        new("Settings", "设置", MaterialIconKind.CogOutline, "外观和插件扫描。扫描不会打开插件界面，也不会改动插件文件。", 1);

    private readonly PluginDatabase _database;
    private readonly string _databasePath;
    private readonly ScanEnvironment _environment;
    private CancellationTokenSource? _scanCancel;
    private IReadOnlyList<HostBlacklistEntry>? _hostBlacklist;
    private ProjectReportResult? _projectReport;
    private readonly Dictionary<string, RadioButton> _navItems = [];
    private readonly Dictionary<string, TextBlock> _navCounts = [];
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Border _navDivider;

    public string CurrentPage { get; private set; } = "";

    // 设计器需要无参构造函数。
    public MainWindow() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db")), "") { }

    /// <param name="environment">扫描器、插件目录和 DAW 记录在哪；不给就用这台电脑上的。测试给临时目录。</param>
    public MainWindow(PluginDatabase database, string databasePath, ScanEnvironment? environment = null)
    {
        _database = database;
        _databasePath = databasePath;
        _environment = environment ?? ScanEnvironment.ThisComputer(Path.Combine(AppContext.BaseDirectory, AppPlatform.Executable("WhatPluginScanner")));
        InitializeComponent();

        ThemeManager.Apply(_database.GetSetting(ThemeManager.Setting));
        PublishLookupTheme();
        SetupChrome();

        _navDivider = new Border { Height = 1, Margin = new(6, 10), IsHitTestVisible = false };
        _navDivider.Bind(Border.BackgroundProperty, this.GetResourceObservable("Line"));
        foreach (var page in Pages)
            _navItems[page.Key] = CreateNavItem(page);
        NavFixed.Children.Add(_navItems[SettingsPage.Key] = CreateNavItem(SettingsPage));
        ApplyNavOrder();
        RefreshCounts();

        RefreshHealth();
        RefreshProjects();

        Navigate("Plugins");
        Closing += (_, _) =>
        {
            _scanCancel?.Cancel();
            if (Projects is { IsScanning: true } projects)
                projects.CancelScan();
            Library(CurrentPage)?.SaveCurrent();
        };
    }

    /// <summary>状态栏上的文字（测试检查用）。</summary>
    public string Status => StatusText.Text ?? "";

    public void ShowStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// 窗口外框：内容延伸到标题栏，和 Windows 版一样没有单独的标题栏。
    /// Mac 上左上角留出系统的红黄绿按钮；顶部空白处可以拖动窗口。
    /// </summary>
    private void SetupChrome()
    {
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 38;
        if (OperatingSystem.IsMacOS())
            Brand.Margin = new(6, 40, 0, 22);

        foreach (var strip in new Control[] { TitleStrip, Brand })
            strip.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
    }

    private RadioButton CreateNavItem(NavDefinition page)
    {
        var count = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        count.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Muted"));
        _navCounts[page.Key] = count;

        var content = new DockPanel();
        DockPanel.SetDock(count, Dock.Right);
        content.Children.Add(count);
        var icon = new MaterialIcon { Kind = page.Icon, Width = 17, Height = 17, Margin = new(0, 0, 12, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = page.Label, VerticalAlignment = VerticalAlignment.Center });

        var item = new RadioButton { Content = content, Tag = page.Key, Classes = { "Nav" } };
        AutomationProperties.SetName(item, page.Label);
        item.IsCheckedChanged += (_, _) =>
        {
            if (item.IsChecked == true)
                Navigate(page.Key);
        };
        item.ContextMenu = page == SettingsPage ? null : NavMenu(page.Key);
        return item;
    }

    // ---------- 导航顺序（和 Windows 版共用保存格式） ----------

    private IReadOnlyList<string> DefaultOrder => Pages.Select(p => p.Key).ToList();

    public List<string> NavKeys => NavList.Children.OfType<RadioButton>().Select(r => (string)r.Tag!).ToList();

    private void ApplyNavOrder() =>
        Arrange(NavOrder.Apply(DefaultOrder, NavOrder.Parse(_database.GetSetting(NavOrder.Setting))));

    private void Arrange(IReadOnlyList<string> order)
    {
        NavList.Children.Clear();
        foreach (var key in order)
        {
            // 分隔线不是导航项：始终在“健康检查”上面，它被移到最上面时不显示。
            if (key == "Health" && NavList.Children.Count > 0)
                NavList.Children.Add(_navDivider);
            NavList.Children.Add(_navItems[key]);
        }
    }

    private ContextMenu NavMenu(string key)
    {
        var up = new MenuItem { Header = "上移" };
        var down = new MenuItem { Header = "下移" };
        var reset = new MenuItem { Header = "恢复默认顺序" };
        up.Click += (_, _) => MoveNav(key, -1);
        down.Click += (_, _) => MoveNav(key, 1);
        reset.Click += (_, _) => SaveNavOrder(DefaultOrder.ToList());
        var menu = new ContextMenu { ItemsSource = new object[] { up, down, new Separator(), reset } };
        menu.Opening += (_, _) =>
        {
            var keys = NavKeys;
            up.IsEnabled = keys.IndexOf(key) > 0;
            down.IsEnabled = keys.IndexOf(key) < keys.Count - 1;
        };
        return menu;
    }

    public void MoveNav(string key, int delta)
    {
        if (NavOrder.Move(NavKeys, key, delta) is { } order)
            SaveNavOrder(order);
    }

    private void SaveNavOrder(List<string> order)
    {
        var saved = NavOrder.Parse(_database.GetSetting(NavOrder.Setting));
        _database.SetSetting(NavOrder.Setting, NavOrder.Serialize(NavOrder.Merge(saved, order)));
        Arrange(order);
        _navItems[CurrentPage].Focus();
    }

    // ---------- 页面 ----------

    public void Navigate(string key)
    {
        if (CurrentPage == key)
            return;
        // 离开插件库时把还在等待自动保存的内容写进去。
        Library(CurrentPage)?.SaveCurrent();
        CurrentPage = key;
        _navItems[key].IsChecked = true;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
            PageHost.Children.Add(page);
        }
        foreach (var other in _pages.Values)
            other.IsVisible = other == page;
        // 插件库和垃圾桶各是一页，在一页里移入或恢复后，另一页显示时要重新读。
        Library(key)?.Load();
        if (Settings is { } settings && key == SettingsPage.Key)
        {
            settings.Refresh();
            // 别的页面留下的状态（例如插件库的数量）在这里没意义；扫描进度要留着。
            if (!IsScanning)
                StatusText.Text = "";
        }
        if (Health is { } health && key == "Health")
        {
            RefreshHealth();
            StatusText.Text = health.Summary;
        }
        if (Projects is { } projectPage && key == "Projects" && !projectPage.IsScanning)
            StatusText.Text = projectPage.Status;
        if (Idle is { } idle && key == "Idle")
            StatusText.Text = idle.Summary;
    }

    public SettingsView? Settings => _pages.GetValueOrDefault(SettingsPage.Key) is PageFrame { Content: SettingsView view } ? view : null;

    public HealthView? Health => _pages.GetValueOrDefault("Health") is PageFrame { Content: HealthView view } ? view : null;

    public ProjectsView? Projects => _pages.GetValueOrDefault("Projects") is PageFrame { Content: ProjectsView view } ? view : null;

    public IdleView? Idle => _pages.GetValueOrDefault("Idle") is PageFrame { Content: IdleView view } ? view : null;

    /// <summary>插件库或垃圾桶页面；别的页面返回 null。</summary>
    public PluginLibraryView? Library(string key) =>
        _pages.GetValueOrDefault(key) is PageFrame { Content: PluginLibraryView view } ? view : null;

    private Control CreatePage(string key)
    {
        if (key == SettingsPage.Key)
        {
            var settings = new SettingsView(_database, _environment, this);
            settings.ThemeChosen += OnThemeChosen;
            return new PageFrame(SettingsPage.Label, SettingsPage.Subtitle, settings);
        }
        var definition = Pages.First(p => p.Key == key);
        if (key is "Plugins" or "Blacklist")
        {
            var library = new PluginLibraryView(_database, blacklist: key == "Blacklist");
            var frame = new PageFrame(definition.Label, definition.Subtitle, library);
            library.SubtitleChanged += text => frame.Subtitle = text;
            library.StatusChanged += text => StatusText.Text = text;
            // 移入或恢复会改变闲置报告。
            library.CountsChanged += () =>
            {
                RefreshCounts();
                RefreshProjects();
            };
            return frame;
        }
        if (key == "Health")
        {
            var health = new HealthView();
            var frame = new PageFrame(definition.Label, definition.Subtitle, health);
            health.SubtitleChanged += text => frame.Subtitle = text;
            health.StatusChanged += text => StatusText.Text = text;
            health.OpenPlugin += ShowPlugin;
            return frame;
        }
        if (key == "Projects")
        {
            var projects = new ProjectsView(_database);
            var frame = new PageFrame(definition.Label, definition.Subtitle, projects, projects.Actions);
            projects.StatusChanged += text => StatusText.Text = text;
            projects.ScanningChanged += scanning =>
            {
                ScanProgress.IsVisible = scanning || IsScanning;
                ScanProgress.Value = 0;
            };
            projects.ProgressChanged += (done, total) =>
            {
                ScanProgress.Maximum = Math.Max(total, 1);
                ScanProgress.Value = done;
            };
            projects.ReportChanged += RefreshProjects;
            projects.Show(ProjectReport);
            return frame;
        }
        if (key == "Idle")
        {
            var idle = new IdleView(_database);
            var frame = new PageFrame(definition.Label, IdleView.Subtitle, idle);
            idle.StatusChanged += text => StatusText.Text = text;
            idle.OpenPlugin += ShowPlugin;
            idle.RowsChanged += () => SetIdleCount(idle.AllRows.Count);
            idle.Trashed += () =>
            {
                RefreshCounts();
                RefreshProjects();
                RefreshHealth();
            };
            idle.Show(ProjectReport);
            return frame;
        }
        return new PageFrame(definition.Label, definition.Subtitle, new PendingView(definition.Stage));
    }

    /// <summary>在插件库（或垃圾桶）里打开一款插件。</summary>
    public void ShowPlugin(PluginRow plugin)
    {
        var key = plugin.User.Hidden ? "Blacklist" : "Plugins";
        Navigate(key);
        Library(key)?.ShowPlugin(plugin.Key);
    }

    // ---------- 健康检查 ----------

    /// <summary>安装情况变化后都要重算，侧边栏的数字也靠它。DAW 的屏蔽列表只在扫描后重读。</summary>
    public void RefreshHealth()
    {
        _hostBlacklist ??= _environment.Blacklist();
        var rows = _database.LoadEffects();
        var report = PluginHealth.Build(rows, _hostBlacklist);
        _navCounts["Health"].Text = report.NeedsAttention > 0 ? report.NeedsAttention.ToString() : "";
        Health?.Show(report, UpdateGuide.Build(rows));
    }

    // ---------- 工程和闲置报告 ----------

    private ProjectReportResult ProjectReport => _projectReport ??= ProjectListing.Load(_database);

    /// <summary>工程、垃圾桶或安装情况变化后都要重算：缺失和闲置都依赖它们，侧边栏的数字也是。</summary>
    public void RefreshProjects()
    {
        _projectReport = ProjectListing.Load(_database);
        _navCounts["Projects"].Text = _projectReport.Projects.Count > 0 ? _projectReport.Projects.Count.ToString() : "";
        Projects?.Show(_projectReport);
        if (Idle is { } idle)
            idle.Show(_projectReport);
        else
            SetIdleCount(Core.ProjectReport.FindIdle(_database.LoadEffects(), _projectReport.Usage, IdleListing.DefaultMonths, DateTime.UtcNow).Count);
    }

    /// <summary>没扫描过工程时不显示闲置的数量（那时所有插件都算“从未出现”）。</summary>
    private void SetIdleCount(int count) =>
        _navCounts["Idle"].Text = _projectReport is { Projects.Count: > 0 } && count > 0 ? count.ToString() : "";

    // ---------- 扫描 ----------

    public bool IsScanning => _scanCancel != null;

    public async Task RunScanAsync(ScanRequest request)
    {
        if (_scanCancel != null)
            return;
        Library(CurrentPage)?.SaveCurrent();
        _scanCancel = new CancellationTokenSource();
        SetScanning(true);
        var progress = new Progress<ScanProgress>(p =>
        {
            if (_scanCancel == null)
                return;
            ScanProgress.Maximum = Math.Max(p.Total, 1);
            ScanProgress.Value = p.Done;
            StatusText.Text = $"正在扫描 {p.Done}/{p.Total}：{Path.GetFileName(p.Current)}";
        });

        ScanOutcome outcome;
        try
        {
            // 扫描期间可能还在编辑插件详情，重新读取前先保存。
            outcome = await ScanWorkflow.RunAsync(_database, _environment, request, progress, _scanCancel.Token,
                () => Library(CurrentPage)?.SaveCurrent());
        }
        finally
        {
            _scanCancel.Dispose();
            _scanCancel = null;
            SetScanning(false);
        }

        if (outcome.Summary != null)
        {
            _hostBlacklist = null;
            Library("Plugins")?.Load();
            Library("Blacklist")?.Load();
            RefreshCounts();
            RefreshHealth();
            RefreshProjects();
        }
        Settings?.Refresh();
        StatusText.Text = outcome.Message;
    }

    public void CancelScan() => _scanCancel?.Cancel();

    private void SetScanning(bool scanning)
    {
        ScanProgress.IsVisible = scanning || Projects is { IsScanning: true };
        ScanProgress.Value = 0;
        ImportButton.IsEnabled = !scanning;
        Settings?.SetScanning(scanning);
    }

    private void OnThemeChosen(string id)
    {
        if (id == ThemeManager.Current.Id)
            return;
        ThemeManager.Apply(id);
        _database.SetSetting(ThemeManager.Setting, id);
        PublishLookupTheme();
        StatusText.Text = "已切换到“" + ThemeManager.Current.Name + "”主题";
    }

    /// <summary>DAW 里的查询窗口打开时直接用这份配色（只在用默认数据库时写，测试库不影响它）。</summary>
    private void PublishLookupTheme() =>
        LookupExport.PublishTheme(_databasePath, PluginDatabase.DefaultPath, ThemeManager.Current);

    /// <summary>侧边栏上的数字（测试检查用）。</summary>
    public string NavCount(string key) => _navCounts[key].Text ?? "";

    public void RefreshCounts()
    {
        var counts = NavCounts.Load(_database);
        _navCounts["Plugins"].Text = counts.Plugins.ToString();
        _navCounts["Blacklist"].Text = NavCounts.Label(counts.Blacklisted);
        _navCounts["Chains"].Text = NavCounts.Label(counts.Chains);
        _navCounts["Wishes"].Text = NavCounts.Label(counts.Wishes);
        _navCounts["Accounts"].Text = NavCounts.Label(counts.Purchases);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Alt+上下键调整当前导航项的位置，和 Windows 版一样。
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down
            && e.Source is RadioButton { Tag: string key } && key != SettingsPage.Key)
        {
            MoveNav(key, e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
