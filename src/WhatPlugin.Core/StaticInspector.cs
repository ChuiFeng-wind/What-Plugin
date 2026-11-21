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

        // Mac 插件包：Info.plist 里写着程序文件名，在 Contents/MacOS 下。
        var macOS = Path.Combine(path, "Contents", "MacOS");
        if (Directory.Exists(macOS))
        {
            if (ReadBundleInfo(path)?.ExecutablePath(path) is { } executable && File.Exists(executable))
                return executable;
            var named = Path.Combine(macOS, Path.GetFileNameWithoutExtension(path));
            if (File.Exists(named))
                return named;
            return Directory.GetFiles(macOS).FirstOrDefault();
        }

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

        // Mac 程序文件。通用二进制里同时有几种架构时，取本机能直接加载的那种。
        var macArchs = MachO.Architectures(binary);
        if (macArchs.Count > 0)
            return macArchs.Contains(NativeArch) ? NativeArch : macArchs[0];

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

    /// <summary>本机进程的架构。</summary>
    public static Arch NativeArch => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => Arch.X64,
        System.Runtime.InteropServices.Architecture.Arm64 => Arch.Arm64,
        System.Runtime.InteropServices.Architecture.X86 => Arch.X86,
        _ => Arch.Unknown,
    };

    /// <summary>
    /// 扫描器能不能加载这个架构。Windows 版扫描器是 64 位 Intel 的；
    /// Mac 版是通用二进制，按本机架构运行，只能加载同架构的插件。
    /// </summary>
    public static bool ScannerCanLoad(Arch arch) =>
        arch == Arch.Unknown || arch == (AppPlatform.IsMac ? NativeArch : Arch.X64);

    /// <summary>Mac 插件包的 Info.plist；不是插件包或读不了时返回 null。</summary>
    public static MacBundleInfo? ReadBundleInfo(string bundlePath)
    {
        try
        {
            return Directory.Exists(bundlePath) ? MacBundleInfo.Read(bundlePath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// AU 插件包里登记的插件（Info.plist 的 AudioComponents）。不加载插件。
    /// 没有登记的老式 AU 返回 null，只能记文件信息。
    /// </summary>
    public static IReadOnlyList<PluginClassInfo>? ReadAudioUnits(string bundlePath)
    {
        var info = ReadBundleInfo(bundlePath);
        if (info == null || info.AudioUnits.Count == 0)
            return null;
        return info.AudioUnits
            .Select(unit => new PluginClassInfo(
                unit.Name,
                unit.Vendor,
                unit.Version is { } v ? AudioUnitVersion(v) : info.Version ?? "",
                unit.IsInstrument ? "Instrument" : unit.Type == "aumi" ? "" : "Fx",
                unit.Type == "aumi" ? null : unit.IsInstrument,
                InfoSource.ModuleInfo))
            .ToList();
    }

    /// <summary>AU 的版本号按 0xMMMMmmbb 存（主版本、次版本、修订）。</summary>
    public static string AudioUnitVersion(long version) =>
        $"{(version >> 16) & 0xFFFF}.{(version >> 8) & 0xFF}.{version & 0xFF}";

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

        // Mac 插件包的程序文件里没有 Windows 那样的版本信息，版本写在 Info.plist 里。
        if (version.Length == 0 && ReadBundleInfo(target.Path)?.Version is { } bundleVersion)
            version = bundleVersion.Trim();

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
