using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace WhatPlugin.Core;

/// <summary>
/// 读 Mac 可执行文件（Mach-O）里写着的 CPU 架构。只读文件头，不加载。
/// 通用二进制（Universal / fat）里会有多个架构，例如同时有 Intel 和 Apple 芯片的。
/// </summary>
public static class MachO
{
    private const uint FatMagic = 0xCAFEBABE;    // 通用二进制，大端
    private const uint FatMagic64 = 0xCAFEBABF;  // 通用二进制，偏移是 64 位
    private const uint Magic32 = 0xFEEDFACE;
    private const uint Magic64 = 0xFEEDFACF;
    private const uint Cigam32 = 0xCEFAEDFE;     // 字节序相反的写法
    private const uint Cigam64 = 0xCFFAEDFE;

    private const int CpuArch64 = 0x01000000;
    private const int CpuTypeX86 = 7;
    private const int CpuTypeArm = 12;

    // Java 的 class 文件也以 CAFEBABE 开头；真正的通用二进制里架构不会多到这个数。
    private const uint MaxFatArchs = 30;

    /// <summary>返回文件里有的架构；不是 Mach-O 或读不了时返回空。</summary>
    public static IReadOnlyList<Arch> Architectures(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Architectures(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<Arch> Architectures(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
            return [];
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        switch (magic)
        {
            case FatMagic or FatMagic64:
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                if (count == 0 || count > MaxFatArchs)
                    return [];
                // 每一项：cputype, cpusubtype, offset, size, align；64 位版本 offset 和 size 是 8 字节，后面还多 4 字节保留。
                var entrySize = magic == FatMagic ? 20 : 32;
                var entries = new byte[entrySize * count];
                if (stream.ReadAtLeast(entries, entries.Length, throwOnEndOfStream: false) < entries.Length)
                    return [];
                var archs = new List<Arch>();
                for (var i = 0; i < count; i++)
                {
                    var arch = FromCpuType(BinaryPrimitives.ReadInt32BigEndian(entries.AsSpan(i * entrySize)));
                    if (!archs.Contains(arch))
                        archs.Add(arch);
                }
                return archs;
            }
            case Magic32 or Magic64:
                return [FromCpuType(BinaryPrimitives.ReadInt32BigEndian(header[4..]))];
            case Cigam32 or Cigam64:
                return [FromCpuType(BinaryPrimitives.ReadInt32LittleEndian(header[4..]))];
            default:
                return [];
        }
    }

    private static Arch FromCpuType(int cpuType) => cpuType switch
    {
        CpuTypeX86 | CpuArch64 => Arch.X64,
        CpuTypeX86 => Arch.X86,
        CpuTypeArm | CpuArch64 => Arch.Arm64,
        _ => Arch.Unknown,
    };
}

/// <summary>
/// 读 Apple 的属性列表（plist）。支持 XML 和二进制两种写法，插件包里的 Info.plist 两种都有人用。
/// 结果是普通的 .NET 对象：string、long、double、bool、DateTime、byte[]、List&lt;object?&gt;、Dictionary&lt;string, object?&gt;。
/// </summary>
public static class PropertyList
{
    /// <summary>格式不对时返回 null，不抛异常。</summary>
    public static object? Read(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static object? Read(byte[] data)
    {
        try
        {
            return data.AsSpan().StartsWith("bplist00"u8) ? Binary.Parse(data) : ParseXml(data);
        }
        catch (Exception e) when (e is XmlException or FormatException or OverflowException or IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    // ---------- XML ----------

    private static object? ParseXml(byte[] data)
    {
        // 不解析 DTD，也不访问网络：Info.plist 开头的 DOCTYPE 指向 Apple 的网址。
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new MemoryStream(data), settings);
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        if (document.DocumentElement is not { Name: "plist" } root)
            throw new FormatException("不是 plist");
        var first = root.ChildNodes.OfType<XmlElement>().FirstOrDefault();
        return first == null ? null : XmlValue(first);
    }

    private static object? XmlValue(XmlElement element) => element.Name switch
    {
        "string" => element.InnerText,
        "integer" => long.Parse(element.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture),
        "real" => double.Parse(element.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "date" => DateTime.Parse(element.InnerText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        "data" => Convert.FromBase64String(string.Concat(element.InnerText.Where(c => !char.IsWhiteSpace(c)))),
        "array" => element.ChildNodes.OfType<XmlElement>().Select(XmlValue).ToList(),
        "dict" => XmlDict(element),
        _ => throw new FormatException("不认识的 plist 元素：" + element.Name),
    };

    private static Dictionary<string, object?> XmlDict(XmlElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? key = null;
        foreach (var child in element.ChildNodes.OfType<XmlElement>())
        {
            if (child.Name == "key")
                key = child.InnerText;
            else if (key != null)
            {
                result[key] = XmlValue(child);
                key = null;
            }
        }
        return result;
    }

    // ---------- 二进制（bplist00） ----------

    private sealed class Binary
    {
        private readonly byte[] _data;
        private readonly int _refSize;
        private readonly long[] _offsets;
        private int _depth;

        private Binary(byte[] data, int refSize, long[] offsets)
        {
            _data = data;
            _refSize = refSize;
            _offsets = offsets;
        }

        public static object? Parse(byte[] data)
        {
            // 文件末尾 32 字节：6 字节保留、偏移表每项字节数、对象引用字节数、对象个数、根对象、偏移表位置。
            if (data.Length < 8 + 32)
                throw new FormatException("太短");
            var trailer = data.AsSpan(data.Length - 32);
            int offsetSize = trailer[6], refSize = trailer[7];
            var count = BinaryPrimitives.ReadInt64BigEndian(trailer[8..]);
            var top = BinaryPrimitives.ReadInt64BigEndian(trailer[16..]);
            var tableAt = BinaryPrimitives.ReadInt64BigEndian(trailer[24..]);
            if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8 || count <= 0 || count > data.Length
                || top < 0 || top >= count || tableAt < 8 || tableAt + count * offsetSize > data.Length - 32)
                throw new FormatException("文件尾不对");
            var offsets = new long[count];
            for (var i = 0; i < count; i++)
                offsets[i] = ReadUInt(data, (int)(tableAt + i * offsetSize), offsetSize);
            return new Binary(data, refSize, offsets).Object(top);
        }

        private static long ReadUInt(byte[] data, int at, int size)
        {
            long value = 0;
            for (var i = 0; i < size; i++)
                value = (value << 8) | data[at + i];
            return value;
        }

        private object? Object(long index)
        {
            // 防止互相引用造成无限递归。
            if (++_depth > 64)
                throw new FormatException("嵌套太深");
            try
            {
                var at = (int)_offsets[index];
                var marker = _data[at];
                var kind = marker >> 4;
                var info = marker & 0x0F;
                switch (kind)
                {
                    case 0x0:
                        return info switch { 0x8 => false, 0x9 => true, _ => null };
                    case 0x1: // 整数，长度 2^info 字节
                        return ReadInt(at + 1, 1 << info);
                    case 0x2: // 浮点
                        return info == 2
                            ? BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan(at + 1))
                            : BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan(at + 1));
                    case 0x3: // 日期：2001-01-01 起的秒数
                        return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan(at + 1)));
                    case 0x4:
                    {
                        var (length, start) = Length(at, info);
                        return _data.AsSpan(start, length).ToArray();
                    }
                    case 0x5: // ASCII
                    {
                        var (length, start) = Length(at, info);
                        return Encoding.ASCII.GetString(_data, start, length);
                    }
                    case 0x6: // UTF-16 大端，长度按字符数
                    {
                        var (length, start) = Length(at, info);
                        return Encoding.BigEndianUnicode.GetString(_data, start, length * 2);
                    }
                    case 0xA: // 数组
                    {
                        var (length, start) = Length(at, info);
                        var list = new List<object?>(length);
                        for (var i = 0; i < length; i++)
                            list.Add(Object(Ref(start + i * _refSize)));
                        return list;
                    }
                    case 0xD: // 字典：先是全部键的引用，再是全部值的引用
                    {
                        var (length, start) = Length(at, info);
                        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                        for (var i = 0; i < length; i++)
                        {
                            if (Object(Ref(start + i * _refSize)) is not string key)
                                throw new FormatException("字典的键不是文字");
                            dict[key] = Object(Ref(start + (length + i) * _refSize));
                        }
                        return dict;
                    }
                    default:
                        throw new FormatException($"不认识的 plist 类型 0x{marker:X2}");
                }
            }
            finally
            {
                _depth--;
            }
        }

