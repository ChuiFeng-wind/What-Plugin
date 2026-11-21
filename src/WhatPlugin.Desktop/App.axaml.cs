using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --db 路径：用指定的数据库（测试和排错用），不碰默认位置的那份。
            var args = desktop.Args ?? [];
            var at = Array.IndexOf(args, "--db");
            var databasePath = at >= 0 && at + 1 < args.Length ? args[at + 1] : PluginDatabase.ResolveDefaultPath();

            // 本机宿主自带哪些插件：工程报告和混音笔记都要用。
            HostStock.Current = HostStock.Load(DawLocations.Default);

            var database = new PluginDatabase(databasePath);
            desktop.MainWindow = new MainWindow(database, databasePath);
            desktop.Exit += (_, _) => database.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
