using System.Xml;
using System.Xml.Linq;

namespace WhatPlugin.Core;

public enum HealthKind { HostBlacklisted, ReadFailed, Missing, Only32Bit, OnlyVst2, Duplicate, VersionMismatch, Leftover32Bit, BothFormats }

public sealed record HealthIssue(HealthKind Kind, PluginRow Plugin, string Detail);

public sealed record HealthCategory(HealthKind Kind, string Title, string Explanation, int Count)
{
    /// <summary>列表项的无障碍名称。</summary>
    public override string ToString() => $"{Title} {Count} 款";
}

public sealed record HealthReport(IReadOnlyList<HealthIssue> Issues, int UnmatchedBlacklistFiles)
{
    /// <summary>真正影响使用的问题数。VST2 和 VST3 并存之类只是提示，不算。</summary>
    public int NeedsAttention => Issues.Where(i => PluginHealth.NeedsAttention(i.Kind)).Select(i => i.Plugin.Key).Distinct().Count();

    public IReadOnlyList<HealthCategory> Categories =>
        Enum.GetValues<HealthKind>()
            .Select(kind => (Kind: kind, Count: Issues.Count(i => i.Kind == kind)))
            .Where(c => c.Count > 0)
            .Select(c => new HealthCategory(c.Kind, PluginHealth.Title(c.Kind), PluginHealth.Explain(c.Kind), c.Count))
            .ToList();
}

/// <summary>宿主拉黑的一个插件文件。</summary>
public sealed record HostBlacklistEntry(string Path, string Host);

/// <summary>插件健康检查。只看扫描结果和宿主的配置文件，不运行插件，也不改动任何文件。</summary>
public static class PluginHealth
{
    public static string Title(HealthKind kind) => kind switch
    {
        HealthKind.HostBlacklisted => "被宿主拉黑",
        HealthKind.ReadFailed => "读取失败",
        HealthKind.Missing => "文件缺失",
        HealthKind.Only32Bit => "只有 32 位",
        HealthKind.OnlyVst2 => "只有 VST2",
        HealthKind.Duplicate => "重复安装",
        HealthKind.VersionMismatch => "版本不一致",
        HealthKind.Leftover32Bit => "留着 32 位副本",
        HealthKind.BothFormats => "VST2/VST3 并存",
        _ => kind.ToString(),
    };

    public static string Explain(HealthKind kind) => kind switch
    {
        HealthKind.HostBlacklisted => "宿主启动时扫描这些文件出了问题，所以不再加载。如果同一款还有别的格式，通常不影响使用。",
        HealthKind.ReadFailed => "扫描器读取这些插件时失败或超时，可能缺少依赖文件，也可能文件已损坏。",
        HealthKind.Missing => "以前扫描到过，现在文件不见了。你写的备注和评分还保留着。",
        HealthKind.Only32Bit => "这些插件只装了 32 位版本。64 位宿主一般不能直接加载 32 位插件。",
        HealthKind.OnlyVst2 => "这些插件没有 VST3 版本。如果以后换用不支持 VST2 的宿主，它们会用不了。",
        HealthKind.Duplicate => "同一个文件在多个文件夹里各有一份。宿主可能只认其中一份，升级后也容易新旧混用。",
        HealthKind.VersionMismatch => "同一款插件的不同文件版本号不同，可能有些格式没有更新到。不同格式的版本号写法也可能本来就不一样，请以厂家说明为准。",
        HealthKind.Leftover32Bit => "64 位和 32 位都装了。不用 32 位宿主的话，32 位副本用不上。",
        HealthKind.BothFormats => "只是提示，不算问题。老工程可能还在用 VST2 版，确认工程能打开之前不要删除。",
        _ => "",
    };

    public static bool NeedsAttention(HealthKind kind) =>
        kind is HealthKind.HostBlacklisted or HealthKind.ReadFailed or HealthKind.Missing or HealthKind.Only32Bit;

