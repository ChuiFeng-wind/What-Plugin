using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 主程序给扩展的接口实现，和 Windows 版一样：数据库每有写入就重新计时，安静 1.5 秒后在界面线程通知一次，
/// 扫描、导入这类连续写入只通知一次。
/// </summary>
internal sealed class ExtensionHost : IExtensionHost
{
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Action<string> _status;
    private readonly Action<IAppExtension> _showPage;

    public ExtensionHost(PluginDatabase database, Action<string> status, Action<IAppExtension> showPage)
    {
        Database = database;
        _status = status;
        _showPage = showPage;
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            DataSettled?.Invoke(this, EventArgs.Empty);
        };
        // 写入可能在扫描线程上，计时器只能在界面线程上动。
        database.DataChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _settle.Stop();
            _settle.Start();
        });
    }

    public PluginDatabase Database { get; }

    public event EventHandler? DataSettled;

    public void ShowStatus(string text) => _status(text);

    public void ShowPage(IAppExtension extension) => _showPage(extension);
}

public partial class MainWindow
{
    private readonly Dictionary<string, IAppExtension> _extensions = [];
    private readonly List<string> _extensionProblems = [];

    /// <summary>
    /// 扩展所在的文件夹。只从安装目录下的 Extensions/名称/WhatPlugin.名称.dll 加载：
    /// Windows 上安装目录在 Program Files 里，Mac 上在应用包里，普通程序改不了，不会加载到别人放进来的文件。
    /// 测试指向临时目录。
    /// </summary>
    public static string ExtensionFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, ExtensionApi.FolderName);

    /// <summary>已加载的扩展，键是导航标识（ext:标题）。</summary>
    public IReadOnlyDictionary<string, IAppExtension> Extensions => _extensions;

    /// <summary>设置页“使用统计”下面的一行：装了哪些扩展、哪些没加载成功。</summary>
    public string ExtensionSummary => string.Join("；",
        new[] { _extensions.Count > 0 ? "已安装：" + string.Join("、", _extensions.Values.Select(e => e.Title)) : "没有安装成就包等扩展。" }
            .Concat(_extensionProblems));

    private void LoadExtensions()
    {
        if (!Directory.Exists(ExtensionFolder))
            return;
        ExtensionHost? host = null;
        foreach (var dir in Directory.GetDirectories(ExtensionFolder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var dll = Path.Combine(dir, $"WhatPlugin.{name}.dll");
            if (!File.Exists(dll))
                continue;
            try
            {
                var assembly = Assembly.LoadFrom(dll);
                var type = assembly.GetExportedTypes().FirstOrDefault(t => typeof(IAppExtension).IsAssignableFrom(t) && !t.IsAbstract);
                if (type == null || Activator.CreateInstance(type) is not IAppExtension extension)
                {
                    _extensionProblems.Add($"{name}：文件里没有扩展");
                    continue;
                }
                if (extension.ApiVersion != ExtensionApi.Version)
                {
                    _extensionProblems.Add($"{extension.Title}：和这个版本的主程序不匹配，请安装对应版本的{extension.Title}");
                    continue;
                }
                host ??= new ExtensionHost(_database, text => StatusText.Text = text, ShowExtensionFromHost);
                var key = Onboarding.ExtensionPrefix + extension.Title;
                if (_navItems.ContainsKey(key))
                    continue;
                _extensions[key] = extension;
                _navItems[key] = CreateExtensionNav(key, extension);
                extension.Initialize(host);
            }
            catch (Exception error) when (error is IOException or BadImageFormatException or TypeLoadException or MissingMethodException
                                              or MissingFieldException or ReflectionTypeLoadException or FileLoadException or TargetInvocationException
                                              or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                _extensionProblems.Add($"{name}：加载失败（{error.Message}）。可能和这个版本的主程序不匹配");
            }
        }
        if (_extensionProblems.Count > 0)
            StatusText.Text = string.Join("；", _extensionProblems);
    }

    private RadioButton CreateExtensionNav(string key, IAppExtension extension)
    {
        var badge = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Text = extension.Badge };
        badge.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Muted"));
        _navCounts[key] = badge;
        extension.BadgeChanged += (_, _) => Dispatcher.UIThread.Post(() => badge.Text = extension.Badge);

        var content = new DockPanel();
        DockPanel.SetDock(badge, Dock.Right);
        content.Children.Add(badge);
        var icon = new MaterialIcon
        {
            Kind = Enum.TryParse<MaterialIconKind>(extension.Glyph, out var kind) ? kind : MaterialIconKind.PuzzleOutline,
            Width = 17, Height = 17, Margin = new(0, 0, 12, 0),
        };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = extension.Title, VerticalAlignment = VerticalAlignment.Center });

        var item = new RadioButton { Content = content, Tag = key, Classes = { "Nav" } };
        AutomationProperties.SetName(item, extension.Title);
        item.IsCheckedChanged += (_, _) =>
        {
            if (item.IsChecked == true)
                Navigate(key);
        };
        item.ContextMenu = NavMenu(key);
        return item;
    }

    /// <summary>扩展要求显示自己的页面，例如点了成就弹窗：还原窗口、调到前面，切到这一页。</summary>
    private void ShowExtensionFromHost(IAppExtension extension)
    {
        if (_extensions.FirstOrDefault(e => ReferenceEquals(e.Value, extension)).Key is not { } key)
            return;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        if (CurrentPage == key)
            extension.PageShown();
        else
            Navigate(key);
    }

    /// <summary>扩展的页面自己带标题，这里只给它和其他页面一样的边距。出错时显示原因。</summary>
    private Control CreateExtensionPage(IAppExtension extension)
    {
        Control content;
        try
        {
            content = extension.CreatePage() as Control
                      ?? new TextBlock { Text = $"{extension.Title}的页面不能在这个版本里显示，请安装对应版本的{extension.Title}", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        }
        catch (Exception error) when (error is InvalidOperationException or MissingMethodException or TypeLoadException or Microsoft.Data.Sqlite.SqliteException)
        {
            content = new TextBlock { Text = $"{extension.Title}出错了：{error.Message}", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        }
        return new Border { Margin = new(28, 0, 24, 0), Child = content };
    }

    private void ShowExtensionPage(string key)
    {
        if (!_extensions.TryGetValue(key, out var extension))
            return;
        StatusText.Text = "";
        try
        {
            extension.PageShown();
        }
        catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusText.Text = $"{extension.Title}出错了：{error.Message}";
        }
    }
}
