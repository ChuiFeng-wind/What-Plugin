using System.Text;

namespace WhatPlugin.Core;

/// <summary>导出、导入时的一个勾选项。Parent 不为空时缩进显示，父项没勾时它不能勾。</summary>
public sealed record PartOption(string Id, string Label, string Detail = "", bool Enabled = true, bool Checked = true, string? Parent = null);

/// <summary>导出时选的用途和内容。Share 为 true 时 Parts 是 "reviews"、"notes"，否则是 BackupParts 的名称。</summary>
public sealed record ExportChoice(bool Share, IReadOnlySet<string> Parts, string Password);

/// <summary>
/// 一组勾选项的状态，Windows 版和跨平台版的勾选窗口共用。
/// 每项记着“想要的状态”：父项没勾时子项暂时不能勾，父项再勾上时子项恢复原来的状态。
/// “全选”“全不选”“反选”只对本身能勾的项起作用。
/// </summary>
public sealed class PartSelection
{
    private readonly Dictionary<string, bool> _wanted = [];

    public PartSelection(IReadOnlyList<PartOption> options)
    {
        Options = options;
        foreach (var option in options)
            _wanted[option.Id] = option.Enabled && option.Checked;
    }

    public IReadOnlyList<PartOption> Options { get; }

    public event Action? Changed;

    /// <summary>这一项现在能不能勾：本身可用，而且父项（如果有）已勾上。</summary>
    public bool IsEnabled(string id)
    {
        var option = Options.First(o => o.Id == id);
        if (!option.Enabled)
            return false;
        return option.Parent == null || Options.All(o => o.Id != option.Parent) || IsChecked(option.Parent);
    }

    public bool IsChecked(string id) => IsEnabled(id) && _wanted[id];

    public void Set(string id, bool value)
    {
        if (!IsEnabled(id) || _wanted[id] == value)
            return;
        _wanted[id] = value;
        Changed?.Invoke();
    }

    public void SelectAll() => Apply(_ => true);

    public void SelectNone() => Apply(_ => false);

    /// <summary>子项按它自己想要的状态反转：父项没勾时它被暂时取消，不该把这个当成它的状态。</summary>
    public void Invert() => Apply(current => !current);

    private void Apply(Func<bool, bool> value)
    {
        foreach (var option in Options.Where(o => o.Enabled))
            _wanted[option.Id] = value(_wanted[option.Id]);
        Changed?.Invoke();
    }

    public IReadOnlySet<string> CheckedIds => Options.Where(o => IsChecked(o.Id)).Select(o => o.Id).ToHashSet();

    public bool Any => Options.Any(o => IsChecked(o.Id));
}

/// <summary>左下角“导出备份”“导入备份”的选项、文件名和说明文字，两个界面共用。</summary>
public static class TransferListing
{
    public const string ExportModeSetting = "export.mode";
    public const string ExportPartsSetting = "export.parts";
    public const string ExportSharePartsSetting = "export.shareParts";
    public const string SecretsId = nameof(BackupParts.Secrets);
    public const string ShareReviewsId = "reviews";
    public const string ShareNotesId = "notes";
    public const int MinPasswordLength = 6;

    public const string ExportMessage = "先选用途，再勾要导出的内容。";
    public const string SelfHint = "换电脑、重装或留底用。导入时会覆盖本机相同的条目；没勾的部分，导入时本机内容不动。";
    public const string ShareHint = "发给别人看。不含工程名、路径和账号密码；对方导入后单独存放，不会覆盖对方自己的内容。下一步挑要分享的插件和笔记，两样都勾时存成一个文件。";
    public const string PasswordLabel = "备份密码（用来加密账号、密码和序列号）";
    public const string PasswordNote = "导入时要输入它。它不会保存在任何地方，忘了就解不开账号密码（其他内容不受影响）。";
    public const string AskPasswordMessage = "这份备份里的账号、密码和序列号是加密的，输入导出时设的备份密码才能恢复。跳过的话，其他内容照常导入，本机的账号密码保持不变。";
    public const string ImportBackupMessage = "勾选要导入的内容。相同条目现有的内容会被覆盖；没勾的部分本机内容不动。文件夹和忽略列表只增不减，这台电脑上不存在的文件夹不会添加。";
    public const string ImportFilterName = "WhatPlugin 备份和分享";

