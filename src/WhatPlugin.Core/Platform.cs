using System.Security.Cryptography;
using System.Text;

namespace WhatPlugin.Core;

/// <summary>
/// 每个平台不一样的东西集中在这里：数据放在哪、程序文件叫什么、插件和宿主的目录在哪。
/// 其余代码只问这里，不自己判断平台。
/// </summary>
public static class AppPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 放本软件数据的上级目录。Windows 是 %LOCALAPPDATA%，位置和以前一样；
    /// Mac 明确用 ~/Library/Application Support，不依赖 .NET 在各版本里的默认值。
    /// </summary>
    public static string LocalDataRoot => IsMac
        ? Path.Combine(Home, "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>本机的可执行文件名：Windows 加 .exe，Mac 上没有扩展名。</summary>
    public static string Executable(string name) => IsWindows ? name + ".exe" : name;

    /// <summary>各格式插件的默认目录（不含用户在设置里加的）。</summary>
    public static IReadOnlyList<(string Path, PluginFormat Format)> DefaultPluginRoots()
    {
        if (IsMac)
        {
            // 系统级和当前用户级各一份。
            var roots = new List<(string, PluginFormat)>();
            foreach (var library in new[] { "/Library", Path.Combine(Home, "Library") })
            {
                var plugIns = Path.Combine(library, "Audio", "Plug-Ins");
                roots.Add((Path.Combine(plugIns, "VST3"), PluginFormat.Vst3));
                roots.Add((Path.Combine(plugIns, "VST"), PluginFormat.Vst2));
                roots.Add((Path.Combine(plugIns, "CLAP"), PluginFormat.Clap));
                roots.Add((Path.Combine(plugIns, "Components"), PluginFormat.Au));
            }
            roots.Add(("/Library/Application Support/Avid/Audio/Plug-Ins", PluginFormat.Aax));
            return roots;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var commonX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        var windowsRoots = new List<(string, PluginFormat)>
        {
            (Path.Combine(common, "VST3"), PluginFormat.Vst3),
            (Path.Combine(commonX86, "VST3"), PluginFormat.Vst3),
            (Path.Combine(common, "CLAP"), PluginFormat.Clap),
            (Path.Combine(common, "Avid", "Audio", "Plug-Ins"), PluginFormat.Aax),
            (Path.Combine(common, "VST2"), PluginFormat.Vst2),
            (Path.Combine(programFiles, "VSTPlugins"), PluginFormat.Vst2),
            (Path.Combine(programFiles, "Steinberg", "VSTPlugins"), PluginFormat.Vst2),
            (Path.Combine(programFilesX86, "VSTPlugins"), PluginFormat.Vst2),
            (Path.Combine(programFilesX86, "Steinberg", "VstPlugins"), PluginFormat.Vst2),
        };
        if (OperatingSystem.IsWindows())
            windowsRoots.AddRange(RegisteredVst2Folders().Select(f => (f, PluginFormat.Vst2)));
        return windowsRoots;
    }

    /// <summary>安装程序在注册表里登记的 VST2 目录。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegisteredVst2Folders()
    {
        foreach (var keyName in new[] { @"SOFTWARE\VST", @"SOFTWARE\WOW6432Node\VST" })
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyName);
            if (key?.GetValue("VSTPluginsPath") is string registered && registered.Length > 0)
                yield return registered;
        }
    }

    /// <summary>宿主放配置的目录。Mac 上 Cubase / Nuendo 的配置在“偏好设置”里，其余在“Application Support”里。</summary>
    public static string SteinbergFolder => IsMac
        ? Path.Combine(Home, "Library", "Preferences")
        : Path.Combine(RoamingAppData, "Steinberg");

    public static string ReaperFolder => Path.Combine(IsMac ? MacApplicationSupport : RoamingAppData, "REAPER");

    public static string PreSonusFolder => Path.Combine(IsMac ? MacApplicationSupport : RoamingAppData, "PreSonus");

    public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string MacApplicationSupport => Path.Combine(Home, "Library", "Application Support");
}

/// <summary>账号密码的加密方式。每个平台用系统自带的、绑定当前用户的保护。</summary>
public interface ISecretBackend
{
    byte[] Protect(byte[] plain);

    /// <summary>解不开时抛 CryptographicException。</summary>
    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>Windows 数据保护（DPAPI），绑定当前 Windows 账号。参数和以前完全一样，已保存的密码照常能解开。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsSecretBackend : ISecretBackend
{
    // 这个值和已保存的密码绑定，软件改名后也不能改，否则以前的账号密码都解不开。
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PluginVault.secret.v1");

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>还没有实现的平台（Windows 和 Mac 以外）。不提供不安全的替代：保存密码时直接报错，读取时当作解不开。</summary>
public sealed class UnsupportedSecretBackend : ISecretBackend
{
    public byte[] Protect(byte[] plain) =>
        throw new PlatformNotSupportedException("这个系统上还不能保存账号密码。");

    public byte[] Unprotect(byte[] protectedBytes) =>
        throw new CryptographicException("这个系统上还不能读取账号密码。");
}
