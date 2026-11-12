using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace WhatPlugin.Core;

public enum ProjectHost { Cubase, StudioOne, Reaper, Ableton }

/// <summary>工程里用到的一款效果器。音源在解析时已经排除，读不出的情况由报告阶段再按名称过滤一次。</summary>
public sealed record ProjectPluginUse(string Name, string? Uid, int Instances);

/// <summary>工程文件的解析入口。只读打开，从不写入，也不加载任何插件。</summary>
public static partial class ProjectParser
{
    /// <summary>解析规则变化时加一，已扫描过的工程会重新解析。</summary>
    public const int Version = 1;

    private const long MaxArchiveEntryBytes = 512L * 1024 * 1024;

    static ProjectParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ProjectHost? HostOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cpr" or ".npr" => ProjectHost.Cubase,
        ".song" => ProjectHost.StudioOne,
        ".rpp" => ProjectHost.Reaper,
        ".als" => ProjectHost.Ableton,
        _ => null,
    };

    public static string HostName(ProjectHost host) => host switch
    {
        ProjectHost.Cubase => "Cubase",
        ProjectHost.StudioOne => "Studio One",
        ProjectHost.Reaper => "REAPER",
        ProjectHost.Ableton => "Ableton Live",
        _ => host.ToString(),
    };

    /// <exception cref="InvalidDataException">文件不是预期的格式。</exception>
    public static IReadOnlyList<ProjectPluginUse> Parse(string path, CancellationToken cancel = default)
    {
        var host = HostOf(path) ?? throw new InvalidDataException("不支持的工程格式");
        // 工程可能正开在宿主里，所以允许别的程序继续读写，自己只读。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Parse(host, stream, cancel);
    }

    public static IReadOnlyList<ProjectPluginUse> Parse(ProjectHost host, Stream stream, CancellationToken cancel = default) => host switch
    {
        ProjectHost.Cubase => Cubase(stream, cancel),
        ProjectHost.StudioOne => StudioOne(stream, cancel),
        ProjectHost.Reaper => Reaper(stream, cancel),
        ProjectHost.Ableton => Ableton(stream, cancel),
        _ => throw new InvalidDataException("不支持的工程格式"),
    };

    // ---------- Cubase / Nuendo ----------
    // .cpr 的格式没有公开文档，下面的规则来自真实工程：
    //   字段 = 4 字节大端键长（含 NUL）+ 键 + NUL + 00 08 + 4 字节大端值长 + 文本 + NUL + 可选 BOM
    // 文件分段方式有两种（RIFF / RIF2），所以不依赖分段结构，直接按字节找记录。

    private static readonly byte[] CubaseMarker = "\0\0\0\vPlugin UID\0"u8.ToArray();
    private static readonly byte[] CubaseGuidKey = "GUID\0\0\b"u8.ToArray();
    private static readonly byte[] CubaseInsertClass = "VstCtrlInternalEffect\0"u8.ToArray();
    private static readonly byte[] CubaseIsA = "Plugin isA\0"u8.ToArray();
    private static readonly byte[] CubaseSynthSlot = "Synth Slot\0"u8.ToArray();
    private static readonly byte[] CubaseSynthRack = "Synth Rack\0"u8.ToArray();
    private static readonly byte[] CubaseChannelId = "IDString\0\0\b"u8.ToArray();
    private const int CubaseBack = 512;
    private const int CubaseForward = 8192;
    private const int CubaseChunk = 16 * 1024 * 1024;

    private static IReadOnlyList<ProjectPluginUse> Cubase(Stream stream, CancellationToken cancel)
    {
        var head = new byte[16];
        var headLength = stream.ReadAtLeast(head, 16, throwOnEndOfStream: false);
        if (headLength < 16 || !(head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || head.AsSpan(0, 4).SequenceEqual("RIF2"u8)))
            throw new InvalidDataException("不是 Cubase 工程文件");

        var uses = new UseCounter();
        var buffer = new byte[CubaseChunk];
        head.AsSpan(0, headLength).CopyTo(buffer);
        var length = headLength;
        var start = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var read = stream.ReadAtLeast(buffer.AsSpan(length), buffer.Length - length, throwOnEndOfStream: false);
            length += read;
            var end = length < buffer.Length ? length : length - CubaseForward;

            var position = start;
            while (position < end)
            {
                var offset = buffer.AsSpan(position, end - position).IndexOf(CubaseMarker);
                if (offset < 0)
                    break;
                var hit = position + offset;
                ReadCubaseRecord(buffer.AsSpan(0, length), hit, uses);
                position = hit + CubaseMarker.Length;
            }

            if (length < buffer.Length)
                return uses.ToList();

            // 留下末尾一段：记录可能跨块，既要能往前看类型，也要能往后读完名称。
            var keepFrom = end - CubaseBack;
            buffer.AsSpan(keepFrom, length - keepFrom).CopyTo(buffer);
            length -= keepFrom;
            start = CubaseBack;
        }
    }

    /// <summary>
    /// 老工程把音源放在 Synth Rack 的普通 Slot 里：第一个紧跟容器名，
    /// 之后每个都紧跟上一个音源输出通道的 ID（"Synth"、"Synth 2"）。
    /// </summary>
    private static bool IsInOldSynthRack(ReadOnlySpan<byte> before)
    {
        if (before.LastIndexOf(CubaseSynthRack) >= 0)
            return true;
        var id = before.LastIndexOf(CubaseChannelId);
        if (id < 0)
            return false;
        var text = before[(id + CubaseChannelId.Length)..];
        return text.Length > 9 && text[4..].StartsWith("Synth"u8);
    }

    private static void ReadCubaseRecord(ReadOnlySpan<byte> data, int hit, UseCounter uses)
    {
        if (ReadCubasePlugin(data, hit) is { } plugin)
            uses.Add(plugin.Name, plugin.Uid);
    }

    /// <summary>读出插入槽里的一个效果器；音源、面板模块和读不全的记录返回 null。</summary>
    private static (string Name, string? Uid)? ReadCubasePlugin(ReadOnlySpan<byte> data, int hit)
    {
        // 只有插入槽和乐器槽的记录前面才是这个类型；面板 EQ、声像器、输入滤波不是，顺带排除了。
        var backStart = Math.Max(0, hit - 96);
        var isA = data[backStart..hit].LastIndexOf(CubaseIsA);
        if (isA < 0 || data[backStart..hit].LastIndexOf(CubaseInsertClass) < 0)
            return null;

        // 乐器槽的标记紧挨在类型前面。
        var isAPosition = backStart + isA;
        var slotStart = Math.Max(0, isAPosition - 64);
        if (data[slotStart..isAPosition].IndexOf(CubaseSynthSlot) >= 0)
            return null;
        if (IsInOldSynthRack(data[Math.Max(0, isAPosition - 240)..isAPosition]))
            return null;

        var position = hit + CubaseMarker.Length;
        var window = data.Slice(position, Math.Min(64, data.Length - position));
        var guidKey = window.IndexOf(CubaseGuidKey);
        if (guidKey < 0)
            return null;
        position += guidKey + CubaseGuidKey.Length;

        if (!TryReadValue(data, ref position, out var guid))
            return null;
        string? name = null;
        string? original = null;
        // 改过名的插件，Plugin Name 是用户起的名字，Original Plugin Name 才是真名。
        for (var i = 0; i < 2; i++)
        {
            if (!TryReadKey(data, ref position, out var key))
                break;
            if (key.SequenceEqual("Plugin Name"u8))
            {
                if (!TryReadValue(data, ref position, out name))
                    return null;
            }
            else if (key.SequenceEqual("Original Plugin Name"u8))
            {
                if (!TryReadValue(data, ref position, out original))
                    return null;
            }
            else
            {
                break;
            }
        }

        var resolved = (original ?? name)?.Trim();
        if (string.IsNullOrEmpty(resolved))
            return null;
        return (resolved, IsHex32(guid) ? guid!.ToUpperInvariant() : null);
    }

    private static bool TryReadKey(ReadOnlySpan<byte> data, ref int position, out ReadOnlySpan<byte> key)
    {
        key = default;
        if (position + 4 > data.Length)
            return false;
        var length = ReadBigEndian(data, position);
        // 字段名都很短；键后面必须跟字符串类型 00 08，否则就不是我们要的字段。
        if (length is < 2 or > 64 || position + 4 + length + 2 > data.Length)
            return false;
        var bytes = data.Slice(position + 4, length);
        if (bytes[^1] != 0 || data[position + 4 + length] != 0 || data[position + 4 + length + 1] != 8)
            return false;
        key = bytes[..^1];
        position += 4 + length + 2;
        return true;
    }

    private static bool TryReadValue(ReadOnlySpan<byte> data, ref int position, out string? value)
    {
        value = null;
        if (position + 4 > data.Length)
            return false;
        var length = ReadBigEndian(data, position);
        if (length is < 1 or > 2048 || position + 4 + length > data.Length)
            return false;
        var bytes = data.Slice(position + 4, length);
        var nul = bytes.IndexOf((byte)0);
        value = DecodeText(nul >= 0 ? bytes[..nul] : bytes);
        position += 4 + length;
        return true;
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> data, int position)
    {
        var value = (uint)(data[position] << 24 | data[position + 1] << 16 | data[position + 2] << 8 | data[position + 3]);
        return value > int.MaxValue ? -1 : (int)value;
    }

    /// <summary>新工程是 UTF-8；老版本 Cubase 在中文系统上存的是 GBK。</summary>
    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    private static bool IsHex32(string? text) => text is { Length: 32 } && text.All(Uri.IsHexDigit);

    // ---------- Studio One ----------
    // .song 是 ZIP，每个调音台设备文件里有 Attributes 元素，subCategory 形如 "VST3/EQ"（Studio One 5.1 真实工程）。
    // 自带插件的 subCategory 不带 VST / AU，乐器带 Synth 或 Instrument。

    private static IReadOnlyList<ProjectPluginUse> StudioOne(Stream stream, CancellationToken cancel)
    {
        var uses = new UseCounter();
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("不是 Studio One 工程文件");
        }
        using (zip)
        {
            var isSong = false;
            foreach (var entry in zip.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("Devices/", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;
                isSong = true;
                // 乐器机架单独存放。
                if (name.Contains("audiosynthfolder", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("musictrackdevice", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.Length > MaxArchiveEntryBytes)
                    continue;
                using var content = entry.Open();
                ReadStudioOneDevices(content, uses);
            }
            if (!isSong)
                throw new InvalidDataException("不是 Studio One 工程文件");
        }
        return uses.ToList();
    }

    private static void ReadStudioOneDevices(Stream content, UseCounter uses)
    {
        // Studio One 的 XML 使用了没有声明的 x: 前缀，要关掉命名空间检查才能读。
        using var reader = new XmlTextReader(content) { Namespaces = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                var category = reader.GetAttribute("subCategory");
                var name = reader.GetAttribute("name");
                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(name))
                    continue;
                if (!IsStudioOneThirdParty(category) || IsInstrumentCategory(category + "|" + reader.GetAttribute("category")))
                    continue;
                uses.Add(name.Trim(), reader.GetAttribute("classID"));
            }
        }
        catch (XmlException e)
        {
            throw new InvalidDataException("工程内容无法读取：" + e.Message);
        }
    }

    private static bool IsStudioOneThirdParty(string category) =>
        category.Contains("VST", StringComparison.OrdinalIgnoreCase)
        || category.Contains("AudioUnit", StringComparison.OrdinalIgnoreCase)
        || category.StartsWith("AU", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstrumentCategory(string category) =>
        category.Contains("Synth", StringComparison.OrdinalIgnoreCase)
        || category.Contains("Instrument", StringComparison.OrdinalIgnoreCase);

    // ---------- REAPER ----------
    // .rpp 是文本。乐器的前缀带 i（VSTi、VST3i、CLAPi、AUi），自带的 Cockos 插件不算第三方。

    private static IReadOnlyList<ProjectPluginUse> Reaper(Stream stream, CancellationToken cancel)
    {
        var uses = new UseCounter();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var first = reader.ReadLine();
        if (first == null || !first.TrimStart().StartsWith("<REAPER_PROJECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不是 REAPER 工程文件");
        while (reader.ReadLine() is { } line)
        {
            cancel.ThrowIfCancellationRequested();
            var match = ReaperPlugin().Match(line);
            if (!match.Success)
                continue;
            var kind = match.Groups["kind"].Value;
            if (kind.EndsWith('i') || kind.EndsWith('I'))
                continue;
            var vendor = match.Groups["vendor"].Value;
            if (vendor.Equals("Cockos", StringComparison.OrdinalIgnoreCase))
                continue;
            uses.Add(match.Groups["name"].Value.Trim(), null);
        }
        return uses.ToList();
    }

    [GeneratedRegex("""^\s*<(?:VST|AU|CLAP)\s+"(?<kind>VST3i|VST3|VSTi|VST|AUi|AU|CLAPi|CLAP)\s*:\s*(?<name>.+?)(?:\s*\((?<vendor>[^()]*)\))?(?:\s*\(\d+\s*(?:in|out)[^)]*\))*"\s""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReaperPlugin();

    // ---------- Ableton Live ----------
    // .als 是 gzip 压缩的 XML。工程里没有可靠的乐器标记，报告阶段再按已安装信息过滤。

    private static IReadOnlyList<ProjectPluginUse> Ableton(Stream stream, CancellationToken cancel)
    {
        var uses = new UseCounter();
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = XmlReader.Create(gzip, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            string? nameElement = null;
            var depth = 0;
            var sawRoot = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                cancel.ThrowIfCancellationRequested();
                if (!sawRoot)
                {
                    if (reader.LocalName != "Ableton")
                        throw new InvalidDataException("不是 Ableton Live 工程文件");
                    sawRoot = true;
                }
                switch (reader.LocalName)
                {
                    case "Vst3PluginInfo" or "AuPluginInfo":
                        nameElement = "Name";
                        depth = reader.Depth;
                        break;
                    case "VstPluginInfo":
                        nameElement = "PlugName";
                        depth = reader.Depth;
                        break;
                    default:
                        if (nameElement != null && reader.Depth == depth + 1 && reader.LocalName == nameElement)
                        {
                            var value = reader.GetAttribute("Value");
                            if (!string.IsNullOrWhiteSpace(value))
                                uses.Add(value.Trim(), null);
                            nameElement = null;
                        }
                        break;
                }
            }
        }
        catch (XmlException e)
        {
            throw new InvalidDataException("工程内容无法读取：" + e.Message);
        }
        return uses.ToList();
    }

    /// <summary>同一工程里相同的插件合成一条，记下用了几次。</summary>
    private sealed class UseCounter
    {
        private readonly Dictionary<string, (string? Uid, int Count)> _uses = new(StringComparer.Ordinal);

        public void Add(string name, string? uid)
        {
            var current = _uses.GetValueOrDefault(name);
            _uses[name] = (current.Uid ?? uid, current.Count + 1);
        }

        public IReadOnlyList<ProjectPluginUse> ToList() =>
            _uses.Select(u => new ProjectPluginUse(u.Key, u.Value.Uid, u.Value.Count))
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
