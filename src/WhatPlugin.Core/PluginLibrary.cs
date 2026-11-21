namespace WhatPlugin.Core;

/// <summary>插件库界面上用到的文字。Windows 版和跨平台版共用，两边显示一致。</summary>
public static class PluginLabels
{
    public static string Kind(PluginKind kind) => kind switch
    {
        PluginKind.Effect => "效果器",
        PluginKind.Instrument => "音源",
        _ => "未知",
    };

    public static string Format(PluginFormat format) => format switch
    {
        PluginFormat.Vst3 => "VST3",
        PluginFormat.Vst2 => "VST2",
        PluginFormat.Clap => "CLAP",
        PluginFormat.Aax => "AAX",
        PluginFormat.Au => "AU",
        _ => format.ToString(),
    };

    public static string Arch(Arch arch) => arch switch
    {
        Core.Arch.X64 => "64 位",
        Core.Arch.X86 => "32 位",
        Core.Arch.Arm64 => "ARM64",
        _ => "架构未知",
    };

    public static string Status(ScanStatus status) => status switch
    {
        ScanStatus.Loaded => "已读取插件清单",
        ScanStatus.ModuleInfo => "读取自模块信息",
        ScanStatus.FileInfoOnly => "仅文件信息",
        ScanStatus.LoadFailed => "读取失败",
        ScanStatus.TimedOut => "读取超时",
        _ => status.ToString(),
    };

    public static string TagSource(ClassificationSource source) => source switch
    {
        ClassificationSource.Manual => "分类由你手动修改",
        ClassificationSource.Seed => "分类来自资料库",
        ClassificationSource.SdkCategory => "分类来自插件自身",
        ClassificationSource.NameKeyword => "分类由名称推测，待确认",
        _ => "暂无分类信息",
    };

    /// <summary>列表里显示的厂家。不知道厂家时显示“未知”，厂家筛选也按这个字比较。</summary>
    public static string Vendor(PluginRow row) => row.Vendor.Length > 0 ? row.Vendor : "未知";

    public static string Rating(int rating) => rating > 0 ? new string('★', rating) : "";

    public static string Tags(PluginRow row) => row.Tags.Count > 0 ? string.Join("、", row.Tags) : "未分类";

    public static IReadOnlyList<string> TagList(PluginRow row) => row.Tags.Count > 0 ? row.Tags : ["未分类"];

    /// <summary>插件详情里的工程使用情况。</summary>
    public static string UsageSummary(PluginUsage? usage) => usage == null
        ? "工程里没用过"
        : $"{usage.Projects} 个工程，最近 {usage.LastUsedUtc.ToLocalTime():yyyy-MM-dd}";

    /// <summary>搜索框匹配的全部文字（小写）：名称、厂家、分类、音色、标签、备注、移入原因和混音属性。</summary>
    public static string SearchText(PluginRow row) => string.Join(' ',
        row.Name, row.Vendor, string.Join(' ', row.Tags), string.Join(' ', row.Timbres), string.Join(' ', row.User.MyTags),
        row.User.Note, row.User.BlacklistReason, row.User.Mix?.SearchText ?? "").ToLowerInvariant();
}

/// <summary>插件库上方“评分”下拉框的选项。</summary>
public enum RatingFilter { All, Rated, HighRated, LowRated, Unrated, HasNote }

