using Avalonia;
using Avalonia.Controls;

namespace WhatPlugin.Desktop;

/// <summary>每一页共用的外框：左右边距、大标题和一行说明，和 Windows 版的页面头部一样。</summary>
public sealed class PageFrame : Grid
{
    private readonly TextBlock _subtitle;

    public Control Content { get; }

    /// <summary>说明那一行，有的页面会随状态变化（例如插件库选中分类时显示这一类的使用概况）。</summary>
    public string Subtitle
    {
        get => _subtitle.Text ?? "";
        set => _subtitle.Text = value;
    }

    public PageFrame(string title, string subtitle, Control content)
    {
        Content = content;
        Margin = new Thickness(28, 0, 24, 0);
        RowDefinitions = new RowDefinitions("Auto,*");

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock { Text = title, Classes = { "PageTitle" } });
        header.Children.Add(_subtitle = new TextBlock { Text = subtitle, Classes = { "PageSubtitle" } });
        Children.Add(header);

        SetRow(content, 1);
        Children.Add(content);
    }
}
