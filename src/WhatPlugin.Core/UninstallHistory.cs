namespace WhatPlugin.Core;

/// <summary>插件是怎么没的。</summary>
public enum UninstallWay
{
    /// <summary>1.2.4 之前卸载的，当时没有记录方式。</summary>
    Unknown = 0,
    Official = 1,
    RecycleFiles = 2,
    WavesTool = 3,
    /// <summary>在本软件外卸载，扫描时发现文件已经不在。</summary>
    FoundMissing = 4,
}

/// <summary>
/// 一条卸载记录。和插件记录分开保存：插件记录删了，历史还在。
/// UninstalledUtc 为空表示 1.2.4 之前卸载的，日期未知。Rating、Note 和 TrashReason 是卸载时的快照。
/// </summary>
public sealed record UninstallRecord(
    long Id,
    string PluginKey,
    string Name,
    string Vendor,
    UninstallWay Way,
    string Reason,
    DateTime? UninstalledUtc,
    string TrashReason = "",
    int Rating = 0,
    string Note = "")
{
    public string WayText => UninstallHistory.WayText(Way);

    public string DateText => UninstalledUtc is { } utc ? utc.ToLocalTime().ToString("yyyy-MM-dd") : "1.2.4 之前卸载，日期未知";

    /// <summary>卸载原因；没填时用移入垃圾桶的原因。</summary>
    public string EffectiveReason => Reason.Length > 0 ? Reason : TrashReason;
}

public static class UninstallHistory
{
    public static string WayText(UninstallWay way) => way switch
    {
        UninstallWay.Official => "官方卸载程序",
        UninstallWay.RecycleFiles => "文件移到回收站",
        UninstallWay.WavesTool => "Waves 卸载工具",
        UninstallWay.FoundMissing => "扫描时发现已不在",
        _ => "方式未知",
    };

    /// <summary>卸载这一刻的记录，原因没填就留空（显示时用移入原因）。</summary>
    public static UninstallRecord From(PluginRow plugin, UninstallWay way, string reason, DateTime utc) =>
        new(0, plugin.Key, plugin.Name, plugin.Vendor, way, reason.Trim(), utc,
            plugin.User.BlacklistReason, plugin.User.Rating, plugin.User.Note);

    /// <summary>扫描前装着、扫描后不在了的垃圾桶插件。</summary>
    public static IReadOnlyList<PluginRow> NewlyMissing(IReadOnlySet<long> installedTrashBefore, IEnumerable<PluginRow> after) =>
        after.Where(p => installedTrashBefore.Contains(p.Id) && p.IsMissing && p.User.Hidden).ToList();

    /// <summary>按名称找最近一条卸载记录（名称可以带或不带厂家前缀）。同名的现在还装着就不算。</summary>
    public static UninstallRecord? Find(string name, IReadOnlyList<UninstallRecord> history, IEnumerable<PluginRow> plugins)
    {
        if (string.IsNullOrWhiteSpace(name) || history.Count == 0)
            return null;
        var installed = new PluginNameMatcher(plugins.Where(p => !p.IsMissing));
        if (installed.Find(name) != null)
            return null;
        var key = Normalizer.Name(name);
        return Latest(history.Where(r =>
            Normalizer.Name(r.Name) == key
            || (Normalizer.Vendor(r.Vendor) is { Length: > 0 } vendor && (key == vendor + Normalizer.Name(r.Name) || vendor + key == Normalizer.Name(r.Name)))));
    }

    /// <summary>这款插件（按键或名称）最近一条卸载记录。</summary>
    public static UninstallRecord? ForPlugin(PluginRow plugin, IReadOnlyList<UninstallRecord> history) =>
        Latest(history.Where(r => r.PluginKey == plugin.Key || Normalizer.Name(r.Name) == Normalizer.Name(plugin.Name)));

    /// <summary>这条记录的插件现在又装着了。</summary>
    public static bool IsBack(UninstallRecord record, IEnumerable<PluginRow> plugins) =>
        plugins.Any(p => !p.IsMissing && (p.Key == record.PluginKey || Normalizer.Name(p.Name) == Normalizer.Name(record.Name)));

    private static UninstallRecord? Latest(IEnumerable<UninstallRecord> records) =>
        records.OrderByDescending(r => r.UninstalledUtc ?? DateTime.MinValue).ThenByDescending(r => r.Id).FirstOrDefault();

    /// <summary>
    /// 再加入心愿单等场合的提醒文字。垃圾桶里还有插件记录时用它当前的原因、评分和感受；
    /// 插件记录已经删了就用卸载时的快照。两者都没有返回空。
    /// </summary>
    public static string Reminder(PluginRow? trashRow, UninstallRecord? record)
    {
        if (trashRow == null && record == null)
            return "";
        var parts = new List<string>();
        if (record != null)
        {
            parts.Add(record.UninstalledUtc is { } utc ? $"{utc.ToLocalTime():yyyy-MM-dd} 卸载" : "以前卸载过（日期未知）");
            if (record.Reason.Length > 0)
                parts.Add("卸载原因：" + record.Reason);
        }
        var trashReason = trashRow?.User.BlacklistReason ?? record!.TrashReason;
        var rating = trashRow?.User.Rating ?? record!.Rating;
        var note = trashRow?.User.Note ?? record!.Note;
        if (trashReason.Length > 0)
            parts.Add("移入原因：" + trashReason);
        if (rating > 0)
            parts.Add($"评分 {rating} 星");
        if (note.Length > 0)
            parts.Add("感受：" + note);
        if (record?.Reason.Length is null or 0 && trashReason.Length == 0 && rating == 0 && note.Length == 0)
            parts.Add("当时没有写原因");
        return string.Join("；", parts);
    }

    /// <summary>一行说明，例如“2026-11-12 卸载（官方卸载程序），原因：太吃 CPU”。</summary>
    public static string Describe(UninstallRecord record)
    {
        var when = record.UninstalledUtc is { } utc ? $"{utc.ToLocalTime():yyyy-MM-dd} 卸载" : "以前卸载过（日期未知）";
        var reason = record.EffectiveReason;
        return reason.Length > 0 ? $"{when}，原因：{reason}" : $"{when}，没有写原因";
    }
}
