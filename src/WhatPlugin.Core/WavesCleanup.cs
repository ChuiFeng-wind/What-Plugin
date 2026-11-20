using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WhatPlugin.Core;

/// <summary>清理项。标准清理和完全清理的默认值见 <see cref="WavesCategories"/>。</summary>
public enum WavesCategory
{
    PluginBody,
    WaveShell,
    Installer,
    Background,
    Licenses,
    UserPresets,
    FactoryPresets,
    Settings,
    Data,
    Central,
    Traces,
}

public static class WavesCategories
{
    /// <summary>卸载程序本来就会移除的内容，不能选择保留。</summary>
    public static bool IsLocked(WavesCategory category) =>
        category is WavesCategory.PluginBody or WavesCategory.WaveShell or WavesCategory.Installer;

    public static bool NeedsBackupWhenKept(WavesCategory category) =>
        category is not (WavesCategory.Central or WavesCategory.Traces) && !IsLocked(category);

    public static IReadOnlyCollection<WavesCategory> Standard { get; } =
        [WavesCategory.PluginBody, WavesCategory.WaveShell, WavesCategory.Installer, WavesCategory.Background];

    public static IReadOnlyCollection<WavesCategory> Full { get; } = Enum.GetValues<WavesCategory>();

    public static string Label(WavesCategory category) => category switch
    {
        WavesCategory.PluginBody => "插件本体",
        WavesCategory.WaveShell => "WaveShell 文件",
        WavesCategory.Installer => "安装程序残留",
        WavesCategory.Background => "后台组件和缓存",
        WavesCategory.Licenses => "授权文件 Licenses",
        WavesCategory.UserPresets => "用户预设",
        WavesCategory.FactoryPresets => "工厂预设",
        WavesCategory.Settings => "软件设置",
        WavesCategory.Data => "脉冲响应等数据",
        WavesCategory.Central => "Waves Central",
        WavesCategory.Traces => "Windows 使用痕迹",
        _ => category.ToString(),
    };

    public static string KeepAction(WavesCategory category) => category switch
    {
        WavesCategory.Central => "保留，不运行它的卸载程序",
        WavesCategory.Traces => "保留，不影响重装",
        _ => "先备份，卸载后恢复",
    };
}

/// <summary>工具用到的系统位置。测试时全部指向假目录。</summary>
public sealed record WavesPaths(
    string ProgramData,
    string LocalAppData,
    string RoamingAppData,
    string Public,
    string ProgramFiles,
    string ProgramFilesX86,
    string CommonFiles,
    string CommonFilesX86,
    string CommonPrograms,
    string Windows,
    IReadOnlyList<string> SearchRoots)
{
    public static WavesPaths Current()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return new WavesPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("PUBLIC") is { Length: > 0 } pub ? pub : Path.Combine(system, "Users", "Public"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList());
    }

    public static WavesPaths UnderTestRoot(string root)
    {
        string At(params string[] parts) => Path.Combine([root, .. parts]);
        return new WavesPaths(
            At("ProgramData"), At("Local"), At("Roaming"), At("Public"), At("PF"), At("PF86"), At("PF", "Common"), At("PF86", "Common"),
            At("ProgramData", "StartMenu"), At("Windows"), [At("Drives", "D"), At("Drives", "E")]);
    }

    public string ProgramDataWaves => Path.Combine(ProgramData, "Waves Audio");
    public string CentralFolder => Path.Combine(ProgramFiles, "Waves Central");
}

public sealed record WavesTarget(string Path, WavesCategory Category, bool IsDirectory, long Size, int Files);

/// <param name="Value">为 null 时表示整个注册表项，否则只处理这一个值。</param>
public sealed record WavesRegistryTarget(string Key, string? Value, WavesCategory Category)
{
    public string Id => Value == null ? Key : Key + "|" + Value;
    public override string ToString() => Value == null ? Key : $"{Key}（值：{Value}）";
}

/// <summary>一个卸载对象。同一 MSI 的显示项和隐藏项合并成一个。</summary>
public sealed record WavesProduct(string Name, string Version, string? MsiCode, string Command, string? Fallback, IReadOnlyList<string> UninstallKeys, bool IsCentral);

public interface IWavesRegistry
{
    bool KeyExists(string key);
    string? ReadValue(string key, string name);
    IReadOnlyList<string> SubKeys(string key);
    IReadOnlyList<string> ValueNames(string key);

    /// <returns>失败原因，成功时返回 null。</returns>
    string? Export(string key, string file);

    /// <summary>删除注册表项或值。这里不做判断，调用前必须过 <see cref="WavesRegistryGuard"/>。</summary>
    void Delete(string key, string? value);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsWavesRegistry : IWavesRegistry
{
    private static RegistryKey? Open(string key, bool write = false)
    {
        var (hive, sub) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(sub, write);
    }

    private static (RegistryHive Hive, string Sub) Split(string key)
    {
        var index = key.IndexOf('\\');
        var hive = index < 0 ? key : key[..index];
        return (hive.ToUpperInvariant() switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            _ => throw new ArgumentException("只支持 HKLM 和 HKCU：" + key),
        }, index < 0 ? "" : key[(index + 1)..]);
    }

    private static T Quiet<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return fallback;
        }
    }

    public bool KeyExists(string key) => Quiet(() =>
    {
        using var k = Open(key);
        return k != null;
    }, false);

    public string? ReadValue(string key, string name) => Quiet(() =>
    {
        using var k = Open(key);
        return k?.GetValue(name) as string;
    }, null);

    public IReadOnlyList<string> SubKeys(string key) => Quiet<IReadOnlyList<string>>(() =>
    {
        using var k = Open(key);
        return k?.GetSubKeyNames() ?? [];
    }, []);

    public IReadOnlyList<string> ValueNames(string key) => Quiet<IReadOnlyList<string>>(() =>
    {
        using var k = Open(key);
        return k?.GetValueNames() ?? [];
    }, []);

    public string? Export(string key, string file)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("reg.exe")
            {
                ArgumentList = { "export", key, file, "/y", "/reg:64" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return process.ExitCode == 0 && File.Exists(file) ? null : $"reg export 返回 {process.ExitCode}";
        }
        catch (Win32Exception error)
        {
            return error.Message;
        }
    }

    public void Delete(string key, string? value)
    {
        if (value != null)
        {
            using var k = Open(key, write: true);
            k?.DeleteValue(value, throwOnMissingValue: false);
            return;
        }
        var (hive, sub) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }
}