    /// <summary>导入时能选的文件扩展名。</summary>
    public static IReadOnlyList<string> ImportExtensions { get; } = [".json", ShareBundles.Extension, ReviewShare.Extension, NoteShare.Extension];

    public static string SafeFileName(string text) =>
        string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c));

    private static string Prefixed(string author, string name)
    {
        var safe = SafeFileName(author);
        return (safe.Length > 0 ? safe + "-" : "") + name;
    }

    // ---------- 导出：选项 ----------

    /// <summary>“给自己备份”的选项，数字都从数据库数出来。</summary>
    public static IReadOnlyList<PartOption> SelfOptions(PluginDatabase database)
    {
        var remembered = Enum.TryParse<BackupParts>(database.GetSetting(ExportPartsSetting), out var saved) ? saved.Valid() : BackupParts.All;
        var plugins = database.LoadPlugins();
        var reviewed = plugins.Count(p => UserDataBackup.KeepParts(p.User, true, false).HasData);
        var organized = plugins.Count(p => UserDataBackup.KeepParts(p.User, false, true).HasData);
        var notes = database.LoadChains().Count;
        var wishes = database.LoadWishes();
        var purchased = wishes.Count(w => w.Status == WishStatus.Purchased);
        var secretCount = wishes.Count(w => w.Account.Length > 0 || w.Password.Length > 0 || w.Serial.Length > 0 || w.SecretsLocked)
            + database.LoadPlatformAccounts().Count;

        PartOption Self(BackupParts part, string label, string detail, string? parent = null) =>
            new(part.ToString(), label, detail, Checked: remembered.HasFlag(part), Parent: parent);
        return
        [
            Self(BackupParts.Reviews, "插件评价", $"评分、感受、我的标签、混音属性、适合音色（{reviewed} 款）"),
            Self(BackupParts.Organize, "插件整理", $"分类、厂家、标签修正、隐藏、黑名单原因、获取方式（{organized} 款）"),
            Self(BackupParts.Notes, "混音笔记", $"文件夹、标签和路由（{notes} 条）"),
            Self(BackupParts.Wishes, "心愿单和已购账号", $"心愿 {wishes.Count - purchased} 条，已购 {purchased} 条"),
            Self(BackupParts.Secrets, "账号、密码和序列号", $"{secretCount} 条，含厂家账号；用备份密码加密", nameof(BackupParts.Wishes)),
            Self(BackupParts.SharedReviews, "别人分享的评价", $"{database.LoadReviewSources().Count} 位分享者，以及采用记录"),
            Self(BackupParts.Settings, "设置", "工程文件夹、VST2 文件夹、忽略的插件、主题和界面状态等"),
            Self(BackupParts.Usage, "使用记录和成就", $"已解锁成就 {database.LoadAchievements().Count} 个"),
            Self(BackupParts.Uninstalls, "卸载历史", $"{database.LoadUninstallHistory().Count} 条"),
        ];
    }

    /// <summary>“分享给别人”的选项。rows 是插件库里的插件（不含乐器）。</summary>
    public static IReadOnlyList<PartOption> ShareOptions(PluginDatabase database, IEnumerable<PluginRow> rows)
    {
        var remembered = (database.GetSetting(ExportSharePartsSetting) ?? $"{ShareReviewsId},{ShareNotesId}").Split(',');
        var shareable = rows.Count(ReviewShare.HasReview);
        var notes = database.LoadChains().Count;
        return
        [
            new(ShareReviewsId, "插件评价",
                shareable > 0 ? $"评分、感受和标签，下一步挑插件（写过评价的 {shareable} 款）" : "还没有写过评价，先给插件打分或写点感受",
                Enabled: shareable > 0, Checked: remembered.Contains(ShareReviewsId)),
            new(ShareNotesId, "混音笔记",
                notes > 0 ? $"下一步按文件夹勾选，可选带上路由，不含工程名和路径（共 {notes} 条）" : "还没有混音笔记",
                Enabled: notes > 0, Checked: remembered.Contains(ShareNotesId)),
        ];
    }

    public static bool RememberShareMode(PluginDatabase database) => database.GetSetting(ExportModeSetting) == "share";

    /// <summary>记住这次的选择，下次打开时还是这样。</summary>
    public static void Remember(PluginDatabase database, ExportChoice choice)
    {
        database.SetSetting(ExportModeSetting, choice.Share ? "share" : "self");
        if (choice.Share)
            database.SetSetting(ExportSharePartsSetting, string.Join(",", choice.Parts));
        else
            database.SetSetting(ExportPartsSetting, ((int)ToParts(choice.Parts)).ToString());
    }

    public static BackupParts ToParts(IEnumerable<string> ids) =>
        ids.Aggregate(BackupParts.None, (all, id) => all | Enum.Parse<BackupParts>(id)).Valid();

    /// <summary>勾了账号密码时才要备份密码。</summary>
    public static bool NeedsPassword(bool share, IReadOnlySet<string> parts) => !share && parts.Contains(SecretsId);

    /// <summary>备份密码不合格的原因；合格返回 null。</summary>
    public static string? CheckPassword(string password, string confirm) =>
        password.Length < MinPasswordLength ? $"备份密码至少 {MinPasswordLength} 位，或者不勾“账号、密码和序列号”"
        : password != confirm ? "两次输入的备份密码不一样"
        : null;

    // ---------- 导出：文件名和结果 ----------

    public static string BackupFileName(BackupParts parts, DateTime now) =>
        (parts | BackupParts.Secrets) == BackupParts.All ? $"WhatPlugin-备份-{now:yyyyMMdd}.json" : $"WhatPlugin-部分备份-{now:yyyyMMdd}.json";

    public static string ShareFileName(string author) => Prefixed(author, $"评价和混音笔记{ShareBundles.Extension}");

    public static string ReviewFileName(string author) => Prefixed(author, $"插件评价{ReviewShare.Extension}");

    public static string NoteFileName(string author) => Prefixed(author, $"混音笔记{NoteShare.Extension}");

    /// <summary>导出备份后状态栏的文字。locked 是解不开、没有导出的密码数。</summary>
    public static string Exported(BackupContent content, BackupParts parts, int locked)
    {
        var done = new List<string>();
        if (parts.HasFlag(BackupParts.Reviews) || parts.HasFlag(BackupParts.Organize))
            done.Add($"{content.Plugins.Count} 款插件的" + (parts.HasFlag(BackupParts.Reviews) && parts.HasFlag(BackupParts.Organize) ? "评价和整理" : parts.HasFlag(BackupParts.Reviews) ? "评价" : "整理"));
        if (parts.HasFlag(BackupParts.Notes))
            done.Add($"{content.Chains.Count} 条混音笔记");
        if (parts.HasFlag(BackupParts.Wishes))
            done.Add($"{content.Wishes.Count} 条心愿和已购");
        if (parts.HasFlag(BackupParts.SharedReviews))
            done.Add($"{content.Reviews?.Sources?.Count ?? 0} 位分享者的评价");
        if (parts.HasFlag(BackupParts.Settings))
            done.Add("设置");
        if (parts.HasFlag(BackupParts.Usage))
            done.Add("使用记录和成就");
        if (parts.HasFlag(BackupParts.Uninstalls))
            done.Add($"{content.Uninstalls?.Count ?? 0} 条卸载历史");
        var secrets = content.Secrets == null
            ? "，没有包含账号密码"
            : $"，账号密码 {content.Secrets.WishCount} 条、厂家账号 {content.Secrets.PlatformCount} 个已用备份密码加密"
              + (locked > 0 ? $"（{locked} 条密码是{WishListing.ForeignKeyOwner}加密的，解不开，没有导出）" : "");
        return "已导出 " + string.Join("、", done) + secrets;
    }

    public static string ReviewsExported(ReviewFile file, string path) => $"已导出 {file.Reviews.Count} 条评价到 {path}";

    public static string NotesExported(NoteFile file, string path) => $"已导出 {file.Notes.Count} 条混音笔记到 {path}";

    public static string ShareExported(ReviewFile reviews, NoteFile notes, string path) =>
        $"已导出 {reviews.Reviews.Count} 条评价和 {notes.Notes.Count} 条混音笔记到 {path}";

    // ---------- 导入备份 ----------

    public static IReadOnlyList<PartOption> BackupOptions(LoadedBackup backup)
    {
        var content = backup.Content;
        var settings = content.Settings;
        PartOption Part(BackupParts part, string label, string detail, string? parent = null)
        {
            var present = content.Parts.HasFlag(part) && (part != BackupParts.Secrets || backup.HasSecrets);
            return new(part.ToString(), label, present ? detail : "这份备份里没有", Enabled: present, Parent: parent);
        }
        return
        [
            Part(BackupParts.Reviews, "插件评价", $"{content.Plugins.Count(p => UserDataBackup.KeepParts(p.ToUserData(), true, false).HasData)} 款插件的评分、感受、标签等"),
            Part(BackupParts.Organize, "插件整理", $"{content.Plugins.Count(p => UserDataBackup.KeepParts(p.ToUserData(), false, true).HasData)} 款插件的分类、厂家、隐藏、获取方式等"),
            Part(BackupParts.Notes, "混音笔记", $"{content.Chains.Count} 条，同名的会被备份内容替换"),
            Part(BackupParts.Wishes, "心愿单和已购账号", $"心愿和已购 {content.Wishes.Count} 条"),
            Part(BackupParts.Secrets, "账号、密码和序列号", "加密保存，下一步输入备份密码；不勾的话本机的账号密码保持不变", nameof(BackupParts.Wishes)),
            Part(BackupParts.SharedReviews, "别人分享的评价", $"{content.Reviews?.Sources?.Count ?? 0} 位分享者，采用记录 {content.Reviews?.Adoptions?.Count ?? 0} 条"),
            Part(BackupParts.Settings, "设置", settings == null ? "没有内容" : $"设置 {settings.Values?.Count ?? 0} 项，工程文件夹 {settings.ProjectFolders?.Count ?? 0} 个，VST2 文件夹 {settings.Vst2Folders?.Count ?? 0} 个，忽略的插件 {settings.IgnoredProjectPlugins?.Count ?? 0} 个"),
            Part(BackupParts.Usage, "使用记录和成就", $"使用记录 {content.Usage?.Events?.Count ?? 0} 条，成就 {content.Usage?.Achievements?.Count ?? 0} 个"),
            Part(BackupParts.Uninstalls, "卸载历史", $"{content.Uninstalls?.Count ?? 0} 条"),
        ];
    }

    /// <summary>试着用这个密码解开账号密码。解开返回 null 和内容，否则返回错误提示。</summary>
    public static (string? Error, BackupSecrets? Secrets) TryDecrypt(LoadedBackup backup, string password)
    {
        try
        {
            return (null, UserDataBackup.DecryptSecrets(backup, password));
        }
        catch (BackupPasswordException error)
        {
            return (error.Message, null);
        }
        catch (FormatException error)
        {
            return (error.Message, null);
        }
    }

    /// <summary>导入备份后状态栏的文字。</summary>
    public static string Imported(RestoreSummary result, BackupParts parts, BackupContent content, bool withSecrets)
    {
        var done = new List<string>();
        if (parts.HasFlag(BackupParts.Reviews) || parts.HasFlag(BackupParts.Organize))
            done.Add($"插件更新 {result.Plugins.Updated} 款（另有 {result.Plugins.Added} 款当前未安装，已保留记录）");
        if (parts.HasFlag(BackupParts.Wishes))
            done.Add($"心愿和已购新增 {result.Wishes.Added} 条、更新 {result.Wishes.Updated} 条");
        if (parts.HasFlag(BackupParts.Notes))
            done.Add($"混音笔记 {result.Chains} 条");
        if (parts.HasFlag(BackupParts.Settings) && content.Settings != null)
            done.Add($"设置 {result.Settings} 项、文件夹 {result.Folders} 个" + (result.FoldersMissing > 0 ? $"（{result.FoldersMissing} 个在这台电脑上不存在，未添加）" : ""));
        if (parts.HasFlag(BackupParts.SharedReviews))
            done.Add($"别人的评价 {result.ReviewSources} 位、采用记录 {result.Adoptions} 条");
        if (withSecrets)
            done.Add($"账号密码 {result.WishSecrets} 条、厂家账号 {result.Platforms} 个");
        if (parts.HasFlag(BackupParts.Usage))
            done.Add($"使用记录 {result.UsageEvents} 条、成就 {result.Achievements} 个");
        if (parts.HasFlag(BackupParts.Uninstalls))
            done.Add($"卸载历史 {result.Uninstalls} 条");
        return "已导入：" + string.Join("；", done);
    }
}

