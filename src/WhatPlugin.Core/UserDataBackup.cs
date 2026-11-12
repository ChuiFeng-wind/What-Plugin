using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatPlugin.Core;

public sealed record BackupEntry(
    string Key,
    string Name,
    string Vendor,
    int Rating,
    string Note,
    IReadOnlyList<string> MyTags,
    IReadOnlyList<string>? TagsOverride,
    PluginKind? KindOverride,
    string? VendorOverride,
    bool Hidden = false,
    string? BlacklistReason = "",
    MixAttributes? Mix = null,
    IReadOnlyList<string>? TimbresOverride = null,
    PurchaseKind? Purchase = null)
{
    public PluginUserData ToUserData()
    {
        // 旧备份把人声、鼓、吉他/贝斯放在功能分类里，恢复时挪到适合音色。
        var tags = TagsOverride;
        var timbres = TimbresOverride;
        if (tags != null && timbres == null && Classifier.TimbresIn(tags).Count > 0)
        {
            timbres = Classifier.TimbresIn(tags);
            tags = Classifier.FunctionTagsIn(tags).ToList();
        }
        return new PluginUserData(Rating, Note ?? "", MyTags ?? [], tags, KindOverride, VendorOverride, Hidden, BlacklistReason ?? "", Mix, timbres,
            Purchase ?? PurchaseKind.Unknown).Normalized();
    }
}

public sealed record ImportResult(int Updated, int Added);

/// <summary>心愿和购买信息的备份。账号、密码和序列号不在这里，在用备份密码加密的 <see cref="BackupSecrets"/> 里。</summary>
public sealed record WishBackupEntry(
    string Name,
    string? Vendor,
    decimal? Price,
    string? Currency,
    int Priority,
    string? Reason,
    string? Url,
    string? Note,
    WishStatus Status,
    DateOnly? PurchasedOn,
    decimal? PaidPrice,
    DateTime CreatedUtc,
    LicenseKind LicenseKind = LicenseKind.Unknown,
    string? ActivatedOn = "",
    int? ActivationsLeft = null,
    decimal? TargetPrice = null,
    decimal? LowestPrice = null,
    string? Category = null,
    bool? OwnLogin = null,
    string? Invoice = null)
{
    public static WishBackupEntry From(WishItem item) => new(
        item.Name, item.Vendor, item.Price, item.Currency, item.Priority, item.Reason, item.Url, item.Note,
        item.Status, item.PurchasedOn, item.PaidPrice, item.CreatedUtc,
        item.LicenseKind, item.ActivatedOn, item.ActivationsLeft, item.TargetPrice, item.LowestPrice, item.Category, item.OwnLogin, item.Invoice);

    public WishItem ToItem(long id) => new(
        id, Name, Vendor ?? "", Price, Currency ?? "¥", Priority, Reason ?? "", Url ?? "", Note ?? "",
        Status, PurchasedOn, PaidPrice, CreatedUtc: CreatedUtc,
        LicenseKind: LicenseKind, ActivatedOn: ActivatedOn ?? "", ActivationsLeft: ActivationsLeft,
        TargetPrice: TargetPrice, LowestPrice: LowestPrice, Category: Category ?? "", OwnLogin: OwnLogin ?? false,
        Invoice: Invoice ?? "");
}

/// <summary>某条心愿或已购的账号、密码、序列号。Index 是它在备份 Wishes 列表里的位置。</summary>
public sealed record WishSecretEntry(int Index, string? Account, string? Password, string? Serial)
{
    public override string ToString() => $"WishSecretEntry {{ Index = {Index} }}";
}

/// <summary>厂家账号（含 iLok）。备注里可能有账号名，所以整条加密。</summary>
public sealed record PlatformBackupEntry(string Platform, string? Account, string? Password, string? Note)
{
    public override string ToString() => $"PlatformBackupEntry {{ Platform = {Platform} }}";
}

/// <summary>备份里加密的内容。</summary>
public sealed record BackupSecrets(IReadOnlyList<WishSecretEntry>? Wishes, IReadOnlyList<PlatformBackupEntry>? Platforms)
{
    public int WishCount => Wishes?.Count ?? 0;
    public int PlatformCount => Platforms?.Count ?? 0;
}

/// <summary>常用清单里手动移出的插件，恢复后同步使用次数时不再加回来。</summary>
public sealed record FavoriteRemovalEntry(string List, string PluginKey);

/// <summary>设置：app_settings 里的全部键值、工程文件夹、自己加的 VST2 文件夹、工程里忽略的插件名。</summary>
public sealed record SettingsBackup(
    IReadOnlyDictionary<string, string>? Values,
    IReadOnlyList<string>? ProjectFolders,
    IReadOnlyList<string>? Vst2Folders,
    IReadOnlyList<string>? IgnoredProjectPlugins);

