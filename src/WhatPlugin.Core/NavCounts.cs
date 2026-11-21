namespace WhatPlugin.Core;

/// <summary>左侧导航上显示的数字。Windows 版和跨平台版用同一套算法。</summary>
public sealed record NavCounts(int Plugins, int Blacklisted, int Chains, int Wishes, int Purchases)
{
    public static NavCounts Load(PluginDatabase database)
    {
        var effects = database.LoadEffects();
        var hidden = effects.Count(p => p.User.Hidden);
        var wishes = database.LoadWishes();
        return new NavCounts(
            effects.Count - hidden,
            hidden,
            database.LoadChains().Count,
            wishes.Count(w => w.Status == WishStatus.Wished),
            wishes.Count(w => w.Status == WishStatus.Purchased));
    }

    /// <summary>导航上 0 不显示（插件库除外，0 也是有意义的信息）。</summary>
    public static string Label(int count) => count > 0 ? count.ToString() : "";
}
