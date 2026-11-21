using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WhatPlugin.Desktop;

/// <summary>还没移植过来的页面：写明在第几阶段移植，免得以为是坏了。</summary>
public sealed class PendingView : Border
{
    // 样式按类型匹配：让 Border.Card 的样式也用在这个子类上。
    protected override Type StyleKeyOverride => typeof(Border);

    public PendingView(int stage)
    {
        Classes.Add("Card");
        Padding = new Thickness(28, 26);
        VerticalAlignment = VerticalAlignment.Top;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "这一页还在移植中", Classes = { "SectionTitle" } });
        text.Children.Add(new TextBlock
        {
            Text = $"跨平台版按阶段移植 Windows 版的页面，这一页安排在第 {stage} 阶段。现在请先用 Windows 版。",
            Classes = { "SmallNote" },
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        Child = text;
    }
}