    /// <param name="plugins">不含音源的列表。垃圾桶里的插件是用户决定不用的，这里跳过。</param>
    public static HealthReport Build(IEnumerable<PluginRow> plugins, IEnumerable<HostBlacklistEntry> blacklist)
    {
        var rows = plugins.Where(p => !p.User.Hidden && p.Kind != PluginKind.Instrument).ToList();
        var blocked = blacklist
            .GroupBy(b => b.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Host).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<HealthIssue>();

        foreach (var row in rows)
        {
            if (row.IsMissing)
            {
                issues.Add(new HealthIssue(HealthKind.Missing, row, "重新扫描时没有找到文件"));
                continue;
            }
            var files = row.Files;
            if (files.Count == 0)
                continue;

            var hits = files.Where(f => blocked.ContainsKey(f.Path)).ToList();
            if (hits.Count > 0)
            {
                matched.UnionWith(hits.Select(f => f.Path));
                var hosts = hits.SelectMany(f => blocked[f.Path]).Distinct();
                var others = files.Where(f => !blocked.ContainsKey(f.Path)).Select(f => f.Format).Distinct().ToList();
                var rest = others.Count > 0 ? $"，还有 {string.Join("、", others.Select(FormatName))} 可用" : "，没有别的可用文件";
                issues.Add(new HealthIssue(HealthKind.HostBlacklisted, row,
                    $"{string.Join("、", hosts)} 拉黑了 {string.Join("、", hits.Select(f => System.IO.Path.GetFileName(f.Path)))}{rest}"));
            }

            var failed = files.Where(f => f.Status is ScanStatus.LoadFailed or ScanStatus.TimedOut).ToList();
            if (failed.Count > 0)
            {
                var reason = failed.Select(f => f.Status == ScanStatus.TimedOut ? "读取超时" : f.Error ?? "读取失败").Distinct();
                issues.Add(new HealthIssue(HealthKind.ReadFailed, row, string.Join("；", reason)));
            }

            if (files.All(f => f.Arch == Arch.X86))
                issues.Add(new HealthIssue(HealthKind.Only32Bit, row, string.Join("、", files.Select(f => FormatName(f.Format)).Distinct())));
            else if (files.Any(f => f.Arch == Arch.X86))
                issues.Add(new HealthIssue(HealthKind.Leftover32Bit, row, $"{files.Count(f => f.Arch == Arch.X86)} 个 32 位文件"));

            if (files.All(f => f.Format == PluginFormat.Vst2))
                issues.Add(new HealthIssue(HealthKind.OnlyVst2, row, $"{files.Count} 个 VST2 文件"));
            else if (files.Any(f => f.Format == PluginFormat.Vst2) && files.Any(f => f.Format == PluginFormat.Vst3))
                issues.Add(new HealthIssue(HealthKind.BothFormats, row, "VST2 和 VST3 都装了"));

            // 文件名不同的不算重复：(Mono)、(SC) 这些是同一款的变体。
            var duplicates = files
                .GroupBy(f => (f.Format, f.Arch, Name: System.IO.Path.GetFileName(f.Path).ToLowerInvariant()))
                .Where(g => g.Count() > 1)
                .ToList();
            if (duplicates.Count > 0)
            {
                issues.Add(new HealthIssue(HealthKind.Duplicate, row,
                    string.Join("；", duplicates.Select(g => $"{System.IO.Path.GetFileName(g.First().Path)} 有 {g.Count()} 份"))));
            }

            var versions = files.Select(f => f.Version).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
            if (HasVersionMismatch(versions))
                issues.Add(new HealthIssue(HealthKind.VersionMismatch, row, string.Join("、", versions)));
        }

        // 不在扫描结果里的文件可能是音源，这里只给数量，不列名字。
        return new HealthReport(issues, blocked.Keys.Count(k => !matched.Contains(k)));
    }

    /// <summary>
    /// 同一个版本的写法很多：1.18、1.1.8.0、5, 3, 1, 18。只比较数字，去掉末尾的 0，
    /// 一方是另一方的开头也算相同。宁可漏报，也不把写法差异报成问题。
    /// </summary>
    public static bool HasVersionMismatch(IReadOnlyCollection<string> versions)
    {
        var digits = versions.Select(v => new string(v.Where(char.IsAsciiDigit).ToArray()).TrimEnd('0')).Where(d => d.Length > 0).Distinct().ToList();
        return digits.Any(a => digits.Any(b => !a.StartsWith(b, StringComparison.Ordinal) && !b.StartsWith(a, StringComparison.Ordinal)));
    }

    private static string FormatName(PluginFormat format) => format switch
    {
        PluginFormat.Vst3 => "VST3",
        PluginFormat.Vst2 => "VST2",
        PluginFormat.Clap => "CLAP",
        PluginFormat.Aax => "AAX",
        PluginFormat.Au => "AU",
        _ => format.ToString(),
    };
}

