namespace WhatPlugin.Core;

/// <summary>
/// 移入垃圾桶后又卸载了的插件：记录留在垃圾桶里（标成已卸载），当时写的原因、评分和感受都在，
/// 以后想再加入心愿单、或者在笔记和工程里看到它时提醒一下。
/// </summary>
public static class TrashMemory
{
    /// <summary>在垃圾桶里、文件已经不在了。</summary>
    public static bool IsRemembered(PluginRow plugin) => plugin.IsMissing && plugin.User.Hidden;

    /// <summary>有没有写下什么值得提醒的：移入原因、评分或使用感受。</summary>
    public static bool HasVerdict(PluginRow plugin) =>
        plugin.User.BlacklistReason.Length > 0 || plugin.User.Rating > 0 || plugin.User.Note.Length > 0;

    /// <summary>
    /// 按名称找已卸载的垃圾桶记录。同名的还装着（比如另一个格式）就不算，返回 null。
    /// 名称可以带或不带厂家前缀。
    /// </summary>
    public static PluginRow? Find(string name, IEnumerable<PluginRow> plugins)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Find(name, new PluginNameMatcher(plugins));
    }

    /// <summary>同上，查很多个名字时先建好一个 matcher 共用。</summary>
    public static PluginRow? Find(string name, PluginNameMatcher matcher)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var match = matcher.Find(name);
        return match != null && IsRemembered(match) ? match : null;
    }

    /// <summary>扫描前已卸载的垃圾桶插件里，这次又扫到文件的（重新装回来了，仍在垃圾桶里）。</summary>
    public static IReadOnlyList<PluginRow> Returned(IReadOnlySet<long> rememberedBefore, IEnumerable<PluginRow> after) =>
        after.Where(p => rememberedBefore.Contains(p.Id) && !p.IsMissing && p.User.Hidden).ToList();

    /// <summary>一行说明，例如“移入原因：太吃 CPU；评分 2 星；感受：底噪大”。</summary>
    public static string Describe(PluginRow plugin)
    {
        var parts = new List<string>();
        if (plugin.User.BlacklistReason.Length > 0)
            parts.Add("移入原因：" + plugin.User.BlacklistReason);
        if (plugin.User.Rating > 0)
            parts.Add($"评分 {plugin.User.Rating} 星");
        if (plugin.User.Note.Length > 0)
            parts.Add("感受：" + plugin.User.Note);
        return parts.Count > 0 ? string.Join("；", parts) : "当时没有写原因";
    }
}
