using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>
/// 新手指引：第一次打开时弹欢迎窗口，然后按左侧导航逐项导览。
/// 只对新用户自动弹出；设置里可以随时重新查看。导览只是一层遮罩，不改任何数据。
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<TourStop> _tourStops = [];
    private Dictionary<string, RadioButton> _tourNav = [];
    private int _tourIndex;

    private bool TourActive => TourLayer.Visibility == Visibility.Visible;

    /// <summary>构造函数最后调用：导航顺序和扩展页面都已就位。</summary>
    private void InitializeOnboarding()
    {
        RootFrame.SizeChanged += (_, _) => PlaceTour();
        PreviewKeyDown += Tour_PreviewKeyDown;

        switch (Onboarding.OnStartup(_database.GetSetting(Onboarding.Setting), _plugins.Count))
        {
            case OnboardingStart.Show:
                // 等主窗口画出来再弹，欢迎窗口才能居中在主窗口上。
                ContentRendered += ShowWelcomeOnce;
                break;
            case OnboardingStart.MarkSeen:
                MarkOnboardingSeen();
                break;
        }
    }

    private void ShowWelcomeOnce(object? sender, EventArgs e)
    {
        ContentRendered -= ShowWelcomeOnce;
        Dispatcher.BeginInvoke(ShowWelcome, DispatcherPriority.ApplicationIdle);
    }

    private void MarkOnboardingSeen() =>
        _database.SetSetting(Onboarding.Setting, Onboarding.CurrentVersion.ToString());

    private void ShowWelcome()
    {
        if (TourActive)
            return;
        var dialog = new WelcomeDialog(canScan: _scanCancel == null) { Owner = this };
        dialog.ShowDialog();
        // 不管选了什么都算看过，下次不再自动弹出。
        MarkOnboardingSeen();
        switch (dialog.Choice)
        {
            case WelcomeChoice.ScanAndTour:
                _ = RunScanAsync(full: false, NonVst2Formats);
                StartTour();
                break;
            case WelcomeChoice.Tour:
                StartTour();
                break;
        }
    }

    private void ShowOnboarding_Click(object sender, RoutedEventArgs e) => ShowWelcome();

    // ---------- 导览 ----------

    /// <summary>导航项按当前显示顺序排列；扩展页面用 Uid（ext:标题），内置页面用 Tag。</summary>
    private Dictionary<string, RadioButton> VisibleNavItems()
    {
        var items = new Dictionary<string, RadioButton>(StringComparer.Ordinal);
        foreach (var nav in NavList.Children.OfType<RadioButton>().Append(NavSettings))
        {
            if (nav.Visibility != Visibility.Visible)
                continue;
            var key = nav.Uid.StartsWith(Onboarding.ExtensionPrefix, StringComparison.Ordinal) ? nav.Uid : nav.Tag as string;
            if (!string.IsNullOrEmpty(key))
                items.TryAdd(key, nav);
        }
        return items;
    }

    private void StartTour()
    {
        _tourNav = VisibleNavItems();
        _tourStops = Onboarding.Stops(_tourNav.Keys);
        if (_tourStops.Count == 0)
            return;
        _tourIndex = 0;
        TourLayer.Visibility = Visibility.Visible;
        ShowTourStop();
    }

    private void ShowTourStop()
    {
        var stop = _tourStops[_tourIndex];
        var nav = _tourNav[stop.Target];
        // 顺便切到这一页，背后能看到页面的样子。
        nav.IsChecked = true;
        nav.BringIntoView();

        var last = _tourIndex == _tourStops.Count - 1;
        TourTitle.Text = stop.Title;
        TourBody.Text = stop.Body;
        TourProgress.Text = Onboarding.Progress(_tourIndex, _tourStops.Count);
        TourBackButton.Visibility = _tourIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        TourNextButton.Content = last ? "完成" : "下一步";
        TourSkipButton.Visibility = last ? Visibility.Hidden : Visibility.Visible;
        TourScanNote.Visibility = _scanCancel != null ? Visibility.Visible : Visibility.Collapsed;
        SetTourAutomationName(stop);

        // 切页面和滚动以后位置才定下来。
        Dispatcher.BeginInvoke(PlaceTour, DispatcherPriority.Loaded);
        TourNextButton.Focus();
    }

    private void SetTourAutomationName(TourStop stop) =>
        System.Windows.Automation.AutomationProperties.SetName(TourBubble, $"新手导览 {Onboarding.Progress(_tourIndex, _tourStops.Count)}：{stop.Title}");

    /// <summary>挖空当前导航项，气泡放在它右边，上下不出窗口。</summary>
    private void PlaceTour()
    {
        if (!TourActive || _tourStops.Count == 0)
            return;
        var nav = _tourNav[_tourStops[_tourIndex].Target];
        var width = TourLayer.ActualWidth;
        var height = TourLayer.ActualHeight;
        if (!nav.IsVisible || width <= 0 || height <= 0)
            return;

        var hole = new Rect(nav.TranslatePoint(new Point(0, 0), TourLayer), new Size(nav.ActualWidth, nav.ActualHeight));
        hole.Inflate(4, 3);
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

    private void MoveTour(int step)
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

    private void EndTour()
    {
        TourLayer.Visibility = Visibility.Collapsed;
        TourShade.Data = null;
        NavPlugins.IsChecked = true;
        if (_scanCancel == null)
            StatusText.Text = "导览结束。想再看一遍，可以在“设置”里点“重新查看”。";
    }

    private void TourNext_Click(object sender, RoutedEventArgs e) => MoveTour(1);

    private void TourBack_Click(object sender, RoutedEventArgs e) => MoveTour(-1);

    private void TourSkip_Click(object sender, RoutedEventArgs e) => EndTour();

    /// <summary>导览时遮罩挡住下面的点击和拖动，免得误操作或者拖乱导航顺序。</summary>
    private void TourLayer_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Tour_PreviewKeyDown(object sender, KeyEventArgs e)
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
                if (e.Key == Key.Enter && Keyboard.FocusedElement is Button button && button != TourNextButton && TourBubble.IsAncestorOf(button))
                    return;
                MoveTour(1);
                break;
            case Key.Left or Key.PageUp:
                MoveTour(-1);
                break;
            case Key.System:
                // Alt+上下键会调整导航顺序，导览时先不让动。
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
