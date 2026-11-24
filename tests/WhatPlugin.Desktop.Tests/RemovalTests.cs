using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>
/// 测试用的回收处：不碰系统回收站或废纸篓，把文件挪到临时目录里的 trash 文件夹。
/// 和真的实现一样先过 RemovalGuard，并且每个路径都确认在临时目录里。
/// </summary>
public sealed class TempTrash(string folder) : IPluginTrash
{
    public string Name => PluginTrash.Name;

    public List<string> Moved { get; } = [];

    public Task<IReadOnlyList<FileOpResult>> MoveAsync(IReadOnlyList<string> paths, IReadOnlyList<string> extraRoots)
    {
        Directory.CreateDirectory(folder);
        var results = paths.Select(path =>
        {
            Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
            var rejected = RemovalGuard.Reject(path, extraRoots);
            if (rejected != null)
                return new FileOpResult(path, false, rejected);
            var target = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            if (Directory.Exists(path))
                Directory.Move(path, target);
            else
                File.Move(path, target);
            Moved.Add(path);
            return new FileOpResult(path, true, TrashPath: target);
        }).ToList();
        return Task.FromResult<IReadOnlyList<FileOpResult>>(results);
    }

    public Task<FileOpResult> RestoreAsync(RemovedFile file, IReadOnlyList<string> extraRoots)
    {
        Assert.StartsWith(Path.GetTempPath(), file.Path, StringComparison.OrdinalIgnoreCase);
        var rejected = RemovalGuard.Reject(file.Path, extraRoots, mustExist: false);
        if (rejected != null)
            return Task.FromResult(new FileOpResult(file.Path, false, rejected));
        if (!RemovalPlanner.PathExists(file.TrashPath))
            return Task.FromResult(new FileOpResult(file.Path, false, PluginTrash.Gone));
        if (Directory.Exists(file.TrashPath))
            Directory.Move(file.TrashPath, file.Path);
        else
            File.Move(file.TrashPath, file.Path);
        return Task.FromResult(new FileOpResult(file.Path, true));
    }
}

