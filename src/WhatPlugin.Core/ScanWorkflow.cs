using Microsoft.Data.Sqlite;

namespace WhatPlugin.Core;

/// <summary>两种扫描各管哪些格式。</summary>
public static class ScanFormats
{
    /// <summary>“扫描新增”和“完整重扫”：VST3，同时记录 CLAP、AAX 的文件信息，Mac 上还有 AU。</summary>
    public static readonly IReadOnlyCollection<PluginFormat> Main = [PluginFormat.Vst3, PluginFormat.Clap, PluginFormat.Aax, PluginFormat.Au];

    /// <summary>VST2 只读文件信息，单独一个按钮。</summary>
    public static readonly IReadOnlyCollection<PluginFormat> Vst2 = [PluginFormat.Vst2];
}

/// <summary>
/// 扫描要用到的、和这台电脑有关的东西：扫描器在哪、默认插件目录、DAW 的记录。
/// 界面测试换成临时目录里的假数据，不会读到真实的插件和 DAW 配置。
/// </summary>
public sealed record ScanEnvironment(
    string ScannerPath,
    Func<IReadOnlyList<(string Path, PluginFormat Format)>> DefaultRoots,
    Func<HostPluginIndex> Hosts,
    Func<IReadOnlyList<HostBlacklistEntry>> Blacklist)
{
    public static ScanEnvironment ThisComputer(string scannerPath) =>
        new(scannerPath, PluginDiscovery.DefaultRoots, () => HostPluginCache.LoadAll(), () => HostBlacklist.Load());

    /// <summary>默认目录加上用户在设置里添加的 VST2 目录。</summary>
    public IReadOnlyList<(string Path, PluginFormat Format)> Roots(IEnumerable<string> extraVst2Folders) =>
        PluginDiscovery.Roots(DefaultRoots(), extraVst2Folders);
}

/// <param name="ForgetMissing">移除 VST2 文件夹后：没写过评价的缺失插件从列表里去掉。</param>
/// <param name="RecordMissing">垃圾桶里装着的插件这次扫不到了，记进卸载历史（在本软件外卸载的）。</param>
public sealed record ScanRequest(bool Full, IReadOnlyCollection<PluginFormat> Formats, bool ForgetMissing = false, bool RecordMissing = true);

/// <param name="Message">扫描结束后状态栏上的一句话。</param>
/// <param name="GoneFromTrash">这次新记进卸载历史的插件。</param>
public sealed record ScanOutcome(string Message, ScanSummary? Summary, IReadOnlyList<PluginRow> GoneFromTrash);

