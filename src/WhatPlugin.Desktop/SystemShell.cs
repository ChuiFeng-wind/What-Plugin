using System.ComponentModel;
using System.Diagnostics;

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
}
