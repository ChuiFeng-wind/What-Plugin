using System.Xml;

namespace WhatPlugin.Core;

/// <summary>宿主自己扫描 VST2 时记下的信息。</summary>
public sealed record HostPluginInfo(string Name, string Vendor, string Version, string Category);

/// <summary>
/// 各 DAW 的记录合在一起。先按完整路径找，再按文件名（REAPER 只记文件名），最后按插件名（FL Studio 有的记录没有路径）。
/// </summary>
public sealed class HostPluginIndex
{
    private readonly Dictionary<string, HostPluginInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HostPluginInfo> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HostPluginInfo> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _dawsByFolder = new(StringComparer.OrdinalIgnoreCase);

    public static HostPluginIndex Empty { get; } = new([], []);

    /// <param name="records">按写入时间从旧到新排好，后面的覆盖前面的。</param>
    /// <param name="knownPaths">DAW 见过的 VST2 文件，包括它没读出信息的，用来建议添加文件夹。</param>
    public HostPluginIndex(IEnumerable<DawPluginRecord> records, IEnumerable<string> knownPaths)
    {
        var daws = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            daws.Add(record.Daw);
            if (record.Path != null)
            {
                _byPath[record.Path] = record.Info;
                var folder = Path.GetDirectoryName(record.Path);
                if (!string.IsNullOrEmpty(folder))
                {
                    if (!_dawsByFolder.TryGetValue(folder, out var set))
                        _dawsByFolder[folder] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(record.Daw);
                }
            }
            if (record.FileName != null)
                _byFileName[FileKey(record.FileName)] = record.Info;
            else if (record.Path == null && Normalizer.Name(record.Info.Name) is { Length: >= 3 } name)
                _byName[name] = record.Info;
        }
        Daws = daws.ToList();
        KnownPaths = knownPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>找到了记录的 DAW 名称。</summary>
    public IReadOnlyList<string> Daws { get; }

    public IReadOnlyList<string> KnownPaths { get; }

    public int Count => _byPath.Count + _byFileName.Count + _byName.Count;

    public HostPluginInfo? Find(string path)
    {
        if (_byPath.TryGetValue(path, out var info))
            return info;
        if (_byFileName.TryGetValue(FileKey(Path.GetFileName(path)), out info))
            return info;
        var stem = Normalizer.Name(Path.GetFileNameWithoutExtension(path));
        return stem.Length >= 3 && _byName.TryGetValue(stem, out info) ? info : null;
    }

    /// <summary>某个文件夹（含子文件夹）里有记录的 DAW，用于界面上说明是谁在用。</summary>
    public IReadOnlyList<string> DawsUsing(string folder) => _dawsByFolder
        .Where(p => p.Key.Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase) || HostPluginCache.IsInside(p.Key, folder))
        .SelectMany(p => p.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>REAPER 的键把空格换成下划线，比较时统一。</summary>
    private static string FileKey(string fileName) => fileName.Trim().Replace(' ', '_');
}

/// <summary>
/// 读取 Cubase / Nuendo 的 VST2 缓存（Vst2xPlugin Infos *.xml）。
/// VST2 要创建实例才能问出类型和厂家，而本软件不创建实例，所以借用宿主已经问到的结果。
/// 只读，不存在时返回空。
/// </summary>
public static class HostPluginCache
{
    private const string GroupPrefix = "Vst2xPlug\\";