/// <summary>
/// 导入别人分享的评价和混音笔记前的检查和说明。ownAuthorId 是本机导出评价时用的编号，
/// rows 是插件库里的插件，用来数有几条对应已安装的插件。
/// </summary>
public sealed class ShareImport(string? ownAuthorId, IReadOnlyList<ReviewSource> sources, IReadOnlyList<PluginRow> rows)
{
    public static ShareImport For(PluginDatabase database) =>
        new(database.GetSetting(ReviewListing.AuthorIdSetting), database.LoadReviewSources(), database.LoadEffects());

    /// <summary>不能导入的原因；能导入时返回 null。</summary>
    public string? CheckReviews(ReviewFile file) =>
        !string.IsNullOrEmpty(ownAuthorId) && file.AuthorId == ownAuthorId ? "这是你自己导出的评价，不需要导入"
        : file.Reviews.Count == 0 ? "这份文件里没有可用的评价"
        : null;

    public string DescribeReviews(ReviewFile file)
    {
        var preview = new ReviewSource(0, file.AuthorId, file.Author, DateTime.UtcNow, file.Reviews.Count);
        var matched = new ReviewLibrary(file.Reviews.Select(r => new AttributedReview(preview, r))).CountMatched(preview, rows);
        var message = new StringBuilder($"来自“{file.Author}”的评价，共 {file.Reviews.Count} 条，其中 {matched} 条对应你已安装的插件。其余的会保留，以后装上就能看到。不会改动你自己的评价。{ReviewListing.IdentityNote}");
        if (sources.FirstOrDefault(s => s.AuthorId == file.AuthorId) is { } existing)
            message.Append($"\n\n会替换之前导入的 {existing.Count} 条“{existing.Name}”的评价。");
        else if (sources.Any(s => s.Name == file.Author))
            message.Append($"\n\n已有另一位也叫“{file.Author}”的分享者，两份评价会分开保存。");
        return message.ToString();
    }

