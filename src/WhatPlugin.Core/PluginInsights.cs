using System.Globalization;
using System.Text;

namespace WhatPlugin.Core;

// ---------- 混音属性 ----------

public enum Tri { Unknown, Yes, No }

public enum LatencyLevel { Unknown, Zero, Low, High }

public enum CpuLevel { Unknown, Light, Medium, Heavy }

/// <summary>
/// 用户手动填写的混音属性。延迟和 CPU 要运行插件才测得出来，而本软件不运行插件，所以不自动填。
/// </summary>
public sealed record MixAttributes(
    LatencyLevel Latency = LatencyLevel.Unknown,
    CpuLevel Cpu = CpuLevel.Unknown,
    Tri LinearPhase = Tri.Unknown,
    Tri Sidechain = Tri.Unknown,
    Tri Oversampling = Tri.Unknown)
{
    public bool IsEmpty => this == new MixAttributes();

    public string LatencyText => Latency switch
    {
        LatencyLevel.Zero => "无延迟",
        LatencyLevel.Low => "低延迟",
        LatencyLevel.High => "高延迟",
        _ => "",
    };

    public string CpuText => Cpu switch
    {
        CpuLevel.Light => "轻",
        CpuLevel.Medium => "中",
        CpuLevel.Heavy => "重",
        _ => "",
    };

    public static string TriText(Tri value) => value switch
    {
        Tri.Yes => "有",
        Tri.No => "没有",
        _ => "",
    };

    /// <summary>搜索用的文字，只含确实具备的属性。</summary>
    public string SearchText
    {
        get
        {
            var parts = new List<string>();
            if (Latency != LatencyLevel.Unknown)
                parts.Add(LatencyText);
            if (Cpu != CpuLevel.Unknown)
                parts.Add("cpu" + CpuText);
            if (LinearPhase == Tri.Yes)
                parts.Add("线性相位");
            if (Sidechain == Tri.Yes)
                parts.Add("侧链");
            if (Oversampling == Tri.Yes)
                parts.Add("过采样");
            return string.Join(' ', parts);
        }
    }
}

// ---------- 同类对比 ----------

public sealed record ComparisonCategory(string Tag, int Count)
{
    /// <summary>列表项的无障碍名称。</summary>
    public override string ToString() => $"{Tag} {Count} 款";
}

public sealed record ComparisonRow(PluginRow Plugin, PluginUsage? Usage);

public enum LibraryCategoryKind { All, Header, Function, NoFunction, Timbre, NoTimbre }

/// <summary>插件库左边的一项：全部、分组标题、功能分类或适合音色。</summary>
public sealed record LibraryCategory(LibraryCategoryKind Kind, string Label, int Count)
{
    public bool IsHeader => Kind == LibraryCategoryKind.Header;
    public string CountText => IsHeader ? "" : Count.ToString();

    /// <summary>选中它时插件库只显示哪些插件。</summary>
    public bool Matches(PluginRow row) => Kind switch
    {
        LibraryCategoryKind.Function => row.Tags.Contains(Label, StringComparer.OrdinalIgnoreCase),
        LibraryCategoryKind.NoFunction => row.Tags.Count == 0,
        LibraryCategoryKind.Timbre => row.Timbres.Contains(Label, StringComparer.OrdinalIgnoreCase),
        LibraryCategoryKind.NoTimbre => row.Timbres.Count == 0,
        _ => true,
    };

    /// <summary>选中某个分类时的对比顺序：用得多的在前，其次看评分。“全部”保持原来的顺序。</summary>
    public bool IsComparison => Kind is not (LibraryCategoryKind.All or LibraryCategoryKind.Header);
}

public static class LibraryCategories
{
    public const string NoFunctionLabel = "未分类";
    public const string NoTimbreLabel = "未标音色";

