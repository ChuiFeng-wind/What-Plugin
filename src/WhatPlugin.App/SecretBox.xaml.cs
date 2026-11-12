using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace WhatPlugin.App;

/// <summary>默认遮住的密码框，可以临时显示和复制。每次赋新值都会重新遮住。</summary>
public partial class SecretBox
{
    private bool _updating;

    public SecretBox()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AutomationProperties.SetAutomationId(Hidden, Name + "_Hidden");
            AutomationProperties.SetAutomationId(Plain, Name + "_Plain");
            AutomationProperties.SetAutomationId(RevealButton, Name + "_Reveal");
            AutomationProperties.SetAutomationId(CopyButton, Name + "_Copy");
            AutomationProperties.SetName(Hidden, Label);
            AutomationProperties.SetName(Plain, Label);
            AutomationProperties.SetName(RevealButton, "显示或隐藏" + Label);
            AutomationProperties.SetName(CopyButton, "复制" + Label);
        };
    }

    public event EventHandler? Changed;
    public event EventHandler? Copied;

    /// <summary>复制失败时给界面显示的原因。</summary>
    public string? CopyError { get; private set; }

    public string Label { get; set; } = "密码";

    public bool ShowCopy
    {
        set => CopyButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Secret
    {
        get => IsRevealed ? Plain.Text : Hidden.Password;
        set
        {
            _updating = true;
            Hidden.Password = value;
            Plain.Text = value;
            _updating = false;
            SetRevealed(false);
        }
    }

    private bool IsRevealed => Plain.Visibility == Visibility.Visible;

    private void SetRevealed(bool revealed)
    {
        Plain.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        RevealButton.Content = revealed ? "" : "";
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        _updating = true;
        if (IsRevealed)
            Hidden.Password = Plain.Text;
        else
            Plain.Text = Hidden.Password;
        _updating = false;
        SetRevealed(!IsRevealed);
    }

    private void Hidden_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_updating)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Plain_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_updating)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Secret.Length == 0)
            return;
        CopyError = SecureClipboard.Copy(Secret) ? null : SecureClipboard.LastError;
        Copied?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>复制的敏感内容不进 Windows 剪贴板历史，30 秒后如果还在剪贴板里就清掉。</summary>
public static class SecureClipboard
{
    private static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);
    private static DispatcherTimer? _timer;

    public static string? LastError { get; private set; }

    public static bool Copy(string secret)
    {
        try
        {
            var data = new DataObject(DataFormats.UnicodeText, secret);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (ExternalException error)
        {
            LastError = error.Message;
            return false;
        }

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = ClearAfter };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            ClearIfStillThere(secret);
        };
        _timer.Start();
        return true;
    }

    /// <summary>退出前调用，否则计时器来不及清除。</summary>
    public static void ClearPending()
    {
        _timer?.Stop();
        _timer = null;
    }

    private static void ClearIfStillThere(string secret)
    {
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == secret)
                Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // 剪贴板正被别的程序占用，放弃这次清除。
        }
    }
}
