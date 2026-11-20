using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WhatPlugin.Core;

/// <summary>Windows“已安装的应用”里的一项。</summary>
public sealed record InstalledProgram(
    string Key,
    string Name,
    string Publisher,
    string Version,
    string UninstallCommand,
    string InstallLocation,
    bool IsMsi);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class InstalledPrograms
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>只读取注册表。系统组件和没有卸载命令的条目不会出现在结果里。</summary>
    /// <param name="includeHidden">也读取系统组件。Waves 工具要用它找隐藏的 MSI 条目，普通卸载不用。</param>
    public static List<InstalledProgram> Load(bool includeHidden = false)
    {
        var found = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall == null)
                    continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || display.Trim().Length == 0)
                        continue;
                    if (entry.GetValue("UninstallString") is not string command || command.Trim().Length == 0)
                        continue;
                    if (!includeHidden && (entry.GetValue("SystemComponent") is 1 || entry.GetValue("ParentKeyName") != null))
                        continue;
                    found.TryAdd(name, new InstalledProgram(
                        name,
                        display.Trim(),
                        (entry.GetValue("Publisher") as string ?? "").Trim(),
                        (entry.GetValue("DisplayVersion") as string ?? "").Trim(),
                        command.Trim(),
                        (entry.GetValue("InstallLocation") as string ?? "").Trim(),
                        entry.GetValue("WindowsInstaller") is 1));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 读不到的位置当作没有，卸载时会退回到列出文件。
            }
        }
        return found.Values.ToList();
    }
}

