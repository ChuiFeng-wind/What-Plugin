using System.Text.RegularExpressions;
using System.Xml;

namespace WhatPlugin.Core;

/// <summary>宿主自带的一款效果器。Product 是界面上显示的宿主名，如 "Cubase 14"。</summary>
public sealed record StockPlugin(ProjectHost Host, string Product, string Name, string? Uid);

/// <summary>
/// 本机已安装的宿主自带哪些效果器。不靠写死的名单：读宿主自己写下的清单，宿主升级、新增插件后自动跟上。
/// <list type="bullet">
/// <item>Cubase / Nuendo：%APPDATA%\Steinberg\&lt;产品&gt;\*Module Cache.xml，宿主启动时登记的内部组件，
/// 带 VST3 类 ID，和 .cpr 里记的 GUID 是同一个，能精确对上。</item>
/// <item>REAPER：reaper-vstplugins*.ini 里厂家是 Cockos 的。</item>
/// </list>
/// Studio One 和 Ableton 在解析工程时已经从工程本身分出了自带的设备，不需要这里。
/// 全部只读；文件不存在、被占用或格式不认识时跳过。
/// </summary>
public sealed partial class HostStock
{
    private readonly Dictionary<string, StockPlugin> _byUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(ProjectHost, string), StockPlugin> _byHostName = new();
    private readonly Dictionary<string, StockPlugin> _byName = new(StringComparer.Ordinal);

    public static HostStock Empty { get; } = new([]);

    /// <summary>程序启动时读一次本机的清单。测试里不改它，直接把实例传进去。</summary>
    public static HostStock Current { get; set; } = Empty;

    public HostStock(IEnumerable<StockPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var key = Normalizer.Name(PluginNameMatcher.CleanName(plugin.Name));
            if (key.Length == 0)
                continue;
            Count++;
            if (plugin.Uid is { Length: > 0 } uid)
                _byUid.TryAdd(uid, plugin);
            _byHostName.TryAdd((plugin.Host, key), plugin);
            _byName.TryAdd(key, plugin);
        }
    }

    public int Count { get; }

    /// <summary>工程里的一款插件是不是这个宿主自带的。有 GUID 时先按 GUID，对不上再按名称。</summary>
    public bool Contains(ProjectHost host, string name, string? uid = null) =>
        (uid is { Length: > 0 } && _byUid.TryGetValue(uid, out var byUid) && byUid.Host == host)
        || _byHostName.ContainsKey((host, Normalizer.Name(PluginNameMatcher.CleanName(name))));

    /// <summary>不知道是哪个宿主时（混音笔记）按名称找，返回自带它的宿主名。</summary>
    public string? ProductOf(string name, string? uid = null)
    {
        if (uid is { Length: > 0 } && _byUid.TryGetValue(uid, out var byUid))
            return byUid.Product;
        return _byName.TryGetValue(Normalizer.Name(PluginNameMatcher.CleanName(name)), out var plugin) ? plugin.Product : null;
    }

    public static HostStock Load(DawLocations locations)
    {
        var plugins = new List<StockPlugin>();
        plugins.AddRange(Safe(() => LoadSteinberg(locations.Steinberg)));
        plugins.AddRange(Safe(() => DawPluginRecords.LoadReaper(locations.Reaper)
            .Select(r => r.Record.Info)
            .Where(info => info.Vendor.Equals("Cockos", StringComparison.OrdinalIgnoreCase) && info.Category != "Instrument")
            .Select(info => new StockPlugin(ProjectHost.Reaper, "REAPER", info.Name, null))
            .ToList()));
        return new HostStock(plugins);
    }

    private static List<StockPlugin> Safe(Func<List<StockPlugin>> load)
    {
        try
        {
            return load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or System.Security.SecurityException)
        {
            return [];
        }
    }

    // ---------- Cubase / Nuendo ----------

    /// <summary>新版本排在前面：同名插件显示新版本的宿主名。</summary>
    private static List<StockPlugin> LoadSteinberg(string folder)
    {
        var results = new List<StockPlugin>();
        if (!Directory.Exists(folder))
            return results;
        var products = Directory.GetDirectories(folder)
            .Select(dir => (Dir: dir, Product: SteinbergProduct(Path.GetFileName(dir))))
            .Where(p => p.Product != null)
            .OrderByDescending(p => VersionOf(p.Product!));
        foreach (var (dir, product) in products)
        {
            foreach (var file in Directory.GetFiles(dir, "*Module Cache.xml"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    results.AddRange(ParseSteinbergModuleCache(stream, product!));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
                {
                }
            }
        }
        return results;
    }

    /// <summary>"Cubase 14_64" → "Cubase 14"，"Nuendo 13_64" → "Nuendo 13"。别的 Steinberg 软件（WaveLab、SpectraLayers…）打不开 .cpr，不算。</summary>
    public static string? SteinbergProduct(string folderName)
    {
        var match = SteinbergFolder().Match(folderName);
        return match.Success ? match.Groups["product"].Value.Trim() : null;
    }

    private static double VersionOf(string product) =>
        Regex.Match(product, @"\d+(?:\.\d+)?") is { Success: true } m && double.TryParse(m.Value, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex(@"^(?<product>(?:Cubase|Nuendo)\b.*?)(?:_64|_32)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteinbergFolder();

    /// <summary>
    /// 每个 item 是一个组件文件，里面的类写成 "Class 00 - CID"、"Class 00 - Name"、"Class 00 - Category"、"Class 00 - SubCategories"。
    /// 只取 Category 是 Audio Module Class 的类（Controller、文件格式处理等不是插件），音源不算。
    /// </summary>
    public static List<StockPlugin> ParseSteinbergModuleCache(Stream stream, string product)
    {
        var results = new List<StockPlugin>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        Dictionary<string, Dictionary<string, string>>? classes = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "item")
            {
                Flush(classes, product, results);
                classes = new(StringComparer.Ordinal);
            }
            else if (reader.NodeType == XmlNodeType.Element && classes != null
                && reader.GetAttribute("name") is { } field && ClassField().Match(field) is { Success: true } match)
            {
                var index = match.Groups["index"].Value;
                if (!classes.TryGetValue(index, out var values))
                    classes[index] = values = new(StringComparer.Ordinal);
                values[match.Groups["field"].Value] = reader.GetAttribute("value") ?? "";
            }
        }
        Flush(classes, product, results);
        return results;
    }

    private static void Flush(Dictionary<string, Dictionary<string, string>>? classes, string product, List<StockPlugin> results)
    {
        if (classes == null)
            return;
        foreach (var values in classes.Values)
        {
            if (values.GetValueOrDefault("Category") != "Audio Module Class")
                continue;
            var name = values.GetValueOrDefault("Name")?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            var sub = values.GetValueOrDefault("SubCategories") ?? "";
            if (sub.Contains("Instrument", StringComparison.OrdinalIgnoreCase) || sub.Contains("Synth", StringComparison.OrdinalIgnoreCase))
                continue;
            var cid = values.GetValueOrDefault("CID")?.Trim();
            results.Add(new StockPlugin(ProjectHost.Cubase, product, name, cid is { Length: 32 } ? cid.ToUpperInvariant() : null));
        }
    }

    [GeneratedRegex(@"^Class (?<index>\d+) - (?<field>\w+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassField();
}
