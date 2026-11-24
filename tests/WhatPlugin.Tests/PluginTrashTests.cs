using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>
/// 移到回收站（Mac 上是废纸篓）、卸载选项和结果报告。真的移动文件的测试只用临时目录里的假插件，
/// 每次都先确认路径在临时目录里。
/// </summary>
public sealed class PluginTrashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-trash-" + Guid.NewGuid().ToString("N"));
    private static long _nextId;

    public PluginTrashTests()
    {
        Directory.CreateDirectory(_dir);
        Assert.StartsWith(Path.GetTempPath(), _dir, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MergedFile File1(string path, PluginFormat format = PluginFormat.Vst3) =>
        new(path, format, Arch.X64, "1", "", ScanStatus.ModuleInfo, null);

    private static PluginRow Row(string name, params MergedFile[] files) =>
        new(++_nextId, Normalizer.Name(name), name, "Acme", PluginKind.Effect, [], ClassificationSource.None, false, files,
            new PluginUserData(0, "", [], null, null, null, true, "太吃 CPU", null));

    /// <summary>临时目录里的假插件包（文件夹），返回路径。</summary>
    private string Bundle(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.Combine(path, "Contents"));
        File.WriteAllText(Path.Combine(path, "Contents", "Info.plist"), "假插件");
        return path;
    }

    // ---------- 卸载选项 ----------

    [Fact]
    public void OptionsWithoutUninstallerAreMoveOrSkip()
    {
        var row = Row("Foo", File1(Path.Combine(_dir, "Foo.vst3")));
        var plan = new UninstallPlan(row, null, [], [new RemovableFile(row.Files[0].Path, PluginFormat.Vst3, 2048)], [], [], 3);

        var options = UninstallOptions.For(plan);

        Assert.Equal([UninstallMethod.RecycleFiles, UninstallMethod.Skip], options.Select(o => o.Method));
        Assert.Contains(PluginTrash.Name, options[0].Label);
        var detail = UninstallOptions.Detail(plan, options[0]);
        Assert.Contains("2 KB", detail);
        Assert.Contains("3 个工程用过", detail);
        Assert.DoesNotContain("工程用过", UninstallOptions.Detail(plan, options[1]));
    }

    [Fact]
    public void SummaryCountsFilesAndNeedsSomethingToDo()
    {
        var row = Row("Foo", File1("a"), File1("b"));
        var plan = new UninstallPlan(row, null, [], [new RemovableFile("a", PluginFormat.Vst3, 1), new RemovableFile("b", PluginFormat.Vst2, 1)], [], [], 0);

        var (text, canStart) = UninstallOptions.Summary([new UninstallChoice(plan, UninstallMethod.RecycleFiles, null)]);
        Assert.True(canStart);
        Assert.Equal($"将把 2 个文件移到{PluginTrash.Name}", text);

        var (none, can) = UninstallOptions.Summary([new UninstallChoice(plan, UninstallMethod.Skip, null)]);
        Assert.False(can);
        Assert.Equal("没有要执行的操作", none);
    }

    [Fact]
    public void ReportFollowsTheDisk()
    {
        var gone = Row("Gone", File1("g1"));
        var left = Row("Left", File1("l1"), File1("l2"));
        var choices = new[]
        {
            new UninstallChoice(new UninstallPlan(gone, null, [], [], [], [], 0), UninstallMethod.RecycleFiles, null, "不好用"),
            new UninstallChoice(new UninstallPlan(left, null, [], [], [], [], 0), UninstallMethod.RecycleFiles, null),
        };
        var moved = new[] { new FileOpResult("g1", true), new FileOpResult("l1", false, "已取消"), new FileOpResult("l2", true, Unconfirmed: true) };
        var now = new DateTime(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc);

        var report = RemovalText.Report(choices, moved, now, path => path == "l1");

        Assert.Equal([gone.Id], report.Gone);
        var record = Assert.Single(report.History);
        Assert.Equal(UninstallWay.RecycleFiles, record.Way);
        Assert.Equal("不好用", record.Reason);
        Assert.Equal("太吃 CPU", record.TrashReason);
        Assert.Equal(now, record.UninstalledUtc);
        Assert.Contains("Gone：文件已全部移除", report.Text);
        Assert.Contains("Left：还剩 1 个文件", report.Text);
        Assert.Contains("l1（已取消）", report.Text);
        Assert.Contains("有 1 个文件已不在原位", report.Text);
        Assert.Equal("卸载结束：1 款已移除，1 款还有文件", report.Status);
    }

    [Fact]
    public void DescribeMentionsFailuresAndUnconfirmed()
    {
        var text = RemovalText.VersionsMoved([new FileOpResult("a", true, Unconfirmed: true), new FileOpResult("b", false, "没有权限")]);

        Assert.Equal($"已把 1 个版本移到{PluginTrash.Name}，可以在详情里恢复；其中 1 个没能在{PluginTrash.Name}里确认；1 个失败：没有权限", text);
        Assert.True(RemovalText.ShouldForget(new FileOpResult("a", false, PluginTrash.Gone)));
        Assert.False(RemovalText.ShouldForget(new FileOpResult("a", false, "没有权限")));
    }

    [Fact]
    public void RemovedFileKeepsTrashPath()
    {
        var path = Path.Combine(_dir, "t.db");
        using var db = new PluginDatabase(path);
        db.SaveRemovedFile("foo", "Foo", "/Library/Audio/Plug-Ins/VST3/Foo.vst3", PluginFormat.Vst3, 10, DateTime.UtcNow, "/Users/me/.Trash/Foo 2.vst3");
        db.SaveRemovedFile("bar", "Bar", @"C:\x\Bar.dll", PluginFormat.Vst2, 10, DateTime.UtcNow);

        var files = db.LoadRemovedFiles().ToDictionary(f => f.PluginKey);

        Assert.Equal("/Users/me/.Trash/Foo 2.vst3", files["foo"].TrashPath);
        Assert.Equal("", files["bar"].TrashPath);
    }

    [Fact]
    public void FinderOutputParsesEachLine()
    {
        var lines = FinderOutput.Parse(
            ["ok\t0\t/Users/me/.Trash/Foo.vst3/\r", "error\t-128\tUser canceled.", "", "ok\t0\t/extra"], 2);

        Assert.Equal(2, lines.Count);
        Assert.Equal(new FinderOutput.Line(true, 0, "/Users/me/.Trash/Foo.vst3"), lines[0]);
        Assert.Equal(new FinderOutput.Line(false, -128, "User canceled."), lines[1]);
    }

    // ---------- Mac ----------

    [MacFact]
    public void TrashLocationOnlyAcceptsTrashFolders()
    {
        const string home = "/Users/me";
        const string original = "/Library/Audio/Plug-Ins/VST3/Foo.vst3";

        Assert.True(TrashLocation.IsInTrash("/Users/me/.Trash/Foo.vst3", original, home));
        Assert.True(TrashLocation.IsInTrash("/Users/me/.Trash/Foo 2.vst3", original, home));
        Assert.True(TrashLocation.IsInTrash("/Volumes/Plugins/.Trashes/501/Foo.vst3", original, home));
        Assert.False(TrashLocation.IsInTrash("", original, home));
        Assert.False(TrashLocation.IsInTrash("/Users/me/Documents/Foo.vst3", original, home));
        Assert.False(TrashLocation.IsInTrash("/Users/me/.Trash/Foo.component", original, home));
        Assert.False(TrashLocation.IsInTrash("/Users/me/.Trash/../Documents/Foo.vst3", original, home));
        Assert.False(TrashLocation.IsInTrash("Foo.vst3", original, home));
    }

    [MacFact]
    public void MacGuardAcceptsBundlesInPluginFolders()
    {
        var root = Path.Combine(_dir, "Components");
        var component = Bundle("Components/Foo.component");
        var vst = Bundle("Components/Bar.vst");
        var app = Bundle("Components/Baz.app");

        Assert.Null(RemovalGuard.Reject(component, [root]));
        Assert.Null(RemovalGuard.Reject(vst, [root]));
        Assert.Equal("不是插件文件", RemovalGuard.Reject(app, [root]));
        Assert.Equal("不在插件文件夹里", RemovalGuard.Reject(component, []));
        Assert.NotNull(RemovalGuard.Reject(root, [root]));
        Assert.NotNull(RemovalGuard.Reject(component, ["/"]));
        Assert.NotNull(RemovalGuard.Reject(component, [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]));
        Assert.Equal("在系统文件夹里", RemovalGuard.Reject("/System/Library/Foo.component", ["/System/Library"]));
    }

    [MacFact]
    public void MacGuardRejectsLinks()
    {
        var root = Path.Combine(_dir, "VST3");
        var real = Bundle("elsewhere/Foo.vst3");
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "Foo.vst3");
        Directory.CreateSymbolicLink(link, real);

        Assert.Equal("路径里有链接", RemovalGuard.Reject(link, [root]));
    }

    [MacFact]
    public async Task MacTrashRoundTrip()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var root = Path.Combine(_dir, "VST3");
        var bundle = Bundle("VST3/WhatPluginTrashTest.vst3");
        var trash = new MacPluginTrash();

        var moved = Assert.Single(await trash.MoveAsync([bundle], [root]));

        Assert.True(moved.Ok, moved.Error);
        Assert.False(Directory.Exists(bundle));
        Assert.NotNull(moved.TrashPath);
        Assert.True(TrashLocation.IsInTrash(moved.TrashPath!, bundle), moved.TrashPath);
        Assert.True(Directory.Exists(moved.TrashPath));

        var file = new RemovedFile(bundle, "k", "WhatPluginTrashTest", PluginFormat.Vst3, 1, DateTime.UtcNow, moved.TrashPath!);
        var back = await trash.RestoreAsync(file, [root]);

        Assert.True(back.Ok, back.Error);
        Assert.Equal("假插件", File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")));
        Assert.False(Directory.Exists(moved.TrashPath));
    }

    [MacFact]
    public async Task MacTrashRefusesWithoutTouching()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var outside = Bundle("outside/Foo.vst3");
        var root = Path.Combine(_dir, "VST3");
        Directory.CreateDirectory(root);
        var trash = new MacPluginTrash();

        var moved = Assert.Single(await trash.MoveAsync([outside], [root]));
        Assert.False(moved.Ok);
        Assert.True(Directory.Exists(outside));

        // 记下的废纸篓位置不在废纸篓里：不搬。
        var inside = Path.Combine(root, "Foo.vst3");
        var back = await trash.RestoreAsync(new RemovedFile(inside, "k", "Foo", PluginFormat.Vst3, 1, DateTime.UtcNow, outside), [root]);
        Assert.False(back.Ok);
        Assert.Equal(PluginTrash.Gone, back.Error);
        Assert.True(Directory.Exists(outside));
        Assert.False(Directory.Exists(inside));
    }
}
