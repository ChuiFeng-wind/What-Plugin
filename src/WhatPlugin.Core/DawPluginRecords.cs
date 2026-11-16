using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace WhatPlugin.Core;

/// <summary>一条 DAW 的记录。Path 为空时只能按文件名或插件名对上。</summary>
public sealed record DawPluginRecord(string Daw, string? Path, string? FileName, HostPluginInfo Info);

/// <summary>各 DAW 记录所在的位置。默认是当前 Windows 用户的位置；测试时换成临时文件夹。</summary>
public sealed record DawLocations(
    string? SteinbergFolder = null,
    string? ReaperFolder = null,
    string? PreSonusFolder = null,
    IReadOnlyList<string>? FlStudioDatabases = null,
    Func<IEnumerable<CakewalkEntry>>? CakewalkInventory = null)
{
    public static DawLocations Default { get; } = new();

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string Steinberg => SteinbergFolder ?? System.IO.Path.Combine(AppData, "Steinberg");
    public string Reaper => ReaperFolder ?? System.IO.Path.Combine(AppData, "REAPER");
    public string PreSonus => PreSonusFolder ?? System.IO.Path.Combine(AppData, "PreSonus");

    /// <summary>FL Studio 20 以后放在“文档\Image-Line\FL Studio”，较新的版本在“文档\Image-Line\Data\FL Studio”。</summary>
    public IReadOnlyList<string> FlStudio => FlStudioDatabases ??
    [
        System.IO.Path.Combine(Documents, "Image-Line", "FL Studio", "Presets", "Plugin database"),
        System.IO.Path.Combine(Documents, "Image-Line", "Data", "FL Studio", "Presets", "Plugin database"),
    ];

    public IEnumerable<CakewalkEntry> Cakewalk() => (CakewalkInventory ?? CakewalkRegistry.Read)();
}

