using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WhatPlugin.Core;

public enum LookupPluginState { NotBundled, NotInstalled, Outdated, Installed }

/// <summary>
/// Mac 版把 DAW 内查询插件（WhatPluginLookup.vst3）放在 WhatPlugin.app/Contents/Resources 里，
/// 由用户在设置里点一下装到 ~/Library/Audio/Plug-Ins/VST3（当前用户的文件夹，不用管理员权限）。
/// Windows 版由安装程序装，不用这里。
/// </summary>
public sealed class LookupPluginInstaller(string source, string target)
{
    public const string BundleName = LookupExport.OwnPluginName + ".vst3";

    /// <summary>应用里带的插件，没有时（开发时直接运行、Windows）不显示安装功能。</summary>
    public string Source { get; } = source;

    public string Target { get; } = target;

    /// <summary>本机的位置：插件在 .app 的 Resources 里，装到当前用户的 VST3 文件夹。</summary>
    public static LookupPluginInstaller ThisComputer()
    {
        var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(Path.Combine(resources, BundleName), Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3", BundleName));
    }

    public LookupPluginState State
    {
        get
        {
            if (!Directory.Exists(Source))
                return LookupPluginState.NotBundled;
            if (!Directory.Exists(Target))
                return LookupPluginState.NotInstalled;
            return Fingerprint(Source) == Fingerprint(Target) ? LookupPluginState.Installed : LookupPluginState.Outdated;
        }
    }

    public string StateText => State switch
    {
        LookupPluginState.NotInstalled => "还没有安装。装好后在 DAW 里重新扫描插件，就能在效果器列表里找到 WhatPluginLookup。",
        LookupPluginState.Outdated => "已安装的是旧版本，可以更新。更新前请先关掉正在用它的 DAW。",
        LookupPluginState.Installed => "已安装在：" + Target,
        _ => "",
    };

    /// <summary>
    /// 复制到 VST3 文件夹：先复制到旁边的临时文件夹，再换掉旧的，中途失败不会留下半个插件。
    /// 只动 Target 这一个插件包。
    /// </summary>
    /// <returns>失败原因；成功返回 null。</returns>
    public string? Install()
    {
        if (!Directory.Exists(Source))
            return "这个版本没有带查询插件";
        if (!string.Equals(Path.GetFileName(Target), BundleName, StringComparison.Ordinal))
            return "安装位置不对";
        var folder = Path.GetDirectoryName(Target)!;
        var temp = Path.Combine(folder, "." + BundleName + "-" + Guid.NewGuid().ToString("N"));
        var old = temp + "-old";
        try
        {
            Directory.CreateDirectory(folder);
            Copy(Source, temp);
            if (Directory.Exists(Target))
                Directory.Move(Target, old);
            Directory.Move(temp, Target);
            if (Directory.Exists(old))
                Directory.Delete(old, recursive: true);
            AllowToLoad(Target);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 换到一半失败：把旧的放回去。
            if (!Directory.Exists(Target) && Directory.Exists(old))
                Directory.Move(old, Target);
            return "安装失败：" + e.Message;
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from))
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            // 插件包里的链接（例如框架的 Current）照原样复制成链接，不复制指向的内容。
            if (new FileInfo(file).LinkTarget is { } link)
                File.CreateSymbolicLink(target, link);
            else
                File.Copy(file, target);
        }
    }

    /// <summary>
    /// 从下载的 .dmg 里拷出来的文件带“隔离”标记，DAW 加载时会被系统拦下。
    /// 这是本软件自带的插件，装的时候去掉这个标记（只针对刚装的这一个插件包）。
    /// </summary>
    private static void AllowToLoad(string bundle)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/xattr", ["-dr", "com.apple.quarantine", bundle])
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            });
            process?.WaitForExit(10_000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 去不掉也不影响安装，DAW 里被拦时按打开说明处理。
        }
    }

    /// <summary>插件包的内容摘要：各文件的相对路径和内容。用来判断已装的是不是同一版本。</summary>
    private static string Fingerprint(string bundle)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(bundle, file).Replace('\\', '/') + "\n"));
                hash.AppendData(File.ReadAllBytes(file));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
