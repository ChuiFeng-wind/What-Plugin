using Avalonia;

namespace WhatPlugin.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Windows 上移动 Program Files 里的插件要管理员权限：本程序以提权方式再启动一次，只处理这一次文件操作。
        if (OperatingSystem.IsWindows() && args is [Core.FileOpRunner.HelperArgument, var pipe])
            return Core.FileOpRunner.Serve(pipe);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>界面设计器和测试也从这里取应用配置。</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