/// <summary>Cakewalk 清单里的一项：子键名（插件路径）和它下面的值。</summary>
public sealed record CakewalkEntry(string Key, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// 读取其他 DAW 扫描 VST2 时留下的记录。全部只读；文件不存在、被占用或格式不认识时跳过那一份。
/// REAPER 和 FL Studio 的格式有公开资料核实过；Cakewalk 和 Studio One 的字段名没有找到公开资料，
/// 按宽松规则识别，认不出就不用，不会误判。
/// </summary>
public static class DawPluginRecords
{
    public const string Reaper = "REAPER";
    public const string FlStudio = "FL Studio";
    public const string Cakewalk = "Cakewalk";
    public const string StudioOne = "Studio One";

    /// <summary>返回 (写入时间, 记录)。时间用来在多个 DAW 说法不同时取最新的。</summary>
    public static List<(DateTime Written, DawPluginRecord Record)> LoadOthers(DawLocations locations)
    {
        var all = new List<(DateTime, DawPluginRecord)>();
        all.AddRange(Safe(() => LoadReaper(locations.Reaper)));
        all.AddRange(Safe(() => LoadFlStudio(locations.FlStudio)));
        all.AddRange(Safe(() => LoadStudioOne(locations.PreSonus)));
        all.AddRange(Safe(() => locations.Cakewalk().SelectMany(e => ParseCakewalk(e) is { } r ? [(DateTime.MinValue, r)] : Array.Empty<(DateTime, DawPluginRecord)>()).ToList()));
        return all;
    }

    private static List<(DateTime, DawPluginRecord)> Safe(Func<List<(DateTime, DawPluginRecord)>> load)
    {
        try
        {
            return load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or System.Security.SecurityException or FormatException)
        {
            return [];
        }
    }

    // ---------- REAPER ----------

    internal static List<(DateTime Written, DawPluginRecord Record)> LoadReaper(string folder)
    {
        var results = new List<(DateTime, DawPluginRecord)>();
        if (!Directory.Exists(folder))
            return results;
        foreach (var file in Directory.GetFiles(folder, "reaper-vstplugins*.ini"))
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
                results.AddRange(ParseReaper(reader).Select(r => (written, r)));
            }
            catch (IOException)
            {
            }
        }
        return results;
    }

    /// <summary>
    /// 每行“文件名=哈希,ID,显示名 (厂家)”，音源在末尾带 !!!VSTi。只有哈希、没有名字的行是没扫成功的，跳过。
    /// 键只有文件名，没有路径。
    /// </summary>
    public static List<DawPluginRecord> ParseReaper(TextReader reader)
    {
        var results = new List<DawPluginRecord>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] is '[' or ';')
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var file = line[..eq].Trim();
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            var fields = line[(eq + 1)..].Split(',', 3);
            if (fields.Length < 3)
                continue;
            var display = fields[2].Trim();
            var instrument = display.EndsWith("!!!VSTi", StringComparison.OrdinalIgnoreCase);
            if (instrument)
                display = display[..^"!!!VSTi".Length].TrimEnd();
            var (name, vendor) = SplitVendor(display);
            if (name.Length == 0)
                continue;
            results.Add(new DawPluginRecord(Reaper, null, file, new HostPluginInfo(name, vendor, "", instrument ? "Instrument" : "Fx")));
        }
        return results;
    }

    /// <summary>"Pro-Q 3 (FabFilter)" → ("Pro-Q 3", "FabFilter")。名字里本来就有括号的，只拆最后一组。</summary>
    private static (string Name, string Vendor) SplitVendor(string display)
    {
        if (display.EndsWith(')'))
        {
            var open = display.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0)
                return (display[..open].Trim(), display[(open + 2)..^1].Trim());
        }
        return (display, "");
    }

    // ---------- FL Studio ----------

    private static List<(DateTime, DawPluginRecord)> LoadFlStudio(IReadOnlyList<string> databases)
    {
        var results = new List<(DateTime, DawPluginRecord)>();
        foreach (var database in databases)
        {
            foreach (var (kind, instrument) in new[] { ("Effects", false), ("Generators", true) })
            {
                // VST 文件夹是 VST2；VST3、CLAP 自带分类，不需要借用。
                var folder = System.IO.Path.Combine(database, "Installed", kind, "VST");
                if (!Directory.Exists(folder))
                    continue;
                foreach (var nfo in Directory.GetFiles(folder, "*.nfo", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (ParseFlStudio(File.ReadAllText(nfo), instrument) is { } record)
                            results.Add((File.GetLastWriteTimeUtc(nfo), record));
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        return results;
    }

    /// <summary>
    /// .nfo 是“键=值”的文本。ps_name 是插件名，ps_file_vendorname_0 是厂家，ps_file_type_0 为 1 是效果器、2 是音源；
    /// 没有类型时按所在的 Effects / Generators 文件夹。有 ps_file_filename_0 且是 .dll 时按路径对上，否则按插件名。
    /// </summary>
    public static DawPluginRecord? ParseFlStudio(string text, bool instrumentByFolder)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq > 0)
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        var name = values.GetValueOrDefault("ps_name", "");
        if (name.Length == 0)
            return null;
        var instrument = values.GetValueOrDefault("ps_file_type_0") switch
        {
            "1" => false,
            "2" => true,
            _ => instrumentByFolder,
        };
        var path = values.GetValueOrDefault("ps_file_filename_0", "");
        var isDll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && System.IO.Path.IsPathFullyQualified(path);
        return new DawPluginRecord(FlStudio, isDll ? path : null, isDll ? System.IO.Path.GetFileName(path) : null,
            new HostPluginInfo(name, values.GetValueOrDefault("ps_file_vendorname_0", ""), "", instrument ? "Instrument" : "Fx"));
    }

    // ---------- Cakewalk ----------

    /// <summary>
    /// 子键名是插件路径（斜杠可能是 /）。标记音源的值名没有公开资料，找名字里带 synth 的数值或文字；
    /// 找不到就不用这一项。
    /// </summary>
    public static DawPluginRecord? ParseCakewalk(CakewalkEntry entry)
    {
        var path = new[] { Find(entry.Values, "FullPath") as string, Find(entry.Values, "Path") as string, Find(entry.Values, "FileName") as string, entry.Key }
            .Select(p => p?.Replace('/', '\\').Trim() ?? "")
            .FirstOrDefault(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && System.IO.Path.IsPathFullyQualified(p));
        if (path == null)
            return null;
        var flag = entry.Values.FirstOrDefault(v => v.Key.Contains("synth", StringComparison.OrdinalIgnoreCase)).Value;
        bool? instrument = flag switch
        {
            int i => i != 0,
            long l => l != 0,
            string s when s is "1" or "0" => s == "1",
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };
        if (instrument == null)
            return null;
        var name = (Find(entry.Values, "Name", "PlugInName", "PluginName", "ProductName") as string)?.Trim();
        if (string.IsNullOrEmpty(name))
            name = System.IO.Path.GetFileNameWithoutExtension(path);
        var vendor = (Find(entry.Values, "Vendor", "VendorString", "Manufacturer") as string)?.Trim() ?? "";
        return new DawPluginRecord(Cakewalk, path, System.IO.Path.GetFileName(path),
            new HostPluginInfo(name, vendor, "", instrument == true ? "Instrument" : "Fx"));
    }

    private static object? Find(IReadOnlyDictionary<string, object?> values, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = values.FirstOrDefault(v => string.Equals(v.Key, name, StringComparison.OrdinalIgnoreCase));
            if (hit.Key != null)
                return hit.Value;
        }
        return null;
    }

    // ---------- Studio One ----------

    private static List<(DateTime, DawPluginRecord)> LoadStudioOne(string presonus)
    {
        var results = new List<(DateTime, DawPluginRecord)>();
        if (!Directory.Exists(presonus))
            return results;
        foreach (var version in Directory.GetDirectories(presonus, "Studio One*"))
        {
            var x64 = System.IO.Path.Combine(version, "x64");
            if (!Directory.Exists(x64))
                continue;
            foreach (var file in Directory.GetFiles(x64, "*.settings"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var written = File.GetLastWriteTimeUtc(file);
                    results.AddRange(ParseStudioOne(stream).Select(r => (written, r)));
                }
                catch (Exception e) when (e is IOException or XmlException)
                {
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Studio One 的 .settings 是 XML，但具体的元素名没有公开资料。这里只认同时满足两点的元素：
    /// 某个属性是 .dll 的完整路径，并且它自己或直接子元素的属性里明确写着 Instrument / Fx 一类的分类。
    /// </summary>
    public static List<DawPluginRecord> ParseStudioOne(Stream stream)
    {
        var results = new List<DawPluginRecord>();
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            document.Load(reader);
        foreach (XmlElement element in document.GetElementsByTagName("*"))
        {
            var path = element.Attributes.Cast<XmlAttribute>().Select(a => a.Value.Trim())
                .FirstOrDefault(v => v.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && System.IO.Path.IsPathFullyQualified(v));
            if (path == null)
                continue;
            var attributes = element.Attributes.Cast<XmlAttribute>()
                .Concat(element.ChildNodes.OfType<XmlElement>().SelectMany(c => c.Attributes.Cast<XmlAttribute>()))
                .ToList();
            bool? instrument = null;
            foreach (var attribute in attributes)
            {
                if (!attribute.Name.Contains("categ", StringComparison.OrdinalIgnoreCase) && !attribute.Name.Contains("type", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = attribute.Value;
                if (value.Contains("Instrument", StringComparison.OrdinalIgnoreCase) || value.Contains("Synth", StringComparison.OrdinalIgnoreCase))
                    instrument = true;
                else if (instrument == null && (value.Split('|')[0].Trim() is var head
                         && (head.Equals("Fx", StringComparison.OrdinalIgnoreCase) || head.Contains("Effect", StringComparison.OrdinalIgnoreCase))))
                    instrument = false;
            }
            if (instrument == null)
                continue;
            var name = attributes.FirstOrDefault(a => a.Name.Equals("name", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
            var vendor = attributes.FirstOrDefault(a => a.Name.Contains("vendor", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
            results.Add(new DawPluginRecord(StudioOne, path, System.IO.Path.GetFileName(path),
                new HostPluginInfo(string.IsNullOrEmpty(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name, vendor, "", instrument == true ? "Instrument" : "Fx")));
        }
        return results;
    }
}

/// <summary>只读打开 Cakewalk 的 VST 清单。不同版本的产品名（Cakewalk、SONAR…）都看。</summary>
public static class CakewalkRegistry
{
    private const string Root = @"Software\Cakewalk Music Software";

    public static IEnumerable<CakewalkEntry> Read()
    {
        var entries = new List<CakewalkEntry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root, writable: false);
            if (root == null)
                return entries;
            foreach (var product in root.GetSubKeyNames())
            {
                using var inventory = root.OpenSubKey(product + @"\Cakewalk VST X64\Inventory", writable: false);
                if (inventory == null)
                    continue;
                foreach (var name in inventory.GetSubKeyNames())
                {
                    using var plugin = inventory.OpenSubKey(name, writable: false);
                    if (plugin == null)
                        continue;
                    var values = plugin.GetValueNames().ToDictionary(v => v, v => plugin.GetValue(v), StringComparer.OrdinalIgnoreCase);
                    entries.Add(new CakewalkEntry(name, values));
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return entries;
    }
}
