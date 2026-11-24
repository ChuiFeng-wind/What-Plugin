using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;

namespace WhatPlugin.Desktop;

/// <summary>默认遮住的密码框，可以临时显示和复制。每次赋新值都会重新遮住。</summary>
public sealed class SecretBox : DockPanel
{
    private readonly TextBox _box;
    private readonly Button _reveal;
    private readonly Button _copy;
    private readonly MaterialIcon _revealIcon;
    private string _label = "密码";
    private string _value = "";

    public SecretBox()
    {
        _copy = IconButton(MaterialIconKind.ContentCopy, "复制，30 秒后自动清除");
        _revealIcon = new MaterialIcon { Kind = MaterialIconKind.EyeOutline, Width = 16, Height = 16 };
        _reveal = IconButton(_revealIcon, "显示或隐藏");
        SetDock(_copy, Dock.Right);
        SetDock(_reveal, Dock.Right);
        _box = new TextBox { PasswordChar = '•', RevealPassword = false };
        Children.Add(_copy);
        Children.Add(_reveal);
        Children.Add(_box);
        ApplyLabel();

        // TextChanged 是稍后才触发的：程序赋的值不算用户修改。
        _box.TextChanged += (_, _) =>
        {
            if ((_box.Text ?? "") == _value)
                return;
            _value = _box.Text ?? "";
            Changed?.Invoke();
        };
        _reveal.Click += (_, _) => SetRevealed(!IsRevealed);
        _copy.Click += async (_, _) =>
        {
            if (Secret.Length == 0)
                return;
            CopyError = await SecureClipboard.Copy(TopLevel.GetTopLevel(this), Secret);
            Copied?.Invoke();
        };
    }

    public event Action? Changed;

    public event Action? Copied;

    /// <summary>复制失败时给界面显示的原因。</summary>
    public string? CopyError { get; private set; }

    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            ApplyLabel();
        }
    }

    public bool ShowCopy
    {
        get => _copy.IsVisible;
        set => _copy.IsVisible = value;
    }

    public bool IsRevealed => _box.RevealPassword;

    public string Secret
    {
        get => _box.Text ?? "";
        set
        {
            _value = value;
            _box.Text = value;
            SetRevealed(false);
        }
    }

    /// <summary>测试用：内部的输入框。</summary>
    public TextBox Box => _box;

    public void SetRevealed(bool revealed)
    {
        _box.RevealPassword = revealed;
        _revealIcon.Kind = revealed ? MaterialIconKind.EyeOffOutline : MaterialIconKind.EyeOutline;
    }

    /// <summary>当成用户点了“复制”（测试用）。</summary>
    public async Task CopyAsync()
    {
        CopyError = await SecureClipboard.Copy(TopLevel.GetTopLevel(this), Secret);
        Copied?.Invoke();
    }

    private void ApplyLabel()
    {
        AutomationProperties.SetName(_box, _label);
        AutomationProperties.SetName(_reveal, "显示或隐藏" + _label);
        AutomationProperties.SetName(_copy, "复制" + _label);
    }

    private static Button IconButton(MaterialIconKind kind, string tip) =>
        IconButton(new MaterialIcon { Kind = kind, Width = 16, Height = 16 }, tip);

    private static Button IconButton(MaterialIcon icon, string tip)
    {
        var button = new Button { Content = icon, Classes = { "Ghost" }, Padding = new Thickness(7, 6), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        ToolTip.SetTip(button, tip);
        return button;
    }
}

/// <summary>
/// 复制账号密码：不进剪贴板历史，30 秒后如果还在剪贴板里就清掉。
/// Windows 用系统认的三个格式排除历史和云同步；Mac 用剪贴板工具通用的 org.nspasteboard.ConcealedType 标记。
/// </summary>
public static class SecureClipboard
{
    public static TimeSpan ClearAfter { get; set; } = TimeSpan.FromSeconds(30);

    private static DispatcherTimer? _timer;

    /// <summary>成功返回 null，失败返回原因。</summary>
    public static async Task<string?> Copy(TopLevel? top, string secret)
    {
        if (top?.Clipboard is not { } clipboard)
            return "找不到剪贴板";
        try
        {
            var item = new DataTransferItem();
            item.SetText(secret);
            if (OperatingSystem.IsWindows())
            {
                foreach (var name in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                    item.Set(DataFormat.CreateBytesPlatformFormat(name), new byte[4]);
            }
            else if (OperatingSystem.IsMacOS())
                item.Set(DataFormat.CreateStringPlatformFormat("org.nspasteboard.ConcealedType"), "");
            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException or NotSupportedException)
        {
            return error.Message;
        }

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = ClearAfter };
        _timer.Tick += async (_, _) =>
        {
            _timer?.Stop();
            await ClearIfStillThere(clipboard, secret);
        };
        _timer.Start();
        return null;
    }

    /// <summary>退出前调用，否则计时器来不及清除。</summary>
    public static void ClearPending()
    {
        _timer?.Stop();
        _timer = null;
    }

    private static async Task ClearIfStillThere(IClipboard clipboard, string secret)
    {
        try
        {
            if (await clipboard.TryGetTextAsync() == secret)
                await clipboard.ClearAsync();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            // 剪贴板正被别的程序占用，放弃这次清除。
        }
    }
}
