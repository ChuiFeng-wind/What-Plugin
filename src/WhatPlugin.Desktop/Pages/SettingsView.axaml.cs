using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>一个主题选项。预览用它自己的颜色画，不跟随当前主题。</summary>
public sealed class ThemeOption(ThemePalette palette, bool isCurrent)
{
    public string Id => palette.Id;
    public string Name => palette.Name;
    public bool IsCurrent => isCurrent;
    public string Description => palette.IsDark ? "深色" : "浅色";
    public IBrush Bg => Brush("Bg1");
    public IBrush Side => Brush("Bg0");
    public IBrush Card => Brush("Surface");
    public IBrush Line => Brush("LineStrong");
    public IBrush Ink => Brush("Text");
    public IBrush Dim => Brush("Muted");
    public IBrush Accent => Brush("Accent");

    private IBrush Brush(string key) => ThemeManager.CreateBrush(palette[key]);
}

/// <summary>设置页。第 1 阶段只有主题；扫描相关的设置在第 3 阶段移植。</summary>
public partial class SettingsView : UserControl
{
    /// <summary>用户点了某个主题（参数是主题标识）。保存由主窗口负责。</summary>
    public event Action<string>? ThemeChosen;

    public SettingsView()
    {
        InitializeComponent();
        ThemeList.ItemsSource = ThemePalettes.All.Select(p => new ThemeOption(p, p.Id == ThemeManager.Current.Id)).ToList();
    }

    private void Theme_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string id })
            ThemeChosen?.Invoke(id);
    }
}