    public static string? CheckNotes(NoteFile file) =>
        file.Notes.Count == 0 ? "这份文件里没有可用的混音笔记" : null;

    /// <summary>笔记里用到、这台电脑没装的插件数。</summary>
    public int MissingPlugins(NoteFile file) =>
        file.Picks.Count(p => ChainReport.Resolve(p, rows).State == ChainStepState.Missing);

    public string DescribeNotes(NoteFile file)
    {
        var picks = file.Picks;
        var missing = MissingPlugins(file);
        var message = new StringBuilder(file.Author.Length > 0 ? $"来自“{file.Author}”的混音笔记" : "这份混音笔记");
        message.Append($"，共 {file.Notes.Count} 条，用到 {picks.Count} 款插件");
        message.Append(missing > 0 ? $"，其中 {missing} 款你没有安装。" : "，你都装了。");
        if (file.HasRoutes)
            message.Append("包含笔记之间的路由。");
        message.Append($"\n\n会放进新文件夹“{NoteShare.RootFolderName(file)}”，保留原来的子文件夹，不会改动你已有的笔记。");
        if (file.Author.Length > 0)
            message.Append(ReviewListing.IdentityNote);
        return message.ToString();
    }

    /// <summary>组合分享文件里能勾的两项。都不能导入时调用方显示 NothingText。</summary>
    public IReadOnlyList<PartOption> Options(ShareBundle bundle)
    {
        static string Flat(string text) => text.Replace("\n\n", "");
        var options = new List<PartOption>();
        if (bundle.Reviews is { } reviews)
        {
            var blocked = CheckReviews(reviews);
            options.Add(new(TransferListing.ShareReviewsId, $"插件评价（{reviews.Reviews.Count} 条）", blocked ?? Flat(DescribeReviews(reviews)), Enabled: blocked == null));
        }
        else
            options.Add(new(TransferListing.ShareReviewsId, "插件评价", "这份文件里没有", Enabled: false));
        if (bundle.Notes is { } notes)
        {
            var blocked = CheckNotes(notes);
            options.Add(new(TransferListing.ShareNotesId, $"混音笔记（{notes.Notes.Count} 条）", blocked ?? Flat(DescribeNotes(notes)), Enabled: blocked == null));
        }
        else
            options.Add(new(TransferListing.ShareNotesId, "混音笔记", "这份文件里没有", Enabled: false));
        return options;
    }

