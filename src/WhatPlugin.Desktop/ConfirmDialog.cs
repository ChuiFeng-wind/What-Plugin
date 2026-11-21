using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WhatPlugin.Desktop;

/// <summary>确认对话框：标题、一段说明、取消和确认两个按钮，和 Windows 版的 Dialogs.Confirm 一样。</summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirm, bool danger)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Surface"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Text"));
        this.Bind(FontFamilyProperty, this.GetResourceObservable("UiFont"));
        FontSize = 13;

        var ok = new Button { Content = confirm, Classes = { danger ? "Danger" : "Primary" }, MinWidth = 76, Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Classes = { "Ghost" }, IsCancel = true };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 18) };
        body.Children.Add(new TextBlock { Text = title, Classes = { "SectionTitle" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message, Classes = { "SmallNote" }, Margin = new Thickness(0, 8, 0, 0) });
        body.Children.Add(buttons);
        Content = body;
    }

    public static async Task<bool> Show(Window owner, string title, string message, string confirm, bool danger = false) =>
        await new ConfirmDialog(title, message, confirm, danger).ShowDialog<bool?>(owner) == true;
}
