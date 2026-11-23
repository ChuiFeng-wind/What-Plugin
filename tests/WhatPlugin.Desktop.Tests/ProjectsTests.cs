using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>工程页、闲置报告和路由图。工程是临时目录里手写的 REAPER 工程，插件是假的 VST3 包。</summary>
public sealed class ProjectsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-proj-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;
    private readonly TestEnvironment _env;
    private readonly string _projects;

    public ProjectsTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        _database = new PluginDatabase(_dbPath);
        _env = new TestEnvironment(Path.Combine(_dir, "env"));
        _projects = Path.Combine(_dir, "Projects");
        Assert.StartsWith(Path.GetTempPath(), _projects, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // ---------- 准备数据 ----------

    private string AddProject(string folder, string name, DateTime? modified, params string[] plugins)
    {
        var path = Path.Combine(_projects, folder, name + ".rpp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fx = string.Concat(plugins.Select(p => $"        <VST \"VST3: {p}\" \"x.vst3\" 0 \"\" 1 \"\"\n        >\n"));
        File.WriteAllText(path, $"<REAPER_PROJECT 0.1 \"7.0\" 1700000000\n  <TRACK\n    <FXCHAIN\n{fx}    >\n  >\n>\n");
        File.SetLastWriteTimeUtc(path, modified ?? DateTime.UtcNow.AddDays(-3));
        return path;
    }

    /// <summary>四个工程分在两个客户文件夹里；soothe2 没装；Old Delay 只在两年半前的工程里用过，Unused Comp 从没用过。</summary>
    private void Seed()
    {
        _env.AddVst3("Pro-Q 4", "FabFilter");
        _env.AddVst3("ValhallaRoom", "Valhalla DSP", "Fx|Reverb");
        _env.AddVst3("Unused Comp", "Acme", "Fx|Dynamics");
        _env.AddVst3("Old Delay", "Acme", "Fx|Delay");
        var scan = Task.Run(() => ScanWorkflow.RunAsync(_database, _env.Scan, new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None)).GetAwaiter().GetResult();
        Assert.NotNull(scan.Summary);

        AddProject("Client A", "Song A", null, "Pro-Q 4 (FabFilter)", "soothe2 (oeksound)");
        AddProject("Client A", "Song B", null, "ValhallaRoom (Valhalla DSP)", "soothe2 (oeksound)");
        AddProject("Client B", "Song C", null, "Pro-Q 4 (FabFilter)");
        AddProject("Client B", "Song D", DateTime.UtcNow.AddMonths(-30), "Old Delay (Acme)");
    }

    private MainWindow Open(double width = 1440, double height = 900)
    {
        var window = new MainWindow(_database, _dbPath, _env.Scan) { Width = width, Height = height };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Task task, Window window)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "读取工程没有结束");
        }
        task.GetAwaiter().GetResult();
        Settle(window);
    }

    private static ProjectsView Projects(MainWindow window)
    {
        window.Navigate("Projects");
        Settle(window);
        return window.Projects!;
    }

    private static IdleView Idle(MainWindow window)
    {
        window.Navigate("Idle");
        Settle(window);
        return window.Idle!;
    }

    private ProjectsView Scanned(MainWindow window)
    {
        var page = Projects(window);
        Wait(page.AddFolders([_projects]), window);
        return page;
    }

    private static T Find<T>(Control root, string name) where T : Control => root.FindControl<T>(name)!;

    private static void Click(Control root, string name) => Find<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string NavCount(MainWindow window, string key) => window.NavCount(key);

    // ---------- 工程页 ----------

    [AvaloniaFact]
    public void AddingAFolderReadsProjectsWithoutTouchingThem()
    {
        Seed();
        var before = Directory.GetFiles(_projects, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)));
        var window = Open();
        var page = Projects(window);
        Assert.Equal("点右上角“添加文件夹”，选择存放工程的位置", Find<TextBlock>(page, "EmptyText").Text);

        Wait(page.AddFolders([_projects]), window);

        Assert.Equal([_projects], page.Folders);
        Assert.Equal(["Song A", "Song B", "Song C", "Song D"], page.Rows.Select(r => r.Name).Order());
        Assert.Equal("缺 1 款", page.Rows.First(r => r.Name == "Song A").MissingText);
        Assert.Equal("齐全", page.Rows.First(r => r.Name == "Song C").MissingText);
        Assert.StartsWith("工程读取完成，找到 4 个工程文件，合并为 4 个工程", window.Status);
        Assert.Equal("4", NavCount(window, "Projects"));
        Assert.Equal("有缺失（2）", Find<RadioButton>(page, "SegmentMissing").Content);

        // 工程文件只读：内容和修改时间都没变。
        foreach (var (file, (bytes, time)) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(file));
            Assert.Equal(time, File.GetLastWriteTimeUtc(file));
        }

        // 回到这一页时状态栏是工程的概况。
        window.Navigate("Plugins");
        window.Navigate("Projects");
        Assert.Equal("4 个工程，2 个有缺失，缺失插件 1 种", window.Status);
    }

    [AvaloniaFact]
    public void ProjectDetailListsPluginsAndMissingOnes()
    {
        Seed();
        var window = Open();
        var page = Scanned(window);

        page.SelectedProject = page.Rows.First(r => r.Name == "Song A");
        Settle(window);
        Assert.Equal("Song A", Find<TextBlock>(page, "DetailTitle").Text);
        Assert.StartsWith("REAPER，最近修改", Find<TextBlock>(page, "DetailMeta").Text);
        // 缺失的排在前面。
        Assert.Equal([("soothe2", true), ("Pro-Q 4", false)], page.DetailRows.Select(l => (l.Name, l.IsMissing)));

        // 只看有缺失的。
        Find<RadioButton>(page, "SegmentMissing").IsChecked = true;
        Settle(window);
        Assert.Equal(["Song A", "Song B"], page.Rows.Select(r => r.Name).Order());

        // 搜索插件名。
        Find<RadioButton>(page, "SegmentAll").IsChecked = true;
        Find<TextBox>(page, "SearchBox").Text = "pro-q";
        Settle(window);
        Assert.Equal(["Song A", "Song C"], page.Rows.Select(r => r.Name).Order());
    }

    [AvaloniaFact]
    public void MissingSummaryCanIgnoreANameAndRestoreIt()
    {
        Seed();
        var window = Open();
        var page = Scanned(window);

        Find<RadioButton>(page, "SegmentSummary").IsChecked = true;
        Settle(window);
        var soothe = Assert.Single(page.MissingRows);
        Assert.Equal("soothe2", soothe.Name);
        Assert.False(Find<Border>(page, "TreeCard").IsVisible);
        page.SelectedMissing = soothe;
        Settle(window);
        Assert.Equal("2 个工程的最新版本需要它", Find<TextBlock>(page, "MissingMeta").Text);

        Click(page, "IgnoreMissingButton");
        Settle(window);
        Assert.Empty(page.MissingRows);
        Assert.Equal("没有缺失的插件", Find<TextBlock>(page, "EmptyText").Text);
        Assert.Equal("“soothe2”不再算作缺失，可以点“恢复忽略的名称”撤销", window.Status);
        Assert.True(Find<Button>(page, "ClearIgnoredButton").IsVisible);

        Click(page, "ClearIgnoredButton");
        Settle(window);
        Assert.Single(page.MissingRows);
        Assert.False(Find<Button>(page, "ClearIgnoredButton").IsVisible);
    }

    [AvaloniaFact]
    public void FolderTreeFiltersProjectsAndRemembersCollapsedFolders()
    {
        Seed();
        var window = Open();
        var page = Scanned(window);

        Assert.True(Find<Border>(page, "TreeCard").IsVisible);
        Assert.Equal("全部工程", page.TreeItems[0].Name);
        var root = page.TreeItems[1];
        Assert.Equal(4, root.Count);
        Assert.Equal(["Client A", "Client B"], root.Children.Select(c => c.Name));
        Assert.Equal("缺 2", root.Children[0].MissingText);

        page.SelectedFolder = root.Children[1];
        Settle(window);
        Assert.Equal(["Song C", "Song D"], page.Rows.Select(r => r.Name).Order());

        root.Children[0].IsExpanded = false;
        Assert.Contains(root.Children[0].Path!, _database.GetSetting(ProjectFolderTree.CollapsedSetting)!.Replace(@"\\", @"\"));

        // 隐藏文件夹树后列表回到全部工程。
        Click(page, "TreeToggle");
        Settle(window);
        Assert.False(Find<Border>(page, "TreeCard").IsVisible);
        Assert.Equal(4, page.Rows.Count);
        Assert.Equal("0", _database.GetSetting(ProjectFolderTree.VisibleSetting));
    }

    [AvaloniaFact]
    public void RevealShowsTheFileOrItsFolderWhenItIsGone()
    {
        Seed();
        var window = Open();
        var page = Scanned(window);
        var revealed = new List<string>();
        var opened = new List<string>();
        page.Reveal = path => { revealed.Add(path); return null; };
        page.OpenFolder = path => { opened.Add(path); return null; };

        var song = page.Rows.First(r => r.Name == "Song C");
        page.SelectedProject = song;
        Settle(window);
        Click(page, "RevealButton");
        Assert.Equal([song.Entry.Path], revealed);

        File.Delete(song.Entry.Path);
        Click(page, "RevealButton");
        Assert.Equal([Path.GetDirectoryName(song.Entry.Path)!], opened);
        Assert.StartsWith("工程文件已经不在了", window.Status);
    }

    [AvaloniaFact]
    public void RemovingTheFolderForgetsItsProjects()
    {
        Seed();
        var window = Open();
        var page = Scanned(window);

        Wait(page.RemoveFolder(_projects), window);

        Assert.Empty(page.Folders);
        Assert.Empty(page.Rows);
        Assert.Equal("", NavCount(window, "Projects"));
        Assert.True(Directory.Exists(_projects));
        Assert.Equal(4, Directory.GetFiles(_projects, "*.rpp", SearchOption.AllDirectories).Length);
    }

    // ---------- 闲置报告 ----------

    [AvaloniaFact]
    public void IdleReportNeedsScannedProjects()
    {
        Seed();
        var window = Open();
        var idle = Idle(window);
        Assert.Equal("先到“工程”页扫描工程，才知道哪些插件没用过", Find<TextBlock>(idle, "EmptyText").Text);
        Assert.Equal("闲置报告依据工程扫描结果", window.Status);
        Assert.Equal("", NavCount(window, "Idle"));
    }

    [AvaloniaFact]
    public void IdleReportListsUnusedPluginsAndFilters()
    {
        Seed();
        var window = Open();
        Scanned(window);
        var idle = Idle(window);

        Assert.Equal(["Unused Comp", "Old Delay"], idle.Rows.Select(r => r.Name));
        Assert.Equal("从未出现", idle.Rows[0].LastUsedText);
        Assert.Equal(1, idle.Rows[1].ProjectCount);
        Assert.Equal("依据 4 个工程：1 款从未出现，1 款超过 12 个月没用", window.Status);
        Assert.Equal("2", NavCount(window, "Idle"));

        Find<RadioButton>(idle, "SegmentNever").IsChecked = true;
        Settle(window);
        Assert.Equal(["Unused Comp"], idle.Rows.Select(r => r.Name));

        // 放宽到 36 个月，两年半前用过的就不算闲置了。
        Find<RadioButton>(idle, "SegmentAll").IsChecked = true;
        idle.MonthsIndex = 3;
        Settle(window);
        Assert.Equal(["Unused Comp"], idle.Rows.Select(r => r.Name));
        Assert.Equal("1", NavCount(window, "Idle"));
    }

    [AvaloniaFact]
    public void IdlePluginsCanBeMovedToTheTrashWithAReason()
    {
        Seed();
        var window = Open();
        Scanned(window);
        var idle = Idle(window);
        var asked = 0;
        idle.AskReason = count => { asked = count; return Task.FromResult<string?>("一直不用"); };

        idle.Select(idle.Rows);
        Settle(window);
        Assert.Equal("把选中的 2 款移入垃圾桶…", Find<Button>(idle, "TrashButton").Content);
        Wait(idle.MoveSelectedToTrash(), window);

        Assert.Equal(2, asked);
        Assert.Empty(idle.Rows);
        Assert.Equal("已把 2 款插件移入垃圾桶，可在左侧“垃圾桶”查看或恢复", window.Status);
        var trashed = _database.LoadEffects().Where(p => p.User.Hidden).ToList();
        Assert.Equal(["Old Delay", "Unused Comp"], trashed.Select(p => p.Name).Order());
        Assert.All(trashed, p => Assert.Equal("一直不用", p.User.BlacklistReason));
        Assert.Equal("2", NavCount(window, "Blacklist"));
        // 插件文件不动。
        Assert.True(Directory.Exists(Path.Combine(_env.Vst3, "Unused Comp.vst3")));
    }

    [AvaloniaFact]
    public void CancellingTheReasonKeepsThePlugins()
    {
        Seed();
        var window = Open();
        Scanned(window);
        var idle = Idle(window);
        idle.AskReason = _ => Task.FromResult<string?>(null);
        idle.Select(idle.Rows.Take(1));
        Wait(idle.MoveSelectedToTrash(), window);
        Assert.Equal(2, idle.Rows.Count);
        Assert.DoesNotContain(_database.LoadEffects(), p => p.User.Hidden);
    }

    [AvaloniaFact]
    public void EnterOpensTheIdlePluginInTheLibrary()
    {
        Seed();
        var window = Open();
        Scanned(window);
        var idle = Idle(window);
        idle.Select(idle.Rows.Take(1));
        Find<ListBox>(idle, "IdleList").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Settle(window);

        Assert.Equal("Plugins", window.CurrentPage);
        Assert.Equal("Unused Comp", window.Library("Plugins")!.Selected?.Row.Name);
    }

    // ---------- 路由图 ----------

    private static (List<PluginChain> Notes, List<NoteRoute> Routes) Mix()
    {
        var notes = new List<PluginChain>
        {
            new(1, "Kick", "", [new ChainStep("pro-q 4", "Pro-Q 4"), new ChainStep("", "soothe2")]),
            new(2, "Snare", "", [new ChainStep("pro-q 4", "Pro-Q 4")]),
            new(3, "Drums", "", [new ChainStep("", "Bus Comp")], Role: ChannelRole.Group),
            new(4, "Vox", "", [new ChainStep("pro-q 4", "Pro-Q 4")]),
            new(5, "Verb", "", [new ChainStep("valhallaroom", "ValhallaRoom"), new ChainStep("", "Pro-C 2")], Role: ChannelRole.Effect),
            new(6, "Pad", "", []),
        };
        var routes = new List<NoteRoute>
        {
            new(1, 3, RouteKind.Output),
            new(2, 3, RouteKind.Output),
            new(4, 5, RouteKind.Send),
            new(1, 5, RouteKind.Send),
            new(4, 5, RouteKind.Sidechain, "Pro-C 2"),
        };
        return (notes, routes);
    }

    private static IReadOnlyList<(string, bool)> Plugins(PluginChain note) =>
        note.Steps.Select(s => (s.PluginName, s.PluginKey.Length > 0)).ToList();

    [AvaloniaFact]
    public void RouteMapPlacesTracksGroupsEffectsAndMaster()
    {
        var (notes, routes) = Mix();
        var map = new RouteMapView();
        var window = new Window { Width = 1200, Height = 600, Content = map };
        window.Show();
        map.Show(notes, routes, Plugins);
        Settle(window);

        Assert.False(map.IsEmpty);
        // 没接路由的 Pad 不画；没有主输出笔记，补一张“主输出”。
        Assert.Equal([RouteMapLayout.ImplicitMaster, 1, 2, 3, 4, 5], map.Cards.Keys.Order());
        double Left(long id) => Canvas.GetLeft(map.Cards[id]);
        Assert.Equal(Left(1), Left(4));
        Assert.True(Left(3) > Left(1));
        Assert.True(Left(5) > Left(3));
        Assert.True(Left(RouteMapLayout.ImplicitMaster) > Left(5));
        // 5 条路由，加上 Drums、Vox、Verb 补到主输出的 3 条。
        Assert.Equal(8, map.EdgeCount);

        map.Highlight(5);
        Assert.Equal(0.4, map.Cards[2].Opacity);
        Assert.Equal(1, map.Cards[4].Opacity);
        map.Highlight(null);
        Assert.Equal(1, map.Cards[2].Opacity);

        var opened = new List<long>();
        map.OpenNote += opened.Add;
        map.Show([notes[5]], [], Plugins);
        Assert.True(map.IsEmpty);
        Assert.Equal(RouteMapLayout.EmptyText, map.EmptyText);
    }

    // ---------- 截图 ----------

    [AvaloniaFact]
    public void RendersProjectsIdleAndRouteMap()
    {
        var output = Environment.GetEnvironmentVariable("WHATPLUGIN_SCREENSHOTS");
        if (output != null)
            Directory.CreateDirectory(output);
        void Save(Window window, string name)
        {
            Settle(window);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(output ?? _dir, name + ".png"), new PngBitmapEncoderOptions());
        }

        Seed();
        var window = Open();
        var page = Scanned(window);
        page.SelectedProject = page.Rows.First(r => r.Name == "Song A");
        Save(window, "projects");

        Find<RadioButton>(page, "SegmentSummary").IsChecked = true;
        Settle(window);
        page.SelectedMissing = page.MissingRows[0];
        Save(window, "projects-missing");

        var idle = Idle(window);
        idle.Select(idle.Rows.Take(1));
        Save(window, "idle");

        var (notes, routes) = Mix();
        var map = new RouteMapView();
        var host = new Window { Width = 1100, Height = 520, Content = new Border { Padding = new(16), Child = map } };
        host.Bind(Window.BackgroundProperty, host.GetResourceObservable("Bg1"));
        host.Show();
        map.Show(notes, routes, Plugins);
        Settle(host);
        map.Highlight(4);
        Save(host, "route-map");
    }
}
