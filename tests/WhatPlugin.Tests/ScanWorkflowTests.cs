using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>扫描流程、设置页的文件夹列表和健康检查的列表（Windows 版和跨平台版共用）。只用临时目录。</summary>
public sealed class ScanWorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-flow-" + Guid.NewGuid().ToString("N"));
    private readonly string _vst3;
    private readonly string _vst2;
    private readonly string _scanner;
    private readonly PluginDatabase _db;

    public ScanWorkflowTests()
    {
        _vst3 = Path.Combine(_dir, "VST3");
        _vst2 = Path.Combine(_dir, "VST2");
        Directory.CreateDirectory(_vst3);
        Directory.CreateDirectory(_vst2);
        _scanner = Path.Combine(_dir, AppPlatform.Executable("WhatPluginScanner"));
        File.WriteAllText(_scanner, "");
        _db = new PluginDatabase(Path.Combine(_dir, "t.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private ScanEnvironment Environment(HostPluginIndex? hosts = null) =>
        new(_scanner, () => [(_vst3, PluginFormat.Vst3), (_vst2, PluginFormat.Vst2)], () => hosts ?? HostPluginIndex.Empty, () => []);

    private string AddVst3(string name, string category = "Fx|EQ")
    {
        var bundle = Path.Combine(_vst3, name + ".vst3");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json"),
            $$"""{ "Classes": [ { "Name": "{{name}}", "Category": "Audio Module Class", "Vendor": "Acme", "Sub Categories": ["{{category.Replace("|", "\", \"")}}"] } ] }""");
        return bundle;
    }

    [Fact]
    public async Task ScanReportsWhatItFoundAndRecordsUsage()
    {
        AddVst3("Alpha");
        AddVst3("Synth", "Instrument");
        var outcome = await ScanWorkflow.RunAsync(_db, Environment(), new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None);

        Assert.Equal("扫描完成，共 1 款效果器，本次处理 2 个文件，跳过未变化的 0 个", outcome.Message);
        Assert.Equal("Alpha", Assert.Single(_db.LoadEffects()).Name);
        Assert.Equal(1, _db.CountUsageEvents());
    }

    [Fact]
    public async Task MissingScannerStopsBeforeTouchingTheDatabase()
    {
        AddVst3("Alpha");
        File.Delete(_scanner);
        var outcome = await ScanWorkflow.RunAsync(_db, Environment(), new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None);

        Assert.Null(outcome.Summary);
        Assert.StartsWith("找不到扫描器", outcome.Message);
        Assert.Empty(_db.LoadFileScans());
    }

    [Fact]
    public async Task TrashPluginThatDisappearedGoesIntoUninstallHistory()
    {
        var bundle = AddVst3("Old Comp");
        await ScanWorkflow.RunAsync(_db, Environment(), new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None);
        var row = Assert.Single(_db.LoadEffects());
        _db.SaveUserData(row.Id, row.User with { Hidden = true, BlacklistReason = "发闷" });

        Directory.Delete(bundle, recursive: true);
        var saved = false;
        var outcome = await ScanWorkflow.RunAsync(_db, Environment(), new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None, () => saved = true);

        Assert.True(saved);
        Assert.Equal("Old Comp", Assert.Single(outcome.GoneFromTrash).Name);
        Assert.EndsWith("垃圾桶里的 Old Comp 已经不在了，已记进卸载历史", outcome.Message);
        Assert.Equal(UninstallWay.FoundMissing, Assert.Single(_db.LoadUninstallHistory()).Way);

        // 卸载后自己清理或重装都不重复记。
        var again = await ScanWorkflow.RunAsync(_db, Environment(), new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None);
        Assert.Empty(again.GoneFromTrash);
    }

    [Fact]
    public async Task SettingsListFoldersFileCountsAndSuggestions()
    {
        AddVst3("Alpha");
        var mine = Path.Combine(_dir, "Mine");
        var gone = Path.Combine(_dir, "Unplugged");
        var dawFolder = Path.Combine(_dir, "DawFolder");
        Directory.CreateDirectory(mine);
        Directory.CreateDirectory(dawFolder);
        _db.AddVst2Folder(mine);
        _db.AddVst2Folder(gone);
        var hosts = new HostPluginIndex(
            [new DawPluginRecord("REAPER", Path.Combine(dawFolder, "x.dll"), null, new HostPluginInfo("X", "Acme", "1", "Fx"))],
            [Path.Combine(dawFolder, "x.dll"), Path.Combine(mine, "y.dll")]);
        await ScanWorkflow.RunAsync(_db, Environment(hosts), new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None);

        var info = ScanSettings.Build(_db, Environment(hosts));
        Assert.Equal(new ScanFolderInfo(_vst3, "VST3，1 个文件", false), Assert.Single(info.MainFolders));
        Assert.Equal(
            [
                new ScanFolderInfo(mine, "我添加的，还没有扫描到文件", true),
                new ScanFolderInfo(gone, "我添加的，现在找不到这个文件夹", true),
                new ScanFolderInfo(_vst2, "默认，还没有扫描到文件", false),
            ],
            info.Vst2Folders);
        // 已经在扫描范围里的文件夹不再建议。
        var suggestion = Assert.Single(info.Suggestions);
        Assert.Equal(dawFolder, suggestion.Path);
        Assert.Equal(["REAPER"], suggestion.Daws);
        Assert.Equal("已扫描 1 个文件。", info.MainSummary);
        Assert.Equal("还没有扫描过 VST2。", info.Vst2Summary);
        Assert.Contains("REAPER", info.Vst2HostNote);
    }

    [Fact]
    public void HealthListingGroupsIssuesAndVendors()
    {
        static PluginRow Row(long id, string name, string vendor, ScanStatus status, string? error = null) =>
            new(id, Normalizer.Name(name), name, vendor, PluginKind.Effect, ["EQ"], ClassificationSource.Seed, false,
                [new MergedFile(name + ".vst3", PluginFormat.Vst3, Arch.X64, "2.1", "Fx", status, error)], PluginUserData.Empty);
        var rows = new[] { Row(1, "Zeta", "", ScanStatus.LoadFailed, "崩溃"), Row(2, "Alpha", "Acme", ScanStatus.TimedOut), Row(3, "Ok", "Acme", ScanStatus.Loaded) };
        var report = PluginHealth.Build(rows, []);
        var updates = UpdateGuide.Build(rows);

        var categories = HealthListing.Categories(report, updates);
        Assert.Equal(HealthListing.UpdatesTitle, categories[^1].Title);
        Assert.Null(categories[^1].Kind);

        var failed = HealthListing.Rows(categories.First(c => c.Kind == HealthKind.ReadFailed), report, updates);
        Assert.Equal(["Alpha", "Zeta"], failed.Select(r => r.Title));
        Assert.Equal("未知", failed[1].Subtitle);
        Assert.Equal("读取超时", HealthListing.Note(failed[0]));
        Assert.Equal("VST3  64 位  2.1", Assert.Single(HealthListing.Files(failed[0].Plugin)).Summary);
        Assert.Equal(UpdateGuide.SearchUrl("Acme", "Alpha"), HealthListing.SearchUrl(failed[0]));

        var vendors = HealthListing.Rows(categories[^1], report, updates);
        var acme = vendors.First(v => v.Title == "Acme");
        Assert.Equal("2 款", acme.Subtitle);
        Assert.Empty(HealthListing.Files(acme.Plugin));
        Assert.Equal(UpdateGuide.SearchUrl("Acme"), HealthListing.SearchUrl(acme));
        Assert.Equal("2 款插件需要留意", HealthListing.Describe(report));
    }
}
