using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 新手指引：第一次打开时弹欢迎窗口，然后按左侧导航逐项导览。和 Windows 版一样，
/// 只对新用户自动弹出；设置里可以随时重新查看。导览只是一层遮罩，不改任何数据。
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<TourStop> _tourStops = [];
    private Dictionary<string, RadioButton> _tourNav = [];
    private int _tourIndex;

    /// <summary>
    /// 启动时是否按需自动弹出欢迎窗口。只在用默认数据库启动时打开（App 里设）；
    /// 测试和 --db 指定的数据库不自动弹，老用户照样记成看过。
    /// </summary>
    public bool AutoOnboarding { get; set; }

    /// <summary>显示欢迎窗口，参数是现在能不能扫描。测试换成脚本。</summary>
    public Func<bool, Task<WelcomeChoice>> ShowWelcomeDialog { get; set; } = null!;

    public bool TourActive => TourLayer.IsVisible;

    /// <summary>导览当前一站的标题、正文和进度（测试检查用）。</summary>
    public (string Title, string Body, string Progress) TourText =>
        (TourTitle.Text ?? "", TourBody.Text ?? "", TourProgress.Text ?? "");

    public IReadOnlyList<TourStop> TourStops => _tourStops;

    /// <summary>构造函数最后调用：导航顺序已经排好。</summary>
    private void InitializeOnboarding()
    {
        ShowWelcomeDialog = canScan => WelcomeDialog.Show(this, canScan);
        TourNextButton.Click += (_, _) => MoveTour(1);
        TourBackButton.Click += (_, _) => MoveTour(-1);
        TourSkipButton.Click += (_, _) => EndTour();
        // 遮罩挡住下面的点击和拖动，免得误操作或者拖乱导航顺序。
        TourLayer.PointerPressed += (_, e) => e.Handled = true;
        TourLayer.SizeChanged += (_, _) => PlaceTour();
        AddHandler(KeyDownEvent, Tour_KeyDown, RoutingStrategies.Tunnel);

        switch (Onboarding.OnStartup(_database.GetSetting(Onboarding.Setting), _database.LoadEffects().Count))
        {
            case OnboardingStart.Show:
                // 等主窗口画出来再弹，欢迎窗口才能居中在主窗口上。
                Opened += ShowWelcomeOnce;
                break;
            case OnboardingStart.MarkSeen:
                MarkOnboardingSeen();
                break;
        }
    }

    private void ShowWelcomeOnce(object? sender, EventArgs e)
    {
        Opened -= ShowWelcomeOnce;
        if (AutoOnboarding)
            Dispatcher.UIThread.Post(() => _ = ShowWelcomeAsync(), DispatcherPriority.Background);
    }

    private void MarkOnboardingSeen() =>
        _database.SetSetting(Onboarding.Setting, Onboarding.CurrentVersion.ToString());

    /// <summary>欢迎窗口；设置里的“重新查看”也走这里。</summary>
    public async Task ShowWelcomeAsync()
    {
        if (TourActive)
            return;
        var choice = await ShowWelcomeDialog(!IsScanning);
        // 不管选了什么都算看过，下次不再自动弹出。
        MarkOnboardingSeen();
        switch (choice)
        {
            case WelcomeChoice.ScanAndTour:
                _ = RunScanAsync(new ScanRequest(false, ScanFormats.Main));
                StartTour();
                break;
            case WelcomeChoice.Tour:
                StartTour();
                break;
        }
    }

    void IScanHost.ShowOnboarding() => _ = ShowWelcomeAsync();

    // ---------- 导览 ----------

    /// <summary>导航项按当前显示顺序排列，“设置”在最后。</summary>
    private Dictionary<string, RadioButton> VisibleNavItems()
    {
        var items = new Dictionary<string, RadioButton>(StringComparer.Ordinal);
        foreach (var nav in NavList.Children.OfType<RadioButton>().Concat(NavFixed.Children.OfType<RadioButton>()))
            if (nav.IsVisible && nav.Tag is string key)
                items.TryAdd(key, nav);
        return items;
    }

    public void StartTour()
    {
        _tourNav = VisibleNavItems();
        _tourStops = Onboarding.Stops(_tourNav.Keys);
        if (_tourStops.Count == 0)
            return;
        _tourIndex = 0;
        TourLayer.IsVisible = true;
        ShowTourStop();
    }

    private void ShowTourStop()
    {
        var stop = _tourStops[_tourIndex];
        var nav = _tourNav[stop.Target];
        // 顺便切到这一页，背后能看到页面的样子。
        Navigate(stop.Target);
        nav.BringIntoView();

        var last = _tourIndex == _tourStops.Count - 1;
        var progress = Onboarding.Progress(_tourIndex, _tourStops.Count);
        TourTitle.Text = stop.Title;
        TourBody.Text = stop.Body;
        TourProgress.Text = progress;
        TourBackButton.IsVisible = _tourIndex > 0;
        TourNextButton.Content = last ? "完成" : "下一步";
        // 最后一站不需要“跳过”，但位置留着，按钮不跳来跳去。
        TourSkipButton.Opacity = last ? 0 : 1;
        TourSkipButton.IsHitTestVisible = !last;
        TourScanNote.IsVisible = IsScanning;
        AutomationProperties.SetName(TourBubble, $"新手导览 {progress}：{stop.Title}");

        // 切页面和滚动以后位置才定下来。
        Dispatcher.UIThread.Post(PlaceTour, DispatcherPriority.Loaded);
        PlaceTour();
        TourNextButton.Focus();
    }

    /// <summary>挖空当前导航项，气泡放在它右边，上下不出窗口。</summary>
    private void PlaceTour()
    {
        if (!TourActive || _tourStops.Count == 0)
            return;
        var nav = _tourNav[_tourStops[_tourIndex].Target];
        var width = TourLayer.Bounds.Width;
        var height = TourLayer.Bounds.Height;
        if (!nav.IsEffectivelyVisible || width <= 0 || height <= 0 || nav.TranslatePoint(default, TourLayer) is not { } origin)
            return;

        var hole = new Rect(origin, nav.Bounds.Size).Inflate(new Thickness(4, 3));
        TourShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height)),
            new RectangleGeometry(hole, 10, 10));

        Canvas.SetLeft(TourRing, hole.X);
        Canvas.SetTop(TourRing, hole.Y);
        TourRing.Width = hole.Width;
        TourRing.Height = hole.Height;

        TourBubble.Measure(new Size(TourBubble.Width, double.PositiveInfinity));
        var bubbleHeight = TourBubble.DesiredSize.Height;
        var center = hole.Y + hole.Height / 2;
        var top = Math.Clamp(center - 30, 12, Math.Max(12, height - bubbleHeight - 12));
        Canvas.SetLeft(TourBubble, hole.Right + 16);
        Canvas.SetTop(TourBubble, top);
        // 小三角对准导航项中间，但不跑出气泡的圆角。
        var arrowTop = Math.Clamp(center - top - 8, 14, Math.Max(14, bubbleHeight - 30));
        TourArrow.Margin = new Thickness(-7, arrowTop, 0, 0);
    }

    public void MoveTour(int step)
    {
        var next = _tourIndex + step;
        if (next >= _tourStops.Count)
        {
            EndTour();
            return;
        }
        if (next < 0)
            return;
        _tourIndex = next;
        ShowTourStop();
    }

    public void EndTour()
    {
        if (!TourActive)
            return;
        TourLayer.IsVisible = false;
        TourShade.Data = null;
        Navigate("Plugins");
        if (!IsScanning)
            StatusText.Text = "导览结束。想再看一遍，可以在“设置”里点“重新查看”。";
    }

    private void Tour_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!TourActive)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                EndTour();
                break;
            case Key.Enter or Key.Right or Key.PageDown:
                // 焦点在“上一步”或“跳过”上按 Enter 时，照按钮本身的意思来。
                if (e.Key == Key.Enter && e.Source is Button button && button != TourNextButton)
                    return;
                MoveTour(1);
                break;
            case Key.Left or Key.PageUp:
                MoveTour(-1);
                break;
            default:
                // Alt+上下键会调整导航顺序，导览时先不让动；其他按键也不传到下面的页面。
                if (e.KeyModifiers == KeyModifiers.Alt || e.Source is not Button)
                    break;
                return;
        }
        e.Handled = true;
    }
}
