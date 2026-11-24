using System.ComponentModel;
using System.Diagnostics;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>交给系统去做的事：用浏览器打开网页、在访达或资源管理器里定位文件。出错时返回一句说明，成功返回 null。</summary>
public static class SystemShell
{
    public static string? OpenWebPage(string url)
    {
        // 只打开网页链接，这个地址最终会交给系统执行。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "这不是有效的网页链接";
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return "打不开链接：" + error.Message;
        }
    }

    /// <summary>只定位，不运行也不改动文件。</summary>
    public static string? Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", ["-R", path]) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", [Path.GetDirectoryName(path) ?? path]) { UseShellExecute = false });
            return null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return "无法打开文件夹：" + error.Message;
        }
    }

    /// <summary>打开文件夹窗口，不改动任何文件。</summary>
    public static string? OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            return $"找不到文件夹：{path}";
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [path]) { UseShellExecute = false });
            return null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return "无法打开文件夹：" + error.Message;
        }
    }

    /// <summary>
    /// 定位工程文件，不启动宿主，避免工程被意外打开。文件不在了就打开它原来所在的文件夹。
    /// reveal 和 openFolder 由调用方传入（测试换成记录下来的函数）。
    /// </summary>
    public static string? RevealProject(string path, Func<string, string?> reveal, Func<string, string?> openFolder)
    {
        if (File.Exists(path))
            return reveal(path);
        if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            return openFolder(folder) ?? $"工程文件已经不在了，打开了它原来所在的文件夹：{folder}";
        return $"找不到工程文件：{path}";
    }

    /// <summary>打开 Waves Central，让用户在里面卸载 Waves 产品。没装时返回说明。</summary>
    public static string? OpenWavesCentral()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                const string app = "/Applications/Waves Central.app";
                if (!Directory.Exists(app))
                    return "没有找到 Waves Central，请先从 Waves 官网安装。";
                Process.Start(new ProcessStartInfo("open", [app]) { UseShellExecute = false });
                return null;
            }
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Waves Central", "Waves Central.exe");
            if (!File.Exists(exe))
                return "没有找到 Waves Central，请先从 Waves 官网安装。";
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return "打开失败：" + error.Message;
        }
    }
}

/// <summary>运行“已安装的应用”里登记的卸载命令（Windows），不加静默参数，用户在官方窗口里操作。</summary>
public static class UninstallerLauncher
{
    /// <returns>失败原因，成功时返回 null。</returns>
    public static string? Start(InstalledProgram program)
    {
        var command = UninstallCommand.Parse(program.UninstallCommand);
        if (command == null)
            return "卸载命令无法识别：" + program.UninstallCommand;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command.Value.FileName, command.Value.Arguments) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception error)
        {
            return error.NativeErrorCode == 1223 ? "你取消了管理员授权。" : $"启动失败：{error.Message}";
        }
    }
}
