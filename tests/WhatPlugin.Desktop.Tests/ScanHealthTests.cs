using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>设置页（扫描、VST2 文件夹、使用统计）和健康检查页。插件目录和 DAW 记录都是临时目录里的假数据。</summary>
public sealed class ScanHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-scan-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;
    private readonly TestEnvironment _env;

    public ScanHealthTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        _database = new PluginDatabase(_dbPath);
        _env = new TestEnvironment(Path.Combine(_dir, "env"));
        // 破坏性的操作（删插件包）只在这个临时目录里做。
        Assert.StartsWith(Path.GetTempPath(), _env.Vst3, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
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

    /// <summary>扫描在后台线程跑，结束后回到界面线程：一边处理界面消息一边等。</summary>
    private static void Wait(Task task, Window window)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "扫描没有结束");
        }
        task.GetAwaiter().GetResult();
        Settle(window);
    }

    private static void WaitUntil(Func<bool> done, Window window)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "等待超时");
        }
        Settle(window);
    }

    private static void Click(Control root, string name) =>
        root.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static SettingsView Settings(MainWindow window)
    {
        window.Navigate(MainWindow.SettingsPage.Key);
        Settle(window);
        return window.Settings!;
    }

    private static HealthView Health(MainWindow window)
    {
        window.Navigate("Health");
        Settle(window);
        return window.Health!;
    }

    // ---------- 扫描 ----------

    [AvaloniaFact]
    public void ScanButtonFindsNewPluginsAndUpdatesTheLibrary()
    {
        _env.AddVst3("Pro-Q 4", "FabFilter");
        _env.AddVst3("ValhallaRoom", "Valhalla DSP", "Fx|Reverb");
        _env.AddVst3("Diva", "u-he", "Instrument|Synth");
        var window = Open();
        Assert.Empty(window.Library("Plugins")!.Shown);

        var settings = Settings(window);
        var vst3 = Assert.Single(settings.MainFolders, f => f.Path == _env.Vst3);
        Assert.Equal("VST3，还没有扫描到文件", vst3.Note);
        Assert.False(vst3.Removable);

        Click(settings, "ScanButton");
        Assert.True(window.IsScanning);
        Assert.False(settings.FindControl<Button>("ScanButton")!.IsEnabled);
        Assert.True(settings.FindControl<Button>("CancelScanButton")!.IsVisible);
        WaitUntil(() => !window.IsScanning, window);

        Assert.StartsWith("扫描完成，共 2 款效果器，本次处理 3 个文件，跳过未变化的 0 个", window.Status);
        Assert.True(settings.FindControl<Button>("ScanButton")!.IsEnabled);
        Assert.False(settings.FindControl<Button>("CancelScanButton")!.IsVisible);
        Assert.Equal("VST3，3 个文件", Assert.Single(settings.MainFolders, f => f.Path == _env.Vst3).Note);
        Assert.Equal("已扫描 3 个文件。", settings.FindControl<TextBlock>("MainSummary")!.Text);

        // 音源不进插件库。
        window.Navigate("Plugins");
        Settle(window);
        Assert.Equal(["Pro-Q 4", "ValhallaRoom"], window.Library("Plugins")!.Shown.Select(p => p.Name).Order());
        Assert.Equal("2", NavCount(window, "Plugins"));

        // 没变化的文件第二次跳过。
        Wait(window.RunScanAsync(new ScanRequest(false, ScanFormats.Main)), window);
        Assert.StartsWith("扫描完成，共 2 款效果器，本次处理 0 个文件，跳过未变化的 3 个", window.Status);
    }

    private static string? NavCount(MainWindow window, string key) =>
        window.GetVisualDescendants().OfType<RadioButton>().First(r => (string?)r.Tag == key)
            .GetVisualDescendants().OfType<TextBlock>().First().Text;

    [AvaloniaFact]
    public void PluginUninstalledOutsideTheAppIsRecordedFromTheTrash()
    {
        var bundle = _env.AddVst3("Old Free Comp", "Freeware", "Fx|Dynamics");
        _env.AddVst3("Pro-C 2", "FabFilter", "Fx|Dynamics");
        var window = Open();
        Wait(window.RunScanAsync(new ScanRequest(false, ScanFormats.Main)), window);

        var row = _database.LoadEffects().Single(p => p.Name == "Old Free Comp");
        _database.SaveUserData(row.Id, row.User with { Hidden = true, BlacklistReason = "音色发闷" });

        Directory.Delete(bundle, recursive: true);
        Wait(window.RunScanAsync(new ScanRequest(false, ScanFormats.Main)), window);

        Assert.Contains("垃圾桶里的 Old Free Comp 已经不在了，已记进卸载历史", window.Status);
        var record = Assert.Single(_database.LoadUninstallHistory());
        Assert.Equal("Old Free Comp", record.Name);
        // 写过原因的插件留在垃圾桶里，标成已卸载。
        window.Navigate("Blacklist");
        Settle(window);
        Assert.True(Assert.Single(window.Library("Blacklist")!.Shown).IsUninstalled);
    }

    [AvaloniaFact]
    public void MissingScannerIsReportedInsteadOfScanning()
    {
        File.Delete(_env.ScannerPath);
        _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        Wait(window.RunScanAsync(new ScanRequest(false, ScanFormats.Main)), window);

        Assert.StartsWith("找不到扫描器 WhatPluginScanner", window.Status);
        Assert.Empty(_database.LoadEffects());
        Assert.False(window.IsScanning);
    }

    [AvaloniaFact]
    public void Vst2FoldersCanBeAddedSuggestedAndRemoved()
    {
        var mine = Path.Combine(_dir, "My VST2");
        var suggested = Path.Combine(_dir, "Cubase VST2");
        Directory.CreateDirectory(mine);
        Directory.CreateDirectory(suggested);
        _env.Hosts = new HostPluginIndex(
            [new DawPluginRecord("Cubase", Path.Combine(suggested, "Comp.dll"), null, new HostPluginInfo("Comp", "Acme", "1", "Fx"))],
            [Path.Combine(suggested, "Comp.dll")]);
        var window = Open();
        var settings = Settings(window);

        Assert.Equal("还没有扫描过 VST2。", settings.FindControl<TextBlock>("Vst2Summary")!.Text);
        Assert.Contains("这台电脑上找到了 Cubase 的记录", settings.FindControl<TextBlock>("Vst2HostNote")!.Text);
        var default2 = Assert.Single(settings.Vst2Folders);
        Assert.Equal(_env.Vst2, default2.Path);
        Assert.Equal("默认，还没有扫描到文件", default2.Note);
        var suggestion = Assert.Single(settings.Suggestions);
        Assert.Equal(suggested, suggestion.Path);
        Assert.Equal("DAW（Cubase）在用这个文件夹：", suggestion.Label);

        Wait(settings.AddVst2Folders([mine]), window);
        Assert.Contains(mine, _database.LoadVst2Folders());
        var added = Assert.Single(settings.Vst2Folders, f => f.Path == mine);
        Assert.True(added.Removable);
        Assert.Equal("我添加的，还没有扫描到文件", added.Note);
        Assert.StartsWith("扫描完成", window.Status);

        // “添加并扫描”以后不再建议。
        var addSuggested = settings.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Tag == suggested && b.Content as string == "添加并扫描");
        addSuggested.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(() => !window.IsScanning && settings.Suggestions.Count == 0, window);
        Assert.Contains(suggested, _database.LoadVst2Folders());

        Wait(settings.RemoveVst2Folder(mine), window);
        Assert.DoesNotContain(mine, _database.LoadVst2Folders());
        Assert.DoesNotContain(settings.Vst2Folders, f => f.Path == mine);
        // 文件夹本身不动。
        Assert.True(Directory.Exists(mine));
    }

    [AvaloniaFact]
    public void FoldersCannotChangeWhileScanning()
    {
        var mine = Path.Combine(_dir, "My VST2");
        Directory.CreateDirectory(mine);
        _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        var settings = Settings(window);

        var scan = window.RunScanAsync(new ScanRequest(false, ScanFormats.Main));
        if (window.IsScanning)
        {
            settings.AddVst2Folders([mine]);
            Assert.Equal("正在扫描，等结束后再改文件夹。", window.Status);
            Assert.Empty(_database.LoadVst2Folders());
        }
        Wait(scan, window);
    }

    [AvaloniaFact]
    public void LookupPluginCardInstallsIntoTempVst3Folder()
    {
        var window = Open();
        var settings = Settings(window);
        // 这台机器上的应用没有带查询插件（开发时、Windows）：不显示卡片。
        Assert.False(settings.LookupCardVisible);

        var source = Path.Combine(_dir, "Resources", LookupPluginInstaller.BundleName);
        var target = Path.Combine(_dir, "VST3", LookupPluginInstaller.BundleName);
        Assert.StartsWith(Path.GetTempPath(), target, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.Combine(source, "Contents"));
        File.WriteAllText(Path.Combine(source, "Contents", "Info.plist"), "plist");
        settings.LookupPlugin = new LookupPluginInstaller(source, target);
        settings.Refresh();
        Settle(window);

        var install = settings.FindControl<Button>("InstallLookupButton")!;
        Assert.True(settings.LookupCardVisible);
        Assert.StartsWith("还没有安装", settings.LookupStateText);
        Assert.Equal("安装到 VST3 文件夹", install.Content);
        Assert.False(settings.FindControl<Button>("RevealLookupButton")!.IsVisible);

        Click(settings, "InstallLookupButton");
        Settle(window);
        Assert.True(File.Exists(Path.Combine(target, "Contents", "Info.plist")));
        Assert.StartsWith("已安装查询插件", window.Status);
        Assert.Equal("已安装在：" + target, settings.LookupStateText);
        Assert.Equal("重新安装", install.Content);
        Assert.True(settings.FindControl<Button>("RevealLookupButton")!.IsVisible);

        // 应用更新后带的是新版本：提示更新。
        File.WriteAllText(Path.Combine(source, "Contents", "Info.plist"), "plist v2");
        settings.Refresh();
        Settle(window);
        Assert.Equal("更新", install.Content);
        Assert.StartsWith("已安装的是旧版本", settings.LookupStateText);
    }

    [AvaloniaFact]
    public void UsageStatisticsCanBeTurnedOffAndCleared()
    {
        _database.RecordUsage(UsageKinds.Scan, 3);
        var window = Open();
        var settings = Settings(window);
        var box = settings.FindControl<CheckBox>("UsageEnabledBox")!;
        var summary = settings.FindControl<TextBlock>("UsageSummary")!;
        Assert.True(box.IsChecked);
        Assert.Equal("正在记录。已记下 1 条。", summary.Text);

        box.IsChecked = false;
        Settle(window);
        Assert.False(_database.UsageEnabled);
        Assert.StartsWith("已关闭，不再记录。", summary.Text);

        // 取消就不清空。
        settings.Confirm = (_, _, _) => Task.FromResult(false);
        Click(settings, "ClearUsageButton");
        Settle(window);
        Assert.Equal(1, _database.CountUsageEvents());

        settings.Confirm = (_, _, _) => Task.FromResult(true);
        Click(settings, "ClearUsageButton");
        Settle(window);
        Assert.Equal(0, _database.CountUsageEvents());
        Assert.Equal("已清空使用统计", window.Status);
        Assert.False(settings.FindControl<Button>("ClearUsageButton")!.IsEnabled);
    }

    // ---------- 健康检查 ----------

    private static MergedPlugin Plugin(string name, string vendor, params MergedFile[] files) =>
        new(Normalizer.Name(name), name, vendor, InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.Seed), files);

    private static MergedFile File3(string path, ScanStatus status = ScanStatus.Loaded, string? error = null, string version = "1.0") =>
        new(path, PluginFormat.Vst3, Arch.X64, version, "Fx", status, error);

    private void SeedHealth()
    {
        var blocked = Path.Combine(_env.Vst3, "Blocked.vst3");
        _database.SaveMerged(
        [
            Plugin("Pro-Q 4", "FabFilter", File3(Path.Combine(_env.Vst3, "Pro-Q 4.vst3"))),
            Plugin("Broken Verb", "Acme", File3(Path.Combine(_env.Vst3, "Broken Verb.vst3"), ScanStatus.LoadFailed, "加载时崩溃")),
            Plugin("Blocked", "Acme", File3(blocked)),
            Plugin("Old Delay", "Retro", new MergedFile(Path.Combine(_env.Vst2, "Old Delay.dll"), PluginFormat.Vst2, Arch.X64, "", "", ScanStatus.FileInfoOnly, null)),
        ]);
        _env.Blacklist.Add(new HostBlacklistEntry(blocked, "Cubase"));
    }

    [AvaloniaFact]
    public void HealthListsProblemsByCategory()
    {
        SeedHealth();
        var window = Open();
        // 需要留意的（读取失败、DAW 拉黑）显示在侧边栏上。
        Assert.Equal("2", NavCount(window, "Health"));

        var health = Health(window);
        Assert.Equal("2 款插件需要留意", window.Status);
        var titles = health.Categories.Select(c => c.Title).ToList();
        Assert.Contains(PluginHealth.Title(HealthKind.ReadFailed), titles);
        Assert.Contains(PluginHealth.Title(HealthKind.HostBlacklisted), titles);
        Assert.Equal(HealthListing.UpdatesTitle, titles[^1]);

        health.SelectedCategory = health.Categories.First(c => c.Kind == HealthKind.ReadFailed);
        Settle(window);
        var row = Assert.Single(health.Rows);
        Assert.Equal("Broken Verb", row.Title);
        Assert.Equal("加载时崩溃", row.Detail);
        Assert.Equal(PluginHealth.Explain(HealthKind.ReadFailed), ((PageFrame)window.GetVisualDescendants().OfType<PageFrame>().First(f => f.IsVisible)).Subtitle);

        health.SelectedCategory = health.Categories.First(c => c.Kind == HealthKind.HostBlacklisted);
        Settle(window);
        Assert.StartsWith("Cubase 拉黑了 Blocked.vst3", Assert.Single(health.Rows).Detail);

        health.SelectedCategory = health.Categories.First(c => c.Kind == HealthKind.OnlyVst2);
        Settle(window);
        Assert.Equal("Old Delay", Assert.Single(health.Rows).Title);
    }

    [AvaloniaFact]
    public void HealthDetailShowsFilesAndOpensThePlugin()
    {
        SeedHealth();
        var window = Open();
        var health = Health(window);
        var urls = new List<string>();
        var revealed = new List<string>();
        health.OpenUrl = url => { urls.Add(url); return null; };
        health.Reveal = path => { revealed.Add(path); return null; };

        Assert.True(health.FindControl<TextBlock>("DetailEmpty")!.IsVisible);
        health.SelectedCategory = health.Categories.First(c => c.Kind == HealthKind.ReadFailed);
        Settle(window);
        health.SelectedRow = health.Rows[0];
        Settle(window);

        Assert.False(health.FindControl<TextBlock>("DetailEmpty")!.IsVisible);
        Assert.Equal("Broken Verb", health.FindControl<TextBlock>("DetailTitle")!.Text);
        var file = Assert.Single(health.Files);
        Assert.Equal("VST3  64 位  1.0", file.Summary);

        Click(health, "SearchButton");
        Assert.Equal(UpdateGuide.SearchUrl("Acme", "Broken Verb"), Assert.Single(urls));
        Click(health, "RevealButton");
        Assert.Equal(file.Path, Assert.Single(revealed));

        Click(health, "OpenButton");
        Settle(window);
        Assert.Equal("Plugins", window.CurrentPage);
        Assert.Equal("Broken Verb", window.Library("Plugins")!.Selected?.Name);
    }

    [AvaloniaFact]
    public void UpdateGuideListsVendors()
    {
        SeedHealth();
        var window = Open();
        var health = Health(window);
        var urls = new List<string>();
        health.OpenUrl = url => { urls.Add(url); return null; };

        health.SelectedCategory = health.Categories[^1];
        Settle(window);
        Assert.Equal(HealthListing.UpdatesExplanation, window.GetVisualDescendants().OfType<PageFrame>().First(f => f.IsVisible).Subtitle);
        var fabfilter = health.Rows.First(r => r.Title == "FabFilter");
        Assert.Equal("1 款", fabfilter.Subtitle);
        health.SelectedRow = fabfilter;
        Settle(window);
        // 厂家这一行没有文件，也不能跳到插件库。
        Assert.False(health.FindControl<Button>("RevealButton")!.IsVisible);
        Assert.False(health.FindControl<Button>("OpenButton")!.IsVisible);
        Click(health, "SearchButton");
        Assert.Equal(UpdateGuide.SearchUrl("FabFilter"), Assert.Single(urls));
    }

    [AvaloniaFact]
    public void ScanningRefreshesHealth()
    {
        _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        Assert.Equal("", NavCount(window, "Health"));
        Wait(window.RunScanAsync(new ScanRequest(false, ScanFormats.Main)), window);
        _env.Blacklist.Add(new HostBlacklistEntry(_database.LoadEffects().Single().Files[0].Path, "Cubase"));
        // DAW 的屏蔽列表扫描后才重读。
        Wait(window.RunScanAsync(new ScanRequest(false, ScanFormats.Main)), window);
        Assert.Equal("1", NavCount(window, "Health"));
    }

    [AvaloniaFact]
    public void RendersSettingsAndHealth()
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

        SeedHealth();
        var suggested = Path.Combine(_dir, "Cubase VST2");
        Directory.CreateDirectory(suggested);
        _env.Hosts = new HostPluginIndex(
            [new DawPluginRecord("Cubase", Path.Combine(suggested, "Comp.dll"), null, new HostPluginInfo("Comp", "Acme", "1", "Fx"))],
            [Path.Combine(suggested, "Comp.dll")]);
        _database.AddVst2Folder(Path.Combine(_dir, "My VST2"));
        var window = Open();

        var settings = Settings(window);
        foreach (var scroller in settings.GetVisualDescendants().OfType<ScrollViewer>())
            scroller.ScrollToEnd();
        Save(window, "settings-scan");

        var health = Health(window);
        health.SelectedCategory = health.Categories.First(c => c.Kind == HealthKind.ReadFailed);
        Settle(window);
        health.SelectedRow = health.Rows[0];
        Save(window, "health");

        health.SelectedCategory = health.Categories[^1];
        Settle(window);
        health.SelectedRow = health.Rows[0];
        Save(window, "health-updates");
    }
}