/// <summary>插件库的筛选条件。界面只负责把下拉框和搜索框的状态填进来。</summary>
public sealed record LibraryFilter(
    bool Blacklist = false,
    string Search = "",
    string? Vendor = null,
    PluginFormat? Format = null,
    RatingFilter Rating = RatingFilter.All,
    LibraryCategory? Category = null)
{
    public const string AllVendors = "全部厂家";
    public const string AllFormats = "全部格式";

    /// <summary>评分下拉框的文字，顺序和 <see cref="RatingFilter"/> 一致。</summary>
    public static IReadOnlyList<string> RatingLabels { get; } = ["全部评分", "已评分", "4 星及以上", "2 星及以下", "未评分", "有备注"];

    /// <summary>格式下拉框的选项。AU 只有 Mac 上有，其他系统上不列。</summary>
    public static IReadOnlyList<PluginFormat> FormatsOnThisSystem { get; } =
        Enum.GetValues<PluginFormat>().Where(f => f != PluginFormat.Au || AppPlatform.IsMac).ToList();

    /// <summary>插件库和垃圾桶各看各的：垃圾桶里的插件不出现在插件库里，反之亦然。</summary>
    public bool InView(PluginRow row) => row.User.Hidden == Blacklist;

    public bool Matches(PluginRow row)
    {
        if (!InView(row))
            return false;
        var search = Search.Trim().ToLowerInvariant();
        if (search.Length > 0)
        {
            var text = PluginLabels.SearchText(row);
            if (!search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(text.Contains))
                return false;
        }
        if (Vendor != null && PluginLabels.Vendor(row) != Vendor)
            return false;
        if (Category is { IsComparison: true } category && !category.Matches(row))
            return false;
        if (Format is { } format && !row.Files.Any(f => f.Format == format))
            return false;
        var rating = row.User.Rating;
        return Rating switch
        {
            RatingFilter.Rated => rating > 0,
            RatingFilter.HighRated => rating >= 4,
            RatingFilter.LowRated => rating is > 0 and <= 2,
            RatingFilter.Unrated => rating == 0,
            RatingFilter.HasNote => row.User.Note.Length > 0,
            _ => true,
        };
    }

    /// <summary>
    /// 列表顺序。选中一个分类就是在做同类对比：用得多的排前面，其次看评分，再按名称。
    /// 垃圾桶里已经卸载的排在最后，其余按厂家、名称。插件库选“全部”时保持读出来的顺序。
    /// </summary>
    public IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, PluginRow> row, Func<T, PluginUsage?> usage)
    {
        IOrderedEnumerable<T>? ordered = null;
        if (Blacklist)
            ordered = items.OrderBy(i => TrashMemory.IsRemembered(row(i)));
        if (Category is { IsComparison: true })
        {
            ordered = ordered == null
                ? items.OrderByDescending(i => usage(i)?.Projects ?? 0)
                : ordered.ThenByDescending(i => usage(i)?.Projects ?? 0);
            return ordered.ThenByDescending(i => row(i).User.Rating).ThenBy(i => row(i).Name, StringComparer.CurrentCultureIgnoreCase);
        }
        if (ordered != null)
            return ordered.ThenBy(i => PluginLabels.Vendor(row(i)), StringComparer.CurrentCultureIgnoreCase).ThenBy(i => row(i).Name, StringComparer.CurrentCultureIgnoreCase);
        return items;
    }
}

/// <summary>插件详情里编辑分类和标签的规则。</summary>
public static class PluginEdits
{
    private static readonly char[] TagSeparators = [',', '，', '、', ';', '；'];

    public static List<string> SplitTags(string text) =>
        text.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>勾选或取消一个功能分类。结果和自动分类一致时返回 null（不算手动修改，以后资料库更新还能生效）。</summary>
    public static List<string>? ToggleTag(PluginRow row, IReadOnlyList<string>? tagsOverride, string tag, bool on)
    {
        var tags = (tagsOverride ?? row.VisibleAutoTags).ToList();
        tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (on)
            tags.Add(tag);
        return SameAsAuto(row, tags) ? null : tags;
    }

