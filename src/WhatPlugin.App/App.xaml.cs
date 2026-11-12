using System.Windows;

namespace WhatPlugin.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 按需提权时启动自己的这个模式：只处理一次文件请求，不打开界面。
        if (e.Args is [WhatPlugin.Core.FileOpRunner.HelperArgument, var pipe])
            Environment.Exit(WhatPlugin.Core.FileOpRunner.Serve(pipe));
        base.OnStartup(e);
    }
}
