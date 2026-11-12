using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>
/// 主程序给扩展的接口实现。数据库每有写入就重新计时，安静 1.5 秒后在界面线程通知一次，
/// 扫描、导入这类连续写入只通知一次。
/// </summary>
internal sealed class ExtensionHost : IExtensionHost
{
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Action<string> _status;
    private readonly Action<IAppExtension> _showPage;

    public ExtensionHost(PluginDatabase database, Dispatcher dispatcher, Action<string> status, Action<IAppExtension> showPage)
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
        database.DataChanged += () => dispatcher.BeginInvoke(() =>
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

/// <summary>加载好的一个扩展，以及它在左侧导航里的按钮。</summary>
internal sealed record LoadedExtension(IAppExtension Extension, RadioButton Nav, TextBlock Badge);

public partial class MainWindow
{
    private readonly List<LoadedExtension> _extensions = [];
    private readonly List<string> _extensionProblems = [];
    private LoadedExtension? _currentExtension;
    private bool _loadingUsage;

    /// <summary>使用记录不能影响正常操作：写不进去就算了。</summary>
    private void RecordUsage(Action record)
    {
        try
        {
            record();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
    }

    /// <summary>
    /// 只从安装目录下的 Extensions\名称\WhatPlugin.名称.dll 加载。安装目录在 Program Files 里，
    /// 普通程序改不了，所以不会加载到别人放进来的文件。接口版本对不上的不加载。
    /// </summary>
    private void LoadExtensions()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, ExtensionApi.FolderName);
        if (!Directory.Exists(folder))
            return;
        ExtensionHost? host = null;
        foreach (var dir in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
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
                host ??= new ExtensionHost(_database, Dispatcher, text => StatusText.Text = text, ShowExtensionFromHost);
                extension.Initialize(host);
                AddExtensionNav(extension);
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

    private void AddExtensionNav(IAppExtension extension)
    {
        var badge = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Text = extension.Badge };
        badge.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(badge, Dock.Right);
        var icon = new TextBlock { Text = extension.Glyph, Margin = new Thickness(0, 0, 12, 0) };
        icon.Style = (Style)FindResource("Icon");
        var panel = new DockPanel();
        panel.Children.Add(badge);
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = extension.Title, VerticalAlignment = VerticalAlignment.Center });

        var nav = new RadioButton { Content = panel, Style = (Style)FindResource("NavItem") };
        AutomationProperties.SetName(nav, extension.Title);
        // 导航排序用的标识。
        nav.Uid = "ext:" + extension.Title;
        var loaded = new LoadedExtension(extension, nav, badge);
        nav.Checked += (_, _) => ShowExtension(loaded);
        extension.BadgeChanged += (_, _) => badge.Text = extension.Badge;
        NavList.Children.Add(nav);
        _extensions.Add(loaded);
    }

    /// <summary>扩展要求显示自己的页面，例如点了成就弹窗。</summary>
    private void ShowExtensionFromHost(IAppExtension extension)
    {
        if (_extensions.FirstOrDefault(l => ReferenceEquals(l.Extension, extension)) is not { } loaded)
            return;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        if (loaded.Nav.IsChecked == true)
            ShowExtension(loaded);
        else
            loaded.Nav.IsChecked = true;
    }

    private void ShowExtension(LoadedExtension extension)
    {
        if (_page == AppPage.Extension && _currentExtension == extension)
            return;
        SaveCurrent();
        FlushItems();
        FlushChain();
        _currentExtension = extension;
        _page = AppPage.Extension;
        ShowPage();
    }

    private void ShowExtensionPage()
    {
        if (_currentExtension == null)
            return;
        StatusText.Text = "";
        try
        {
            if (!ReferenceEquals(ExtensionPage.Tag, _currentExtension))
            {
                ExtensionPage.Content = _currentExtension.Extension.CreatePage();
                ExtensionPage.Tag = _currentExtension;
            }
            _currentExtension.Extension.PageShown();
        }
        catch (Exception error) when (error is InvalidOperationException or MissingMethodException or TypeLoadException or Microsoft.Data.Sqlite.SqliteException)
        {
            ExtensionPage.Content = new TextBlock { Text = $"{_currentExtension.Extension.Title}出错了：{error.Message}", TextWrapping = TextWrapping.Wrap };
        }
    }

    // ---------- 设置：使用统计 ----------

    private void ShowUsageSettings()
    {
        _loadingUsage = true;
        UsageEnabledBox.IsChecked = _database.UsageEnabled;
        _loadingUsage = false;
        var count = _database.CountUsageEvents();
        UsageSummary.Text = (_database.UsageEnabled ? "正在记录。" : "已关闭，不再记录。") + (count > 0 ? $"已记下 {count} 条。" : "还没有记录。");
        ClearUsageButton.IsEnabled = count > 0;
        var installed = _extensions.Select(e => e.Extension.Title).ToList();
        ExtensionSummary.Text = string.Join("；",
            new[] { installed.Count > 0 ? "已安装：" + string.Join("、", installed) : "没有安装成就包等扩展。" }.Concat(_extensionProblems));
    }

    private void UsageEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUsage)
            return;
        var enabled = UsageEnabledBox.IsChecked == true;
        RecordUsage(() => _database.UsageEnabled = enabled);
        ShowUsageSettings();
        StatusText.Text = enabled ? "已开始记录使用统计" : "已停止记录使用统计，已经记下的还在，可以点“清空记录”删除";
    }

    private void ClearUsage_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(this, "清空使用统计？", "已经记下的打开日期、导出次数等会被删除，以后依赖它们的成就要重新累计。已经解锁的成就不受影响。", "清空", danger: true))
            return;
        RecordUsage(_database.ClearUsage);
        ShowUsageSettings();
        StatusText.Text = "已清空使用统计";
    }
}
