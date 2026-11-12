using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace WhatPlugin.Core;

/// <summary>不加载插件，只从文件本身读取信息。</summary>
public static class StaticInspector
{
    private static readonly HashSet<string> Vst2EntryPoints = new(StringComparer.Ordinal) { "VSTPluginMain", "main", "MAIN", "main_plugin" };

    private static readonly HashSet<string> PlaceholderVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "todo", "company", "companyname", "your company", "yourcompany", "n/a", "none", "unknown",
    };

    /// <summary>返回真正含有机器码的文件：bundle 目录要进入 Contents 里找。</summary>
    public static string? ResolveBinary(string path, PluginFormat format)
    {
        if (File.Exists(path))
            return path;
        if (!Directory.Exists(path))
            return null;

        var extension = format switch
        {
            PluginFormat.Aax => ".aaxplugin",
            PluginFormat.Clap => ".clap",
            _ => ".vst3",
        };
        foreach (var archDir in new[] { "x86_64-win", "arm64x-win", "arm64ec-win", "arm64-win", "x86-win", "Win32", "x64" })
        {
            var dir = Path.Combine(path, "Contents", archDir);
            if (!Directory.Exists(dir))
                continue;
            var preferred = Path.Combine(dir, Path.GetFileName(path));
            if (File.Exists(preferred))
                return preferred;
            var any = Directory.GetFiles(dir, "*" + extension).FirstOrDefault();
            if (any != null)
                return any;
        }
        return null;
    }

    public static Arch DetectArch(string path, PluginFormat format)
    {
        var binary = ResolveBinary(path, format);
        if (binary == null)
            return Arch.Unknown;
        try
        {
            using var stream = File.OpenRead(binary);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine switch
            {
                Machine.Amd64 => Arch.X64,
                Machine.I386 => Arch.X86,
                Machine.Arm64 => Arch.Arm64,
                _ => Arch.Unknown,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Arch.Unknown;
        }
    }

    public static bool LooksLikeVst2(string dllPath) => ReadExportNames(dllPath).Any(Vst2EntryPoints.Contains);

    public static IReadOnlyList<string> ReadExportNames(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var directory = pe.PEHeaders.PEHeader?.ExportTableDirectory;
            if (directory is not { Size: > 0 } dir)
                return [];

            var table = pe.GetSectionData(dir.RelativeVirtualAddress).GetReader();
            table.Offset = 24;
            var nameCount = table.ReadInt32();
            table.Offset = 32;
            var namesRva = table.ReadInt32();

            var names = new List<string>(nameCount);
            var nameTable = pe.GetSectionData(namesRva).GetReader();
            for (var i = 0; i < nameCount; i++)
            {
                var nameRva = nameTable.ReadInt32();
                var reader = pe.GetSectionData(nameRva).GetReader();
                var sb = new StringBuilder();
                while (reader.RemainingBytes > 0)
                {
                    var b = reader.ReadByte();
                    if (b == 0)
                        break;
                    sb.Append((char)b);
                }
                names.Add(sb.ToString());
            }
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>读取 VST3 bundle 自带的 moduleinfo.json；没有或内容无效时返回 null。</summary>
    public static IReadOnlyList<PluginClassInfo>? ReadModuleInfo(string bundlePath)
    {
        if (!Directory.Exists(bundlePath))
            return null;

        var candidates = new[]
        {
            Path.Combine(bundlePath, "Contents", "Resources", "moduleinfo.json"),
            Path.Combine(bundlePath, "Contents", "moduleinfo.json"),
        };
        var file = candidates.FirstOrDefault(File.Exists);
        if (file == null)
            return null;

        try
        {
            return ParseModuleInfo(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<PluginClassInfo>? ParseModuleInfo(string json)
    {
        try
        {
            // moduleinfo 是 JSON5 风格，常见尾逗号和注释。
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            var factoryVendor = root.TryGetProperty("Factory Info", out var factory) && factory.TryGetProperty("Vendor", out var fv)
                ? fv.GetString() ?? ""
                : "";

            var results = new List<PluginClassInfo>();
            if (!root.TryGetProperty("Classes", out var classes))
                return null;

            foreach (var c in classes.EnumerateArray())
            {
                // 同一模块里还有控制器类，只有 Audio Module Class 才是插件。
                if (GetString(c, "Category") != "Audio Module Class")
                    continue;

                var subCategories = c.TryGetProperty("Sub Categories", out var sub)
                    ? string.Join('|', sub.EnumerateArray().Select(s => s.GetString()))
                    : "";
                var vendor = GetString(c, "Vendor");
                results.Add(new PluginClassInfo(
                    GetString(c, "Name"),
                    vendor.Length > 0 ? vendor : factoryVendor,
                    GetString(c, "Version"),
                    subCategories,
                    IsInstrumentCategory(subCategories),
                    InfoSource.ModuleInfo));
            }
            return results.Count > 0 ? results : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool? IsInstrumentCategory(string category)
    {
        var parts = category.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("Instrument", StringComparer.OrdinalIgnoreCase))
            return true;
        if (parts.Contains("Fx", StringComparer.OrdinalIgnoreCase))
            return false;
        return null;
    }

    /// <summary>只靠文件信息生成一条记录：名称来自文件名，厂家来自版本信息或所在文件夹。</summary>
    public static PluginClassInfo FromFileInfo(ScanTarget target)
    {
        var name = Path.GetFileNameWithoutExtension(target.Path);
        var vendor = "";
        var version = "";
        var source = InfoSource.FileName;

        var binary = ResolveBinary(target.Path, target.Format);
        if (binary != null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(binary);
                if (!PlaceholderVendors.Contains(info.CompanyName?.Trim() ?? ""))
                {
                    vendor = info.CompanyName!.Trim();
                    source = InfoSource.FileInfo;
                }
                version = (info.ProductVersion ?? info.FileVersion ?? "").Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                // 读不到版本信息时保留文件名结果。
            }
        }

        if (vendor.Length == 0)
            vendor = VendorFromFolder(target.Path, target.Root);

        return new PluginClassInfo(name, vendor, version, "", null, source);
    }

    /// <summary>宿主从插件那里问到的名称、厂家和类型比文件信息可靠，有就用。</summary>
    public static PluginClassInfo WithHostInfo(PluginClassInfo fromFile, HostPluginInfo? host)
    {
        if (host == null)
            return fromFile;
        return new PluginClassInfo(
            host.Name,
            host.Vendor.Length > 0 ? host.Vendor : fromFile.Vendor,
            host.Version.Length > 0 ? host.Version : fromFile.Version,
            host.Category,
            IsInstrumentCategory(host.Category),
            InfoSource.ModuleInfo);
    }

    /// <summary>插件目录下的第一层文件夹通常是厂家名。</summary>
    public static string VendorFromFolder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] : "";
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
