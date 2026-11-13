namespace WhatPlugin.Core;

/// <summary>导入了多少。用来在确认框和完成提示里分项说明。</summary>
public sealed record RestoreSummary(
    ImportResult Plugins,
    UpsertResult Wishes,
    int WishSecrets,
    int Platforms,
    int Lists,
    int Removals,
    int Chains,
    int Settings,
    int Folders,
    int FoldersMissing,
    int Ignored,
    int ReviewSources,
    int Adoptions,
    int UsageEvents = 0,
    int Achievements = 0,
    int Uninstalls = 0);

/// <summary>收集要导出的全部用户内容，以及把备份写回数据库。</summary>
public static class BackupRestore
{
    /// <summary>自己的作者标识。本机已有时不用备份里的，免得把自己分享过的评价认成别人。</summary>
    public const string ReviewAuthorIdSetting = "review.authorId";

    /// <param name="includeSecrets">是否带上账号、密码、序列号和厂家账号。</param>
    /// <param name="lockedSkipped">解不开（另一个 Windows 账号加密）而没能导出的密码或序列号条数。</param>
    public static BackupContent Collect(PluginDatabase database, bool includeSecrets, out int lockedSkipped) =>
        Collect(database, includeSecrets ? BackupParts.All : BackupParts.All & ~BackupParts.Secrets, out lockedSkipped);

    /// <summary>只收集选中的几部分。没选的部分在备份里是空的，导入时本机内容不动。</summary>
    public static BackupContent Collect(PluginDatabase database, BackupParts parts, out int lockedSkipped)
    {
        parts = parts.Valid();
        var includeSecrets = parts.HasFlag(BackupParts.Secrets);
        var wishes = parts.HasFlag(BackupParts.Wishes) ? database.LoadWishes() : [];
        var lists = database.LoadFavoriteLists();
        var listNames = lists.ToDictionary(l => l.Id, l => l.Name);
        var removals = database.LoadFavoriteRemovals()
            .Where(p => listNames.ContainsKey(p.Key))
            .SelectMany(p => p.Value.Order(StringComparer.Ordinal).Select(key => new FavoriteRemovalEntry(listNames[p.Key], key)))
            .ToList();

        var sources = database.LoadReviewSources();
        var reviews = database.LoadSharedReviews().ToLookup(r => r.Source.Id, r => r.Review);
        var reviewBackup = new ReviewsBackup(
            sources.Select(s => new ReviewSourceBackup(s.AuthorId, s.Name, s.ImportedUtc, reviews[s.Id].ToList())).ToList(),
            database.LoadReviewAdoptions().Values.OrderBy(a => a.PluginKey, StringComparer.Ordinal).ToList());

        var settings = new SettingsBackup(
            database.LoadSettings(),
            database.LoadProjectFolders(),
            database.LoadVst2Folders(),
            database.LoadIgnoredProjectPluginNames());

        lockedSkipped = 0;
        BackupSecrets? secrets = null;
        if (includeSecrets)
        {
            var wishSecrets = new List<WishSecretEntry>();
            for (var i = 0; i < wishes.Count; i++)
            {
                var w = wishes[i];
                if (w.SecretsLocked)
                    lockedSkipped++;
                if (w.Account.Length > 0 || w.Password.Length > 0 || w.Serial.Length > 0)
                    wishSecrets.Add(new WishSecretEntry(i, w.Account, w.Password, w.Serial));
            }
            var platforms = database.LoadPlatformAccounts();
            lockedSkipped += platforms.Count(p => p.Locked);
            secrets = new BackupSecrets(
                wishSecrets,
                platforms.Select(p => new PlatformBackupEntry(p.Platform, p.Account, p.Password, p.Note)).ToList());
        }

        // 常用清单已经去掉，旧数据随混音笔记一起带着，免得丢。
        var notes = parts.HasFlag(BackupParts.Notes);
        return new BackupContent(
            UserDataBackup.PluginEntries(database.LoadPlugins(), parts),
            wishes.Select(WishBackupEntry.From).ToList(),
            notes ? lists.Select(f => new FavoriteBackupEntry(f.Name, f.PluginKeys)).ToList() : [],
            notes ? ChainEntries(database.LoadChains(), database.LoadNoteFolders(), database.LoadRoutes()) : [],
            notes ? removals : [],
            parts.HasFlag(BackupParts.Settings) ? settings : null,
            parts.HasFlag(BackupParts.SharedReviews) ? reviewBackup : null,
            secrets,
            parts.HasFlag(BackupParts.Usage) ? new UsageBackup(database.LoadUsage(), database.LoadAchievements()) : null,
            parts.HasFlag(BackupParts.Uninstalls) ? database.LoadUninstallHistory().Select(UninstallBackupEntry.From).ToList() : [],
            parts);
    }