        private long ReadInt(int at, int size) => size switch
        {
            1 => _data[at],
            2 => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at)),
            4 => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at)),
            8 => BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(at)),
            _ => throw new FormatException("整数长度不对"),
        };

        /// <summary>长度在低 4 位；等于 0xF 时后面再跟一个整数对象表示长度。</summary>
        private (int Length, int Start) Length(int at, int info)
        {
            if (info != 0xF)
                return (info, at + 1);
            var marker = _data[at + 1];
            if (marker >> 4 != 0x1)
                throw new FormatException("长度不是整数");
            var size = 1 << (marker & 0x0F);
            var length = ReadInt(at + 2, size);
            if (length < 0 || length > _data.Length)
                throw new FormatException("长度不对");
            return ((int)length, at + 2 + size);
        }

        private long Ref(int at)
        {
            var index = ReadUInt(_data, at, _refSize);
            if (index >= _offsets.Length)
                throw new FormatException("引用越界");
            return index;
        }
    }
}

/// <summary>Mac 插件包里登记的一个 Audio Unit。名称按惯例写成“厂家: 插件名”。</summary>
public sealed record AudioUnitComponent(string Name, string Vendor, string Type, string SubType, string Manufacturer, long? Version)
{
    /// <summary>效果器（aufx、aumf 等）。aumu 是音源，aumi 是 MIDI 处理。</summary>
    public bool IsInstrument => Type == "aumu";
}