    /// <summary>键是插件文件的完整路径。同一文件在多个版本的缓存里时，取最近写入的缓存。</summary>
    public static Dictionary<string, HostPluginInfo> Load(string? steinbergFolder = null)
    {
        var results = new Dictionary<string, HostPluginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in CacheFiles(steinbergFolder).OrderBy(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                foreach (var (path, info) in Parse(stream))
                {
                    if (info != null)
                        results[path] = info;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
            {
                // 宿主可能正在写，或文件损坏：缺一份缓存只影响分类，不影响扫描。
            }
        }
        return results;
    }

    /// <summary>
    /// 宿主用过的 VST2 文件夹。子文件夹并入上层，只留下用户需要添加的那一层。
    /// 插件数量少于 <paramref name="minimumPlugins"/> 的零散位置不算。
    /// </summary>
    public static List<string> SuggestFolders(string? steinbergFolder = null, int minimumPlugins = 1)
    {
        var paths = new List<string>();
        foreach (var file in CacheFiles(steinbergFolder))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                paths.AddRange(Parse(stream).Select(e => e.Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
            {
            }
        }
        return TopFolders(paths, minimumPlugins);
    }

    public static List<string> TopFolders(IEnumerable<string> pluginPaths, int minimumPlugins = 1)
    {
        var folders = pluginPaths
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => Path.TrimEndingDirectorySeparator(d!))
            .ToList();
        var distinct = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tops = distinct.Where(d => !distinct.Any(other => IsInside(d, other))).ToList();
        return tops
            .Where(top => folders.Count(f => f.Equals(top, StringComparison.OrdinalIgnoreCase) || IsInside(f, top)) >= minimumPlugins)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsInside(string path, string folder) =>
        !path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        && path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>逐条返回缓存记录。info 为 null 的是宿主没能读出信息的文件（如 WaveShell 外壳、加载失败的文件）。</summary>
    public static List<(string Path, HostPluginInfo? Info)> Parse(Stream stream)
    {
        var results = new List<(string, HostPluginInfo?)>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });

        string? path = null;
        Dictionary<string, string>? fields = null;
        void Flush()
        {
            if (path == null)
                return;
            results.Add((path, ToInfo(fields)));
            path = null;
            fields = null;
        }

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "string")
                continue;
            var name = reader.GetAttribute("name");
            var value = reader.GetAttribute("value") ?? "";
            if (name == "Group")
            {
                Flush();
                if (value.StartsWith(GroupPrefix, StringComparison.Ordinal) && value.Length > GroupPrefix.Length)
                {
                    path = value[GroupPrefix.Length..];
                    fields = new Dictionary<string, string>(StringComparer.Ordinal);
                }
            }
            else if (fields != null && name != null)
            {
                fields[name] = value;
            }
        }
        Flush();
        return results;
    }

    private static HostPluginInfo? ToInfo(Dictionary<string, string>? fields)
    {
        if (fields == null || !fields.TryGetValue("name", out var name) || name.Trim().Length == 0)
            return null;
        return new HostPluginInfo(
            name.Trim(),
            fields.GetValueOrDefault("vendor", "").Trim(),
            fields.GetValueOrDefault("vendorVersion", "").Trim(),
            // 只留 Fx / Instrument：后面的细分类是厂家随手填的（Pro-C 填 Mastering，Timeless 填 Reverb），比按名称判断还不准。
            fields.GetValueOrDefault("subCategory", "").Split('|')[0].Trim());
    }

    /// <summary>
    /// 读取所有支持的 DAW 的记录，合成一份索引。同一个文件多个 DAW 说法不同时，取最近写入的记录。
    /// </summary>
    public static HostPluginIndex LoadAll(DawLocations? locations = null)
    {
        locations ??= DawLocations.Default;
        var records = new List<(DateTime Written, DawPluginRecord Record)>();
        var paths = new List<string>();
        foreach (var file in CacheFiles(locations.Steinberg))
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                foreach (var (path, info) in Parse(stream))
                {
                    paths.Add(path);
                    if (info != null)
                        records.Add((written, new DawPluginRecord("Cubase", path, Path.GetFileName(path), info)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
            {
            }
        }
        var others = DawPluginRecords.LoadOthers(locations);
        records.AddRange(others);
        paths.AddRange(others.Select(o => o.Record.Path).OfType<string>());
        return new HostPluginIndex(records.OrderBy(r => r.Written).Select(r => r.Record), paths);
    }

    private static IEnumerable<string> CacheFiles(string? steinbergFolder)
    {
        var root = steinbergFolder ?? AppPlatform.SteinbergFolder;
        if (!Directory.Exists(root))
            return [];
        try
        {
            return Directory.GetDirectories(root)
                .Where(d => Path.GetFileName(d).StartsWith("Cubase", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(d).StartsWith("Nuendo", StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => Directory.GetFiles(d, "Vst2xPlugin Infos*.xml"))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