    public static List<ChainBackupEntry> ChainEntries(IEnumerable<PluginChain> chains, IReadOnlyList<NoteFolder> folders, IReadOnlyList<NoteRoute>? routes = null)
    {
        var list = chains.ToList();
        var byId = list.ToDictionary(c => c.Id);
        ChainRouteRef? Ref(long id) => byId.TryGetValue(id, out var c) ? new ChainRouteRef(MixNotes.PathOf(c.FolderId, folders), c.Name) : null;
        var from = (routes ?? []).ToLookup(r => r.From);
        return list.Select(c =>
        {
            var mine = from[c.Id].ToList();
            var output = mine.FirstOrDefault(r => r.Kind == RouteKind.Output);
            var sends = mine.Where(r => r.Kind == RouteKind.Send).Select(r => Ref(r.To)).OfType<ChainRouteRef>().ToList();
            var sidechains = mine.Where(r => r.Kind == RouteKind.Sidechain)
                .Select(r => Ref(r.To) is { } target ? target with { Plugin = r.Plugin } : null)
                .OfType<ChainRouteRef>()
                .ToList();
            return new ChainBackupEntry(c.Name, c.Note, c.Steps, MixNotes.PathOf(c.FolderId, folders), c.TagList, c.Source,
                c.Role == ChannelRole.Track ? null : c.Role,
                output == null ? null : Ref(output.To),
                sends.Count > 0 ? sends : null,
                sidechains.Count > 0 ? sidechains : null);
        }).ToList();
    }

    /// <param name="secrets">解开的账号密码；null 表示不导入（备份里没有，或用户选择跳过）。</param>
    /// <param name="folderExists">判断文件夹在这台电脑上是否存在。不存在的不加，免得扫描时报错。</param>
    /// <param name="only">只导入其中几部分（和备份实际含有的取交集）；null 表示备份里有的都导入。没导入的部分本机内容不动。</param>
    public static RestoreSummary Apply(PluginDatabase database, BackupContent content, BackupSecrets? secrets, Func<string, bool>? folderExists = null, BackupParts? only = null)
    {
        folderExists ??= Directory.Exists;
        var parts = (content.Parts & (only ?? BackupParts.All)).Valid();
        if (!parts.HasFlag(BackupParts.Secrets))
            secrets = null;
        var reviewsPart = parts.HasFlag(BackupParts.Reviews);
        var organizePart = parts.HasFlag(BackupParts.Organize);
        var entries = content.Plugins;
        if (!reviewsPart || !organizePart)
        {
            var local = database.LoadPlugins().GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().User);
            entries = !reviewsPart && !organizePart
                ? []
                : entries.Select(e => UserDataBackup.MergeParts(e, local.GetValueOrDefault(e.Key) ?? PluginUserData.Empty, reviewsPart, organizePart))
                    .Where(e => local.ContainsKey(e.Key) || e.ToUserData().HasData)
                    .ToList();
        }
        var plugins = entries.Count > 0 ? database.ImportUserData(entries) : new ImportResult(0, 0);
        var wishes = parts.HasFlag(BackupParts.Wishes) ? database.ImportWishes(content.Wishes, secrets?.Wishes) : new UpsertResult(0, 0);
        var notes = parts.HasFlag(BackupParts.Notes);
        var lists = notes ? database.ImportFavoriteLists(content.Favorites) : 0;
        var removals = notes ? database.ImportFavoriteRemovals(content.FavoriteRemovals) : 0;
        var chains = notes ? database.ImportChains(content.Chains) : 0;

