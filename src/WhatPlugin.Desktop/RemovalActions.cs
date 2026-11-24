using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 删除多余版本、卸载垃圾桶里的插件和恢复文件。只有这里会动插件文件，而且都要用户先确认；
/// 文件只移到回收站（Mac 上是废纸篓），可以恢复。流程和 Windows 版一样，文字在 Core 的 RemovalText 里。
/// Windows 上和 Windows 版一样，找到官方卸载程序时优先用它（在官方窗口里操作）；Mac 上只有“移到废纸篓”和“先不卸载”。
/// </summary>
public sealed class RemovalActions(PluginDatabase database, Func<IReadOnlyList<string>> pluginRoots)
{
    private static readonly PluginFormat[] EveryFormat = Enum.GetValues<PluginFormat>();

    /// <summary>移动文件的方式。测试换成移到临时目录的实现。</summary>
    public IPluginTrash Trash { get; set; } = PluginTrash.ForThisComputer();

    /// <summary>重新扫描（参数是 RecordMissing）。由主窗口提供，扫描结束后各页会重新读取。</summary>
    public Func<ScanRequest, Task>? Rescan { get; set; }

    /// <summary>正在扫描时不能开始。</summary>
    public Func<bool> IsScanning { get; set; } = () => false;

    /// <summary>“已安装的应用”里登记的卸载程序。只有 Windows 上有，测试换成假的列表。</summary>
    public Func<IReadOnlyList<InstalledProgram>> Programs { get; set; } =
        () => OperatingSystem.IsWindows() ? InstalledPrograms.Load() : [];

    /// <summary>运行官方卸载命令（不加静默参数），返回失败原因。测试换成脚本，不真的启动。</summary>
    public Func<InstalledProgram, string?> StartUninstaller { get; set; } = UninstallerLauncher.Start;

    public bool Busy { get; private set; }

    public event Action<string>? StatusChanged;

    /// <summary>垃圾桶、卸载历史或已移除文件变了，页面要重新读取。</summary>
    public event Action? Changed;

    private void Status(string text) => StatusChanged?.Invoke(text);

    private bool CanStart()
    {
        if (!Busy && !IsScanning())
            return true;
        Status("正在扫描或卸载，请等它结束");
        return false;
    }

    /// <summary>
    /// 打开 Waves 完全卸载工具（只在 Windows 上有），返回它是否动过文件。由主窗口提供，测试换成脚本。
    /// 测试目录模式下返回 false：假目录里的变化不该影响插件库。
    /// </summary>
    public Func<Task<bool>>? ShowWavesTool { get; set; }

    /// <summary>
    /// Waves 完全卸载工具。卸载后重新扫描，原来在垃圾桶里、现在文件都不在的 Waves 插件记进卸载历史，
    /// 和 Windows 版一样。
    /// </summary>
    public async Task OpenWavesToolAsync()
    {
        if (ShowWavesTool == null || !CanStart())
            return;
        Busy = true;
        try
        {
            var trashBefore = database.LoadPlugins().Where(p => p.User.Hidden && !p.IsMissing).Select(p => p.Id).ToHashSet();
            if (!await ShowWavesTool())
                return;
            await RescanAsync(recordMissing: false);
            var gone = database.LoadPlugins()
                .Where(p => RemovalPlanner.IsWaves(p) && p.Files.All(f => !RemovalPlanner.PathExists(f.Path)))
                .ToList();
            var now = DateTime.UtcNow;
            database.AddUninstallRecords(gone.Where(p => trashBefore.Contains(p.Id))
                .Select(p => UninstallHistory.From(p, UninstallWay.WavesTool, "", now)));
            database.ForgetUninstalled(gone.Select(p => p.Id).ToList());
            Changed?.Invoke();
        }
        finally
        {
            Busy = false;
        }
    }

    private Task RescanAsync(bool recordMissing) =>
        Rescan?.Invoke(new ScanRequest(false, EveryFormat, RecordMissing: recordMissing)) ?? Task.CompletedTask;

    // ---------- 详情：已安装版本 ----------

    /// <summary>一款插件的已安装版本，和能不能勾选删除。</summary>
    public IReadOnlyList<VersionInfo> Versions(PluginRow row)
    {
        var waves = RemovalPlanner.IsWaves(row);
        var owners = RemovalPlanner.OwnersByPath(database.LoadPlugins());
        return row.Files.Select(file =>
        {
            var exists = RemovalPlanner.PathExists(file.Path);
            var shared = owners.TryGetValue(file.Path, out var ids) && ids.Any(id => id != row.Id);
            var note = RemovalText.VersionNote(exists, shared, () => RemovalPlanner.SizeOf(file.Path), file.Error);
            return new VersionInfo(file, RemovalText.VersionTitle(file), note, exists && !shared && !waves && row.Files.Count > 1);
        }).ToList();
    }

    public IReadOnlyList<RemovedFile> RemovedFor(PluginRow row) =>
        database.LoadRemovedFiles().Where(r => r.PluginKey == row.Key).ToList();