/// <summary>读取 Cubase、Nuendo 等 Steinberg 宿主记录的黑名单。只读。</summary>
public static class HostBlacklist
{
    public static List<HostBlacklistEntry> Load(string? steinbergFolder = null)
    {
        var root = steinbergFolder ?? AppPlatform.SteinbergFolder;
        var results = new List<HostBlacklistEntry>();
        if (!Directory.Exists(root))
            return results;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var host = Path.GetFileName(folder).Replace("_64", "", StringComparison.OrdinalIgnoreCase).Trim();
                foreach (var file in Directory.EnumerateFiles(folder, "Vst2xPlugin Blacklist*.xml"))
                    ReadInto(results, file, host, ParseVst2);
                foreach (var cache in Directory.EnumerateDirectories(folder, "*VST3 Cache"))
                {
                    var file = Path.Combine(cache, "vst3blacklist.xml");
                    if (File.Exists(file))
                        ReadInto(results, file, host, ParseVst3);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 读不到宿主配置只是少一项检查。
        }
        return results;
    }

    private static void ReadInto(List<HostBlacklistEntry> results, string file, string host, Func<Stream, IEnumerable<string>> parse)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            results.AddRange(parse(stream).Select(path => new HostBlacklistEntry(path, host)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            // 宿主可能正在写，或文件损坏。
        }
    }

    private static XDocument Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader);
    }

    /// <summary>结构：obj class="FNPath" 下的 string name="Name" 和 string name="Path"。</summary>
    public static IEnumerable<string> ParseVst2(Stream stream)
    {
        var paths = new List<string>();
        foreach (var obj in Read(stream).Descendants("obj").Where(o => (string?)o.Attribute("class") == "FNPath"))
        {
            string? Value(string name) => obj.Elements("string").FirstOrDefault(s => (string?)s.Attribute("name") == name)?.Attribute("value")?.Value;
            var (file, folder) = (Value("Name"), Value("Path"));
            if (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(folder))
                paths.Add(Path.Combine(folder, file));
        }
        return paths;
    }

    /// <summary>目前手上的样本是空的，不知道具体结构，所以取所有以 .vst3 结尾的属性和文本。</summary>
    public static IEnumerable<string> ParseVst3(Stream stream)
    {
        var document = Read(stream);
        return document.Descendants()
            .SelectMany(e => e.Attributes().Select(a => a.Value).Concat(e.HasElements ? [] : [e.Value]))
            .Select(v => v.Trim())
            .Where(v => v.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record VendorUpdate(string Vendor, int Count, string Tool, IReadOnlyList<PluginRow> Plugins);

/// <summary>
/// 查更新的入口。没有可靠的公开版本数据库，自动比较容易误报，所以这里只告诉你该去哪里查。
/// </summary>
public static class UpdateGuide
{
    private static readonly (string Match, string Tool)[] Tools =
    [
        ("waves", "Waves Central"),
        ("izotope", "Native Access 或 iZotope Product Portal"),
        ("native instruments", "Native Access"),
        ("plugin alliance", "Plugin Alliance Installation Manager"),
        ("brainworx", "Plugin Alliance Installation Manager"),
        ("universal audio", "UA Connect"),
        ("uad", "UA Connect"),
        ("slate digital", "Slate Digital Connect"),
        ("softube", "Softube Central"),
        ("arturia", "Arturia Software Center"),
        ("ik multimedia", "IK Product Manager"),
        ("antares", "Antares Central"),
        ("steinberg", "Steinberg Download Assistant"),
    ];

    public static string ToolFor(string vendor)
    {
        var lower = vendor.ToLowerInvariant();
        foreach (var (match, tool) in Tools)
        {
            if (lower.Contains(match, StringComparison.Ordinal))
                return tool;
        }
        return "厂家官网";
    }

    public static IReadOnlyList<VendorUpdate> Build(IEnumerable<PluginRow> plugins) =>
        plugins.Where(p => !p.IsMissing && !p.User.Hidden && p.Kind != PluginKind.Instrument && p.Vendor.Length > 0)
            .GroupBy(p => p.Vendor, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VendorUpdate(g.First().Vendor, g.Count(), ToolFor(g.Key), g.ToList()))
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Vendor, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>只是打开一次网页搜索，软件自己不联网。</summary>
    public static string SearchUrl(string vendor, string? name = null)
    {
        var query = (name == null ? $"{vendor} plugin updates" : $"{vendor} {name} latest version").Trim();
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
    }
}
