using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>新手指引：欢迎窗口和左侧导航的逐项导览。临时数据库，欢迎窗口换成脚本。</summary>
public sealed class OnboardingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-tour-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;
    private readonly TestEnvironment _env;
    private readonly List<bool> _welcomes = [];

    public OnboardingTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        _database = new PluginDatabase(_dbPath);
        _env = new TestEnvironment(Path.Combine(_dir, "env"));
        Assert.StartsWith(Path.GetTempPath(), _env.Vst3, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private MainWindow Open(WelcomeChoice choice, bool auto = true)
    {
        var window = new MainWindow(_database, _dbPath, _env.Scan) { Width = 1400, Height = 860, AutoOnboarding = auto };
        window.ShowWelcomeDialog = canScan =>
        {
            _welcomes.Add(canScan);
            return Task.FromResult(choice);
        };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private void SeedPlugin() =>
        _database.SaveMerged(
        [
            new MergedPlugin("proq4", "Pro-Q 4", "FabFilter", InfoSource.PluginLoad,
                new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.Seed),
                [new MergedFile(Path.Combine("Plugins", "proq4.vst3"), PluginFormat.Vst3, Arch.X64, "1.0", "Fx", ScanStatus.Loaded, null)]),
        ]);

    [AvaloniaFact]
    public void NewUserSeesWelcomeOnceThenTour()
    {
        var window = Open(WelcomeChoice.Tour);
        Assert.Equal([true], _welcomes);
        Assert.True(window.TourActive);
        Assert.Equal(Onboarding.CurrentVersion.ToString(), _database.GetSetting(Onboarding.Setting));

        // 按导航的显示顺序：8 个页面，再加“设置”。
        Assert.Equal([.. MainWindow.Pages.Select(p => p.Key), "Settings"], window.TourStops.Select(s => s.Target));
        Assert.Equal(("插件库", window.TourStops[0].Body, "1 / 9"), window.TourText);
        Assert.Equal("Plugins", window.CurrentPage);
        window.Close();

        // 下次打开不再弹。
        _welcomes.Clear();
        var again = Open(WelcomeChoice.Tour);
        Assert.Empty(_welcomes);
        Assert.False(again.TourActive);
    }

    [AvaloniaFact]
    public void ExistingUserIsMarkedSeenWithoutWelcome()
    {
        SeedPlugin();
        var window = Open(WelcomeChoice.Tour);
        Assert.Empty(_welcomes);
        Assert.False(window.TourActive);
        Assert.Equal(Onboarding.CurrentVersion.ToString(), _database.GetSetting(Onboarding.Setting));
    }

    [AvaloniaFact]
    public void TestDatabaseDoesNotPopUpWelcome()
    {
        var window = Open(WelcomeChoice.Tour, auto: false);
        Assert.Empty(_welcomes);
        Assert.False(window.TourActive);
        // 没看过就不记成看过，用默认数据库时还会弹。
        Assert.Null(_database.GetSetting(Onboarding.Setting));
    }

    [AvaloniaFact]
    public void SkipMarksSeenWithoutTour()
    {
        var window = Open(WelcomeChoice.Skip);
        Assert.Single(_welcomes);
        Assert.False(window.TourActive);
        Assert.Equal(Onboarding.CurrentVersion.ToString(), _database.GetSetting(Onboarding.Setting));
    }

    [AvaloniaFact]
    public void TourFollowsNavOrderAndKeys()
    {
        var window = Open(WelcomeChoice.Skip);
        window.MoveNav("Projects", -7);
        window.StartTour();
        Settle(window);
        Assert.Equal("Projects", window.TourStops[0].Target);
        Assert.Equal("Projects", window.CurrentPage);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("2 / 9", window.TourText.Progress);
        Assert.Equal(window.TourStops[1].Target, window.CurrentPage);

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("1 / 9", window.TourText.Progress);

        // 导览时 Alt+下 不会改导航顺序。
        var order = window.NavKeys;
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Assert.Equal(order, window.NavKeys);

        for (var i = 0; i < 8; i++)
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(("设置", window.TourStops[^1].Body, "9 / 9"), window.TourText);
        Assert.Equal("Settings", window.CurrentPage);
        Assert.Equal("完成", window.FindControl<Button>("TourNextButton")!.Content);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.False(window.TourActive);
        Assert.Equal("Plugins", window.CurrentPage);
        Assert.StartsWith("导览结束", window.Status);
    }

    [AvaloniaFact]
    public void EscapeEndsTourAndBlocksClicksUnderneath()
    {
        var window = Open(WelcomeChoice.Tour);
        var layer = window.FindControl<Canvas>("TourLayer")!;
        Assert.True(layer.IsVisible);
        window.FindControl<Button>("TourSkipButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.TourActive);

        window.StartTour();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(window.TourActive);
    }

    [AvaloniaFact]
    public void ScanAndTourStartsScanAndSettingsCanReopen()
    {
        _env.AddVst3("Pro-Q 4", "FabFilter");
        var window = Open(WelcomeChoice.ScanAndTour);
        Assert.True(window.TourActive);
        var clock = Stopwatch.StartNew();
        while (window.IsScanning || _database.LoadEffects().Count == 0)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "扫描没有结束");
        }
        Assert.Single(_database.LoadEffects());
        window.EndTour();

        // 设置里的“重新查看”：扫描完了可以再扫。
        window.Navigate(MainWindow.SettingsPage.Key);
        Settle(window);
        window.Settings!.FindControl<Button>("ShowOnboardingButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal([true, true], _welcomes);
        Assert.True(window.TourActive);
    }

    [AvaloniaFact]
    public void RendersWelcomeAndTour()
    {
        var output = Environment.GetEnvironmentVariable("WHATPLUGIN_SCREENSHOTS") ?? _dir;
        Directory.CreateDirectory(output);
        void Save(Window w, string name)
        {
            Settle(w);
            var frame = w.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
        }

        var window = Open(WelcomeChoice.Tour);
        window.MoveTour(2);
        Save(window, "tour");

        var welcome = new WelcomeDialog(canScan: true);
        welcome.Show();
        Save(welcome, "welcome");
        welcome.Close();
    }
}