/// <summary>
/// 一次扫描的完整流程：扫描、保存、清理、记卸载历史，再拼出结果说明。Windows 版和跨平台版共用。
/// 不打开插件界面，也不改动插件文件。
/// </summary>
public static class ScanWorkflow
{
    /// <param name="beforeReload">扫描完、重新读取之前调用：扫描期间界面上可能还在编辑，先保存。</param>
    public static async Task<ScanOutcome> RunAsync(
        PluginDatabase database,
        ScanEnvironment environment,
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancel,
        Action? beforeReload = null)
    {
        if (!File.Exists(environment.ScannerPath))
            return new($"找不到扫描器 {Path.GetFileName(environment.ScannerPath)}，请重新生成程序。", null, []);

        var before = database.LoadEffects();
        var rememberedBefore = before.Where(TrashMemory.IsRemembered).Select(p => p.Id).ToHashSet();
        var trashInstalledBefore = before.Where(p => p.User.Hidden && !p.IsMissing).Select(p => p.Id).ToHashSet();
        try
        {
            var service = new ScanService(database, new ScannerRunner(environment.ScannerPath, TimeSpan.FromSeconds(60)));
            var roots = environment.Roots(database.LoadVst2Folders());
            var summary = await Task.Run(
                () => service.ScanAsync(roots, new ScanOptions(Full: request.Full, Formats: request.Formats, HostInfo: environment.Hosts()), progress, cancel),
                CancellationToken.None);

            beforeReload?.Invoke();
            if (request.ForgetMissing && !summary.Cancelled)
                database.DeleteMissingWithoutUserData();
            var after = database.LoadEffects();

            IReadOnlyList<PluginRow> gone = request.RecordMissing && !summary.Cancelled
                ? UninstallHistory.NewlyMissing(trashInstalledBefore, after)
                : [];
            if (gone.Count > 0)
            {
                var now = DateTime.UtcNow;
                database.AddUninstallRecords(gone.Select(p => UninstallHistory.From(p, UninstallWay.FoundMissing, "", now)));
            }
            if (!summary.Cancelled)
                Quietly(() => database.RecordUsage(UsageKinds.Scan, after.Count));

            var message = Describe(summary, after.Count) + CheckReturnedFiles(database);
            var returned = TrashMemory.Returned(rememberedBefore, after);
            if (returned.Count > 0)
                message += returned.Count == 1
                    ? $"。{returned[0].Name} 以前卸载过，又装回来了，仍在垃圾桶里（{TrashMemory.Describe(returned[0])}）"
                    : $"。有 {returned.Count} 款以前卸载过的插件又装回来了，仍在垃圾桶里：{string.Join("、", returned.Select(p => p.Name))}";
            if (gone.Count > 0)
                message += $"。垃圾桶里的 {string.Join("、", gone.Take(5).Select(p => p.Name))}{(gone.Count > 5 ? $" 等 {gone.Count} 款" : "")} 已经不在了，已记进卸载历史";
            return new(message, summary, gone);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new("扫描出错：" + error.Message, null, []);
        }
    }

    public static string Describe(ScanSummary s, int total)
    {
        var scanned = s.Loaded + s.ModuleInfo + s.FileInfoOnly + s.LoadFailed + s.TimedOut;
        var parts = new List<string>
        {
            s.Cancelled ? "扫描已停止" : "扫描完成",
            $"共 {total} 款效果器",
            $"本次处理 {scanned} 个文件，跳过未变化的 {s.Skipped} 个",
        };
        if (s.LoadFailed > 0)
            parts.Add($"{s.LoadFailed} 个读取失败");
        if (s.TimedOut > 0)
            parts.Add($"{s.TimedOut} 个超时");
        return string.Join("，", parts);
    }

    /// <summary>以前移走的文件又出现了（多半是官方更新或修复装回来的）：从“已移除的文件”里去掉，提醒一句。</summary>
    public static string CheckReturnedFiles(PluginDatabase database)
    {
        var returned = database.LoadRemovedFiles().Where(r => RemovalPlanner.PathExists(r.Path)).ToList();
        // 同一款的几个格式一起回来，算一次。
        foreach (var plugin in returned.GroupBy(r => r.PluginKey))
            Quietly(() => database.RecordUsage(UsageKinds.Reappeared, 1, plugin.Max(r => r.RemovedUtc).Ticks, plugin.Key));
        foreach (var file in returned)
            database.DeleteRemovedFile(file.Path);
        return returned.Count == 0
            ? ""
            : $"。注意：{string.Join("、", returned.Select(r => $"{r.PluginName} 的 {PluginLabels.Format(r.Format)}").Distinct().Take(5))} 又回来了，可能被官方更新或修复装回";
    }

    /// <summary>使用统计写不进去不影响扫描。</summary>
    private static void Quietly(Action record)
    {
        try
        {
            record();
        }
        catch (SqliteException)
        {
        }
    }
}

/// <summary>设置页里的一个插件文件夹。</summary>
public sealed record ScanFolderInfo(string Path, string Note, bool Removable);