public sealed record ReviewSourceBackup(string AuthorId, string Name, DateTime ImportedUtc, IReadOnlyList<SharedReview>? Reviews);

/// <summary>导入的别人的评价和采用记录。</summary>
public sealed record ReviewsBackup(IReadOnlyList<ReviewSourceBackup>? Sources, IReadOnlyList<ReviewAdoption>? Adoptions);

/// <summary>一份备份的全部内容。Secrets 为 null 表示不含账号密码。</summary>
public sealed record BackupContent(
    IReadOnlyList<BackupEntry> Plugins,
    IReadOnlyList<WishBackupEntry> Wishes,
    IReadOnlyList<FavoriteBackupEntry> Favorites,
    IReadOnlyList<ChainBackupEntry> Chains,
    IReadOnlyList<FavoriteRemovalEntry> FavoriteRemovals,
    SettingsBackup? Settings,
    ReviewsBackup? Reviews,
    BackupSecrets? Secrets = null,
    UsageBackup? Usage = null,
    IReadOnlyList<UninstallBackupEntry>? Uninstalls = null);

/// <summary>卸载历史的一条。日期为空表示 1.2.4 之前卸载的，日期未知。</summary>
public sealed record UninstallBackupEntry(
    string? PluginKey,
    string Name,
    string? Vendor,
    UninstallWay Way,
    string? Reason,
    DateTime? UninstalledUtc,
    string? TrashReason,
    int Rating,
    string? Note)
{
    public static UninstallBackupEntry From(UninstallRecord r) =>
        new(r.PluginKey, r.Name, r.Vendor, r.Way, r.Reason, r.UninstalledUtc, r.TrashReason, r.Rating, r.Note);

    public UninstallRecord ToRecord() =>
        new(0, PluginKey ?? "", Name ?? "", Vendor ?? "", Way, Reason ?? "", UninstalledUtc, TrashReason ?? "", Rating, Note ?? "");
}

/// <summary>读出来的备份。加密的部分要用 <see cref="UserDataBackup.DecryptSecrets"/> 另外解开。</summary>
public sealed record LoadedBackup(int Version, BackupContent Content, EncryptedSection? EncryptedSecrets)
{
    public bool HasSecrets => EncryptedSecrets != null;
}

public sealed record FavoriteBackupEntry(string Name, IReadOnlyList<string>? PluginKeys);

/// <summary>混音笔记的备份。Folder 是从最外层开始的文件夹名称路径。</summary>
public sealed record ChainBackupEntry(
    string Name,
    string? Note,
    IReadOnlyList<ChainStep>? Steps,
    IReadOnlyList<string>? Folder = null,
    IReadOnlyList<string>? Tags = null,
    string? Source = null,
    ChannelRole? Role = null,
    ChainRouteRef? Output = null,
    IReadOnlyList<ChainRouteRef>? Sends = null,
    IReadOnlyList<ChainRouteRef>? Sidechains = null);

/// <summary>备份里指向另一条笔记：文件夹路径加名称（同一文件夹里笔记不重名）。侧链时 Plugin 是接收侧链的插件。</summary>
public sealed record ChainRouteRef(IReadOnlyList<string>? Folder, string Name, string? Plugin = null);

/// <summary>把用户填写的内容导出为 JSON。键是插件的稳定标识，可以恢复到新数据库。</summary>
public static class UserDataBackup
{
    // 2：加了常用清单移出记录、设置、评价分享，以及加密的账号密码。
    // 3：想要程度改为两级；插件加了适合音色；混音笔记加了文件夹、标签和备选插件。
    // 4：插件加了购买方式，已购加了发票号；加了使用记录和已解锁的成就。
    // 5：混音笔记加了通道类型和路由（主输出、发送、侧链）。
    // 6：加了卸载历史。
    private const int CurrentVersion = 6;

    private sealed record BackupFile(
        int Version,
        DateTime ExportedUtc,
        IReadOnlyList<BackupEntry> Plugins,
        IReadOnlyList<WishBackupEntry>? Wishes = null,
        IReadOnlyList<FavoriteBackupEntry>? Favorites = null,
        IReadOnlyList<ChainBackupEntry>? Chains = null,
        IReadOnlyList<FavoriteRemovalEntry>? FavoriteRemovals = null,
        SettingsBackup? Settings = null,
        ReviewsBackup? Reviews = null,
        EncryptedSection? Secrets = null,
        UsageBackup? Usage = null,
        IReadOnlyList<UninstallBackupEntry>? Uninstalls = null);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<BackupEntry> PluginEntries(IEnumerable<PluginRow> plugins) => plugins
        .Where(p => p.User.HasData)
        .Select(p => new BackupEntry(
            p.Key, p.Name, p.AutoVendor, p.User.Rating, p.User.Note, p.User.MyTags,
            p.User.TagsOverride, p.User.KindOverride, p.User.VendorOverride, p.User.Hidden, p.User.BlacklistReason, p.User.Mix,
            p.User.TimbresOverride, p.User.Purchase))
        .ToList();

