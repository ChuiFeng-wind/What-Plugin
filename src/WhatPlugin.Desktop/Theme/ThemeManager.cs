using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 换主题就是换一份同名画刷：界面用 DynamicResource 引用它们，会立刻更新，不用重启。
/// 配色和 Windows 版共用 <see cref="ThemePalettes"/>，键名也一样。
/// </summary>
public static class ThemeManager
{
    public const string Setting = "theme";

    public static ThemePalette Current { get; private set; } = ThemePalettes.Find(null);

    public static void Apply(string? id)
    {
        Current = ThemePalettes.Find(id);
        var app = Application.Current ?? throw new InvalidOperationException("应用还没启动");
        foreach (var (key, color) in Current.Colors)
            app.Resources[key] = CreateBrush(color);
        // 系统自带的控件（滚动条、输入框的细节）跟着深浅色走。
        app.RequestedThemeVariant = Current.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public static IBrush CreateBrush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