/// <summary>Mac 插件包（.vst3、.component、.clap、.vst、.aaxplugin 都是带 Contents/Info.plist 的文件夹）的基本信息。</summary>
public sealed record MacBundleInfo(
    string? Identifier,
    string? Name,
    string? Version,
    string? Executable,
    IReadOnlyList<AudioUnitComponent> AudioUnits)
{
    /// <summary>真正的程序文件：Contents/MacOS/&lt;CFBundleExecutable&gt;。</summary>
    public string? ExecutablePath(string bundlePath) =>
        string.IsNullOrEmpty(Executable) ? null : Path.Combine(bundlePath, "Contents", "MacOS", Executable);

    public static MacBundleInfo? Read(string bundlePath)
    {
        var plist = Path.Combine(bundlePath, "Contents", "Info.plist");
        return File.Exists(plist) && PropertyList.Read(plist) is Dictionary<string, object?> root ? FromPlist(root) : null;
    }

    public static MacBundleInfo FromPlist(Dictionary<string, object?> root)
    {
        var units = new List<AudioUnitComponent>();
        if (root.GetValueOrDefault("AudioComponents") is List<object?> components)
        {
            foreach (var item in components.OfType<Dictionary<string, object?>>())
            {
                var full = Text(item, "name") ?? "";
                var colon = full.IndexOf(':');
                var vendor = colon > 0 ? full[..colon].Trim() : "";
                var name = colon > 0 ? full[(colon + 1)..].Trim() : full.Trim();
                units.Add(new AudioUnitComponent(name, vendor, Text(item, "type") ?? "", Text(item, "subtype") ?? "",
                    Text(item, "manufacturer") ?? "", item.GetValueOrDefault("version") as long?));
            }
        }
        return new MacBundleInfo(
            Text(root, "CFBundleIdentifier"),
            Text(root, "CFBundleName"),
            Text(root, "CFBundleShortVersionString") ?? Text(root, "CFBundleVersion"),
            Text(root, "CFBundleExecutable"),
            units);
    }

    private static string? Text(Dictionary<string, object?> dict, string key) =>
        dict.GetValueOrDefault(key) is string { Length: > 0 } s ? s : null;
}