/// <summary>删除多余版本、垃圾桶卸载、恢复和卸载历史。插件都是临时目录里的假插件。</summary>
public sealed class RemovalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-removal-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;
    private readonly TestEnvironment _env;
    private readonly ScriptedDialogs _dialogs = new();
    private readonly TempTrash _trash;

    public RemovalTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        _database = new PluginDatabase(_dbPath);
        _env = new TestEnvironment(Path.Combine(_dir, "env"));
        _trash = new TempTrash(Path.Combine(_dir, "trash"));
        // 会移动文件的测试只在这个临时目录里做。
        Assert.StartsWith(Path.GetTempPath(), _env.Vst3, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private readonly List<InstalledProgram> _programs = [];
    private readonly List<string> _started = [];

    private MainWindow Open()
    {
        var window = new MainWindow(_database, _dbPath, _env.Scan) { Width = 1440, Height = 900 };
        window.Removal.Trash = _trash;
        // 不读真实的“已安装的应用”，也不真的启动卸载程序。
        window.Removal.Programs = () => _programs;
        window.Removal.StartUninstaller = program =>
        {
            _started.Add(program.Name);
            return null;
        };
        window.Show();
        Settle(window);
        Wait(window.RunScanAsync(new ScanRequest(false, ScanFormats.Main)), window);
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
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "没有结束");
        }
        task.GetAwaiter().GetResult();
        Settle(window);
    }

    private void Blacklist(string name, string reason = "太吃 CPU")
    {
        var row = _database.LoadEffects().Single(p => p.Name == name);
        _database.SaveUserData(row.Id, row.User with { Hidden = true, BlacklistReason = reason });
    }

    private PluginLibraryView Trash(MainWindow window)
    {
        window.Navigate("Blacklist");
        Settle(window);
        var view = window.Library("Blacklist")!;
        view.Load();
        view.Dialogs = _dialogs;
        Settle(window);
        return view;
    }

    private static IReadOnlyList<UninstallChoice> MoveAll(IReadOnlyList<UninstallPlan> plans, string reason = "") =>
        plans.Select(p => new UninstallChoice(p, UninstallMethod.RecycleFiles, null, reason)).ToList();

    [AvaloniaFact]
    public void UninstallMovesFilesAndRecordsHistory()
    {
        var bundle = _env.AddVst3("Pro-Q 4", "FabFilter");
        _env.AddVst3("Keep", "Acme");
        var window = Open();
        Blacklist("Pro-Q 4");
        Blacklist("Keep");
        var view = Trash(window);

        Assert.False(view.UninstallButton.IsEnabled);
        view.SelectMany("Pro-Q 4");
        Settle(window);
        Assert.Equal("卸载选中的 1 款…", view.UninstallButton.Content);

        _dialogs.Answers.Enqueue((Func<IReadOnlyList<UninstallPlan>, IReadOnlyList<UninstallChoice>?>)(plans => MoveAll(plans, "不用了")));
        _dialogs.Answers.Enqueue(null);
        Wait(view.UninstallSelectedAsync(), window);

        var plan = Assert.Single(Assert.Single(_dialogs.UninstallPlans));
        Assert.Equal(bundle, Assert.Single(plan.Files).Path);
        Assert.Equal([bundle], _trash.Moved);
        Assert.False(Directory.Exists(bundle));
        Assert.Contains("Pro-Q 4：文件已全部移除", Assert.Single(_dialogs.Shown));
        Assert.Equal("卸载结束：1 款已移除，0 款还有文件", window.Status);

        var record = Assert.Single(_database.LoadUninstallHistory());
        Assert.Equal(("Pro-Q 4", UninstallWay.RecycleFiles, "不用了", "太吃 CPU"), (record.Name, record.Way, record.Reason, record.TrashReason));
        // 写过原因，留在垃圾桶里显示为已卸载。
        view = window.Library("Blacklist")!;
        Assert.True(view.Shown.Single(p => p.Name == "Pro-Q 4").IsUninstalled);
        Assert.Equal("卸载历史（1）", view.UninstallHistoryButton.Content);
        Assert.Equal("已移除的文件（1）", view.RemovedFilesButton.Content);
        Assert.True(view.RemovedFilesButton.IsVisible);
        Assert.True(Directory.Exists(_env.Vst3 + Path.DirectorySeparatorChar + "Keep.vst3"));
    }

    [AvaloniaFact]
    public void RestoreBringsTheFileBack()
    {
        var bundle = _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        Blacklist("Pro-Q 4");
        var view = Trash(window);
        view.SelectMany("Pro-Q 4");
        _dialogs.Answers.Enqueue((Func<IReadOnlyList<UninstallPlan>, IReadOnlyList<UninstallChoice>?>)(plans => MoveAll(plans)));
        _dialogs.Answers.Enqueue(null);
        Wait(view.UninstallSelectedAsync(), window);
        Assert.False(Directory.Exists(bundle));

        _dialogs.Answers.Enqueue((Func<IReadOnlyList<RemovedFile>, RemovedFile?>)(files => Assert.Single(files)));
        Wait(window.Removal.ShowRemovedFilesAsync(_dialogs), window);

        Assert.True(File.Exists(Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json")));
        Assert.Empty(_database.LoadRemovedFiles());
        Assert.Equal("已恢复 " + bundle, window.Status);
        Assert.False(window.Library("Blacklist")!.Shown.Single().IsUninstalled);
    }

    [AvaloniaFact]
    public void CancelledUninstallMovesNothing()
    {
        var bundle = _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        Blacklist("Pro-Q 4");
        var view = Trash(window);
        view.SelectMany("Pro-Q 4");

        _dialogs.Answers.Enqueue(null);
        Wait(view.UninstallSelectedAsync(), window);
        _dialogs.Answers.Enqueue((Func<IReadOnlyList<UninstallPlan>, IReadOnlyList<UninstallChoice>?>)(plans =>
            plans.Select(p => new UninstallChoice(p, UninstallMethod.Skip, null)).ToList()));
        Wait(view.UninstallSelectedAsync(), window);

        Assert.True(Directory.Exists(bundle));
        Assert.Empty(_trash.Moved);
        Assert.Empty(_database.LoadUninstallHistory());
    }

    [AvaloniaFact]
    public void WavesOnlyGetsAnExplanation()
    {
        var waves = _env.AddVst3("CLA-76", "Waves");
        var other = _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        Blacklist("CLA-76");
        Blacklist("Pro-Q 4");
        var view = Trash(window);
        view.SelectMany("CLA-76", "Pro-Q 4");

        _dialogs.Answers.Enqueue(WavesAnswer.Cancel);
        Wait(view.UninstallSelectedAsync(), window);
        Assert.Equal(["Waves｜1｜1"], _dialogs.Asked);
        Assert.Empty(_dialogs.UninstallPlans);

        _dialogs.Answers.Enqueue(WavesAnswer.SkipWaves);
        _dialogs.Answers.Enqueue((Func<IReadOnlyList<UninstallPlan>, IReadOnlyList<UninstallChoice>?>)(plans => MoveAll(plans)));
        _dialogs.Answers.Enqueue(null);
        Wait(view.UninstallSelectedAsync(), window);

        Assert.Equal("Pro-Q 4", Assert.Single(_dialogs.UninstallPlans[0]).Plugin.Name);
        Assert.True(Directory.Exists(waves));
        Assert.False(Directory.Exists(other));
    }

    [AvaloniaFact]
    public void OfficialUninstallerRunsInsteadOfMovingFiles()
    {
        var bundle = _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        Blacklist("Pro-Q 4");
        _programs.Add(new InstalledProgram("FabFilter Pro-Q 4", "FabFilter Pro-Q 4", "FabFilter", "4.0", "\"C:\\Fake\\unins000.exe\"", Path.GetDirectoryName(bundle)!, false));
        var view = Trash(window);
        view.SelectMany("Pro-Q 4");

        _dialogs.Answers.Enqueue((Func<IReadOnlyList<UninstallPlan>, IReadOnlyList<UninstallChoice>?>)(plans =>
        {
            var plan = Assert.Single(plans);
            Assert.NotNull(plan.Exact);
            var official = UninstallOptions.For(plan)[0];
            Assert.Equal(UninstallMethod.Official, official.Method);
            return [new UninstallChoice(plan, official.Method, official.Program, "不用了")];
        }));
        // 官方卸载程序把插件删掉（只在临时目录里），用户点“重新扫描”继续。
        Assert.StartsWith(Path.GetTempPath(), bundle, StringComparison.OrdinalIgnoreCase);
        window.Removal.StartUninstaller = program =>
        {
            _started.Add(program.Name);
            Directory.Delete(bundle, recursive: true);
            return null;
        };
        _dialogs.Answers.Enqueue(true);
        _dialogs.Answers.Enqueue(null);
        Wait(view.UninstallSelectedAsync(), window);

        Assert.Equal(["FabFilter Pro-Q 4"], _started);
        Assert.Empty(_trash.Moved);
        var record = Assert.Single(_database.LoadUninstallHistory());
        Assert.Equal(UninstallWay.Official, record.Way);
        Assert.Equal("不用了", record.Reason);
    }

    [AvaloniaFact]
    public void WavesFullToolRescansAndRecordsHistory()
    {
        var waves = _env.AddVst3("CLA-76", "Waves");
        _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open();
        Blacklist("CLA-76");
        var view = Trash(window);
        view.SelectMany("CLA-76");

        // 工具在临时目录里“卸载”了 Waves（真实的工具由 WavesToolTests 测）。
        Assert.StartsWith(Path.GetTempPath(), waves, StringComparison.OrdinalIgnoreCase);
        var opened = 0;
        window.Removal.ShowWavesTool = () =>
        {
            opened++;
            Directory.Delete(waves, recursive: true);
            return Task.FromResult(true);
        };
        _dialogs.Answers.Enqueue(WavesAnswer.FullTool);
        Wait(view.UninstallSelectedAsync(), window);

        Assert.Equal(1, opened);
        Assert.Empty(_dialogs.UninstallPlans);
        var record = Assert.Single(_database.LoadUninstallHistory());
        Assert.Equal("CLA-76", record.Name);
        Assert.Equal(UninstallWay.WavesTool, record.Way);
        // 写过移入原因，留在垃圾桶里显示为已卸载。
        Assert.True(_database.LoadPlugins().Single(p => p.Name == "CLA-76").IsMissing);
        Assert.Contains(_database.LoadPlugins(), p => p.Name == "Pro-Q 4");

        // 工具没动文件（或在测试目录里跑）时不重新扫描、不记历史。
        window.Removal.ShowWavesTool = () => Task.FromResult(false);
        Wait(window.Removal.OpenWavesToolAsync(), window);
        Assert.Single(_database.LoadUninstallHistory());
    }

    [AvaloniaFact]
    public void DeleteOneVersionKeepsTheOther()
    {
        var vst3 = _env.AddVst3("Pro-Q 4", "FabFilter");
        var clap = Path.Combine(_env.Vst3, "Pro-Q 4.clap");
        Directory.CreateDirectory(clap);
        File.WriteAllText(Path.Combine(clap, "plugin"), "假插件");
        var window = Open();
        var view = window.Library("Plugins")!;
        view.Dialogs = _dialogs;
        view.Select("Pro-Q 4");
        Settle(window);

        Assert.True(view.VersionsVisible);
        Assert.Equal(2, view.VersionRows.Count);
        view.VersionRows.Single(v => v.Info.File.Format == PluginFormat.Clap).Box.IsChecked = true;
        _dialogs.Answers.Enqueue(true);
        Wait(view.DeleteVersionsAsync(), window);

        Assert.Contains("删除这些版本？", _dialogs.Asked[0]);
        Assert.False(Directory.Exists(clap));
        Assert.True(Directory.Exists(vst3));
        Assert.StartsWith($"已把 1 个版本移到{PluginTrash.Name}", window.Status);
        view = window.Library("Plugins")!;
        view.Select("Pro-Q 4");
        Settle(window);
        Assert.Equal(1, view.RemovedVersionCount);
        // 只剩一个版本，不能再删。
        Assert.Empty(view.VersionRows);
    }

    [AvaloniaFact]
    public void HistoryDialogEditsAndDeletes()
    {
        _database.AddUninstallRecords([
            new UninstallRecord(0, "a", "Old Comp", "Acme", UninstallWay.RecycleFiles, "", DateTime.UtcNow, "太吵"),
            new UninstallRecord(0, "b", "Old Verb", "Acme", UninstallWay.FoundMissing, "听腻了", DateTime.UtcNow),
        ]);
        var window = new MainWindow(_database, _dbPath, _env.Scan);
        window.Show();
        var dialog = new UninstallHistoryDialog(_database, []);
        dialog.Show(window);
        Settle(dialog);

        Assert.Equal(2, dialog.ShownNames.Count);
        dialog.Search("腻");
        Settle(dialog);
        Assert.Equal(["Old Verb"], dialog.ShownNames);

        dialog.Reason("Old Comp").Text = "  延迟太大 ";
        dialog.SaveAll();
        Wait(dialog.DeleteAsync("Old Verb", () => Task.FromResult(true)), dialog);

        var record = Assert.Single(_database.LoadUninstallHistory());
        Assert.Equal("延迟太大", record.Reason);
        Assert.True(dialog.Changed);
    }

    [AvaloniaFact]
    public void UninstallDialogSummaryFollowsChoices()
    {
        var row = new PluginRow(1, "foo", "Foo", "Acme", PluginKind.Effect, [], ClassificationSource.None, false,
            [new MergedFile(Path.Combine(_dir, "Foo.vst3"), PluginFormat.Vst3, Arch.X64, "1", "", ScanStatus.ModuleInfo, null)],
            new PluginUserData(0, "", [], null, null, null, true, "太吃 CPU", null));
        var plan = new UninstallPlan(row, null, [], [new RemovableFile(row.Files[0].Path, PluginFormat.Vst3, 4096)], [], [], 0);
        var dialog = new UninstallDialog([plan]);
        dialog.Show();
        Settle(dialog);

        Assert.True(dialog.CanStart);
        Assert.Equal("太吃 CPU", dialog.ReasonBox("Foo").Text);
        dialog.MethodBox("Foo").SelectedIndex = 1;
        Settle(dialog);
        Assert.False(dialog.CanStart);
        Assert.Equal("没有要执行的操作", dialog.Summary);
        dialog.MethodBox("Foo").SelectedIndex = 0;
        dialog.ReasonBox("Foo").Text = "不用了";
        Assert.Equal(new UninstallChoice(plan, UninstallMethod.RecycleFiles, null, "不用了"), Assert.Single(dialog.Choices));
    }

    [AvaloniaFact]
    public void Screenshots()
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

        _env.AddVst3("Pro-Q 4", "FabFilter");
        var clap = Path.Combine(_env.Vst3, "Pro-Q 4.clap");
        Directory.CreateDirectory(clap);
        _env.AddVst3("Old Comp", "Acme");
        _env.AddVst3("Old Verb", "Acme");
        var window = Open();
        Blacklist("Old Comp");
        Blacklist("Old Verb", "");
        _database.AddUninstallRecords([new UninstallRecord(0, "x", "Gone Delay", "Acme", UninstallWay.RecycleFiles, "延迟太大", DateTime.UtcNow, "听腻了", 2)]);

        var library = window.Library("Plugins")!;
        library.Select("Pro-Q 4");
        Settle(window);
        library.FindControl<ScrollViewer>("DetailPanel")!.ScrollToEnd();
        Save(window, "removal-versions");

        var trash = Trash(window);
        trash.Select("Old Comp");
        trash.SelectMany("Old Comp", "Old Verb");
        Save(window, "removal-trash");

        var plans = RemovalPlanner.Plan(trash.SelectedMany.Select(p => p.Row).ToList(), _database.LoadPlugins(), [], new Dictionary<string, PluginUsage>());
        var dialog = new UninstallDialog(plans);
        dialog.Show(window);
        Save(dialog, "removal-uninstall-dialog");
        dialog.Close();

        var history = new UninstallHistoryDialog(_database, _database.LoadPlugins());
        history.Show(window);
        Save(history, "removal-history");
        history.Close();
    }
}
