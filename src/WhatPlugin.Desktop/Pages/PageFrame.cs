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

    /// <param name="actions">标题右边的按钮（例如工程页的“添加文件夹”“扫描工程”）。</param>
    public PageFrame(string title, string subtitle, Control content, Control? actions = null)
    {
        Content = content;
        Margin = new Thickness(28, 0, 24, 0);
        RowDefinitions = new RowDefinitions("Auto,*");

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (actions != null)
        {
            actions.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            actions.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);
        }
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = title, Classes = { "PageTitle" } });
        titles.Children.Add(_subtitle = new TextBlock { Text = subtitle, Classes = { "PageSubtitle" } });
        header.Children.Add(titles);
        Children.Add(header);

        SetRow(content, 1);
        Children.Add(content);
    }
}