    public async Task DeleteVersionsAsync(IDialogs dialogs, PluginRow row, IReadOnlyList<MergedFile> chosen, int projects)
    {
        if (!CanStart())
            return;
        var problem = RemovalPlanner.CheckFormatRemoval(row, chosen.Select(f => f.Path).ToList(), database.LoadPlugins());
        if (problem != null)
        {
            Status(problem);
            return;
        }
        var msi = OperatingSystem.IsWindows() && UninstallMatcher.Find(row, InstalledPrograms.Load()).Exact is { IsMsi: true };
        if (!await dialogs.Confirm("删除这些版本？", RemovalText.ConfirmDeleteVersions(chosen, projects, msi), RemovalText.MoveConfirm, danger: true))
            return;

        Busy = true;
        try
        {
            var files = chosen.Select(f => (row, new RemovableFile(f.Path, f.Format, RemovalPlanner.SizeOf(f.Path)))).ToList();
            var results = await MoveAsync(files);
            await RescanAsync(recordMissing: true);
            Changed?.Invoke();
            Status(RemovalText.VersionsMoved(results));
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<IReadOnlyList<FileOpResult>> MoveAsync(IReadOnlyList<(PluginRow Plugin, RemovableFile File)> files)
    {
        Status(RemovalText.Moving);
        var results = await Trash.MoveAsync(files.Select(f => f.File.Path).ToList(), pluginRoots());
        foreach (var (plugin, file) in files)
        {
            var result = results.First(r => string.Equals(r.Path, file.Path, StringComparison.OrdinalIgnoreCase));
            if (result.Ok)
                database.SaveRemovedFile(plugin.Key, plugin.Name, file.Path, file.Format, file.Size, DateTime.UtcNow, result.TrashPath ?? "");
        }
        return results;
    }

    public async Task RestoreAsync(RemovedFile file)
    {
        if (!CanStart())
            return;
        Busy = true;
        try
        {
            Status(RemovalText.Restoring);
            var result = await Trash.RestoreAsync(file, pluginRoots());
            if (RemovalText.ShouldForget(result))
                database.DeleteRemovedFile(file.Path);
            if (result.Ok)
                await RescanAsync(recordMissing: true);
            Changed?.Invoke();
            Status(RemovalText.Restored(result));
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task ShowRemovedFilesAsync(IDialogs dialogs)
    {
        var chosen = await dialogs.RemovedFiles(database.LoadRemovedFiles());
        if (chosen != null)
            await RestoreAsync(chosen);
    }

    public async Task ShowHistoryAsync(IDialogs dialogs)
    {
        if (await dialogs.UninstallHistory(database, database.LoadPlugins()))
            Changed?.Invoke();
    }

    // ---------- 垃圾桶：卸载 ----------

    /// <returns>卸载结果的报告；取消或没有可做的事时返回 null。</returns>
    public async Task<UninstallReport?> UninstallAsync(IDialogs dialogs, IReadOnlyList<PluginRow> selected, IReadOnlyDictionary<string, PluginUsage> usage)
    {
        if (!CanStart())
            return null;
        var installed = selected.Where(p => !p.IsMissing).ToList();
        if (installed.Count == 0)
        {
            Status(selected.Count > 0 ? "选中的插件已经卸载了" : "先在列表里选中要卸载的插件");
            return null;
        }

        var waves = installed.Where(RemovalPlanner.IsWaves).ToList();
        var others = installed.Except(waves).ToList();
        if (waves.Count > 0)
        {
            var answer = await dialogs.WavesNotice(waves, others.Count);
            if (answer == WavesAnswer.FullTool)
            {
                await OpenWavesToolAsync();
                return null;
            }
            if (answer != WavesAnswer.SkipWaves || others.Count == 0)
                return null;
        }

        var plans = RemovalPlanner.Plan(others, database.LoadPlugins(), Programs(), usage);
        var choices = await dialogs.Uninstall(plans);
        if (choices == null)
            return null;
        var chosen = choices.Where(c => c.Method != UninstallMethod.Skip).ToList();
        if (chosen.Count == 0)
            return null;

        Busy = true;
        try
        {
            var recycle = chosen.Where(c => c.Method == UninstallMethod.RecycleFiles)
                .SelectMany(c => c.Plan.Files.Select(f => (c.Plan.Plugin, File: f))).ToList();
            IReadOnlyList<FileOpResult> moved = recycle.Count > 0 ? await MoveAsync(recycle) : [];

            // 官方卸载程序一个一个打开，用户在官方窗口里操作完再继续。
            var programs = chosen.Where(c => c.Method == UninstallMethod.Official).Select(c => c.Program!).DistinctBy(p => p.Key).ToList();
            for (var i = 0; i < programs.Count; i++)
            {
                var program = programs[i];
                var problem = StartUninstaller(program);
                var last = i == programs.Count - 1;
                var goOn = await dialogs.Confirm(problem == null ? "官方卸载程序已打开" : "没能打开官方卸载程序",
                    (problem ?? $"请在“{program.Name}”的卸载窗口里完成操作。完成后再点下面的按钮。")
                    + (last ? "" : $"\n\n接下来还有 {programs.Count - i - 1} 个卸载程序。"),
                    last ? "重新扫描" : "下一个");
                if (!goOn)
                {
                    Status("已停止。卸载程序完成后，可以到“设置”里重新扫描");
                    return null;
                }
            }
            // 这次卸载的按选的方式和原因记进历史，扫描时不再当成“发现已不在”重复记。
            await RescanAsync(recordMissing: false);
            var report = RemovalText.Report(chosen, moved, DateTime.UtcNow);
            database.AddUninstallRecords(report.History);
            database.ForgetUninstalled(report.Gone);
            Changed?.Invoke();
            await dialogs.ShowText("卸载结果", report.Text, "卸载结果.txt");
            Status(report.Status);
            return report;
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>详情里的一个已安装版本。</summary>
public sealed record VersionInfo(MergedFile File, string Title, string Note, bool CanSelect);
