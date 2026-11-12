namespace WhatPlugin.Core;

/// <summary>
/// 可选扩展（例如成就包）和主程序之间的约定。扩展是装在主程序目录 Extensions\名称\ 下的
/// WhatPlugin.名称.dll，主程序只从这里加载。接口有不兼容的改动时把 <see cref="Version"/> 加一，
/// 版本对不上的扩展不加载。
/// </summary>
public static class ExtensionApi
{
    public const int Version = 2;

    public const string FolderName = "Extensions";
}

/// <summary>主程序提供给扩展的东西。</summary>
public interface IExtensionHost
{
    PluginDatabase Database { get; }

    /// <summary>在主窗口底部状态栏显示一句话。</summary>
    void ShowStatus(string text);

    /// <summary>数据有变化，而且已经安静了一会儿。在界面线程上触发。</summary>
    event EventHandler? DataSettled;

    /// <summary>把主窗口调到前面（最小化的会还原），并切到这个扩展的页面。</summary>
    void ShowPage(IAppExtension extension);
}

/// <summary>一个扩展。必须有公开的无参构造函数。</summary>
public interface IAppExtension
{
    /// <summary>编译时的 <see cref="ExtensionApi.Version"/>。</summary>
    int ApiVersion { get; }

    /// <summary>左侧导航里显示的名字。</summary>
    string Title { get; }

    /// <summary>Segoe Fluent Icons 字符。</summary>
    string Glyph { get; }

    /// <summary>导航右边的小字，例如“12/51”。变化时触发 <see cref="BadgeChanged"/>。</summary>
    string Badge { get; }

    event EventHandler? BadgeChanged;

    /// <summary>在界面线程上调用一次。</summary>
    void Initialize(IExtensionHost host);

    /// <summary>扩展的页面（WPF 的 FrameworkElement）。在界面线程上调用。</summary>
    object CreatePage();

    /// <summary>页面要显示了，可以刷新内容。</summary>
    void PageShown();
}
