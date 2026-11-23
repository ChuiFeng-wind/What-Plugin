namespace WhatPlugin.Core;

/// <summary>工程列表的一行。</summary>
public sealed record ProjectListRow(ProjectEntry Entry)
{
    public string Name => Entry.Name;
    public string Folder => Entry.Folder;
    public string HostText => ProjectParser.HostName(Entry.Host);
    public DateTime Modified => Entry.ModifiedUtc;
    public string ModifiedText => Entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public int Versions => Entry.Versions;
    public int EffectCount => Entry.EffectCount;
    public int MissingCount => Entry.MissingCount;
    public bool Failed => Entry.Status == ProjectScanStatus.Failed;

    /// <summary>读取失败或有缺失时用警示色。</summary>
    public bool Warn => Failed || MissingCount > 0;

    public string MissingText => Failed ? "读取失败" : MissingCount > 0 ? $"缺 {MissingCount} 款" : "齐全";

    public string SearchText => (Name + " " + Entry.Path + " " + string.Join(' ', Entry.Plugins.Select(p => p.Name))).ToLowerInvariant();
}

/// <summary>工程详情里的一款插件。缺失的插件以前移进垃圾桶并卸载过时，带上当时的评价。</summary>
public sealed record ProjectLineRow(ProjectPluginLine Line, PluginRow? Remembered)
{
    public string Name => Line.Name;
    public string InstancesText => Line.Instances > 1 ? $"× {Line.Instances}" : "";
    public bool IsMissing => Line.State == ProjectPluginState.Missing;
    public bool ShowRemembered => IsMissing && Remembered != null;
    public string RememberedText => Remembered == null ? "" : "以前移进垃圾桶并卸载了。" + TrashMemory.Describe(Remembered);
}

/// <summary>缺失汇总的一行。</summary>
public sealed record MissingListRow(MissingPluginSummary Summary, PluginRow? Remembered)
{
    public string Name => Summary.Name;

    /// <summary>以前移进垃圾桶并卸载过的，名字后面标一下。</summary>
    public string NameText => Remembered == null ? Name : Name + "  · 曾差评";
    public string? RememberedText => Remembered == null ? null : "以前移进垃圾桶并卸载了。" + TrashMemory.Describe(Remembered);
    public int ProjectCount => Summary.Projects.Count;
    public string LatestText => Summary.Projects[0].Name;
    public DateTime Latest => Summary.Projects[0].ModifiedUtc;
    public string LatestDateText => Latest.ToLocalTime().ToString("yyyy-MM-dd");
    public string SearchText => (Name + " " + string.Join(' ', Summary.Projects.Select(p => p.Name))).ToLowerInvariant();
}

/// <summary>工程详情面板的内容。</summary>
public sealed record ProjectDetail(string Title, string Meta, string Path, string? Error, IReadOnlyList<ProjectLineRow> Lines, string Notes);