/// <summary>DAW 用过、但还没加进扫描的 VST2 文件夹。</summary>
public sealed record SuggestedFolder(string Path, IReadOnlyList<string> Daws)
{
    public string Label => Daws.Count > 0 ? $"DAW（{string.Join("、", Daws)}）在用这个文件夹：" : "DAW 在用这个文件夹：";
}

/// <summary>设置页“插件扫描”两张卡片上显示的内容。</summary>
public sealed record ScanSettings(
    IReadOnlyList<ScanFolderInfo> MainFolders,
    IReadOnlyList<ScanFolderInfo> Vst2Folders,
    IReadOnlyList<SuggestedFolder> Suggestions,
    string MainSummary,
    string Vst2Summary,
    string Vst2HostNote)
{
    public static string MainFoldersNote => AppPlatform.IsMac
        ? "使用系统标准位置，同时读取 AU，记录 CLAP 和 AAX 的文件信息。"
        : "使用系统标准位置，同时记录 CLAP 和 AAX 的文件信息。";

    public static ScanSettings Build(PluginDatabase database, ScanEnvironment environment)
    {
        var scans = database.LoadFileScans().Values.ToList();
        var custom = database.LoadVst2Folders();
        var defaults = environment.DefaultRoots();
        var roots = PluginDiscovery.Roots(defaults, custom);

        var main = roots
            .Where(r => r.Format != PluginFormat.Vst2 && Directory.Exists(r.Path))
            .Select(r => new ScanFolderInfo(r.Path, PluginLabels.Format(r.Format) + "，" + FileCount(scans, r.Path), false))
            .ToList();

        var vst2 = custom
            .Select(f => new ScanFolderInfo(f, Directory.Exists(f) ? "我添加的，" + FileCount(scans, f) : "我添加的，现在找不到这个文件夹", true))
            .Concat(defaults
                .Where(d => d.Format == PluginFormat.Vst2 && Directory.Exists(d.Path) && !custom.Contains(d.Path, StringComparer.OrdinalIgnoreCase))
                .Select(d => new ScanFolderInfo(d.Path, "默认，" + FileCount(scans, d.Path), false)))
            .ToList();

        var known = roots.Select(r => r.Path).ToList();
        var hosts = environment.Hosts();
        var suggestions = HostPluginCache.TopFolders(hosts.KnownPaths)
            .Where(Directory.Exists)
            .Where(f => !known.Any(k => k.Equals(f, StringComparison.OrdinalIgnoreCase) || HostPluginCache.IsInside(f, k)))
            .Select(f => new SuggestedFolder(f, hosts.DawsUsing(f)))
            .ToList();

        var hostNote = hosts.Daws.Count > 0
            ? $"子文件夹会一起扫描。效果器和音源的区分来自 DAW 自己的记录（这台电脑上找到了 {string.Join("、", hosts.Daws)} 的记录），新装的 VST2 要先在 DAW 里扫描一次。"
            : "子文件夹会一起扫描。效果器和音源的区分来自 DAW 自己的记录（支持 Cubase、Nuendo、Studio One、REAPER、Cakewalk、FL Studio），新装的 VST2 要先在 DAW 里扫描一次。这台电脑上还没找到这些 DAW 的记录。";

        return new(main, vst2, suggestions, DescribeMain(scans), DescribeVst2(scans), hostNote);
    }

    private static string FileCount(List<FileScanResult> scans, string folder)
    {
        var count = scans.Count(s => HostPluginCache.IsInside(s.Path, folder));
        return count > 0 ? $"{count} 个文件" : "还没有扫描到文件";
    }

    private static string DescribeVst2(List<FileScanResult> scans)
    {
        var files = scans.Where(s => s.Format == PluginFormat.Vst2).ToList();
        if (files.Count == 0)
            return "还没有扫描过 VST2。";
        var classes = files.SelectMany(f => f.Classes).ToList();
        var instruments = classes.Count(c => c.IsInstrument == true);
        var effects = classes.Count(c => c.IsInstrument == false);
        var unknown = classes.Count - instruments - effects;
        var text = $"已扫描 {files.Count} 个文件：效果器 {effects} 个，音源 {instruments} 个（不显示）";
        return unknown > 0 ? text + $"，DAW 没有记录的 {unknown} 个（只能按名称判断）" : text;
    }

    private static string DescribeMain(List<FileScanResult> scans)
    {
        var files = scans.Count(s => s.Format != PluginFormat.Vst2);
        return files > 0 ? $"已扫描 {files} 个文件。" : "还没有扫描过。";
    }
}

