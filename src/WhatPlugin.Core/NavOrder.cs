namespace WhatPlugin.Core;

/// <summary>
/// 左侧导航的自定义顺序。保存的是各项的标识，用逗号隔开。
/// 保存的顺序里没有的项（新版本加的、刚装的扩展）按默认顺序插回它前一项的后面；
/// 保存了但现在没有的项（卸载的扩展）忽略，但不从保存的内容里删掉，重新装上还在原位。
/// </summary>
public static class NavOrder
{
    public const string Setting = "nav.order";

    public static IReadOnlyList<string> Parse(string? saved) =>
        (saved ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    public static string Serialize(IEnumerable<string> keys) => string.Join(",", keys.Select(k => k.Replace(",", "")));

    /// <summary>按保存的顺序排列现有的项。</summary>
    public static List<string> Apply(IReadOnlyList<string> defaults, IReadOnlyList<string> saved)
    {
        var present = defaults.ToHashSet(StringComparer.Ordinal);
        var result = saved.Where(present.Contains).Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < defaults.Count; i++)
        {
            var key = defaults[i];
            if (result.Contains(key))
                continue;
            // 放在默认顺序里它前面那一项的后面；前面都没有就放最前。
            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var before = result.IndexOf(defaults[j]);
                if (before >= 0)
                {
                    at = before + 1;
                    break;
                }
            }
            result.Insert(at, key);
        }
        return result;
    }

    /// <summary>把 key 移到 index（按移走之后的列表算）。返回新列表；没有变化时返回 null。</summary>
    public static List<string>? MoveTo(IReadOnlyList<string> current, string key, int index)
    {
        var from = current.ToList().IndexOf(key);
        if (from < 0)
            return null;
        var list = current.ToList();
        list.RemoveAt(from);
        index = Math.Clamp(index, 0, list.Count);
        if (index == from)
            return null;
        list.Insert(index, key);
        return list;
    }

    /// <summary>上移（delta = -1）或下移（delta = 1）。到头了返回 null。</summary>
    public static List<string>? Move(IReadOnlyList<string> current, string key, int delta)
    {
        var from = current.ToList().IndexOf(key);
        if (from < 0 || from + delta < 0 || from + delta >= current.Count)
            return null;
        return MoveTo(current, key, from + delta);
    }

    /// <summary>
    /// 保存时把这次没显示的项（卸载的扩展）按原来的位置留下，免得以后装回来跑到别处。
    /// </summary>
    public static List<string> Merge(IReadOnlyList<string> saved, IReadOnlyList<string> shown)
    {
        var shownSet = shown.ToHashSet(StringComparer.Ordinal);
        var result = shown.ToList();
        for (var i = 0; i < saved.Count; i++)
        {
            var key = saved[i];
            if (shownSet.Contains(key) || result.Contains(key))
                continue;
            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var before = result.IndexOf(saved[j]);
                if (before >= 0)
                {
                    at = before + 1;
                    break;
                }
            }
            result.Insert(at, key);
        }
        return result;
    }
}
