namespace WhatPlugin.Core;

/// <summary>一套配色。颜色是 #RRGGBB，键名和 Theme.xaml 里的画刷一一对应。</summary>
public sealed record ThemePalette(string Id, string Name, bool IsDark, IReadOnlyDictionary<string, string> Colors)
{
    public string this[string key] => Colors[key];
}

public static class ThemePalettes
{
    public const string DefaultId = "graphite";

    public static IReadOnlyList<ThemePalette> All { get; } =
    [
        Create("graphite", "石墨琥珀", true,
            "#101012", "#16161A", "#1D1D22", "#25252B", "#2E2E35", "#121215", "#2A2A31", "#3C3C45",
            "#EFEDE8", "#B4B2AD", "#8D8B90",
            "#F0A93B", "#F7BC5E", "#D9952E", "#1C1408", "#2B2316", "#332918",
            "#EE7770", "#33191A", "#7BCB9B", "#17291F"),
        Create("midnight", "午夜蓝", true,
            "#0B0F17", "#10151F", "#171D29", "#1F2734", "#293242", "#0D121B", "#252E3D", "#3A4557",
            "#E9EEF6", "#AEB8C7", "#8894A6",
            "#6AA5FF", "#8DBBFF", "#4F8DEB", "#07152B", "#16243A", "#1C2D48",
            "#F27F7F", "#331B20", "#6FCFA6", "#14292A"),
        Create("forest", "深林青绿", true,
            "#0C1110", "#111816", "#18201E", "#202A27", "#2A3633", "#0E1412", "#27322F", "#3B4A46",
            "#EAF0ED", "#AEBBB6", "#88968F",
            "#4CCFA6", "#70DDBB", "#37B48D", "#04201A", "#15302A", "#1A3B33",
            "#EF8078", "#331C1B", "#8FD18A", "#1A2C1B"),
        Create("wine", "酒红", true,
            "#120D10", "#181216", "#20181D", "#2A2026", "#352930", "#140F12", "#32262D", "#4A3A43",
            "#F2ECEE", "#BDB0B6", "#978991",
            "#EC7A98", "#F499B1", "#D6617F", "#2A0C15", "#35191F", "#412028",
            "#F58A7B", "#351B18", "#85CDA3", "#172A20"),
        Create("paper", "纸白琥珀", false,
            "#EFEBE3", "#F6F3EC", "#FFFDF9", "#F2EEE5", "#E7E2D6", "#FFFFFF", "#DED8CB", "#C2BBAB",
            "#25221C", "#57524A", "#6F695F",
            "#A7650A", "#8F5608", "#7A4905", "#FFFFFF", "#F7EBD6", "#F1E0C2",
            "#B3372E", "#F8E3E0", "#1F7A4C", "#DFF1E6"),
        Create("mist", "雾白蓝", false,
            "#E8ECF1", "#F1F4F7", "#FBFCFE", "#EDF0F4", "#E0E5EB", "#FFFFFF", "#D6DCE4", "#B7C0CC",
            "#1C232D", "#4B5563", "#626C7A",
            "#2362C4", "#1C53A8", "#17468E", "#FFFFFF", "#E1EAF8", "#D2DFF4",
            "#B4332E", "#F7E2E1", "#1B7A55", "#DCF0E7"),
    ];

    public static ThemePalette Find(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == DefaultId);

    /// <summary>WCAG 对比度，测试用它保证每套配色的文字都看得清。</summary>
    public static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        static double Channel(string s)
        {
            var c = Convert.ToInt32(s, 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(hex[1..3]) + 0.7152 * Channel(hex[3..5]) + 0.0722 * Channel(hex[5..7]);
    }

    private static ThemePalette Create(string id, string name, bool isDark,
        string bg0, string bg1, string surface, string surfaceHover, string surfaceActive, string input, string line, string lineStrong,
        string text, string textDim, string muted,
        string accent, string accentHover, string accentPressed, string onAccent, string accentSoft, string accentSoftHover,
        string danger, string dangerSoft, string good, string goodSoft) =>
        new(id, name, isDark, new Dictionary<string, string>
        {
            ["Bg0"] = bg0, ["Bg1"] = bg1, ["Surface"] = surface, ["SurfaceHover"] = surfaceHover, ["SurfaceActive"] = surfaceActive,
            ["Input"] = input, ["Line"] = line, ["LineStrong"] = lineStrong,
            ["Text"] = text, ["TextDim"] = textDim, ["Muted"] = muted,
            ["Accent"] = accent, ["AccentHover"] = accentHover, ["AccentPressed"] = accentPressed, ["OnAccent"] = onAccent,
            ["AccentSoft"] = accentSoft, ["AccentSoftHover"] = accentSoftHover,
            ["Danger"] = danger, ["DangerSoft"] = dangerSoft, ["Good"] = good, ["GoodSoft"] = goodSoft,
        });
}
