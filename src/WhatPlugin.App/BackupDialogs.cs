using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WhatPlugin.App;

/// <summary>导入时输入备份密码，以及导出、导入勾选窗口共用的小零件。</summary>
public static class BackupDialogs
{
    public const int MinPasswordLength = 6;

    /// <summary>
    /// 让用户输入备份密码。<paramref name="tryPassword"/> 返回 null 表示解开了，否则返回错误提示并留在对话框里。
    /// 返回值：解开时是密码；选择跳过账号密码时是 ""；取消整个导入时是 null。
    /// </summary>
    public static string? AskPassword(Window owner, Func<string, string?> tryPassword)
    {
        var window = NewWindow(owner, "输入备份密码");
        var password = NewPasswordBox("BackupPassword", "备份密码");
        var error = ErrorText();
        string? result = null;

        var ok = OkButton("解开");
        ok.Click += (_, _) =>
        {
            Mouse.OverrideCursor = Cursors.Wait;
            string? message;
            try
            {
                message = tryPassword(password.Password);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            if (message != null)
            {
                Show(error, message);
                password.SelectAll();
                password.Focus();
                return;
            }
            result = password.Password;
            window.DialogResult = true;
        };
        var skip = new Button { Content = "跳过账号密码", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(skip, "BackupSkipSecrets");
        skip.Click += (_, _) =>
        {
            result = "";
            window.DialogResult = true;
        };

        var content = Body("输入备份密码",
            "这份备份里的账号、密码和序列号是加密的，输入导出时设的备份密码才能恢复。跳过的话，其他内容照常导入，本机的账号密码保持不变。");
        content.Children.Add(Label("备份密码"));
        content.Children.Add(password);
        content.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(CancelButton(window));
        buttons.Children.Add(new Border { Width = 8 });
        buttons.Children.Add(skip);
        buttons.Children.Add(ok);
        content.Children.Add(buttons);
        Finish(window, content);
        window.Loaded += (_, _) => password.Focus();

        return window.ShowDialog() == true ? result : null;
    }

    // ---------- 共用的小零件，和 Dialogs 里的样式一致 ----------

    internal static Window NewWindow(Window owner, string title)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);
        return window;
    }

    internal static PasswordBox NewPasswordBox(string id, string name)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, name);
        return box;
    }

    internal static TextBlock Label(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 12, 0, 0),
        Foreground = (Brush)Application.Current.FindResource("TextDim"),
    };

    internal static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 20,
        Margin = new Thickness(0, 10, 0, 0),
        FontSize = 12,
        Foreground = (Brush)Application.Current.FindResource("TextDim"),
    };

    internal static TextBlock ErrorText()
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("Danger"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(text, "DialogError");
        return text;
    }

    internal static void Show(TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
        AutomationProperties.SetName(error, message);
    }

    internal static Button OkButton(string text)
    {
        var ok = new Button { Content = text, MinWidth = 88, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Style = (Style)Application.Current.FindResource("PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "DialogOk");
        return ok;
    }

    internal static Button CancelButton(Window window)
    {
        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "DialogCancel");
        cancel.Click += (_, _) => window.DialogResult = false;
        return cancel;
    }

    internal static StackPanel Buttons(Button cancel, Button ok)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        return buttons;
    }

    internal static StackPanel Body(string title, string message)
    {
        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        };
        AutomationProperties.SetAutomationId(body, "DialogMessage");
        content.Children.Add(body);
        return content;
    }

    internal static void Finish(Window window, StackPanel content) =>
        window.Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
}
