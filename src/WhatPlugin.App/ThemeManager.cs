using System.Windows;
using System.Windows.Media;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>换主题就是换一份同名画刷：界面用 DynamicResource 引用它们，会立刻更新，不用重启。</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _active;

    public static ThemePalette Current { get; private set; } = ThemePalettes.Find(null);

    public static void Apply(string? id)
    {
        Current = ThemePalettes.Find(id);
        var dictionary = new ResourceDictionary();
        foreach (var (key, color) in Current.Colors)
            dictionary[key] = CreateBrush(color);

        // 合并字典里后加入的优先，所以盖得住 Theme.xaml 里的默认值。
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_active != null)
            merged.Remove(_active);
        merged.Add(dictionary);
        _active = dictionary;

        foreach (Window window in Application.Current.Windows)
            WindowEffects.UpdateFrame(window);
    }

    public static Brush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);
}
