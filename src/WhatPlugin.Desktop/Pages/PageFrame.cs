using Avalonia;
using Avalonia.Controls;

namespace WhatPlugin.Desktop;

/// <summary>每一页共用的外框：左右边距、大标题和一行说明，和 Windows 版的页面头部一样。</summary>
public sealed class PageFrame : Grid
{
    public PageFrame(string title, string subtitle, Control content)
    {
        Margin = new Thickness(28, 0, 24, 0);
        RowDefinitions = new RowDefinitions("Auto,*");

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock { Text = title, Classes = { "PageTitle" } });
        header.Children.Add(new TextBlock { Text = subtitle, Classes = { "PageSubtitle" } });
        Children.Add(header);

        SetRow(content, 1);
        Children.Add(content);
    }
}