        var settingCount = 0;
        var folders = 0;
        var missing = 0;
        var ignored = 0;
        if (parts.HasFlag(BackupParts.Settings) && content.Settings is { } settings)
        {
            foreach (var (key, value) in settings.Values ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrEmpty(key) || value == null)
                    continue;
                if (key == ReviewAuthorIdSetting && !string.IsNullOrEmpty(database.GetSetting(key)))
                    continue;
                if (key == PluginDatabase.LastLaunchSetting)
                    continue;
                database.SetSetting(key, value);
                settingCount++;
            }

            var projectFolders = database.LoadProjectFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in settings.ProjectFolders ?? [])
            {
                if (string.IsNullOrWhiteSpace(folder) || projectFolders.Contains(folder))
                    continue;
                if (!folderExists(folder)) { missing++; continue; }
                database.AddProjectFolder(folder);
                folders++;
            }
            var vst2Folders = database.LoadVst2Folders().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in settings.Vst2Folders ?? [])
            {
                if (string.IsNullOrWhiteSpace(folder) || vst2Folders.Contains(Path.TrimEndingDirectorySeparator(folder)))
                    continue;
                if (!folderExists(folder)) { missing++; continue; }
                database.AddVst2Folder(folder);
                folders++;
            }
            var ignoredNow = database.LoadIgnoredProjectNames();
            foreach (var name in settings.IgnoredProjectPlugins ?? [])
            {
                if (string.IsNullOrWhiteSpace(name) || ignoredNow.Contains(ProjectReport.IgnoreKey(name)))
                    continue;
                database.IgnoreProjectName(name);
                ignored++;
            }
        }

        var sources = 0;
        var adoptions = 0;
        if (parts.HasFlag(BackupParts.SharedReviews) && content.Reviews is { } reviews)
        {
            var mine = database.LoadReviewSources().ToDictionary(s => s.AuthorId, StringComparer.Ordinal);
            var ownId = database.GetSetting(ReviewAuthorIdSetting);
            foreach (var source in reviews.Sources ?? [])
            {
                if (string.IsNullOrWhiteSpace(source.AuthorId) || source.AuthorId == ownId)
                    continue;
                // 本机后来又导入过同一个人的新版本，就不用备份里的旧版本替换。
                if (mine.TryGetValue(source.AuthorId, out var current) && current.ImportedUtc > source.ImportedUtc)
                    continue;
                database.SaveReviewFile(new ReviewFile(source.AuthorId, source.Name ?? "", source.ImportedUtc, source.Reviews ?? []), source.ImportedUtc);
                sources++;
            }
            adoptions = database.ImportReviewAdoptions(reviews.Adoptions ?? []);
        }

        var platforms = secrets == null ? 0 : database.ImportPlatformAccounts(secrets.Platforms ?? []);
        var usagePart = parts.HasFlag(BackupParts.Usage);
        var usage = usagePart ? database.ImportUsage(content.Usage?.Events ?? []) : 0;
        var achievements = usagePart ? database.ImportAchievements(content.Usage?.Achievements ?? new Dictionary<string, DateTime>()).Count : 0;
        var uninstalls = !parts.HasFlag(BackupParts.Uninstalls) ? 0 : database.ImportUninstallHistory((content.Uninstalls ?? []).Where(u => u != null && !string.IsNullOrWhiteSpace(u.Name)).Select(u => u.ToRecord()));
        return new RestoreSummary(plugins, wishes, secrets?.WishCount ?? 0, platforms, lists, removals, chains,
            settingCount, folders, missing, ignored, sources, adoptions, usage, achievements, uninstalls);
    }
}
