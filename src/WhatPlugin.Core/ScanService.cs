using System.Collections.Concurrent;
using System.Diagnostics;

namespace WhatPlugin.Core;

/// <param name="Formats">只处理这些格式，其它格式的结果原样保留。null 表示全部。</param>
/// <param name="HostInfo">宿主记下的 VST2 信息，键是文件路径。VST2 不加载，类型和厂家只能从这里来。</param>
public sealed record ScanOptions(
    bool Full = false,
    int MaxParallel = 3,
    Func<ScanTarget, bool>? Filter = null,
    IReadOnlyCollection<PluginFormat>? Formats = null,
    HostPluginIndex? HostInfo = null);

/// <summary>串起发现、扫描、保存和合并。</summary>
public sealed class ScanService(PluginDatabase database, ScannerRunner runner)
{
    public async Task<ScanSummary> ScanAsync(
        IEnumerable<(string Path, PluginFormat Format)> roots,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var rootList = roots.ToList();
        var discovered = PluginDiscovery.Discover(rootList);
        if (options.Formats != null)
            discovered = discovered.Where(t => options.Formats.Contains(t.Format)).ToList();
        var previous = database.LoadFileScans();

        var targets = options.Filter == null ? discovered : discovered.Where(options.Filter).ToList();
        var queue = new List<(ScanTarget Target, long Size, DateTime Modified)>();
        var skipped = 0;
        foreach (var target in targets)
        {
            var (size, modified) = Fingerprint(target);
            if (!options.Full
                && previous.TryGetValue(target.Path, out var old)
                && old.Size == size
                && old.ModifiedUtc == modified
                && !HostInfoChanged(target, old, options.HostInfo))
            {
                skipped++;
                continue;
            }
            queue.Add((target, size, modified));
        }

        var results = new ConcurrentDictionary<string, FileScanResult>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var cancelled = false;
        var saveLock = new object();
        try
        {
            await Parallel.ForEachAsync(
                queue,
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallel, CancellationToken = cancel },
                async (item, token) =>
                {
                    progress?.Report(new ScanProgress(Volatile.Read(ref done), queue.Count, item.Target.Path));
                    var result = await ScanOneAsync(item.Target, item.Size, item.Modified, null, previous.GetValueOrDefault(item.Target.Path), options.HostInfo, token);
                    results[item.Target.Path] = result;
                    lock (saveLock)
                        database.SaveFileScan(result);
                    progress?.Report(new ScanProgress(Interlocked.Increment(ref done), queue.Count, item.Target.Path));
                });

            // 部分插件并行加载时才失败（如争用授权服务），也有插件本身加载很慢：
            // 失败项放宽时限逐个重试一次。
            var retries = queue.Where(q => results[q.Target.Path].Status is ScanStatus.LoadFailed or ScanStatus.TimedOut).ToList();
            for (var i = 0; i < retries.Count; i++)
            {
                var item = retries[i];
                progress?.Report(new ScanProgress(i, retries.Count, "重试 " + item.Target.Path));
                var result = await ScanOneAsync(item.Target, item.Size, item.Modified, runner.Timeout * 3, previous.GetValueOrDefault(item.Target.Path), options.HostInfo, cancel);
                results[item.Target.Path] = result;
                database.SaveFileScan(result);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        var removed = 0;
        // 发现结果完整时才清理：取消不影响发现，但带过滤条件的部分扫描不能据此判断文件已消失。
        if (options.Filter == null)
        {
            var existing = new HashSet<string>(discovered.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
            // 文件夹本身暂时不在（如移动硬盘没插）时，不能据此认为里面的插件被卸载了。
            var offlineRoots = rootList.Where(r => !Directory.Exists(r.Path)).Select(r => r.Path).ToList();
            foreach (var (path, scan) in previous)
            {
                if (existing.Contains(path)
                    || (options.Formats != null && !options.Formats.Contains(scan.Format))
                    || offlineRoots.Any(root => HostPluginCache.IsInside(path, root)))
                    continue;
                database.DeleteFileScan(path);
                removed++;
            }
        }

        var merged = PluginMerger.Merge(database.LoadFileScans().Values);
        database.SaveMerged(merged);

        var byStatus = results.Values.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        return new ScanSummary(
            discovered.Count,
            skipped,
            byStatus.GetValueOrDefault(ScanStatus.Loaded),
            byStatus.GetValueOrDefault(ScanStatus.ModuleInfo),
            byStatus.GetValueOrDefault(ScanStatus.FileInfoOnly),
            byStatus.GetValueOrDefault(ScanStatus.LoadFailed),
            byStatus.GetValueOrDefault(ScanStatus.TimedOut),
            removed,
            merged.Count,
            cancelled,
            clock.Elapsed);
    }

    /// <summary>文件没变但宿主后来才认识它时也要重新处理，否则音源要等到完整重扫才会被过滤。</summary>
    private static bool HostInfoChanged(ScanTarget target, FileScanResult old, HostPluginIndex? hostInfo) =>
        target.Format == PluginFormat.Vst2
        && ((hostInfo?.Find(target.Path) is { } info
             && !string.Equals(old.Classes.FirstOrDefault()?.SdkCategory, info.Category, StringComparison.Ordinal))
            // 以前判断不了、现在厂家目录能认出是效果器的，也重新处理。
            || (old.Classes.FirstOrDefault() is { IsInstrument: null } previous && VendorCatalog.IsKnownEffect(previous.Name)));

    private async Task<FileScanResult> ScanOneAsync(ScanTarget target, long size, DateTime modified, TimeSpan? timeout, FileScanResult? prior, HostPluginIndex? hostInfo, CancellationToken cancel)
    {
        FileScanResult Result(ScanStatus status, string? error, IReadOnlyList<PluginClassInfo> classes) =>
            new(target.Path, target.Format, target.Arch, size, modified, status, error, classes);

        // moduleinfo 是厂家提供的静态信息，可以不加载插件。
        if (target.Format == PluginFormat.Vst3 && StaticInspector.ReadModuleInfo(target.Path) is { } moduleInfo)
            return Result(ScanStatus.ModuleInfo, null, moduleInfo);

        // AU 在 Info.plist 里登记了名称、厂家和类型，同样不用加载。
        if (target.Format == PluginFormat.Au)
        {
            return StaticInspector.ReadAudioUnits(target.Path) is { } units
                ? Result(ScanStatus.ModuleInfo, null, units)
                : Result(ScanStatus.FileInfoOnly, null, [StaticInspector.FromFileInfo(target)]);
        }

        // VST2 必须创建实例才能读信息，这正是本软件不做的事：只用文件信息和宿主缓存。
        // DAW 都没记录时，名字和厂家目录里的效果器对上也算效果器（目录只收效果器）。
        if (target.Format == PluginFormat.Vst2)
        {
            var info = StaticInspector.WithHostInfo(StaticInspector.FromFileInfo(target), hostInfo?.Find(target.Path));
            if (info.IsInstrument == null && VendorCatalog.IsKnownEffect(info.Name))
                info = info with { IsInstrument = false };
            return Result(ScanStatus.FileInfoOnly, null, [info]);
        }

        // 扫描器只加载和自己同架构的 VST3；其他架构的 VST3、CLAP、AAX 按方案只记录文件信息。
        if (target.Format != PluginFormat.Vst3 || !StaticInspector.ScannerCanLoad(target.Arch))
            return Result(ScanStatus.FileInfoOnly, null, [StaticInspector.FromFileInfo(target)]);

        var load = await runner.LoadAsync(target.Path, cancel, timeout);

        // 文件没变而这次读取失败，说明是偶发问题：保留上次成功的结果，
        // 否则一次失败就会让这个文件里的所有插件从列表消失。
        if (load.Outcome != LoadOutcome.Success
            && prior is { Status: ScanStatus.Loaded }
            && prior.Size == size
            && prior.ModifiedUtc == modified)
            return prior;

        return load.Outcome switch
        {
            LoadOutcome.Success => Result(ScanStatus.Loaded, null, load.Classes),
            LoadOutcome.TimedOut => Result(ScanStatus.TimedOut, load.Error, [StaticInspector.FromFileInfo(target)]),
            _ => Result(ScanStatus.LoadFailed, load.Error, [StaticInspector.FromFileInfo(target)]),
        };
    }

    /// <summary>bundle 目录取内部二进制文件的大小和时间，目录本身的时间不可靠。</summary>
    private static (long Size, DateTime Modified) Fingerprint(ScanTarget target)
    {
        var binary = StaticInspector.ResolveBinary(target.Path, target.Format);
        if (binary == null)
            return (0, Directory.Exists(target.Path) ? Directory.GetLastWriteTimeUtc(target.Path) : DateTime.MinValue);
        var info = new FileInfo(binary);
        return (info.Length, info.LastWriteTimeUtc);
    }
}