/// <summary>把卸载命令拆成程序和参数。注册表里的命令经常缺引号。</summary>
public static partial class UninstallCommand
{
    public static (string FileName, string Arguments)? Parse(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command).Trim();
        if (text.Length == 0)
            return null;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close <= 1 ? null : (text[1..close], text[(close + 1)..].Trim());
        }

        var match = ExeSuffix().Match(text);
        if (match.Success)
            return (text[..match.Length].Trim(), text[match.Length..].Trim());
        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    [GeneratedRegex(@"^.*?\.exe(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExeSuffix();
}

public sealed record UninstallMatch(InstalledProgram? Exact, IReadOnlyList<InstalledProgram> Possible);

/// <summary>
/// 找插件对应的官方卸载程序。只有路径或“名字加厂家”对得上才算确定；
/// 只是厂家相同的只列为可能，绝不默认运行。
/// </summary>
public static partial class UninstallMatcher
{
    private const int MaxPossible = 5;
    private static readonly string[] CompanySuffixes = ["incorporated", "limited", "gmbh", "inc", "ltd", "llc", "corp", "co", "audio"];

    public static UninstallMatch Find(PluginRow plugin, IEnumerable<InstalledProgram> programs)
    {
        var list = programs.ToList();
        // 安装目录可能被登记成用户的公共插件文件夹，文件在里面不代表归这个程序，厂商也要对得上。
        var exact = list.FirstOrDefault(p => SameCompany(plugin, p) && plugin.Files.Any(f => IsInside(f.Path, p.InstallLocation)))
                    ?? list.FirstOrDefault(p => NameMatches(plugin, p));
        var vendor = Normalizer.Vendor(plugin.Vendor);
        var possible = vendor.Length < 3
            ? []
            : list.Where(p => p != exact && (PublisherMatches(plugin.Vendor, p.Publisher) || (vendor.Length >= 4 && Normalizer.Name(p.Name).Contains(vendor))))
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxPossible)
                .ToList();
        return new UninstallMatch(exact, possible);
    }

    private static bool NameMatches(PluginRow plugin, InstalledProgram program)
    {
        var name = Normalizer.Name(plugin.Name);
        if (name.Length < 3)
            return false;
        var vendor = Normalizer.Vendor(plugin.Vendor);
        var stripped = Bitness().Replace(DottedVersion().Replace(program.Name, " "), " ");
        var full = Normalizer.Name(stripped);
        var noNumber = Normalizer.Name(TrailingNumber().Replace(stripped.Trim(), ""));
        var candidates = new[] { full, noNumber };
        if (vendor.Length > 0 && candidates.Any(c => c == vendor + name))
            return true;
        return candidates.Contains(name) && PublisherMatches(plugin.Vendor, program.Publisher);
    }

    private static bool SameCompany(PluginRow plugin, InstalledProgram program)
    {
        if (PublisherMatches(plugin.Vendor, program.Publisher))
            return true;
        var company = Company(plugin.Vendor);
        return company.Length >= 4 && Normalizer.Name(program.Name).Contains(company);
    }

    public static bool PublisherMatches(string vendor, string publisher)
    {
        var a = Company(vendor);
        var b = Company(publisher);
        return a.Length >= 3 && b.Length >= 3 && (a == b || a.Contains(b) || b.Contains(a));
    }

    private static string Company(string name)
    {
        var text = Normalizer.Vendor(name);
        foreach (var suffix in CompanySuffixes)
        {
            if (text.Length > suffix.Length + 2 && text.EndsWith(suffix, StringComparison.Ordinal))
                text = text[..^suffix.Length];
        }
        return text;
    }

    /// <summary>安装位置太笼统（驱动器根目录、Program Files、公共插件目录）时不能据此判断。</summary>
    private static bool IsInside(string file, string location)
    {
        if (location.Length == 0 || !Path.IsPathFullyQualified(location))
            return false;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        if (string.Equals(folder, Path.GetPathRoot(folder)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || IsGeneralFolder(folder))
            return false;
        return file.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneralFolder(string folder)
    {
        var general = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.UserProfile,
        }.Select(Environment.GetFolderPath);
        return general.Concat(PluginDiscovery.DefaultRoots().Select(r => r.Path))
            .Select(Path.TrimEndingDirectorySeparator)
            // 公共插件目录本身或它的上级都算笼统，比如 Common Files\Avid\Audio。
            .Any(g => string.Equals(g, folder, StringComparison.OrdinalIgnoreCase) || g.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\b(v|ver|version)?\s*\d+(\.\d+)+\b|\bversion\s+\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex DottedVersion();

    [GeneratedRegex(@"\(?\b(x64|x86|64[- ]?bit|32[- ]?bit)\b\)?", RegexOptions.IgnoreCase)]
    private static partial Regex Bitness();

    [GeneratedRegex(@"\s+\d+$")]
    private static partial Regex TrailingNumber();
}

public sealed record RemovableFile(string Path, PluginFormat Format, long Size);

public sealed record RemovedFile(string Path, string PluginKey, string PluginName, PluginFormat Format, long Size, DateTime RemovedUtc);

/// <summary>垃圾桶里一款插件的卸载方案。Files 已去掉和别的插件共用的文件。</summary>
public sealed record UninstallPlan(
    PluginRow Plugin,
    InstalledProgram? Exact,
    IReadOnlyList<InstalledProgram> Possible,
    IReadOnlyList<RemovableFile> Files,
    IReadOnlyList<string> SharedFiles,
    IReadOnlyList<string> AlsoInProgram,
    int Projects);

public static class RemovalPlanner
{
    /// <summary>WaveShell 由所有 Waves 插件共用，这些插件不能走普通卸载和格式删除。</summary>
    public static bool IsWaves(PluginRow row) =>
        Normalizer.Vendor(row.Vendor) == "waves"
        || row.Files.Any(f => Path.GetFileName(f.Path).StartsWith("WaveShell", StringComparison.OrdinalIgnoreCase));

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public static IReadOnlyList<UninstallPlan> Plan(
        IReadOnlyList<PluginRow> selected,
        IReadOnlyList<PluginRow> all,
        IReadOnlyList<InstalledProgram> programs,
        IReadOnlyDictionary<string, PluginUsage> usage,
        Func<string, bool>? exists = null,
        Func<string, long>? size = null)
    {
        exists ??= PathExists;
        size ??= SizeOf;
        var chosen = selected.Select(r => r.Id).ToHashSet();
        var owners = OwnersByPath(all);
        var exactByRow = all
            .Where(r => r.Kind != PluginKind.Instrument && !IsWaves(r))
            .Select(r => (Row: r, Program: UninstallMatcher.Find(r, programs).Exact))
            .Where(p => p.Program != null)
            .ToList();

        return selected.Select(row =>
        {
            var match = UninstallMatcher.Find(row, programs);
            var shared = row.Files
                .Where(f => owners[f.Path].Any(id => !chosen.Contains(id)))
                .Select(f => f.Path)
                .ToList();
            var files = row.Files
                .Where(f => !shared.Contains(f.Path, StringComparer.OrdinalIgnoreCase) && exists(f.Path))
                .Select(f => new RemovableFile(f.Path, f.Format, size(f.Path)))
                .ToList();
            var others = match.Exact == null
                ? []
                : exactByRow.Where(p => p.Program!.Key == match.Exact.Key && p.Row.Id != row.Id).Select(p => p.Row.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            return new UninstallPlan(row, match.Exact, match.Possible, files, shared, others, usage.GetValueOrDefault(row.Key)?.Projects ?? 0);
        }).ToList();
    }

    public static Dictionary<string, List<long>> OwnersByPath(IEnumerable<PluginRow> all)
    {
        var owners = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in all)
        {
            foreach (var file in row.Files)
            {
                if (!owners.TryGetValue(file.Path, out var list))
                    owners[file.Path] = list = [];
                list.Add(row.Id);
            }
        }
        return owners;
    }

    /// <summary>检查能否删除这些格式。返回不能删除的原因，可以删除时返回 null。</summary>
    public static string? CheckFormatRemoval(
        PluginRow row,
        IReadOnlyCollection<string> paths,
        IReadOnlyList<PluginRow> all,
        Func<string, bool>? exists = null)
    {
        exists ??= PathExists;
        if (IsWaves(row))
            return "Waves 插件共用 WaveShell，不能单独删除某个格式。";
        if (paths.Count == 0)
            return "还没有选择要删除的版本。";
        var owners = OwnersByPath(all);
        var unknown = paths.FirstOrDefault(p => !row.Files.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase)));
        if (unknown != null)
            return "这个文件不属于这款插件：" + unknown;
        var shared = paths.FirstOrDefault(p => owners[p].Any(id => id != row.Id));
        if (shared != null)
            return "这个文件还被其他插件共用：" + shared;
        var usable = row.Files.Any(f =>
            !paths.Contains(f.Path, StringComparer.OrdinalIgnoreCase)
            && f.Status is not (ScanStatus.LoadFailed or ScanStatus.TimedOut)
            && exists(f.Path));
        return usable ? null : "不能删除最后一个可用的版本。要整款移除，请移入垃圾桶后卸载。";
    }
}

/// <summary>
/// 文件操作的路径白名单。提权后的进程也用它检查，所以请求内容不被信任：
/// 只有插件文件夹里的插件文件才能被移动。
/// </summary>
public static class RemovalGuard
{
    private static readonly string[] Extensions = [".vst3", ".dll", ".clap", ".aaxplugin", ".vst"];

    /// <returns>拒绝的原因；可以操作时返回 null。</returns>
    public static string? Reject(string path, IEnumerable<string> extraRoots, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return "路径无效";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            return "路径无效";
        if (!Extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            return "不是插件文件";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
            return "在系统文件夹里";

        var root = AllowedRoots(extraRoots).FirstOrDefault(r => full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        if (root == null)
            return "不在插件文件夹里";
        if (mustExist && !RemovalPlanner.PathExists(full))
            return "文件不存在";

        // 符号链接和目录联接指向别处，移动它们的结果和看到的路径不一致。
        for (var current = full; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
        {
            if (RemovalPlanner.PathExists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return "路径里有链接";
        }
        return null;
    }

    private static IEnumerable<string> AllowedRoots(IEnumerable<string> extraRoots)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var (root, _) in PluginDiscovery.DefaultRoots())
            yield return Path.TrimEndingDirectorySeparator(root);
        foreach (var extra in extraRoots)
        {
            if (!Path.IsPathFullyQualified(extra))
                continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extra));
            // 整个驱动器或系统文件夹不能当作插件文件夹。
            if (root.Length > 3 && !root.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                yield return root;
        }
    }
}

public sealed record RecycledEntry(string OriginalPath, DateTime DeletedUtc, string RecycledPath, string InfoPath);

/// <summary>Windows 回收站。送入用系统接口；恢复时读 $I 记录找到对应的 $R 文件。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400, FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int ShFileOperation(ref ShFileOpStruct operation);

    /// <summary>
    /// 回收站放不下时，系统会先询问，不会悄悄永久删除（FOF_WANTNUKEWARNING）。
    /// </summary>
    /// <returns>0 表示成功，否则是系统错误码。aborted 表示用户在系统询问时取消了。</returns>
    public static int Send(string path, out bool aborted)
    {
        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0",
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofWantNukeWarning),
        };
        var code = ShFileOperation(ref operation);
        aborted = operation.AnyOperationsAborted;
        return code;
    }

    /// <summary>当前用户的回收站目录。</summary>
    public static string FolderFor(string path) =>
        Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", WindowsIdentity.GetCurrent().User!.Value);

    /// <summary>找这个路径最近一次进回收站的记录。没有时返回 null，比如已清空。</summary>
    public static RecycledEntry? Find(string originalPath, string? folder = null)
    {
        folder ??= FolderFor(originalPath);
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "$I*")
                    .Select(ReadInfo)
                    .Where(e => e != null && string.Equals(e.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase) && RemovalPlanner.PathExists(e.RecycledPath))
                    .OrderByDescending(e => e!.DeletedUtc)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>$I 文件格式：版本、大小、删除时间，版本 1 跟固定 520 字节路径，版本 2 跟长度和路径。</summary>
    public static RecycledEntry? ReadInfo(string infoPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoPath);
            if (bytes.Length < 24)
                return null;
            var version = BitConverter.ToInt64(bytes, 0);
            string original;
            if (version == 2 && bytes.Length >= 28)
            {
                var chars = BitConverter.ToInt32(bytes, 24);
                if (chars <= 0 || 28 + (long)chars * 2 > bytes.Length)
                    return null;
                original = Encoding.Unicode.GetString(bytes, 28, chars * 2).TrimEnd('\0');
            }
            else if (version == 1 && bytes.Length >= 24 + 2)
            {
                original = Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24)).Split('\0')[0];
            }
            else
            {
                return null;
            }

            var name = Path.GetFileName(infoPath);
            if (!name.StartsWith("$I", StringComparison.OrdinalIgnoreCase))
                return null;
            var recycled = Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + name[2..]);
            return new RecycledEntry(original, DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16)), recycled, infoPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>放回原位并去掉记录。目标已有同名文件时不覆盖。</summary>
    public static void Restore(RecycledEntry entry)
    {
        if (RemovalPlanner.PathExists(entry.OriginalPath))
            throw new IOException("原位置已经有同名文件");
        Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
        if (Directory.Exists(entry.RecycledPath))
            Directory.Move(entry.RecycledPath, entry.OriginalPath);
        else
            File.Move(entry.RecycledPath, entry.OriginalPath);
        File.Delete(entry.InfoPath);
    }
}