    public static string NothingText(IReadOnlyList<PartOption> options) =>
        "这份分享文件里没有能导入的内容：" + string.Join("；", options.Select(o => o.Detail));

    public static string BundleMessage(ShareBundle bundle)
    {
        var author = bundle.Reviews?.Author is { Length: > 0 } name ? name : bundle.Notes?.Author ?? "";
        return (author.Length > 0 ? $"来自“{author}”的分享。" : "")
            + "勾选要导入的内容。别人的评价单独存放，笔记放进新的文件夹，都不会改动你自己的内容。";
    }

    public static string ReviewsImported(ReviewFile file) => $"已导入 {file.Author} 的 {file.Reviews.Count} 条评价";

    public static string NotesImported(NoteFolder folder, int count, int missing) =>
        $"已导入 {count} 条混音笔记到“{folder.Name}”" + (missing > 0 ? $"，{missing} 款插件没有安装，可以在笔记里一键加入心愿单" : "");
}

/// <summary>插件详情里别人的评价、设置页的评价来源列表，以及导出评价窗口的文字。</summary>
public static class ReviewListing
{
    public const string AuthorIdSetting = BackupRestore.ReviewAuthorIdSetting;
    public const string AuthorNameSetting = "review.authorName";
    public const string IdentityNote = "昵称由分享者自己填写，不能证明身份。";
    public const string ExportExplanation = "别人导入后，会在插件详情里看到你的昵称和评价。文件不包含账号、密码、序列号、价格、工程名和路径。";
    public const string NoteExportExplanation = "导出笔记名、文件夹、标签、每一步的插件和应用思路。别人导入后会放进一个单独的文件夹，不会覆盖他自己的笔记。文件不包含工程名、路径、账号和密码。";
    public const string NoReviews = "还没有写过评价，先给插件打分或写点感受";
    public const string NoNotes = "还没有混音笔记，先新建一条或从工程导入轨道";
    public const string SourcesNote = "分享评价、混音笔记都在左下角的“导出备份”“导入备份”里。导入的评价单独保存，不会覆盖你的内容，也不影响推荐。昵称由分享者自己填写，不能证明身份。";
    public const string NoSources = "还没有导入别人的评价。";

