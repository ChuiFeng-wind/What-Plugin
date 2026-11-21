using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;
using WhatPlugin.Desktop;

[assembly: AvaloniaTestApplication(typeof(WhatPlugin.Desktop.Tests.TestApp))]

namespace WhatPlugin.Desktop.Tests;

public static class TestApp
{
    // 用 Skia 真正画出来（而不是只算布局），截图才有内容。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>主窗口框架：导航、页面切换、主题。每个测试用临时数据库，不碰默认位置的那份。</summary>
public sealed class ShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-ui-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;

    public ShellTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        _database = new PluginDatabase(_dbPath);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private MainWindow Open()
    {
        var window = new MainWindow(_database, _dbPath);
        window.Show();
        return window;
    }

    /// <summary>切换页面后要先排一次版，新页面的控件才会出现在可视树里。</summary>
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static RadioButton NavItem(MainWindow window, string key) =>
        window.GetVisualDescendants().OfType<RadioButton>().Single(r => Equals(r.Tag, key) && r.Classes.Contains("Nav"));

    private static IBrush Resource(string key) => (IBrush)Application.Current!.Resources[key]!;

    [AvaloniaFact]
    public void StartsOnThePluginLibraryWithEveryPageInTheNav()
    {
        var window = Open();

        Assert.Equal("Plugins", window.CurrentPage);
        Assert.Equal(MainWindow.Pages.Select(p => p.Key), window.NavKeys);
        Assert.True(NavItem(window, "Plugins").IsChecked);
        Assert.True(NavItem(window, "Settings").IsVisible);
    }

    [AvaloniaFact]
    public void ClickingANavItemSwitchesThePage()
    {
        var window = Open();

        NavItem(window, "Settings").IsChecked = true;
        Settle(window);

        Assert.Equal("Settings", window.CurrentPage);
        Assert.False(NavItem(window, "Plugins").IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "主题" && t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void NavOrderIsSharedWithTheWindowsVersion()
    {
        // Windows 版保存的顺序：心愿单挪到最前。跨平台版读到同样的顺序。
        _database.SetSetting(NavOrder.Setting, "Wishes,Plugins,Blacklist,Chains,Accounts,Health,Idle,Projects");
        var window = Open();
        Assert.Equal("Wishes", window.NavKeys[0]);

        window.MoveNav("Plugins", -1);

        Assert.Equal(["Plugins", "Wishes"], window.NavKeys.Take(2));
        Assert.StartsWith("Plugins,Wishes,", _database.GetSetting(NavOrder.Setting));
    }

    [AvaloniaFact]
    public void ChoosingAThemeRecolorsAndIsRemembered()
    {
        var window = Open();
        window.Navigate("Settings");
        Settle(window);
        var paper = window.GetVisualDescendants().OfType<RadioButton>().Single(r => Equals(r.Tag, "paper"));

        paper.IsChecked = true;

        Assert.Equal("paper", ThemeManager.Current.Id);
        Assert.Equal(Color.Parse(ThemePalettes.Find("paper")["Bg1"]), ((ISolidColorBrush)Resource("Bg1")).Color);
        Assert.Equal("paper", _database.GetSetting(ThemeManager.Setting));
        Assert.Contains("纸白琥珀", window.Status);

        // 再打开时用保存的主题。
        ThemeManager.Apply(null);
        Open();
        Assert.Equal("paper", ThemeManager.Current.Id);
        ThemeManager.Apply(null);
    }

    [AvaloniaFact]
    public void NavShowsCountsFromTheDatabase()
    {
        _database.AddWish(new WishItem(0, "Pro-Q 4", "FabFilter"));
        var window = Open();

        var wishes = NavItem(window, "Wishes").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("1", wishes);
        Assert.Contains("0", NavItem(window, "Plugins").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
    }

    /// <summary>
    /// 每一套配色、每一页各画一张图。平时只检查画得出来；
    /// 设置 WHATPLUGIN_SCREENSHOTS=文件夹 时把图片保存下来，给人看效果。
    /// </summary>
    [AvaloniaFact]
    public void RendersEveryPageInEveryTheme()
    {
        var output = Environment.GetEnvironmentVariable("WHATPLUGIN_SCREENSHOTS");
        if (output != null)
            Directory.CreateDirectory(output);

        var window = Open();
        foreach (var palette in ThemePalettes.All)
        {
            ThemeManager.Apply(palette.Id);
            foreach (var page in MainWindow.Pages.Append(MainWindow.SettingsPage))
            {
                // 每套配色都画设置页和插件库；其他页（目前是占位）只用默认配色画一次。
                if (palette.Id != ThemePalettes.DefaultId && page.Key is not ("Settings" or "Plugins"))
                    continue;
                window.Navigate(page.Key);
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(output ?? _dir, $"{palette.Id}-{page.Key}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        ThemeManager.Apply(null);
    }
}