public enum FileOpKind { Recycle, Restore, WavesRecycle, WavesRegistry }

public sealed record FileOpRequest(FileOpKind Kind, IReadOnlyList<string> Paths, IReadOnlyList<string> ExtraRoots);

/// <param name="Unconfirmed">文件已不在原位，但回收站里找不到记录。</param>
public sealed record FileOpResult(string Path, bool Ok, string? Error = null, bool AccessDenied = false, bool Unconfirmed = false);

/// <summary>实际移动文件。普通权限和提权后走的都是这里，每个路径都重新过白名单。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class FileOpExecutor
{
    private const int ErrorAccessDenied = 5, ErrorSharingViolation = 32, DeAccessDeniedSource = 0x78, ErrorCancelled = 1223;

    public static IReadOnlyList<FileOpResult> Execute(FileOpRequest request, string? binFolder = null, WavesPaths? wavesPaths = null, IWavesRegistry? wavesRegistry = null)
    {
        var waves = request.Kind is FileOpKind.WavesRecycle or FileOpKind.WavesRegistry ? wavesPaths ?? WavesPaths.Current() : null;
        var registry = request.Kind == FileOpKind.WavesRegistry ? wavesRegistry ?? new WindowsWavesRegistry() : null;
        return request.Paths.Select(path => request.Kind switch
        {
            FileOpKind.Recycle => Recycle(path, RemovalGuard.Reject(path, request.ExtraRoots), binFolder),
            FileOpKind.WavesRecycle => Recycle(path, WavesGuard.Reject(path, request.ExtraRoots, waves!), binFolder),
            FileOpKind.WavesRegistry => DeleteRegistry(path, request.ExtraRoots, waves!, registry!),
            _ => Restore(path, request.ExtraRoots, binFolder),
        }).ToList();
    }

    /// <param name="id">注册表项，或“项|值”。</param>
    private static FileOpResult DeleteRegistry(string id, IReadOnlyList<string> roots, WavesPaths paths, IWavesRegistry registry)
    {
        var split = id.IndexOf('|');
        var key = split < 0 ? id : id[..split];
        var value = split < 0 ? null : id[(split + 1)..];
        var rejected = WavesRegistryGuard.Reject(key, value, roots, paths, registry);
        if (rejected != null)
            return new FileOpResult(id, false, rejected);
        try
        {
            registry.Delete(key, value);
            return new FileOpResult(id, true);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FileOpResult(id, false, "没有权限", AccessDenied: true);
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            return new FileOpResult(id, false, error.Message);
        }
    }

    private static FileOpResult Recycle(string path, string? rejected, string? binFolder)
    {
        if (rejected != null)
            return new FileOpResult(path, false, rejected);

        var code = RecycleBin.Send(path, out var aborted);
        if (aborted || code == ErrorCancelled)
            return new FileOpResult(path, false, "已取消");
        if (code is ErrorAccessDenied or DeAccessDeniedSource)
            return new FileOpResult(path, false, "没有权限", AccessDenied: true);
        if (code == ErrorSharingViolation)
            return new FileOpResult(path, false, "文件正在被占用，请先关闭 DAW");
        if (code != 0)
            return new FileOpResult(path, false, $"系统错误 0x{code:X}，文件可能正在被占用");
        if (RemovalPlanner.PathExists(path))
            return new FileOpResult(path, false, "文件仍在原位，可能正在被占用");
        return new FileOpResult(path, true, Unconfirmed: RecycleBin.Find(path, binFolder) == null);
    }

    private static FileOpResult Restore(string path, IReadOnlyList<string> roots, string? binFolder)
    {
        var rejected = RemovalGuard.Reject(path, roots, mustExist: false);
        if (rejected != null)
            return new FileOpResult(path, false, rejected);
        var entry = RecycleBin.Find(path, binFolder);
        if (entry == null)
            return new FileOpResult(path, false, "回收站里已经没有这个文件");
        try
        {
            RecycleBin.Restore(entry);
            return new FileOpResult(path, true);
        }
        catch (UnauthorizedAccessException)
        {
            return new FileOpResult(path, false, "没有权限", AccessDenied: true);
        }
        catch (IOException error)
        {
            return new FileOpResult(path, false, error.Message);
        }
    }
}