    /// <summary>同名的分享者后面加上导入日期，好区分。</summary>
    public static string AuthorLabel(ReviewSource source, IEnumerable<ReviewSource> all) =>
        all.Count(s => s.Name == source.Name) > 1
            ? $"{source.Name}（{source.ImportedUtc.ToLocalTime():yyyy-MM-dd} 导入）"
            : source.Name;

    /// <summary>对比窗口里列标题用的短一点的写法。</summary>
    public static string ShortLabel(ReviewSource source, IEnumerable<ReviewSource> all) =>
        all.Count(s => s.Name == source.Name) > 1
            ? $"{source.Name}（{source.ImportedUtc.ToLocalTime():yyyy-MM-dd}）"
            : source.Name;

    public static string Stars(int rating) =>
        rating > 0 ? new string('★', rating) + new string('☆', 5 - rating) : "没有打分";

    public static string TagsLine(SharedReview review) =>
        review.Tags.Count > 0 ? "标签：" + string.Join("、", review.Tags) : "";

    public static string Extras(SharedReview review)
    {
        var parts = new List<string>();
        if (review.ProjectCount is { } projects)
            parts.Add($"用在 {projects} 个工程");
        if (review.Categories is { Count: > 0 } categories)
            parts.Add("分类：" + string.Join("、", categories));
        if (review.Mix is { } mix)
        {
            parts.AddRange(new[] { mix.LatencyText, mix.CpuText.Length > 0 ? "CPU " + mix.CpuText : "" }.Where(t => t.Length > 0));
            if (mix.LinearPhase != Tri.Unknown)
                parts.Add("线性相位：" + MixAttributes.TriText(mix.LinearPhase));
            if (mix.Sidechain != Tri.Unknown)
                parts.Add("侧链：" + MixAttributes.TriText(mix.Sidechain));
            if (mix.Oversampling != Tri.Unknown)
                parts.Add("过采样：" + MixAttributes.TriText(mix.Oversampling));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>自己的评分、感受和标签已经和这条一样，“采用”就没有意义了。</summary>
    public static bool SameAsOwn(PluginUserData user, SharedReview review)
    {
        var own = user.Normalized();
        return own.Rating == review.Rating && own.Note == review.Note.Trim() && own.MyTags.SequenceEqual(review.Tags);
    }

    public static string AdoptText(bool same) => same ? "已是这条内容" : "采用";

    public static string ReplacePrompt(PluginRow row, ReviewSource source) =>
        $"“{row.Name}”已经有你写的评分、感受或标签。采用 {source.Name} 的评价会替换这三项，分类和混音属性不变。";

    public static string Adopted(ReviewSource source, PluginRow row) => $"已采用 {source.Name} 对 {row.Name} 的评价，可以继续修改";

    public static string SourceDetail(ReviewSource source, int matched) =>
        $"{source.Count} 条评价，{matched} 条对应已安装的插件，{source.ImportedUtc.ToLocalTime():yyyy-MM-dd} 导入";

    public static string RemovePrompt(ReviewSource source) => $"会移除“{source.Name}”的 {source.Count} 条评价。你已经采用的内容会保留。";

    public static string Removed(ReviewSource source) => $"已移除 {source.Name} 的评价";

    /// <summary>导出评价窗口底部的说明。</summary>
    public static string ExportSummary(string author, int selected) =>
        author.Trim().Length == 0 ? "请填写昵称" : selected > 0 ? $"已选 {selected} 款" : "还没有选择";

    public static string ExportButton(int selected) => selected > 0 ? $"导出 {selected} 款" : "导出";

    /// <summary>本机导出评价用的编号，第一次导出时生成；昵称也记下来，下次预先填好。</summary>
    public static string RememberAuthor(PluginDatabase database, string author)
    {
        var authorId = database.GetSetting(AuthorIdSetting);
        if (string.IsNullOrEmpty(authorId))
        {
            authorId = Guid.NewGuid().ToString("N");
            database.SetSetting(AuthorIdSetting, authorId);
        }
        database.SetSetting(AuthorNameSetting, author);
        return authorId;
    }

    public static string NoteSummary(int selected) => selected > 0 ? $"已选 {selected} 条笔记" : "还没有选择";

    public static string NoteExportButton(int selected) => selected > 0 ? $"导出 {selected} 条" : "导出";
}