    /// <summary>只含插件、心愿、清单和插件链，不含账号密码。</summary>
    public static string Serialize(IEnumerable<PluginRow> plugins, IEnumerable<WishItem>? wishes = null, IEnumerable<FavoriteList>? favorites = null, IEnumerable<PluginChain>? chains = null) =>
        Serialize(new BackupContent(
            PluginEntries(plugins),
            (wishes ?? []).Select(WishBackupEntry.From).ToList(),
            (favorites ?? []).Select(f => new FavoriteBackupEntry(f.Name, f.PluginKeys)).ToList(),
            (chains ?? []).Select(c => new ChainBackupEntry(c.Name, c.Note, c.Steps, null, c.TagList, c.Source)).ToList(),
            [], null, null), null);

    /// <summary>含账号密码时必须给备份密码，它们用备份密码加密后放进文件；其余内容是可读的 JSON。</summary>
    public static string Serialize(BackupContent content, string? password, int iterations = BackupCrypto.DefaultIterations)
    {
        EncryptedSection? secrets = null;
        if (content.Secrets != null)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("包含账号密码时需要备份密码", nameof(password));
            secrets = BackupCrypto.Encrypt(JsonSerializer.Serialize(content.Secrets, Options), password, iterations);
        }
        return JsonSerializer.Serialize(new BackupFile(
            CurrentVersion, DateTime.UtcNow, content.Plugins, content.Wishes, content.Favorites, content.Chains,
            content.FavoriteRemovals, content.Settings, content.Reviews, secrets, content.Usage, content.Uninstalls), Options);
    }

    /// <exception cref="FormatException">文件不是有效的备份。</exception>
    public static LoadedBackup Read(string json)
    {
        var file = ReadFile(json);
        return new LoadedBackup(file.Version, new BackupContent(
            file.Plugins, file.Wishes ?? [], file.Favorites ?? [], file.Chains ?? [],
            file.FavoriteRemovals ?? [], file.Settings, file.Reviews, null, file.Usage, file.Uninstalls ?? []), file.Secrets);
    }

    /// <exception cref="BackupPasswordException">备份密码不对或文件被改过。</exception>
    /// <exception cref="FormatException">加密部分损坏。</exception>
    public static BackupSecrets DecryptSecrets(LoadedBackup backup, string password)
    {
        if (backup.EncryptedSecrets == null)
            throw new InvalidOperationException("这份备份不含账号密码");
        var json = BackupCrypto.Decrypt(backup.EncryptedSecrets, password);
        try
        {
            return JsonSerializer.Deserialize<BackupSecrets>(json, Options) ?? throw new FormatException("备份里加密的部分是空的。");
        }
        catch (JsonException e)
        {
            throw new FormatException("备份里加密的部分已损坏。", e);
        }
    }

    /// <exception cref="FormatException">文件不是有效的备份。</exception>
    public static IReadOnlyList<BackupEntry> Parse(string json) => ReadFile(json).Plugins;

    /// <summary>旧版备份没有心愿单，返回空。</summary>
    public static IReadOnlyList<WishBackupEntry> ParseWishes(string json) => ReadFile(json).Wishes ?? [];

    /// <summary>旧版备份没有常用清单，返回空。</summary>
    public static IReadOnlyList<FavoriteBackupEntry> ParseFavorites(string json) => ReadFile(json).Favorites ?? [];

    /// <summary>旧版备份没有插件链，返回空。</summary>
    public static IReadOnlyList<ChainBackupEntry> ParseChains(string json) => ReadFile(json).Chains ?? [];

    private static BackupFile ReadFile(string json)
    {
        BackupFile? file;
        try
        {
            file = JsonSerializer.Deserialize<BackupFile>(json, Options);
        }
        catch (JsonException e)
        {
            throw new FormatException("文件不是有效的备份：" + e.Message, e);
        }

        if (file?.Plugins == null || file.Version < 1)
            throw new FormatException("文件不是有效的备份。");
        if (file.Version > CurrentVersion)
            throw new FormatException("这份备份来自更新版本的软件，当前版本无法读取。");
        if (file.Version < 3 && file.Wishes != null)
            file = file with { Wishes = file.Wishes.Select(w => w with { Priority = WishPriority.FromThreeLevels(w.Priority) }).ToList() };
        if (file.Plugins.Any(p => string.IsNullOrWhiteSpace(p.Key)))
            throw new FormatException("备份中有缺少标识的插件记录。");
        return file;
    }
}
