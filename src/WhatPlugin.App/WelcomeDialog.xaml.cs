using System.Windows;

namespace WhatPlugin.App;

public enum WelcomeChoice { Skip, Tour, ScanAndTour }

/// <summary>第一次打开时的欢迎窗口，也可以从设置里再打开。</summary>
public partial class WelcomeDialog : Window
{
    public WelcomeChoice Choice { get; private set; } = WelcomeChoice.Skip;

    public WelcomeDialog(bool canScan)
    {
        InitializeComponent();
        WindowEffects.ApplyDialog(this);
        // 已经在扫描时只提供导览。
        if (!canScan)
        {
            ScanButton.Visibility = Visibility.Collapsed;
            TourButton.Style = (Style)FindResource("PrimaryButton");
            TourButton.IsDefault = true;
        }
        Loaded += (_, _) => (canScan ? ScanButton : TourButton).Focus();
    }

    private void Close(WelcomeChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close(WelcomeChoice.Skip);

    private void Tour_Click(object sender, RoutedEventArgs e) => Close(WelcomeChoice.Tour);

    private void Scan_Click(object sender, RoutedEventArgs e) => Close(WelcomeChoice.ScanAndTour);
}