    /// <summary>
    /// 全部在最上面，然后是功能分类（目录里的在前，用户加的在后，未分类最后）和适合音色（按目录顺序）。
    /// 数量为 0 的不列出，但适合音色全部列出，方便看出哪类还没有插件。
    /// </summary>
    public static IReadOnlyList<LibraryCategory> Build(IReadOnlyCollection<PluginRow> rows)
    {
        var list = new List<LibraryCategory> { new(LibraryCategoryKind.All, "全部", rows.Count) };

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in rows.SelectMany(p => p.Tags))
            counts[tag] = counts.GetValueOrDefault(tag) + 1;
        list.Add(new(LibraryCategoryKind.Header, "功能分类", 0));
        list.AddRange(Classifier.TagCatalog.Where(counts.ContainsKey)
            .Concat(counts.Keys.Where(k => !Classifier.TagCatalog.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.CurrentCulture))
            .Select(tag => new LibraryCategory(LibraryCategoryKind.Function, tag, counts[tag])));
        var untagged = rows.Count(p => p.Tags.Count == 0);
        if (untagged > 0)
            list.Add(new(LibraryCategoryKind.NoFunction, NoFunctionLabel, untagged));

        list.Add(new(LibraryCategoryKind.Header, "音色分类", 0));
        list.AddRange(Classifier.TimbreCatalog.Select(t => new LibraryCategory(LibraryCategoryKind.Timbre, t, rows.Count(r => r.Timbres.Contains(t)))));
        var noTimbre = rows.Count(p => p.Timbres.Count == 0);
        if (noTimbre > 0)
            list.Add(new(LibraryCategoryKind.NoTimbre, NoTimbreLabel, noTimbre));
        return list;
    }
}

public sealed record ComparisonSummary(int Total, int UsedRecently, int NeverUsed, int Unrated);

public static class PluginComparison
{
    public const string Uncategorized = "未分类";

    private static IEnumerable<PluginRow> Comparable(IEnumerable<PluginRow> plugins) =>
        plugins.Where(p => !p.IsMissing && !p.User.Hidden && p.Kind != PluginKind.Instrument);

    /// <summary>目录里的分类在前，用户自己加的分类接在后面，“未分类”放最后。</summary>
    public static IReadOnlyList<ComparisonCategory> Categories(IEnumerable<PluginRow> plugins)
    {
        var rows = Comparable(plugins).ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in rows.SelectMany(p => p.Tags))
            counts[tag] = counts.GetValueOrDefault(tag) + 1;