/// <summary>测试用注册表：一个 JSON 文件。每次操作都重新读，假卸载程序可以直接改它。</summary>
public sealed class FileWavesRegistry(string file) : IWavesRegistry
{
    private Dictionary<string, Dictionary<string, string>> Load() =>
        File.Exists(file)
            ? new Dictionary<string, Dictionary<string, string>>(
                JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file)) ?? [], StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    public bool KeyExists(string key) => Load().Keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase));

    public string? ReadValue(string key, string name) => Load().TryGetValue(key, out var values) && values.TryGetValue(name, out var value) ? value : null;

    public IReadOnlyList<string> SubKeys(string key) =>
        Load().Keys.Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(key.Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> ValueNames(string key) => Load().TryGetValue(key, out var values) ? values.Keys.ToList() : [];

    public string? Export(string key, string file2)
    {
        File.WriteAllText(file2, key);
        return null;
    }

    public void Delete(string key, string? value)
    {
        var data = Load();
        if (value != null)
            data.GetValueOrDefault(key)?.Remove(value);
        else
            foreach (var k in data.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                data.Remove(k);
        File.WriteAllText(file, JsonSerializer.Serialize(data));
    }
}

public static partial class WavesIdentity
{
    private static readonly string[] Publishers = ["wavesaudio", "wavesaudioltd", "wavesinc", "waves"];
    private static readonly string[] ShellExtensions = [".vst3", ".dll", ".aaxplugin", ".bundle"];

    public static bool IsWavesPublisher(string publisher) =>
        Publishers.Contains(new string(publisher.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant());

    public static bool IsCentral(InstalledProgram program) =>
        program.Name.Equals("Waves Central", StringComparison.OrdinalIgnoreCase) && IsWavesPublisher(program.Publisher);

    /// <summary>必须发布者和名字都对得上。Waves Factory、WaveLab 这类相近名称不算。</summary>
    public static bool IsMainProduct(InstalledProgram program) =>
        !IsCentral(program) && IsWavesPublisher(program.Publisher) && MainName().IsMatch(program.Name);

    public static bool IsWaveShellName(string name) =>
        name.StartsWith("WaveShell", StringComparison.OrdinalIgnoreCase)
        && ShellExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static string? CodeOf(string text) => Guid().Match(text) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    public static bool IsGuidKey(string name) => GuidOnly().IsMatch(name);

    /// <summary>MSI 在注册表里用的产品码写法：前三段整段反转，后两段每两位互换。</summary>
    public static string SquishedCode(string code)
    {
        var hex = code.Trim('{', '}').Replace("-", "").ToUpperInvariant();
        var result = new StringBuilder();
        foreach (var (start, length) in new[] { (0, 8), (8, 4), (12, 4) })
            result.Append(string.Concat(hex.Substring(start, length).Reverse()));
        for (var i = 16; i < 32; i += 2)
            result.Append(hex[i + 1]).Append(hex[i]);
        return result.ToString();
    }

    public static string MsiProductKey(string code) => @"HKLM\SOFTWARE\Classes\Installer\Products\" + SquishedCode(code);

    [GeneratedRegex(@"^Waves\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex MainName();

    [GeneratedRegex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}")]
    private static partial Regex Guid();

    [GeneratedRegex(@"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$")]
    private static partial Regex GuidOnly();
}

/// <summary>文件白名单。提权进程也用它检查，请求内容不被信任。</summary>
public static class WavesGuard
{
    /// <param name="mainRoots">预检确认过的 Waves 主目录，文件夹名必须是 Waves。</param>
    /// <returns>拒绝的原因；可以操作时返回 null。</returns>
    public static string? Reject(string path, IEnumerable<string> mainRoots, WavesPaths paths, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return "路径无效";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))
            return "路径无效";
        if (IsUnder(full, paths.Windows) || full.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
            return "在系统文件夹里";

        var name = Path.GetFileName(full);
        var isShell = WavesIdentity.IsWaveShellName(name);
        var root = AllowedRoots(mainRoots, paths).FirstOrDefault(r => IsUnder(full, r) || full.Equals(r, StringComparison.OrdinalIgnoreCase));
        var isExact = ExactFiles(paths).Contains(full, StringComparer.OrdinalIgnoreCase) || IsCaphyonFolder(full, paths);
        if (root == null && !isExact && !isShell)
            return "不在 Waves 的已知位置";
        if (mustExist && !RemovalPlanner.PathExists(full))
            return "文件不存在";

        // 链接指向别处，移动它们的结果和看到的路径不一致。整条路径都不能有。
        for (var current = full; current != null && current.Length > 3; current = Path.GetDirectoryName(current))
        {
            if (RemovalPlanner.PathExists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return "路径里有链接";
        }
        return null;
    }

    public static bool IsValidMainRoot(string root, WavesPaths paths)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Path.GetFileName(full).Equals("Waves", StringComparison.OrdinalIgnoreCase) && !IsUnder(full, paths.Windows);
    }

    public static bool IsUnder(string path, string folder) =>
        folder.Length > 0 && path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + "\\", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> AllowedRoots(IEnumerable<string> mainRoots, WavesPaths paths)
    {
        foreach (var main in mainRoots.Where(m => IsValidMainRoot(m, paths)))
            yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(main));
        yield return paths.ProgramDataWaves;
        yield return Path.Combine(paths.LocalAppData, "Waves Audio");
        yield return Path.Combine(paths.RoamingAppData, "Waves Audio");
        yield return Path.Combine(paths.RoamingAppData, "Waves Central");
        yield return Path.Combine(paths.RoamingAppData, "WavesAudio");
        yield return Path.Combine(paths.Public, "Waves Audio");
        yield return paths.CentralFolder;
    }

    private static IEnumerable<string> ExactFiles(WavesPaths paths)
    {
        yield return Path.Combine(paths.CommonPrograms, "Waves Central.lnk");
    }

    private static bool IsCaphyonFolder(string full, WavesPaths paths)
    {
        var parent = Path.GetDirectoryName(full);
        return parent != null
            && parent.Equals(Path.Combine(paths.ProgramData, "Caphyon", "Advanced Installer"), StringComparison.OrdinalIgnoreCase)
            && WavesIdentity.IsGuidKey(Path.GetFileName(full))
            && Directory.Exists(full)
            && Directory.EnumerateFiles(full, "Waves*Setup.exe").Any();
    }
}

/// <summary>注册表白名单。只接受逐条列出的位置，MSI 记录和其他软件一律拒绝。</summary>
public static partial class WavesRegistryGuard
{
    public const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static readonly string[] TraceKeys =
    [
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\electron.app.Waves Central",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications\Backup\electron.app.Waves Central",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Start\TileProperties\W~com.WavesAudio.central",
    ];

    public static readonly string[] TraceValueKeys =
    [
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
        @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
    ];

    /// <returns>拒绝的原因；可以操作时返回 null。</returns>
    public static string? Reject(string key, string? value, IEnumerable<string> mainRoots, WavesPaths paths, IWavesRegistry registry)
    {
        if (key.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase))
            return "不处理 Windows Installer 记录";
        if (value != null)
        {
            if (!TraceValueKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                return "这个位置不允许删除单个值";
            return IsWavesTraceValue(value, mainRoots, paths) ? null : "这个值不属于 Waves";
        }

        if (Exact().IsMatch(key) || TraceKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            return null;
        if (key.Equals(@"HKLM\SOFTWARE\Classes\CEN", StringComparison.OrdinalIgnoreCase))
            return registry.ReadValue(key + @"\shell\open\command", "")?.Contains("Waves Central", StringComparison.OrdinalIgnoreCase) == true ? null : "这个文件关联不属于 Waves Central";

        if (AdvancedInstaller().Match(key) is { Success: true } lzma)
        {
            var code = lzma.Groups[1].Value;
            return IsUninstallRegistered(code, registry) ? "这个产品还没有卸载" : null;
        }
        if (GuidSoftware().IsMatch(key))
            return registry.ReadValue(key, "ShortcutName")?.Equals("Waves Central", StringComparison.OrdinalIgnoreCase) == true ? null : "这个注册表项不属于 Waves Central";
        if (Uninstall().IsMatch(key))
            return IsStaleWavesEntry(key, registry) ? null : "这个卸载项还有效，或不属于 Waves";
        return "不在允许的注册表位置";
    }

    public static bool IsWavesTraceValue(string value, IEnumerable<string> mainRoots, WavesPaths paths) =>
        mainRoots.Where(m => WavesGuard.IsValidMainRoot(m, paths)).Append(paths.CentralFolder).Any(root => WavesGuard.IsUnder(value, root));

    public static bool IsUninstallRegistered(string code, IWavesRegistry registry) =>
        registry.KeyExists(WavesIdentity.MsiProductKey(code)) || registry.KeyExists(@"HKLM\" + UninstallRoot + "\\" + code);

    /// <summary>发布者是 Waves，卸载程序文件已不存在，MSI 也没有登记。这种卸载项才算失效。</summary>
    public static bool IsStaleWavesEntry(string key, IWavesRegistry registry)
    {
        if (!WavesIdentity.IsWavesPublisher(registry.ReadValue(key, "Publisher") ?? ""))
            return false;
        var command = registry.ReadValue(key, "UninstallString") ?? "";
        if (WavesIdentity.CodeOf(key + " " + command) is { } code && registry.KeyExists(WavesIdentity.MsiProductKey(code)))
            return false;
        return UninstallCommand.Parse(command) is not { } parsed || (!IsMsiExec(parsed.FileName) && !File.Exists(parsed.FileName));
    }

    public static bool IsMsiExec(string file) => Path.GetFileNameWithoutExtension(file).Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(HKLM\\SOFTWARE\\(WOW6432Node\\)?WavesAudio|HKCU\\Software\\Classes\\waves-central)$", RegexOptions.IgnoreCase)]
    private static partial Regex Exact();

    [GeneratedRegex(@"^HKLM\\SOFTWARE\\Caphyon\\Advanced Installer\\LZMA\\(\{[0-9a-f-]{36}\})$", RegexOptions.IgnoreCase)]
    private static partial Regex AdvancedInstaller();

    [GeneratedRegex(@"^HKLM\\SOFTWARE\\\{[0-9a-f-]{36}\}$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidSoftware();

    [GeneratedRegex(@"^(HKLM|HKCU)\\SOFTWARE\\(WOW6432Node\\)?Microsoft\\Windows\\CurrentVersion\\Uninstall\\[^\\]+$", RegexOptions.IgnoreCase)]
    private static partial Regex Uninstall();
}

public sealed class WavesEnvironment
{
    public required WavesPaths Paths { get; init; }
    public required IWavesRegistry Registry { get; init; }
    public required Func<IReadOnlyList<InstalledProgram>> Programs { get; init; }
    public required Func<IReadOnlyList<string>> Processes { get; init; }

    /// <summary>启动卸载命令，不加静默参数。返回失败原因。</summary>
    public required Func<string, bool, string?> Launch { get; init; }

    public required Func<IReadOnlyList<string>, IReadOnlyList<string>, Task<IReadOnlyList<FileOpResult>>> Recycle { get; init; }
    public required Func<IReadOnlyList<WavesRegistryTarget>, IReadOnlyList<string>, Task<IReadOnlyList<FileOpResult>>> DeleteRegistry { get; init; }
    public required string BackupRoot { get; init; }
    public bool IsTest { get; init; }

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static WavesEnvironment Real()
    {
        var paths = WavesPaths.Current();
        return new WavesEnvironment
        {
            Paths = paths,
            Registry = new WindowsWavesRegistry(),
            Programs = () => InstalledPrograms.Load(includeHidden: true),
            Processes = () => Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).Distinct().ToList(),
            Launch = LaunchCommand,
            Recycle = (items, roots) => FileOpRunner.RunAsync(new FileOpRequest(FileOpKind.WavesRecycle, items, roots)),
            DeleteRegistry = (items, roots) => FileOpRunner.RunAsync(new FileOpRequest(FileOpKind.WavesRegistry, items.Select(t => t.Id).ToList(), roots)),
            BackupRoot = Path.Combine(paths.LocalAppData, "WhatPlugin", "Waves备份"),
        };
    }

    /// <summary>假目录环境。卸载项和注册表来自测试文件夹里的 JSON，不会读写真实的 Waves。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static WavesEnvironment ForTest(string root)
    {
        var paths = WavesPaths.UnderTestRoot(root);
        var registry = new FileWavesRegistry(Path.Combine(root, "registry.json"));
        var programsFile = Path.Combine(root, "programs.json");
        var processesFile = Path.Combine(root, "processes.txt");
        return new WavesEnvironment
        {
            Paths = paths,
            Registry = registry,
            Programs = () => File.Exists(programsFile) ? JsonSerializer.Deserialize<List<InstalledProgram>>(File.ReadAllText(programsFile)) ?? [] : [],
            Processes = () => File.Exists(processesFile) ? File.ReadAllLines(processesFile) : [],
            Launch = LaunchCommand,
            Recycle = (items, roots) => Task.FromResult(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.WavesRecycle, items, roots), null, paths, registry)),
            DeleteRegistry = (items, roots) => Task.FromResult(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.WavesRegistry, items.Select(t => t.Id).ToList(), roots), null, paths, registry)),
            BackupRoot = Path.Combine(root, "Backup"),
            IsTest = true,
        };
    }

    private static string? LaunchCommand(string commandLine, bool elevated)
    {
        var command = UninstallCommand.Parse(commandLine);
        if (command == null)
            return "卸载命令无法识别：" + commandLine;
        try
        {
            var start = new ProcessStartInfo(command.Value.FileName, command.Value.Arguments) { UseShellExecute = true };
            if (elevated)
                start.Verb = "runas";
            using var process = Process.Start(start);
            return null;
        }
        catch (Win32Exception error)
        {
            return error.NativeErrorCode == 1223 ? "你取消了管理员授权。" : "启动失败：" + error.Message;
        }
    }
}

public sealed record WavesSurvey(
    IReadOnlyList<WavesProduct> Products,
    IReadOnlyList<string> MainRoots,
    IReadOnlyList<string> KnownCodes,
    IReadOnlyList<WavesTarget> Targets,
    IReadOnlyList<WavesRegistryTarget> RegistryTargets,
    IReadOnlyList<string> RunningBlockers,
    IReadOnlyList<string> RunningBackground)
{
    public IEnumerable<WavesProduct> MainProducts => Products.Where(p => !p.IsCentral);
    public WavesProduct? Central => Products.FirstOrDefault(p => p.IsCentral);
    public bool IsEmpty => Products.Count == 0 && Targets.Count == 0 && RegistryTargets.Count == 0;
    public long SizeOf(WavesCategory category) => Targets.Where(t => t.Category == category).Sum(t => t.Size);
}

/// <summary>只读预检。只查已知位置和预检确认的主目录，绝不按“名字里有 Waves”判断。</summary>
public static partial class WavesSurveyor
{
    public static WavesSurvey Scan(WavesEnvironment env, IEnumerable<string>? knownRoots = null, IEnumerable<string>? knownCodes = null)
    {
        var paths = env.Paths;
        var registry = env.Registry;
        var programs = env.Programs().Where(p => WavesIdentity.IsMainProduct(p) || WavesIdentity.IsCentral(p)).ToList();
        var registryTargets = new List<WavesRegistryTarget>();
        var products = BuildProducts(programs, registry, registryTargets);
        var roots = FindMainRoots(env, programs, knownRoots);

        var targets = new List<WavesTarget>();
        void Add(string path, WavesCategory category)
        {
            if (Directory.Exists(path))
            {
                if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                {
                    var (size, files) = Measure(path);
                    targets.Add(new WavesTarget(path, category, true, size, files));
                }
            }
            else if (File.Exists(path))
            {
                targets.Add(new WavesTarget(path, category, false, new FileInfo(path).Length, 1));
            }
        }

        foreach (var root in roots)
        {
            foreach (var child in Children(root))
            {
                if (!Path.GetFileName(child).Equals("Data", StringComparison.OrdinalIgnoreCase))
                {
                    Add(child, WavesCategory.PluginBody);
                    continue;
                }
                foreach (var data in Children(child))
                    Add(data, Path.GetFileName(data).Equals("Presets", StringComparison.OrdinalIgnoreCase) ? WavesCategory.FactoryPresets : WavesCategory.Data);
            }
        }

        foreach (var folder in ShellFolders(paths))
        {
            foreach (var child in Children(folder).Where(c => WavesIdentity.IsWaveShellName(Path.GetFileName(c))))
                Add(child, WavesCategory.WaveShell);
        }

        foreach (var child in Children(paths.ProgramDataWaves))
        {
            var name = Path.GetFileName(child);
            Add(child, name.Equals("Licenses", StringComparison.OrdinalIgnoreCase) ? WavesCategory.Licenses
                : name.Equals("Preset Browser", StringComparison.OrdinalIgnoreCase) ? WavesCategory.FactoryPresets
                : WavesCategory.Background);
        }
        Add(Path.Combine(paths.LocalAppData, "Waves Audio"), WavesCategory.Background);
        Add(Path.Combine(paths.Public, "Waves Audio"), WavesCategory.UserPresets);
        Add(Path.Combine(paths.RoamingAppData, "Waves Audio"), WavesCategory.Settings);
        Add(Path.Combine(paths.RoamingAppData, "WavesAudio"), WavesCategory.Installer);
        Add(paths.CentralFolder, WavesCategory.Central);
        Add(Path.Combine(paths.RoamingAppData, "Waves Central"), WavesCategory.Central);
        Add(Path.Combine(paths.CommonPrograms, "Waves Central.lnk"), WavesCategory.Central);

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in (knownCodes ?? []).Concat(products.Select(p => p.MsiCode).OfType<string>()))
            codes.Add(code);
        foreach (var folder in Children(Path.Combine(paths.ProgramData, "Caphyon", "Advanced Installer")))
        {
            if (WavesIdentity.IsGuidKey(Path.GetFileName(folder)) && Directory.Exists(folder) && Directory.EnumerateFiles(folder, "Waves*Setup.exe").Any())
            {
                Add(folder, WavesCategory.Installer);
                codes.Add(Path.GetFileName(folder).ToUpperInvariant());
            }
        }

        AddRegistryTargets(registry, paths, roots, codes, registryTargets);

        var processes = env.Processes();
        var blockers = processes.Where(IsHost).Concat(processes.Where(p => p.Equals("Waves Central", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var background = processes.Where(p => p.StartsWith("Waves", StringComparison.OrdinalIgnoreCase) && !blockers.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        return new WavesSurvey(products, roots, codes.ToList(), targets, registryTargets, blockers, background);
    }

    /// <summary>全盘搜索标准目录以外的 WaveShell。结果默认不勾选，由用户决定。</summary>
    public static IReadOnlyList<WavesTarget> FindExtraShells(WavesEnvironment env, WavesSurvey survey, CancellationToken cancel = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        var known = survey.Targets.Select(t => t.Path).Concat(survey.MainRoots).Append(env.BackupRoot).Append(env.Paths.Windows).ToList();
        var found = new List<string>();
        foreach (var root in env.Paths.SearchRoots.Where(Directory.Exists))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "WaveShell*", options))
                {
                    cancel.ThrowIfCancellationRequested();
                    if (WavesIdentity.IsWaveShellName(Path.GetFileName(entry))
                        && !entry.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase)
                        && !entry.Contains(@"\System Volume Information\", StringComparison.OrdinalIgnoreCase)
                        && !known.Any(k => k.Equals(entry, StringComparison.OrdinalIgnoreCase) || WavesGuard.IsUnder(entry, k)))
                        found.Add(entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 这个盘读不完就用已经找到的结果。
            }
        }

        // 包里面的同名文件跟着包走，不单独列出。
        return found.Where(f => !found.Any(o => !o.Equals(f, StringComparison.OrdinalIgnoreCase) && WavesGuard.IsUnder(f, o)))
            .Select(f =>
            {
                var directory = Directory.Exists(f);
                var (size, files) = directory ? Measure(f) : (new FileInfo(f).Length, 1);
                return new WavesTarget(f, WavesCategory.WaveShell, directory, size, files);
            }).ToList();
    }

    public static string Describe(WavesSurvey survey, IReadOnlySet<WavesCategory> clean, IEnumerable<WavesTarget>? extras = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Waves 卸载预演清单（只读，没有改动任何内容）").AppendLine();
        text.AppendLine("卸载程序：");
        foreach (var product in survey.Products)
            text.AppendLine($"  {product.Name} {product.Version}：{product.Command}" + (product.Fallback != null && product.Fallback != product.Command ? $"（失败时：{product.Fallback}）" : ""));
        if (survey.Products.Count == 0)
            text.AppendLine("  没有找到");
        text.AppendLine().AppendLine("主目录：" + (survey.MainRoots.Count == 0 ? "没有找到" : string.Join("、", survey.MainRoots)));
        if (survey.RunningBlockers.Count > 0)
            text.AppendLine("必须先关闭：" + string.Join("、", survey.RunningBlockers));
        if (survey.RunningBackground.Count > 0)
            text.AppendLine("正在运行的 Waves 后台程序：" + string.Join("、", survey.RunningBackground));

        foreach (var category in Enum.GetValues<WavesCategory>())
        {
            var files = survey.Targets.Where(t => t.Category == category).ToList();
            var keys = survey.RegistryTargets.Where(t => t.Category == category).ToList();
            if (files.Count == 0 && keys.Count == 0)
                continue;
            var action = clean.Contains(category) ? "卸载后清理，文件进回收站" : WavesCategories.KeepAction(category);
            text.AppendLine().AppendLine($"{WavesCategories.Label(category)}（{FormatSize(files.Sum(f => f.Size))}）：{action}");
            foreach (var file in files)
                text.AppendLine($"  {file.Path}  {FormatSize(file.Size)}");
            foreach (var key in keys)
                text.AppendLine($"  注册表 {key}");
        }

        var extraList = extras?.ToList() ?? [];
        if (extraList.Count > 0)
        {
            text.AppendLine().AppendLine("标准目录以外的 WaveShell（默认不处理）：");
            foreach (var extra in extraList)
                text.AppendLine("  " + extra.Path);
        }
        text.AppendLine().AppendLine("不会处理：Windows Installer 的 MSI 记录（由 MSI 自己卸载）、WOW6432Node 整体、其他软件里名叫 Waves 的文件夹。");
        return text.ToString();
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
        : $"{Math.Max(bytes, bytes > 0 ? 1024 : 0) / 1024} KB";

    public static (long Size, int Files) Measure(string folder)
    {
        long size = 0;
        var files = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            size += file.Length;
            files++;
        }
        return (size, files);
    }

    private static IEnumerable<string> Children(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        try
        {
            return Directory.GetFileSystemEntries(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ShellFolders(WavesPaths paths)
    {
        foreach (var program in new[] { paths.ProgramFiles, paths.ProgramFilesX86 })
        {
            yield return Path.Combine(program, "VSTPlugins");
            yield return Path.Combine(program, "Steinberg", "VSTPlugins");
        }
        foreach (var common in new[] { paths.CommonFiles, paths.CommonFilesX86 })
        {
            yield return Path.Combine(common, "VST3");
            yield return Path.Combine(common, "VST2");
            yield return Path.Combine(common, "Avid", "Audio", "Plug-Ins");
            yield return Path.Combine(common, "WPAPI");
        }
    }

    private static List<string> FindMainRoots(WavesEnvironment env, IReadOnlyList<InstalledProgram> programs, IEnumerable<string>? known)
    {
        var candidates = new List<string>(known ?? []);
        foreach (var location in programs.Where(WavesIdentity.IsMainProduct).Select(p => p.InstallLocation).Where(l => l.Length > 0 && Path.IsPathFullyQualified(l)))
            candidates.AddRange([Path.Combine(location, "Waves"), location]);
        foreach (var sub in env.Registry.SubKeys(@"HKLM\SOFTWARE\WavesAudio"))
        {
            if (env.Registry.ReadValue(@"HKLM\SOFTWARE\WavesAudio\" + sub, "Path") is { Length: > 0 } path && Path.IsPathFullyQualified(path))
                candidates.AddRange([Path.Combine(path, "Waves"), path]);
        }
        candidates.Add(Path.Combine(env.Paths.ProgramFiles, "Waves"));
        candidates.Add(Path.Combine(env.Paths.ProgramFilesX86, "Waves"));
        candidates.AddRange(env.Paths.SearchRoots.Select(r => Path.Combine(r, "Waves")));

        var knownSet = new HashSet<string>(known ?? [], StringComparer.OrdinalIgnoreCase);
        // 测试环境只认测试文件夹里的主目录。假数据写错路径时也不能摸到真实的 Waves。
        var testRoot = env.IsTest ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(env.BackupRoot)) : null;
        return candidates
            .Select(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => testRoot == null || WavesGuard.IsUnder(c, testRoot))
            .Where(c => WavesGuard.IsValidMainRoot(c, env.Paths) && Directory.Exists(c) && (knownSet.Contains(c) || HasSignature(c)))
            .ToList();
    }

    /// <summary>主目录必须有 Waves 特有的结构。其他软件里叫 Waves 的文件夹没有这些。</summary>
    private static bool HasSignature(string folder)
    {
        var names = Children(folder).Select(Path.GetFileName).OfType<string>().ToList();
        return names.Any(n => MainFolder().IsMatch(n))
            || Directory.Exists(Path.Combine(folder, "Data", "IR1Impulses"));
    }

    private static List<WavesProduct> BuildProducts(IReadOnlyList<InstalledProgram> programs, IWavesRegistry registry, List<WavesRegistryTarget> stale)
    {
        var products = new List<WavesProduct>();
        foreach (var group in programs.GroupBy(p => WavesIdentity.CodeOf(p.Key + " " + p.UninstallCommand) ?? p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToList();
            var first = entries.FirstOrDefault(e => !e.IsMsi) ?? entries[0];
            var central = entries.Any(WavesIdentity.IsCentral);
            var code = !central && WavesIdentity.CodeOf(group.Key) is { } c && (entries.Any(e => e.IsMsi) || registry.KeyExists(WavesIdentity.MsiProductKey(c))) ? c : null;
            var msiCommand = code == null ? null : $"MsiExec.exe /X{code}";
            var direct = entries.Select(e => e.UninstallCommand).FirstOrDefault(CanRun);
            if (direct == null && msiCommand == null)
            {
                foreach (var entry in entries)
                {
                    if (FindUninstallKey(entry, registry) is { } key)
                        stale.Add(new WavesRegistryTarget(key, null, central ? WavesCategory.Central : WavesCategory.Installer));
                }
                continue;
            }
            products.Add(new WavesProduct(first.Name, first.Version, code, direct ?? msiCommand!, msiCommand, entries.Select(e => e.Key).ToList(), central));
        }
        return products.OrderBy(p => p.IsCentral).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>MSI 的“修改”命令（/I）不算卸载命令，文件已不存在的也不算。</summary>
    private static bool CanRun(string command)
    {
        if (UninstallCommand.Parse(command) is not { } parsed)
            return false;
        return WavesRegistryGuard.IsMsiExec(parsed.FileName)
            ? parsed.Arguments.StartsWith("/x", StringComparison.OrdinalIgnoreCase)
            : File.Exists(parsed.FileName);
    }

    private static string? FindUninstallKey(InstalledProgram program, IWavesRegistry registry)
    {
        foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            var key = root + "\\" + program.Key;
            if (registry.KeyExists(key))
                return key;
        }
        return null;
    }

    private static void AddRegistryTargets(IWavesRegistry registry, WavesPaths paths, IReadOnlyList<string> roots, IEnumerable<string> codes, List<WavesRegistryTarget> targets)
    {
        void Key(string key, WavesCategory category)
        {
            if (registry.KeyExists(key) && WavesRegistryGuard.Reject(key, null, roots, paths, registry) is null or "这个产品还没有卸载")
                targets.Add(new WavesRegistryTarget(key, null, category));
        }

        Key(@"HKLM\SOFTWARE\WavesAudio", WavesCategory.Installer);
        Key(@"HKLM\SOFTWARE\WOW6432Node\WavesAudio", WavesCategory.Installer);
        foreach (var code in codes)
            Key(@"HKLM\SOFTWARE\Caphyon\Advanced Installer\LZMA\" + code, WavesCategory.Installer);

        foreach (var sub in registry.SubKeys(@"HKLM\SOFTWARE").Where(WavesIdentity.IsGuidKey))
            Key(@"HKLM\SOFTWARE\" + sub, WavesCategory.Central);
        Key(@"HKLM\SOFTWARE\Classes\CEN", WavesCategory.Central);
        Key(@"HKCU\Software\Classes\waves-central", WavesCategory.Central);

        foreach (var key in WavesRegistryGuard.TraceKeys)
            Key(key, WavesCategory.Traces);
        foreach (var key in WavesRegistryGuard.TraceValueKeys)
        {
            foreach (var value in registry.ValueNames(key).Where(v => WavesRegistryGuard.IsWavesTraceValue(v, roots, paths)))
                targets.Add(new WavesRegistryTarget(key, value, WavesCategory.Traces));
        }
    }

    private static bool IsHost(string process)
    {
        var name = process.ToLowerInvariant();
        return name.StartsWith("cubase") || name.StartsWith("nuendo") || name.StartsWith("wavelab") || name.StartsWith("reaper")
            || name.StartsWith("ableton live") || name.StartsWith("studio one") || name.StartsWith("protools") || name.StartsWith("bitwig")
            || name is "fl" or "fl64" or "samplitude" or "sequoia";
    }

    [GeneratedRegex(@"^(Plug-Ins|WaveShells|Applications) V\d+", RegexOptions.IgnoreCase)]
    private static partial Regex MainFolder();
}

public sealed record WavesBackupFile(string Relative, long Size, string Sha256);

public sealed record WavesBackupItem(string Path, WavesCategory Category, bool IsDirectory, string Folder, IReadOnlyList<WavesBackupFile> Files, IReadOnlyList<string> EmptyFolders);

public sealed record WavesBackupManifest(DateTime CreatedUtc, bool Restored, IReadOnlyList<string> MainRoots, IReadOnlyList<WavesBackupItem> Items)
{
    public long Size => Items.Sum(i => i.Files.Sum(f => f.Size));
}

/// <summary>把保留项复制出来并校验。还原时也校验，只覆盖内容不同的文件。</summary>
public static class WavesBackup
{
    public const string ManifestName = "manifest.json";
    private const string OtherLocations = "other-locations.txt";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <returns>放不下时返回说明，否则返回 null。</returns>
    public static string? CheckSpace(IEnumerable<WavesTarget> targets, string backupRoot)
    {
        var need = targets.Sum(t => t.Size);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(backupRoot))!);
            // 留 500 MB 余量，回收站和系统也要用空间。
            return drive.AvailableFreeSpace < need + (500L << 20)
                ? $"备份需要 {WavesSurveyor.FormatSize(need)}，{drive.Name} 只剩 {WavesSurveyor.FormatSize(drive.AvailableFreeSpace)}。请换一个备份位置，或减少保留项。"
                : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "无法读取备份位置的剩余空间：" + e.Message;
        }
    }

    public static string Create(IReadOnlyList<WavesTarget> targets, IReadOnlyList<string> mainRoots, string backupRoot, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var folder = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        var items = new List<WavesBackupItem>();
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var name = i.ToString("000");
            var destination = Path.Combine(folder, "files", name);
            Directory.CreateDirectory(destination);
            var files = new List<WavesBackupFile>();
            var empty = new List<string>();
            progress?.Report($"备份 {target.Path}");
            if (target.IsDirectory)
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(target.Path, "*", options))
                {
                    cancel.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(target.Path, file);
                    files.Add(CopyVerified(file, Path.Combine(destination, relative), relative));
                }
                foreach (var directory in Directory.EnumerateDirectories(target.Path, "*", options))
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        empty.Add(Path.GetRelativePath(target.Path, directory));
                }
            }
            else
            {
                var relative = Path.GetFileName(target.Path);
                files.Add(CopyVerified(target.Path, Path.Combine(destination, relative), relative));
            }
            items.Add(new WavesBackupItem(target.Path, target.Category, target.IsDirectory, name, files, empty));
        }
        Save(folder, new WavesBackupManifest(DateTime.UtcNow, false, mainRoots, items));
        return folder;
    }

    /// <returns>没能恢复的项，全部成功时为空。全部成功才标记为已恢复。</returns>
    public static IReadOnlyList<string> Restore(string folder, WavesPaths paths, IProgress<string>? progress = null)
    {
        var manifest = Load(folder) ?? throw new IOException("备份清单无法读取：" + folder);
        var errors = new List<string>();
        foreach (var item in manifest.Items)
        {
            progress?.Report($"恢复 {item.Path}");
            var rejected = WavesGuard.Reject(item.Path, manifest.MainRoots, paths, mustExist: false);
            if (rejected != null)
            {
                errors.Add($"{item.Path}：{rejected}");
                continue;
            }
            var source = Path.Combine(folder, "files", item.Folder);
            foreach (var file in item.Files)
            {
                var from = Path.Combine(source, file.Relative);
                var to = item.IsDirectory ? Path.Combine(item.Path, file.Relative) : item.Path;
                try
                {
                    if (!WavesGuard.IsUnder(Path.GetFullPath(from), source) || (item.IsDirectory && !WavesGuard.IsUnder(Path.GetFullPath(to), item.Path)))
                        throw new IOException("清单里的路径无效");
                    if (File.Exists(to) && new FileInfo(to).Length == file.Size && HashOf(to) == file.Sha256)
                        continue;
                    if (!File.Exists(from) || HashOf(from) != file.Sha256)
                        throw new IOException("备份文件已损坏或丢失");
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to, overwrite: true);
                    if (HashOf(to) != file.Sha256)
                        throw new IOException("恢复后校验不一致");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{to}：{e.Message}");
                }
            }
            foreach (var directory in item.EmptyFolders)
            {
                try
                {
                    var full = Path.Combine(item.Path, directory);
                    if (WavesGuard.IsUnder(Path.GetFullPath(full), item.Path))
                        Directory.CreateDirectory(full);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{directory}：{e.Message}");
                }
            }
        }
        if (errors.Count == 0)
            Save(folder, manifest with { Restored = true });
        return errors;
    }

    public static WavesBackupManifest? Load(string folder)
    {
        try
        {
            return JsonSerializer.Deserialize<WavesBackupManifest>(File.ReadAllText(Path.Combine(folder, ManifestName)), Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>上次没恢复完的备份，比如卸载中途断电。</summary>
    public static IReadOnlyList<string> FindUnrestored(string backupRoot)
    {
        var roots = new List<string> { backupRoot };
        var pointer = Path.Combine(backupRoot, OtherLocations);
        if (File.Exists(pointer))
            roots.AddRange(File.ReadAllLines(pointer).Where(l => l.Length > 0));
        return roots.Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Where(d => Load(d) is { Restored: false })
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>备份放到别处时在默认位置留个记录，否则中断后下次找不到它。</summary>
    public static void RememberLocation(string defaultRoot, string backupRoot)
    {
        if (string.Equals(defaultRoot, backupRoot, StringComparison.OrdinalIgnoreCase))
            return;
        Directory.CreateDirectory(defaultRoot);
        File.AppendAllLines(Path.Combine(defaultRoot, OtherLocations), [backupRoot]);
    }

    private static void Save(string folder, WavesBackupManifest manifest)
    {
        var temp = Path.Combine(folder, ManifestName + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, Json));
        File.Move(temp, Path.Combine(folder, ManifestName), overwrite: true);
    }

    private static WavesBackupFile CopyVerified(string from, string to, string relative)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
        var hash = HashOf(from);
        if (HashOf(to) != hash)
            throw new IOException("备份校验不一致：" + from);
        return new WavesBackupFile(relative, new FileInfo(from).Length, hash);
    }

    private static string HashOf(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public enum WavesStep { Backup, UninstallMain, UninstallCentral, Restore, CleanFiles, CleanRegistry, Verify }

public enum WavesStepState { Pending, Running, Done, Skipped, Failed }

public enum UninstallerWait { Continue, Stop }

public enum StillInstalledChoice { RetryAsAdmin, Skip, Stop }

public interface IWavesRunUi
{
    void Step(WavesStep step, WavesStepState state, string detail);

    /// <summary>卸载程序已启动（launchError 为 null）或启动失败，等用户在官方窗口里操作完。</summary>
    Task<UninstallerWait> WaitForUninstaller(WavesProduct product, string? launchError);

    /// <param name="canSkip">Waves Central 可以跳过；主产品不行，MSI 还在时不能清理文件。</param>
    Task<StillInstalledChoice> ProductStillInstalled(WavesProduct product, bool canSkip);
}

public sealed record WavesRunResult(bool Finished, string Report, string BackupFolder);

/// <summary>
/// 卸载主流程。顺序不能变：备份 → MSI → Waves Central → 恢复 → 清文件 → 清注册表 → 复查。
/// MSI 还登记着时绝不清理文件，手动删会留下半损坏的安装记录。
/// </summary>
public sealed class WavesRun(WavesEnvironment env, WavesSurvey survey, IReadOnlySet<WavesCategory> clean, IReadOnlyList<WavesTarget> extras, string backupRoot, IWavesRunUi ui)
{
    private readonly StringBuilder _report = new();
    private readonly List<string> _codes = survey.KnownCodes.ToList();

    public static HashSet<WavesCategory> Normalize(IEnumerable<WavesCategory> chosen)
    {
        var set = chosen.ToHashSet();
        foreach (var locked in Enum.GetValues<WavesCategory>().Where(WavesCategories.IsLocked))
            set.Add(locked);
        return set;
    }

    public async Task<WavesRunResult> RunAsync()
    {
        var kept = survey.Targets.Where(t => !clean.Contains(t.Category) && WavesCategories.NeedsBackupWhenKept(t.Category)).ToList();
        string folder;
        ui.Step(WavesStep.Backup, WavesStepState.Running, $"{kept.Count} 项，{WavesSurveyor.FormatSize(kept.Sum(t => t.Size))}");
        try
        {
            var progress = new Progress<string>(text => ui.Step(WavesStep.Backup, WavesStepState.Running, text));
            folder = await Task.Run(() => WavesBackup.Create(kept, survey.MainRoots, backupRoot, progress));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ui.Step(WavesStep.Backup, WavesStepState.Failed, e.Message);
            return new WavesRunResult(false, "备份失败，已停止，Waves 没有被改动。\n" + e.Message, "");
        }
        ui.Step(WavesStep.Backup, WavesStepState.Done, "已备份并校验：" + folder);

        foreach (var product in survey.MainProducts)
        {
            ui.Step(WavesStep.UninstallMain, WavesStepState.Running, product.Name);
            if (!await UninstallAsync(product, canSkip: false))
            {
                ui.Step(WavesStep.UninstallMain, WavesStepState.Failed, product.Name + " 还没有卸载");
                var note = await RestoreAsync(folder);
                return new WavesRunResult(false,
                    $"{product.Name} 没有卸载完成，已停止，没有清理任何残留。\n{note}\n"
                    + "可以重启电脑后再运行一次这个工具。仍然失败时，可以试微软的 Program Install and Uninstall 修复工具。工具不会手动删除 MSI 的安装记录。", folder);
            }
        }
        ui.Step(WavesStep.UninstallMain, survey.MainProducts.Any() ? WavesStepState.Done : WavesStepState.Skipped, survey.MainProducts.Any() ? "" : "没有找到 Waves 的卸载项");

        var cleanCentral = clean.Contains(WavesCategory.Central);
        var centralGone = true;
        if (survey.Central is { } central && cleanCentral)
        {
            ui.Step(WavesStep.UninstallCentral, WavesStepState.Running, central.Name);
            centralGone = await UninstallAsync(central, canSkip: true);
            ui.Step(WavesStep.UninstallCentral, centralGone ? WavesStepState.Done : WavesStepState.Skipped, centralGone ? "" : "还没有卸载，它的文件不会被清理");
        }
        else
        {
            ui.Step(WavesStep.UninstallCentral, WavesStepState.Skipped, cleanCentral ? "没有找到" : "选择了保留");
        }

        ui.Step(WavesStep.Restore, WavesStepState.Running, "");
        var errors = await Task.Run(() => WavesBackup.Restore(folder, env.Paths));
        if (errors.Count > 0)
        {
            ui.Step(WavesStep.Restore, WavesStepState.Failed, $"{errors.Count} 项没有恢复");
            return new WavesRunResult(false, $"保留项没有全部恢复，已停止，没有清理残留。备份还在：{folder}\n\n" + string.Join("\n", errors), folder);
        }
        ui.Step(WavesStep.Restore, kept.Count > 0 ? WavesStepState.Done : WavesStepState.Skipped, kept.Count > 0 ? "" : "没有保留项");

        // 重新读取，卸载程序已经移除了大部分内容。主产品必须确实不在了才继续。
        var current = WavesSurveyor.Scan(env, survey.MainRoots, _codes);
        if (current.MainProducts.Any())
        {
            ui.Step(WavesStep.CleanFiles, WavesStepState.Failed, "卸载项还在");
            return new WavesRunResult(false, "卸载后复查发现 Waves 的卸载项还在，已停止，没有清理残留。请重启后再运行一次。", folder);
        }

        var cleanTargets = current.Targets.Where(t => clean.Contains(t.Category) && (t.Category != WavesCategory.Central || centralGone)).ToList();
        var extraPaths = extras.Select(e => e.Path).Where(RemovalPlanner.PathExists);
        var paths = cleanTargets.Select(t => t.Path).Concat(extraPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ui.Step(WavesStep.CleanFiles, WavesStepState.Running, $"{paths.Count} 项");
        var recycled = paths.Count > 0 ? await env.Recycle(paths, survey.MainRoots) : [];
        var failed = recycled.Where(r => !r.Ok).ToList();
        RemoveEmptyFolders(kept);
        ui.Step(WavesStep.CleanFiles, failed.Count > 0 ? WavesStepState.Failed : paths.Count > 0 ? WavesStepState.Done : WavesStepState.Skipped, failed.Count > 0 ? $"{failed.Count} 项没有移动" : "");

        var keys = current.RegistryTargets.Where(t => clean.Contains(t.Category) && (t.Category != WavesCategory.Central || centralGone)).ToList();
        ui.Step(WavesStep.CleanRegistry, WavesStepState.Running, $"{keys.Count} 项");
        var keyFailures = await CleanRegistryAsync(keys, folder);
        ui.Step(WavesStep.CleanRegistry, keyFailures.Count > 0 ? WavesStepState.Failed : keys.Count > 0 ? WavesStepState.Done : WavesStepState.Skipped, keyFailures.Count > 0 ? $"{keyFailures.Count} 项没有处理" : "");

        ui.Step(WavesStep.Verify, WavesStepState.Running, "");
        var final = WavesSurveyor.Scan(env, survey.MainRoots, _codes);
        AppendReport(final, folder, kept, failed, keyFailures, recycled);
        ui.Step(WavesStep.Verify, WavesStepState.Done, "");
        return new WavesRunResult(true, _report.ToString().TrimEnd(), folder);
    }

    private async Task<string> RestoreAsync(string folder)
    {
        var errors = await Task.Run(() => WavesBackup.Restore(folder, env.Paths));
        return errors.Count == 0 ? "保留项已检查并恢复。" : $"有 {errors.Count} 项保留内容没有恢复，备份还在：{folder}\n" + string.Join("\n", errors);
    }

    private async Task<bool> UninstallAsync(WavesProduct product, bool canSkip)
    {
        var command = product.Command;
        var elevated = false;
        while (true)
        {
            var error = env.Launch(command, elevated);
            if (await ui.WaitForUninstaller(product, error) == UninstallerWait.Stop)
                return false;
            if (!IsRegistered(product))
                return true;
            switch (await ui.ProductStillInstalled(product, canSkip))
            {
                case StillInstalledChoice.RetryAsAdmin:
                    command = product.Fallback ?? product.Command;
                    elevated = true;
                    break;
                default:
                    return false;
            }
        }
    }

    private bool IsRegistered(WavesProduct product)
    {
        var programs = env.Programs();
        return product.UninstallKeys.Any(key => programs.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            || (product.MsiCode != null && env.Registry.KeyExists(WavesIdentity.MsiProductKey(product.MsiCode)));
    }

    /// <summary>只删已经空了的文件夹，里面有东西时系统会拒绝。</summary>
    private void RemoveEmptyFolders(IReadOnlyList<WavesTarget> kept)
    {
        var folders = survey.MainRoots.Select(r => Path.Combine(r, "Data")).Concat(survey.MainRoots)
            .Append(env.Paths.ProgramDataWaves).Append(Path.Combine(env.Paths.ProgramData, "Caphyon", "Advanced Installer"));
        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder, recursive: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 空文件夹留着不影响重装。
            }
        }
    }

    private async Task<List<string>> CleanRegistryAsync(IReadOnlyList<WavesRegistryTarget> keys, string folder)
    {
        var failures = new List<string>();
        var exported = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var ready = new List<WavesRegistryTarget>();
        var backup = Path.Combine(folder, "registry");
        Directory.CreateDirectory(backup);
        foreach (var target in keys)
        {
            if (!exported.TryGetValue(target.Key, out var error))
            {
                error = env.Registry.Export(target.Key, Path.Combine(backup, exported.Count.ToString("000") + ".reg"));
                exported[target.Key] = error;
            }
            // 导不出来就不删，保证每一项都能还原。
            if (error != null)
                failures.Add($"{target}：备份失败，没有删除（{error}）");
            else
                ready.Add(target);
        }
        if (ready.Count > 0)
            failures.AddRange((await env.DeleteRegistry(ready, survey.MainRoots)).Where(r => !r.Ok).Select(r => $"{r.Path}：{r.Error}"));
        return failures;
    }

    private void AppendReport(WavesSurvey final, string folder, IReadOnlyList<WavesTarget> kept, IReadOnlyList<FileOpResult> failed, IReadOnlyList<string> keyFailures, IReadOnlyList<FileOpResult> recycled)
    {
        var left = final.Targets.Where(t => clean.Contains(t.Category)).ToList();
        var leftKeys = final.RegistryTargets.Where(t => clean.Contains(t.Category)).ToList();
        _report.AppendLine(left.Count == 0 && leftKeys.Count == 0 && !final.MainProducts.Any()
            ? "选择清理的内容已经查不到了。这只代表已知位置干净。"
            : "还有内容没有清理，请看下面的列表。");
        if (final.MainProducts.Any())
            _report.AppendLine("卸载项还在：" + string.Join("、", final.MainProducts.Select(p => p.Name)));
        foreach (var target in left)
            _report.AppendLine($"  剩余：{target.Path}" + (failed.FirstOrDefault(f => f.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) is { } why ? $"（{why.Error}）" : ""));
        foreach (var other in failed.Where(f => !left.Any(t => t.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))))
            _report.AppendLine($"  没有移动：{other.Path}（{other.Error}）");
        foreach (var key in leftKeys)
            _report.AppendLine($"  注册表剩余：{key}");
        foreach (var failure in keyFailures)
            _report.AppendLine("  " + failure);

        _report.AppendLine().AppendLine($"移到回收站：{recycled.Count(r => r.Ok)} 项。确认没问题前不要清空回收站。");
        if (kept.Count > 0)
            _report.AppendLine("已恢复的保留项：" + string.Join("、", kept.Select(k => WavesCategories.Label(k.Category)).Distinct()));
        _report.AppendLine("备份和注册表导出保存在：" + folder);
        _report.AppendLine().AppendLine("下一步：重启电脑，再打开这个工具重新检查。确认干净后，用 Waves Central 重装。工具不会生成或修复授权，重装后的激活要通过 Waves Central 或官方支持处理。");
    }
}