/// <summary>工程页的列表、文字和筛选（Windows 版和跨平台版共用）。工程文件只读。</summary>
public static class ProjectListing
{
    /// <summary>读出工程报告，并记下每个工程缺几款（使用统计用，失败也不影响页面）。</summary>
    public static ProjectReportResult Load(PluginDatabase database)
    {
        var report = ProjectReport.Build(database.LoadProjectFiles(), database.LoadPlugins(), database.LoadIgnoredProjectNames());
        try
        {
            // 忽略的也算在内：把缺失的插件标成忽略，不算“补齐了”。
            foreach (var project in report.Projects.Where(p => p.Status == ProjectScanStatus.Ok))
                database.RecordProjectMissing(project.Key, project.Plugins.Count(l => l.State is ProjectPluginState.Missing or ProjectPluginState.Ignored));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
        return report;
    }

    /// <summary>搜索框：空格分开的每个词都要出现。text 已经是小写。</summary>
    public static bool Matches(string search, string text)
    {
        var words = search.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(text.Contains);
    }

    public static IReadOnlyList<MissingListRow> MissingRows(ProjectReportResult report, PluginNameMatcher remembered) =>
        report.Missing.Select(m => new MissingListRow(m, TrashMemory.Find(m.Name, remembered))).ToList();

    public static string Status(int projects, int withMissing, int missingPlugins) =>
        projects == 0 ? "工程文件只会被读取，不会被修改。" : $"{projects} 个工程，{withMissing} 个有缺失，缺失插件 {missingPlugins} 种";

    public static string DescribeScan(ProjectScanSummary s, int projects)
    {
        var parts = new List<string>
        {
            s.Cancelled ? "已停止" : "工程读取完成",
            $"找到 {s.Found} 个工程文件，合并为 {projects} 个工程",
            $"本次读取 {s.Parsed} 个，未变化 {s.Unchanged} 个",
        };
        if (s.Failed > 0)
            parts.Add($"{s.Failed} 个读取失败");
        if (s.Removed > 0)
            parts.Add($"移除 {s.Removed} 条已不存在的记录");
        return string.Join("，", parts);
    }

    /// <summary>列表为空时的提示，按最可能的原因说。</summary>
    public static string EmptyText(bool noFolders, int projects, bool summary, int missingPlugins, bool missingOnly, bool anyMissing, bool folderFiltered) =>
        noFolders ? "点右上角“添加文件夹”，选择存放工程的位置"
        : projects == 0 ? "这些文件夹里没有找到工程"
        : summary && missingPlugins == 0 ? "没有缺失的插件"
        : missingOnly && !anyMissing ? "所有工程需要的插件都装了"
        : folderFiltered ? "这个文件夹里没有符合条件的工程"
        : "没有符合搜索的工程";

    public static ProjectDetail Detail(ProjectEntry entry, PluginNameMatcher remembered)
    {
        var meta = $"{ProjectParser.HostName(entry.Host)}，最近修改 {entry.ModifiedUtc.ToLocalTime():yyyy-MM-dd}"
            + (entry.Versions > 1 ? $"，共 {entry.Versions} 个版本，下面是最新版本" : "");
        var lines = entry.Plugins.Where(p => p.State is ProjectPluginState.Installed or ProjectPluginState.Missing)
            .Select(p => new ProjectLineRow(p, p.State == ProjectPluginState.Missing ? TrashMemory.Find(p.Name, remembered) : null))
            .ToList();
        var stock = entry.Plugins.Where(p => p.State == ProjectPluginState.Stock).Select(p => p.Name).ToList();
        var ignored = entry.Plugins.Where(p => p.State == ProjectPluginState.Ignored).Select(p => p.Name).ToList();
        var notes = new List<string>();
        if (lines.Count == 0 && entry.Error == null)
            notes.Add("这个工程没有用到第三方效果器。");
        if (stock.Count > 0)
            notes.Add($"宿主自带（不算缺失）：{string.Join("、", stock)}");
        if (ignored.Count > 0)
            notes.Add($"已设为不算缺失：{string.Join("、", ignored)}");
        return new ProjectDetail(entry.Name, meta, entry.Path, entry.Error == null ? null : "无法读取：" + entry.Error, lines, string.Join("\n", notes));
    }

    public static string MissingMeta(MissingListRow row) => $"{row.ProjectCount} 个工程的最新版本需要它";

    public static IReadOnlyList<string> MissingProjects(MissingListRow row) =>
        row.Summary.Projects.Select(p => $"{p.ModifiedUtc.ToLocalTime():yyyy-MM-dd}  {p.Name}").ToList();

    public static string Ignored(string name) => $"“{name}”不再算作缺失，可以点“恢复忽略的名称”撤销";

    public const string IgnoredCleared = "已恢复所有忽略的名称";
}

/// <summary>闲置报告的一行。</summary>
public sealed record IdleListRow(IdlePlugin Idle)
{
    public PluginRow Row => Idle.Plugin;
    public string Name => Row.Name;
    public string Vendor => Row.Vendor.Length > 0 ? Row.Vendor : "未知";
    public string TagsText => Row.Tags.Count > 0 ? string.Join("、", Row.Tags) : "未分类";
    public int Rating => Row.User.Rating;
    public string RatingText => Rating > 0 ? new string('★', Rating) : "";
    public int ProjectCount => Idle.Usage?.Projects ?? 0;
    public DateTime LastUsed => Idle.Usage?.LastUsedUtc ?? DateTime.MinValue;
    public string LastUsedText => Idle.Usage == null ? "从未出现" : Idle.Usage.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public string SearchText => (Name + " " + Row.Vendor + " " + TagsText).ToLowerInvariant();
}

public enum IdleFilter { All, NeverUsed, LongUnused }

/// <summary>闲置报告的文字、筛选和移入垃圾桶（两边共用）。只是在软件里隐藏，不动插件文件。</summary>
public static class IdleListing
{
    public static readonly IReadOnlyList<int> MonthOptions = [6, 12, 24, 36];

    public const int DefaultMonths = 12;

    public static string MonthLabel(int months) => $"{months} 个月";

    public static bool Matches(IdleListRow row, IdleFilter filter, string search) =>
        filter switch
        {
            IdleFilter.NeverUsed => row.Idle.Reason == IdleReason.NeverUsed,
            IdleFilter.LongUnused => row.Idle.Reason == IdleReason.LongUnused,
            _ => true,
        } && ProjectListing.Matches(search, row.SearchText);

    public static string Describe(int projects, IReadOnlyList<IdlePlugin> idle, int months)
    {
        var never = idle.Count(i => i.Reason == IdleReason.NeverUsed);
        return projects == 0
            ? "闲置报告依据工程扫描结果"
            : $"依据 {projects} 个工程：{never} 款从未出现，{idle.Count - never} 款超过 {months} 个月没用";
    }

    public static string EmptyText(int projects, int idle) =>
        projects == 0 ? "先到“工程”页扫描工程，才知道哪些插件没用过"
        : idle == 0 ? "没有闲置的插件"
        : "没有符合筛选的插件";

    public static string ButtonText(int selected) =>
        selected > 0 ? $"把选中的 {selected} 款移入垃圾桶…" : "把选中的插件移入垃圾桶…";

    public static string Prompt(int count) =>
        $"把选中的 {count} 款插件移入垃圾桶。只是在本软件中隐藏，文件不动，DAW 里仍可使用。\n移入原因（选填）：";

    /// <summary>移入垃圾桶。没填原因时保留之前写过的。</summary>
    public static void MoveToTrash(PluginDatabase database, IEnumerable<PluginRow> rows, string reason)
    {
        var text = reason.Trim();
        foreach (var row in rows)
            database.SaveUserData(row.Id, row.User with
            {
                Hidden = true,
                BlacklistReason = text.Length > 0 ? text : row.User.BlacklistReason,
            });
    }

    public static string Moved(int count) => $"已把 {count} 款插件移入垃圾桶，可在左侧“垃圾桶”查看或恢复";
}