        var ordered = Classifier.TagCatalog.Where(counts.ContainsKey)
            .Concat(counts.Keys.Where(k => !Classifier.TagCatalog.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.CurrentCulture))
            .Select(tag => new ComparisonCategory(tag, counts[tag]))
            .ToList();
        var untagged = rows.Count(p => p.Tags.Count == 0);
        if (untagged > 0)
            ordered.Add(new ComparisonCategory(Uncategorized, untagged));
        return ordered;
    }

    /// <summary>用得多的在前，其次看评分。</summary>
    public static IReadOnlyList<ComparisonRow> Rows(IEnumerable<PluginRow> plugins, IReadOnlyDictionary<string, PluginUsage> usage, string tag) =>
        Comparable(plugins)
            .Where(p => tag == Uncategorized ? p.Tags.Count == 0 : p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Select(p => new ComparisonRow(p, usage.GetValueOrDefault(p.Key)))
            .OrderByDescending(r => r.Usage?.Projects ?? 0)
            .ThenByDescending(r => r.Plugin.User.Rating)
            .ThenBy(r => r.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static ComparisonSummary Summarize(IReadOnlyCollection<ComparisonRow> rows, DateTime nowUtc, int months = 12)
    {
        var cutoff = nowUtc.AddMonths(-months);
        return new ComparisonSummary(
            rows.Count,
            rows.Count(r => r.Usage != null && r.Usage.LastUsedUtc >= cutoff),
            rows.Count(r => r.Usage == null),
            rows.Count(r => r.Plugin.User.Rating == 0));
    }
}

// ---------- 插件链 ----------

/// <summary>一步里的一款插件：首选或备选。插件卸载后靠名称还能显示。</summary>
public sealed record ChainPick(string PluginKey, string PluginName, string Note = "");

/// <summary>
/// 链上的一步。首选插件直接放在这一步上，没有首选的插件时换用备选，备选按顺序排。
/// 同一款插件可以出现多次。
/// </summary>
public sealed record ChainStep(string PluginKey, string PluginName, string Note = "", IReadOnlyList<ChainPick>? Alternates = null)
{
    public IReadOnlyList<ChainPick> Backups => Alternates ?? [];

    public ChainPick Primary => new(PluginKey, PluginName, Note);

    public IEnumerable<ChainPick> AllPicks => Backups.Prepend(Primary);

    public static ChainStep From(ChainPick primary, IEnumerable<ChainPick> alternates) =>
        new(primary.PluginKey, primary.PluginName, primary.Note, alternates.ToList());
}

/// <summary>混音笔记（以前叫插件链）。FolderId 为空表示放在最外层。Role 是这条笔记对应的通道类型。</summary>
public sealed record PluginChain(
    long Id,
    string Name,
    string Note,
    IReadOnlyList<ChainStep> Steps,
    long? FolderId = null,
    IReadOnlyList<string>? Tags = null,
    string Source = "",
    ChannelRole Role = ChannelRole.Track)
{
    public IReadOnlyList<string> TagList => Tags ?? [];
}

/// <summary>混音笔记的文件夹，可以多层。</summary>
public sealed record NoteFolder(long Id, long? ParentId, string Name);

public static class MixNotes
{
    /// <summary>笔记的预设标签。前 7 个和插件的适合音色是同一套词。</summary>
    public static IReadOnlyList<string> PresetTags { get; } = ["人声", "贝斯", "鼓", "吉他", "键盘", "管弦", "合成器", "总线", "母带"];

    /// <summary>从最外层到这个文件夹的名称，用于备份和显示路径。找不到时返回空。</summary>
    public static IReadOnlyList<string> PathOf(long? folderId, IReadOnlyList<NoteFolder> folders)
    {
        var byId = folders.ToDictionary(f => f.Id);
        var names = new List<string>();
        var seen = new HashSet<long>();
        while (folderId is { } id && byId.TryGetValue(id, out var folder) && seen.Add(id))
        {
            names.Add(folder.Name);
            folderId = folder.ParentId;
        }
        names.Reverse();
        return names;
    }

    /// <summary>这个文件夹和它下面的所有文件夹。</summary>
    public static HashSet<long> Subtree(long folderId, IReadOnlyList<NoteFolder> folders)
    {
        var result = new HashSet<long> { folderId };
        bool added;
        do
        {
            added = false;
            foreach (var folder in folders)
            {
                if (folder.ParentId is { } parent && result.Contains(parent) && result.Add(folder.Id))
                    added = true;
            }
        } while (added);
        return result;
    }
}

public enum ChainStepState { Installed, Missing, Blacklisted, Instrument }

public sealed record ResolvedStep(ChainStep Step, ChainStepState State, PluginRow? Plugin);

public static class ChainReport
{
    /// <summary>先按标识找，找不到再按名称：重新扫描后标识可能变，名称通常不变。</summary>
    public static IReadOnlyList<ResolvedStep> Resolve(PluginChain chain, IEnumerable<PluginRow> plugins)
    {
        var rows = plugins.ToList();
        var byKey = rows.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var byName = rows.Where(p => !p.IsMissing).GroupBy(p => Normalizer.Name(p.Name)).ToDictionary(g => g.Key, g => g.First());

        return chain.Steps.Select(step =>
        {
            var (state, plugin) = Find(step.PluginKey, step.PluginName, byKey, byName);
            return new ResolvedStep(step, state, plugin);
        }).ToList();
    }

    /// <summary>找一款插件的安装状态，首选和备选都用它。</summary>
    public static (ChainStepState State, PluginRow? Plugin) Resolve(ChainPick pick, IEnumerable<PluginRow> plugins)
    {
        var rows = plugins.ToList();
        var byKey = rows.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var byName = rows.Where(p => !p.IsMissing).GroupBy(p => Normalizer.Name(p.Name)).ToDictionary(g => g.Key, g => g.First());
        return Find(pick.PluginKey, pick.PluginName, byKey, byName);
    }

    private static (ChainStepState, PluginRow?) Find(string key, string name, Dictionary<string, PluginRow> byKey, Dictionary<string, PluginRow> byName)
    {
        var plugin = byKey.GetValueOrDefault(key);
        if (plugin is not { IsMissing: false })
            plugin = byName.GetValueOrDefault(Normalizer.Name(name)) ?? plugin;
        // 音源不在这个软件里显示。只有用户把插件改判成音源，或备份带来时才会遇到。
        var state = plugin?.Kind == PluginKind.Instrument ? ChainStepState.Instrument
            : plugin is null or { IsMissing: true } ? ChainStepState.Missing
            : plugin.User.Hidden ? ChainStepState.Blacklisted
            : ChainStepState.Installed;
        return (state, plugin);
    }
}

// ---------- 授权和换机 ----------

public enum LicenseKind { Unknown, ILok, Waves, Serial, Account, Other }

public static class LicenseMigration
{
    public static string KindText(LicenseKind kind) => kind switch
    {
        LicenseKind.ILok => "iLok",
        LicenseKind.Waves => "Waves 授权",
        LicenseKind.Serial => "序列号",
        LicenseKind.Account => "账号登录",
        LicenseKind.Other => "其他",
        _ => "未填",
    };

    private static string DeactivateHint(LicenseKind kind) => kind switch
    {
        LicenseKind.ILok => "在 iLok License Manager 里停用这台机器上的激活",
        LicenseKind.Waves => "在 Waves Central 里把授权移回云端或 U 盘",
        _ => "在厂家的授权工具或账户页面里停用这台机器",
    };

    /// <summary>"Studio, 笔记本" 拆成机器名，判断激活里有没有这台电脑。</summary>
    public static bool IsActivatedOn(WishItem item, string machine) =>
        item.ActivatedOn.Split([',', '，', '、', ';', '；', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(machine, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 生成换机清单。清单会保存成明文文件，所以只写插件名、授权方式和激活情况，
    /// 不写账号、密码和序列号。
    /// </summary>
    public static string BuildChecklist(IEnumerable<WishItem> items, string machine, DateTime now)
    {
        var purchased = items.Where(i => i.Status == WishStatus.Purchased)
            .OrderBy(i => i.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var here = purchased.Where(i => IsActivatedOn(i, machine)).ToList();
        var elsewhere = purchased.Where(i => i.ActivatedOn.Trim().Length > 0 && !IsActivatedOn(i, machine)).ToList();
        var noActivation = purchased.Where(i => i.ActivatedOn.Trim().Length == 0 && i.LicenseKind != LicenseKind.Unknown).ToList();
        var unknown = purchased.Where(i => i.ActivatedOn.Trim().Length == 0 && i.LicenseKind == LicenseKind.Unknown).ToList();

        var text = new StringBuilder();
        text.AppendLine($"What Plugin 换机清单，生成于 {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}，当前电脑：{machine}");
        text.AppendLine($"共 {purchased.Count} 款已购插件。这份清单不含账号、密码和序列号。");

        Section(text, $"换机前先在这台电脑上停用（{here.Count} 款）", "没有停用就重装系统或硬盘损坏，授权可能要找厂家重置。", here, item =>
            $"{Title(item)}：{DeactivateHint(item.LicenseKind)}{Remaining(item)}");
        Section(text, $"激活在其他机器（{elsewhere.Count} 款）", "新电脑要占用激活名额时，先确认还够用。", elsewhere, item =>
            $"{Title(item)}：激活在 {item.ActivatedOn.ReplaceLineEndings(" ").Trim()}{Remaining(item)}");
        Section(text, $"没记录激活，到新电脑上重新登录或输入（{noActivation.Count} 款）", null, noActivation, item =>
            $"{Title(item)}：{KindText(item.LicenseKind)}{SavedLogin(item)}{Remaining(item)}");
        Section(text, $"还没填授权方式（{unknown.Count} 款）", "换机前到“已购账号”里补上，否则不知道要不要先停用。", unknown, item =>
            $"{Title(item)}{SavedLogin(item)}");
        return text.ToString();
    }

    private static void Section(StringBuilder text, string title, string? hint, List<WishItem> items, Func<WishItem, string> line)
    {
        if (items.Count == 0)
            return;
        text.AppendLine();
        text.AppendLine(title);
        if (hint != null)
            text.AppendLine(hint);
        foreach (var item in items)
            text.AppendLine("[ ] " + line(item));
    }

    private static string Title(WishItem item) => item.Vendor.Length > 0 ? $"{item.Vendor} {item.Name}" : item.Name;

    private static string Remaining(WishItem item) => item.ActivationsLeft is { } left ? $"（还剩 {left} 次激活）" : "";

    /// <summary>只说存没存，不写内容。</summary>
    private static string SavedLogin(WishItem item) =>
        item.SecretsLocked ? "（已存的登录信息在另一个 Windows 账号下，这里解不开）"
        : item.HasLogin ? "（登录信息已存在“已购账号”）"
        : "（还没存登录信息）";
}

// ---------- 购买辅助 ----------

public sealed record PurchaseAdvice(string Tag, int Owned, int? IdleOverYear, IReadOnlyList<string> MostUsed);

public static class PurchaseAdvisor
{
    public const string AutoCategory = "自动判断";

    /// <summary>
    /// 这款插件属于哪一类。一款插件常有多个分类（压缩也算动态），取已拥有数量最少的那个，
    /// 否则提示总是落在“动态”这种大类上，没有参考价值。判断为音源时返回 null。
    /// </summary>
    public static string? FindCategory(string name, string vendor, IReadOnlyList<ComparisonCategory> owned)
    {
        var classification = Classifier.Classify(name, vendor, "", null);
        if (classification.Kind == PluginKind.Instrument)
            return null;
        return NarrowestTag(classification.Tags.Where(t => !Classifier.InstrumentTags.Contains(t)), owned);
    }

    public static string? NarrowestTag(IEnumerable<string> tags, IReadOnlyList<ComparisonCategory> owned)
    {
        var list = tags.ToList();
        return list.Select(t => (Tag: t, Count: owned.FirstOrDefault(c => c.Tag.Equals(t, StringComparison.OrdinalIgnoreCase))?.Count ?? 0))
            .OrderBy(t => t.Count == 0)
            .ThenBy(t => t.Count)
            .Select(t => t.Tag)
            .FirstOrDefault();
    }

    /// <param name="category">用户选的类别；空或“自动判断”时按名称判断。</param>
    public static PurchaseAdvice? Advise(
        string name, string vendor, string? category,
        IReadOnlyList<PluginRow> plugins, IReadOnlyDictionary<string, PluginUsage> usage, DateTime nowUtc)
    {
        var owned = PluginComparison.Categories(plugins);
        var tag = string.IsNullOrEmpty(category) || category == AutoCategory ? FindCategory(name, vendor, owned) : category;
        if (tag == null)
            return null;

        var rows = PluginComparison.Rows(plugins, usage, tag);
        // 没扫描过工程时不知道用没用过，不能说成闲置。
        int? idle = usage.Count == 0 ? null : rows.Count - PluginComparison.Summarize(rows, nowUtc).UsedRecently;
        var mostUsed = rows.Where(r => r.Usage != null).Take(3).Select(r => r.Plugin.Name).ToList();
        return new PurchaseAdvice(tag, rows.Count, idle, mostUsed);
    }

    public static string Describe(PurchaseAdvice? advice)
    {
        if (advice == null)
            return "判断不出这款插件的类别，选一个类别后可以看到你已有的同类插件。";
        if (advice.Owned == 0)
            return $"你还没有“{advice.Tag}”类的插件。";
        var text = $"你已有 {advice.Owned} 款“{advice.Tag}”类插件";
        if (advice.IdleOverYear is > 0)
            text += $"，其中 {advice.IdleOverYear} 款过去一年没进工程";
        text += "。";
        if (advice.MostUsed.Count > 0)
            text += "用得最多的是 " + string.Join("、", advice.MostUsed) + "。";
        return text;
    }

    public static bool ReachedTarget(WishItem item) => item is { Price: { } price, TargetPrice: { } target } && price <= target;

    public static string DescribePrice(WishItem item)
    {
        if (item.Price is not { } price)
            return item.TargetPrice == null && item.LowestPrice == null ? "" : "填上现价后可以和目标价、历史最低价比较。";

        var parts = new List<string>();
        if (item.TargetPrice is { } target)
        {
            parts.Add(price <= target
                ? "已到目标价"
                : $"还差 {Money.Format(price - target, item.Currency)} 到目标价");
        }
        if (item.LowestPrice is { } lowest)
        {
            parts.Add(price <= lowest
                ? "现价不高于你记录的历史最低价"
                : $"比历史最低价贵 {Money.Format(price - lowest, item.Currency)}");
        }
        return string.Join("，", parts);
    }

    /// <summary>查价只是打开一次搜索，不抓取价格。</summary>
    public static string PriceSearchUrl(string name, string vendor)
    {
        var query = $"{vendor} {name} plugin price deal".Trim();
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
    }
}
