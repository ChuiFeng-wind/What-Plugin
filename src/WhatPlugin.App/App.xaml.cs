using System.Windows;

namespace WhatPlugin.App;

public partial class App : Application
{
    /// <summary>
    /// 测试用：加 --offscreen 启动时，所有窗口都开在屏幕外、不激活、不进任务栏，
    /// 自动化脚本可以操作和截图，又不会打断正在用电脑的人。平时用不到。
    /// </summary>
    public static bool Offscreen { get; } = Environment.GetCommandLineArgs().Contains("--offscreen");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 按需提权时启动自己的这个模式：只处理一次文件请求，不打开界面。
        if (e.Args is [WhatPlugin.Core.FileOpRunner.HelperArgument, var pipe])
            Environment.Exit(WhatPlugin.Core.FileOpRunner.Serve(pipe));
        // 本机宿主自带哪些插件：读宿主自己的清单（几百 KB 的 XML），工程报告和混音笔记都要用。
        WhatPlugin.Core.HostStock.Current = WhatPlugin.Core.HostStock.Load(WhatPlugin.Core.DawLocations.Default);
        base.OnStartup(e);
    }
}