    /// <summary>在“添加新的分类”里输入的文字：人声、鼓这些属于音色，替用户勾上对应的音色；其余加进功能分类。</summary>
    public static (List<string>? Tags, List<string>? Timbres) AddTags(PluginRow row, IReadOnlyList<string>? tagsOverride, IReadOnlyList<string>? timbresOverride, string text)
    {
        var added = SplitTags(text);
        var timbres = timbresOverride?.ToList();
        var newTimbres = Classifier.TimbresIn(added);
        if (newTimbres.Count > 0)
            timbres = Classifier.CleanTimbres((timbresOverride ?? row.Timbres).Concat(newTimbres));
        var tags = (tagsOverride ?? row.VisibleAutoTags).ToList();
        tags.AddRange(Classifier.FunctionTagsIn(added).Where(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        return (SameAsAuto(row, tags) ? null : tags, timbres);
    }

    /// <summary>勾选或取消一个音色。和自动结果一致时返回 null。</summary>
    public static List<string>? ToggleTimbre(PluginRow row, IReadOnlyList<string>? tagsOverride, IReadOnlyList<string>? timbresOverride, string timbre, bool on)
    {
        var timbres = (timbresOverride ?? row.Timbres).ToList();
        timbres.RemoveAll(t => t.Equals(timbre, StringComparison.OrdinalIgnoreCase));
        if (on)
            timbres.Add(timbre);
        timbres = Classifier.CleanTimbres(timbres);
        var auto = Classifier.TimbresIn(tagsOverride ?? row.AutoTags);
        return timbres.SequenceEqual(auto, StringComparer.OrdinalIgnoreCase) ? null : timbres;
    }

    /// <summary>详情里列出的全部功能分类：目录里的在前，用户新加的接在后面。</summary>
    public static List<string> KnownTags(IEnumerable<PluginRow> rows, IEnumerable<string> active) =>
        Classifier.TagCatalog
            .Concat(rows.SelectMany(p => p.Tags).Order(StringComparer.CurrentCulture))
            .Concat(active)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool SameAsAuto(PluginRow row, List<string> tags)
    {
        var auto = row.VisibleAutoTags;
        return tags.Count == auto.Count && tags.All(t => auto.Contains(t, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>插件库页面上随状态变化的文字：状态栏、空列表提示、分类概况和推荐说明。</summary>
public static class LibraryText
{
    public const string DefaultSubtitle = "已安装的效果器，不含音源。左边选一个分类，就能把同类插件放在一起比较";
    public const string TrashTitle = "垃圾桶";
    public const string TrashSubtitle = "垃圾桶里的插件只在本软件中隐藏，点“卸载”才会动文件";

    public static string Status(int total, int blacklisted, int shown) => total == 0
        ? "还没有数据，到左下方“设置”里扫描插件。"
        : $"效果器 {total - blacklisted} 款，垃圾桶 {blacklisted} 款，当前显示 {shown} 款";

    public static string Empty(bool recommending, bool blacklist, int total, int blacklisted) =>
        recommending ? "没有找到匹配的插件"
        : blacklist && blacklisted == 0 ? "垃圾桶是空的"
        : total == 0 ? "还没有数据，到左下方“设置”里扫描插件"
        : "没有符合筛选的插件";

    /// <summary>选中分类时页面副标题换成这一类的使用概况。</summary>
    public static string CategorySummary(LibraryCategory category, IReadOnlyList<ComparisonRow> rows, bool projectsScanned, DateTime nowUtc)
    {
        var summary = PluginComparison.Summarize(rows, nowUtc);
        return projectsScanned
            ? $"“{category.Label}”共 {summary.Total} 款，过去一年用过 {summary.UsedRecently} 款，工程里从没用过 {summary.NeverUsed} 款，{summary.Unrated} 款未评分"
            : $"“{category.Label}”共 {summary.Total} 款，{summary.Unrated} 款未评分。到“工程”页扫描后可以看到使用情况";
    }

    /// <summary>“看同类”要选中的分类：这款插件所属范围最窄的功能分类，没有分类时选“未分类”。</summary>
    public static LibraryCategory CompareCategory(PluginRow row, IEnumerable<PluginRow> all) => row.Tags.Count > 0
        ? new LibraryCategory(LibraryCategoryKind.Function, PurchaseAdvisor.NarrowestTag(row.Tags, PluginComparison.Categories(all)) ?? row.Tags[0], 0)
        : new LibraryCategory(LibraryCategoryKind.NoFunction, LibraryCategories.NoFunctionLabel, 0);

    public static string DescribeRecommendation(RecommendResult result)
    {
        var parts = new List<string>();
        if (result.Understood.Count > 0)
            parts.Add("需求：" + string.Join("、", result.Understood));
        if (result.Vendors.Count > 0)
            parts.Add("厂家：" + string.Join("、", result.Vendors));
        if (result.Excluded.Count > 0)
            parts.Add("排除：" + string.Join("、", result.Excluded));
        if (result.NotUnderstood.Count > 0)
            parts.Add("没有理解：" + string.Join("、", result.NotUnderstood));
        if (result.NoPluginFor is { Count: > 0 } missing)
            parts.Add("当前列表中没有对应：" + string.Join("、", missing));
        if (result.AttributeHint != null)
            parts.Add(result.AttributeHint);

        var nothingUnderstood = result.Understood.Count == 0 && result.Vendors.Count == 0;
        parts.Add(result.TotalMatches == 0
            ? nothingUnderstood
                ? "没有找到匹配的插件，可以换个说法，例如：压缩、EQ、混响、去齿音、人声、磁带饱和"
                : "已安装的效果器中没有符合的（本软件不包含音源）"
            : $"找到 {result.TotalMatches} 款，最相关的排在最前");
        return string.Join("  |  ", parts);
    }
}
