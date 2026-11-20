using System.Text;
using WhatPlugin.Core;

// 命令行工具：不开界面运行扫描、查看结果，用于验证和排错。
//   discover                         只列出会扫描的对象，不加载插件
//   scan [--full] [--match 文本] [--db 路径] [--timeout 秒] [--vst2-folder 路径] [--format vst2|others]
//   list [--db 路径]
//   projects [文件夹...] [--db 路径] [--missing-only]  只读扫描工程，输出缺失和使用统计
//   tracks 工程文件 [--db 路径]        只读，按轨道列出插入效果，并标出插件库里有没有

Console.OutputEncoding = Encoding.UTF8;

var command = args.FirstOrDefault() ?? "";
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var dbPath = Option("--db") ?? PluginDatabase.ResolveDefaultPath();
HostStock.Current = HostStock.Load(DawLocations.Default);

switch (command)
{
    case "lookup":
    {
        // DAW 里的查询窗口调用这里。只读，输出 JSON。
        if (!File.Exists(dbPath))
        {
            Console.WriteLine(LookupExport.Error("还没有数据，请先打开 WhatPlugin 扫描插件。"));
            return 0;
        }
        try
        {
            using var database = new PluginDatabase(dbPath);
            var sub = args.ElementAtOrDefault(1);
            Console.WriteLine(sub == "recommend"
                ? LookupExport.Recommend(database, args.ElementAtOrDefault(2) ?? "")
                : LookupExport.Dump(database));
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Console.WriteLine(LookupExport.Error("读取数据失败：" + e.Message));
        }
        return 0;
    }
    case "discover":
    {
        var targets = PluginDiscovery.Discover(PluginDiscovery.DefaultRoots());
        foreach (var group in targets.GroupBy(t => (t.Format, t.Arch)).OrderBy(g => g.Key.Format))
            Console.WriteLine($"{group.Key.Format,-5} {group.Key.Arch,-8} {group.Count(),5}");
        Console.WriteLine($"合计 {targets.Count}");
        return 0;
    }
    case "scan":
    {
        var scannerPath = Path.Combine(AppContext.BaseDirectory, AppPlatform.Executable("WhatPluginScanner"));
        if (!File.Exists(scannerPath))
        {
            Console.Error.WriteLine("找不到扫描器: " + scannerPath);
            return 1;
        }
        var timeout = TimeSpan.FromSeconds(int.Parse(Option("--timeout") ?? "60"));
        var match = Option("--match");
        using var database = new PluginDatabase(dbPath);
        var service = new ScanService(database, new ScannerRunner(scannerPath, timeout));
        if (Option("--vst2-folder") is { } vst2Folder)
            database.AddVst2Folder(vst2Folder);
        var options = new ScanOptions(
            Full: args.Contains("--full"),
            Filter: match == null ? null : t => t.Path.Contains(match, StringComparison.OrdinalIgnoreCase),
            Formats: Option("--format") switch
            {
                "vst2" => [PluginFormat.Vst2],
                "others" => [PluginFormat.Vst3, PluginFormat.Clap, PluginFormat.Aax],
                _ => null,
            },
            HostInfo: HostPluginCache.LoadAll());

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var progress = new SynchronousProgress(p =>
        {
            if (p.Done == p.Total || p.Done % 10 == 0)
                Console.Error.WriteLine($"[{p.Done}/{p.Total}] {p.Current}");
        });

        var summary = await service.ScanAsync(PluginDiscovery.Roots(database.LoadVst2Folders()), options, progress, cancel.Token);
        Console.WriteLine(summary);
        return 0;
    }
    case "list":
    {
        using var database = new PluginDatabase(dbPath);
        foreach (var p in database.LoadPlugins())
        {
            var formats = string.Join('+', p.Files.Select(f => f.Format).Distinct());
            var status = p.IsMissing ? "缺失" : string.Join('+', p.Files.Select(f => f.Status).Distinct());
            Console.WriteLine($"{p.Vendor}\t{p.Name}\t{p.Kind}\t{string.Join(',', p.Tags)}\t{p.TagSource}\t{formats}\t{status}");
        }
        return 0;
    }
    case "recommend":
    {
        using var database = new PluginDatabase(dbPath);
        var plugins = database.LoadEffects();
        foreach (var need in args.Skip(1).TakeWhile(a => a != "--db"))
        {
            var result = Recommender.Recommend(need, plugins, limit: int.Parse(Option("--top") ?? "8"));
            Console.WriteLine($"【{need}】理解为 [{string.Join("、", result.Understood)}] 排除 [{string.Join("、", result.Excluded)}] 厂家 [{string.Join("、", result.Vendors)}] 未理解 [{string.Join("、", result.NotUnderstood)}] 共 {result.TotalMatches} 款");
            foreach (var item in result.Items)
                Console.WriteLine($"  {item.Score,5:0.#}  {item.Plugin.Vendor} / {item.Plugin.Name}  —  {string.Join("；", item.Reasons)}");
        }
        return 0;
    }
    case "tracks":
    {
        var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("缺少工程文件");
        var host = ProjectParser.HostOf(file) ?? throw new ArgumentException("不支持的工程格式");
        var matcher = new PluginNameMatcher(File.Exists(dbPath) ? new PluginDatabase(dbPath).LoadPlugins() : []);
        var tracks = ProjectParser.ParseTracks(file);
        Console.WriteLine($"轨道 {tracks.Count}，有插件的 {tracks.Count(t => t.Plugins.Count > 0)}");
        foreach (var track in tracks.Where(t => t.Plugins.Count > 0))
        {
            Console.WriteLine(track.Name);
            foreach (var plugin in track.Plugins)
            {
                var row = matcher.Find(plugin.Name);
                var mark = row == null ? (plugin.Stock || StockPlugins.Contains(host, plugin.Name, plugin.Uid) ? "自带" : "未装") : row.Kind == PluginKind.Instrument ? "音源" : "已装";
                Console.WriteLine($"   [{mark}] {plugin.Name}");
            }
        }
        return 0;
    }
    case "projects":
    {
        // projects [文件夹...] [--db 路径] [--missing-only]：把文件夹加入扫描范围，扫描并输出报告。
        using var database = new PluginDatabase(dbPath);
        foreach (var folder in args.Skip(1).TakeWhile(a => !a.StartsWith("--")))
            database.AddProjectFolder(Path.GetFullPath(folder));
        var summary = ProjectScanner.Scan(database);
        Console.WriteLine(summary);
        var report = ProjectReport.Build(database.LoadProjectFiles(), database.LoadPlugins(), database.LoadIgnoredProjectNames());
        Console.WriteLine($"歌曲 {report.Projects.Count}，有缺失的 {report.Projects.Count(p => p.MissingCount > 0)}，缺失插件 {report.Missing.Count} 种");
        Console.WriteLine("--- 缺失（工程数 名称）");
        foreach (var missing in report.Missing)
            Console.WriteLine($"{missing.Projects.Count,4} {missing.Name}");
        if (!args.Contains("--missing-only"))
        {
            Console.WriteLine("--- 已安装的使用次数（工程数 实例 最近 名称）");
            var plugins = database.LoadPlugins().ToDictionary(p => p.Key);
            foreach (var usage in report.Usage.OrderByDescending(u => u.Value.Projects))
                Console.WriteLine($"{usage.Value.Projects,4} {usage.Value.Instances,4} {usage.Value.LastUsedUtc:yyyy-MM-dd} {plugins[usage.Key].Name}");
        }
        return 0;
    }
    default:
        Console.Error.WriteLine("用法: discover | scan [--full] [--match 文本] [--db 路径] [--timeout 秒] | list [--db 路径] | recommend 需求... [--db 路径] | projects [文件夹...] [--db 路径] [--missing-only]");
        return 2;
}

sealed class SynchronousProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => report(value);
}