/// <summary>按需提权：不需要管理员权限的文件直接处理，其余的才弹一次 UAC。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class FileOpRunner
{
    public const string HelperArgument = "--file-helper";

    private static readonly JsonSerializerOptions Json = new();

    public static bool NeedsElevation(string path) =>
        new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Windows,
        }.Select(Environment.GetFolderPath).Any(f => path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));

    public static async Task<IReadOnlyList<FileOpResult>> RunAsync(FileOpRequest request)
    {
        var results = new Dictionary<string, FileOpResult>(StringComparer.OrdinalIgnoreCase);
        var local = request.Paths.Where(p => !NeedsElevation(p)).ToList();
        if (local.Count > 0)
        {
            foreach (var result in await Task.Run(() => FileOpExecutor.Execute(request with { Paths = local })))
                results[result.Path] = result;
        }

        var elevated = request.Paths.Where(NeedsElevation)
            .Concat(results.Values.Where(r => r.AccessDenied).Select(r => r.Path))
            .ToList();
        if (elevated.Count > 0)
        {
            foreach (var result in await RunElevatedAsync(request with { Paths = elevated }))
                results[result.Path] = result;
        }
        return request.Paths.Select(p => results[p]).ToList();
    }

    private static async Task<IReadOnlyList<FileOpResult>> RunElevatedAsync(FileOpRequest request)
    {
        IReadOnlyList<FileOpResult> Fail(string error) => request.Paths.Select(p => new FileOpResult(p, false, error)).ToList();

        var pipeName = "WhatPlugin-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{HelperArgument} {pipeName}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return Fail("没有得到管理员授权");
        }
        if (process == null)
            return Fail("无法启动提权进程");

        using (process)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
        {
            // 提权进程没连上就退出时不用等满时限。留一点时间读完它已经写入的结果。
            _ = process.WaitForExitAsync(timeout.Token).ContinueWith(
                _ => { try { timeout.CancelAfter(TimeSpan.FromSeconds(3)); } catch (ObjectDisposedException) { } },
                TaskScheduler.Default);
            try
            {
                return await ExchangeAsync(server, request, timeout.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException)
            {
                return Fail("提权进程没有返回结果，请重新扫描确认");
            }
        }
    }

    public static async Task<IReadOnlyList<FileOpResult>> ExchangeAsync(NamedPipeServerStream server, FileOpRequest request, CancellationToken cancel)
    {
        await server.WaitForConnectionAsync(cancel);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), cancel);
        var line = await reader.ReadLineAsync(cancel) ?? throw new IOException("没有返回结果");
        return JsonSerializer.Deserialize<List<FileOpResult>>(line, Json) ?? throw new IOException("没有返回结果");
    }

    /// <summary>提权进程的入口。只连接自己的管道，处理一次请求后退出。</summary>
    public static int Serve(string pipeName)
    {
        if (!Regex.IsMatch(pipeName, "^WhatPlugin-[0-9a-f]{32}$"))
            return 2;
        try
        {
            // Identification：管道另一端只能确认身份，不能冒用提权后的令牌。
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            client.Connect(10_000);
            using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var request = JsonSerializer.Deserialize<FileOpRequest>(reader.ReadLine() ?? "", Json);
            if (request == null)
                return 3;
            writer.WriteLine(JsonSerializer.Serialize(FileOpExecutor.Execute(request), Json));
            return 0;
        }
        catch (Exception error) when (error is IOException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return 4;
        }
    }
}