/// <summary>健康检查页左边的一项。<see cref="Kind"/> 为空的是“按厂家查更新”。</summary>
public sealed record HealthCategoryRow(string Title, string Explanation, int Count, HealthKind? Kind)
{
    public override string ToString() => $"{Title} {Count}";
}

/// <summary>健康检查列表里的一行：一款插件，或者“按厂家查更新”里的一个厂家。</summary>
public sealed record HealthRow(string Title, string Subtitle, string Detail, PluginRow? Plugin, VendorUpdate? Vendor)
{
    public override string ToString() => Title;
}

public sealed record HealthFileRow(string Summary, string Path)
{
    public override string ToString() => Path;
}

/// <summary>健康检查页上的分类、列表和文字。只读：不运行插件，不改动任何文件。</summary>
public static class HealthListing
{
    public const string UpdatesTitle = "按厂家查更新";

    public const string UpdatesExplanation =
        "没有可靠的公开版本数据库，自动比对容易误报，所以这里列出每个厂家通常用什么工具更新。这个软件自己不联网。";

    public static IReadOnlyList<HealthCategoryRow> Categories(HealthReport report, IReadOnlyList<VendorUpdate> updates) =>
        report.Categories
            .Select(c => new HealthCategoryRow(c.Title, c.Explanation, c.Count, c.Kind))
            .Append(new HealthCategoryRow(UpdatesTitle, UpdatesExplanation, updates.Count, null))
            .ToList();

    public static IReadOnlyList<HealthRow> Rows(HealthCategoryRow category, HealthReport report, IReadOnlyList<VendorUpdate> updates) =>
        category.Kind is { } kind
            ? report.Issues.Where(i => i.Kind == kind)
                .OrderBy(i => i.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => new HealthRow(i.Plugin.Name, PluginLabels.Vendor(i.Plugin), i.Detail, i.Plugin, null))
                .ToList()
            : updates.Select(v => new HealthRow(v.Vendor, $"{v.Count} 款", "通常用 " + v.Tool, null, v)).ToList();

    /// <summary>右边详情标题下面那句话。</summary>
    public static string Note(HealthRow row) => row.Vendor != null ? "通常用 " + row.Vendor.Tool + " 检查更新" : row.Detail;

    public static IReadOnlyList<HealthFileRow> Files(PluginRow? plugin) =>
        plugin?.Files.Select(f => new HealthFileRow(
            $"{PluginLabels.Format(f.Format)}  {PluginLabels.Arch(f.Arch)}" + (f.Version.Length > 0 ? $"  {f.Version}" : ""), f.Path)).ToList() ?? [];

    /// <summary>“查更新”用浏览器搜索的地址。</summary>
    public static string SearchUrl(HealthRow row) =>
        row.Plugin is { } plugin ? UpdateGuide.SearchUrl(plugin.Vendor, plugin.Name) : UpdateGuide.SearchUrl(row.Title);

    public static string Describe(HealthReport report)
    {
        var text = report.NeedsAttention > 0 ? $"{report.NeedsAttention} 款插件需要留意" : "没有影响使用的问题";
        if (report.UnmatchedBlacklistFiles > 0)
            text += $"。宿主黑名单里另有 {report.UnmatchedBlacklistFiles} 个文件不在扫描结果中，可能是音源或已卸载";
        return text;
    }
}
