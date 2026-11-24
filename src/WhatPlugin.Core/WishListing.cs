namespace WhatPlugin.Core;

/// <summary>心愿单卡片和已购表格的一行要显示的文字。密码只给遮罩，不进搜索文本。</summary>
public sealed record WishRow(WishItem Item, bool IsInstalled, EffectiveLogin Login)
{
    public WishRow(WishItem item, bool installed) : this(item, installed, new EffectiveLogin(item.Account, item.Password, item.SecretsLocked, false, "")) { }

    /// <summary>账号、密码或序列号至少有一项可用。</summary>
    public bool HasLogin => Login.HasAny || Item.Serial.Length > 0;

    public string Name => Item.Name;
    public string VendorText => Item.Vendor.Length > 0 ? Item.Vendor : "厂家未填";
    public string PriceText => Item.Price == null ? "未定价" : Money.Format(Item.Price, Item.Currency);
    public bool HasPrice => Item.Price != null;
    public string ReasonLine => Item.Reason.Length > 0 ? Item.Reason.ReplaceLineEndings(" ") : "还没写为什么想买";
    public bool ReachedTarget => PurchaseAdvisor.ReachedTarget(Item);
    public string LicenseText => LicenseMigration.KindText(Item.LicenseKind);
    public string PriorityText => WishPriority.Text(Item.Priority);
    public int Priority => WishPriority.Clean(Item.Priority);
    public decimal PriceSort => Item.Price ?? decimal.MaxValue;
    public DateTime Created => Item.CreatedUtc;

    public string AccountText => Login.Account.Length > 0 ? Login.Account + (Login.FromPlatform ? "（厂家账号）" : "") : "未填";
    public string PasswordMask => Login.Locked ? "无法解开" : Login.Password.Length > 0 ? "••••••••" : "未填";
    public string PaidText => Money.Format(Item.PaidPrice, Item.Currency);
    public decimal PaidSort => Item.PaidPrice ?? -1;
    public string DateText => Item.PurchasedOn?.ToString("yyyy-MM-dd") ?? "";

    public string SearchText => string.Join(' ', Item.Name, Item.Vendor, Item.Reason, Item.Note, Login.Account, Login.Platform, Item.ActivatedOn,
        Item.LicenseKind == LicenseKind.Unknown ? "" : LicenseText).ToLowerInvariant();

    public override string ToString() => $"WishRow {{ Name = {Item.Name} }}";
}

/// <summary>心愿单和已购账号两页共用的读取、筛选、文字和字段检查（Windows 版和跨平台版共用）。</summary>
public static class WishListing
{
    public const string SortByPriority = "按想要程度";
    public const string SortByPrice = "按价格";
    public const string SortByRecent = "最近添加";
    public static readonly IReadOnlyList<string> SortOptions = [SortByPriority, SortByPrice, SortByRecent];

    public const string AllVendors = "全部厂家";
    public const string AllStates = "全部";
    public const string WithLogin = "已填账号";
    public const string WithoutLogin = "未填账号";
    public static readonly IReadOnlyList<string> StateOptions = [AllStates, WithLogin, WithoutLogin];

    public const string KeepLicense = "不修改";

    public const string WishPanelEmpty = "选择一款心愿，记下原因和价格";
    public const string NameRequired = "请先输入插件名";
    public const string NoAccounts = "还没有填过账号。填过一次以后，可以在这里直接选";

    public static IReadOnlyList<string> LicenseOptions => Enum.GetValues<LicenseKind>().Select(LicenseMigration.KindText).ToList();

    /// <summary>批量填写时的授权方式：第一项是“不修改”。</summary>
    public static IReadOnlyList<string> BatchLicenseOptions => [KeepLicense, .. LicenseOptions.Skip(1)];

    /// <summary>密码解不开时的说明：Windows 是另一个 Windows 账号加密的，Mac 是另一台 Mac 或另一个用户的钥匙串。</summary>
    public static string ForeignKeyOwner => AppPlatform.IsMac ? "另一台 Mac 或另一个 macOS 用户" : "另一个 Windows 账号";

    public static string PurchaseLockedText => $"这条的密码是在{ForeignKeyOwner}下加密的，这里解不开。原来的内容没有改动。";
    public static string PlatformLockedText => $"这个密码是在{ForeignKeyOwner}下加密的，这里解不开。原来的内容没有改动。";
    public static string ILokLockedText => $"iLok 的密码是在{ForeignKeyOwner}下加密的，这里解不开，不能填入";

    public static string ExportWarning => "导出的文件里账号、密码、序列号都是明文，任何拿到文件的人都能看到。\n\n"
        + (AppPlatform.IsMac
            ? "这个功能用于换电脑或重装系统：加密用的密钥存在这台 Mac 的钥匙串里，换机后解不开。迁移完请删除这个文件。"
            : "这个功能用于换电脑或重装系统：加密内容绑定当前 Windows 账号，重装后解不开。迁移完请删除这个文件。");

    public static string ExportFileName(DateTime now) => $"WhatPlugin-账号-{now:yyyyMMdd}.txt";

    public static string ChecklistFileName(DateTime now) => $"WhatPlugin-换机清单-{now:yyyyMMdd}.txt";

    // ---------- 读取 ----------

    /// <summary>装着的插件的规范化名称，判断心愿单里哪些已经装了。</summary>
    public static HashSet<string> InstalledNames(IEnumerable<PluginRow> plugins) =>
        plugins.Where(p => !p.IsMissing).Select(p => Normalizer.Name(p.Name)).ToHashSet();

    /// <summary>插件名可能带厂家（FabFilter Pro-Q 3），也可能不带，两种都算已安装。</summary>
    public static bool IsInstalled(WishItem item, IReadOnlySet<string> installed)
    {
        var name = Normalizer.Name(item.Name);
        return installed.Contains(name) || (item.Vendor.Length > 0 && installed.Contains(Normalizer.Vendor(item.Vendor) + name));
    }

    public static List<WishRow> Rows(IEnumerable<WishItem> items, IReadOnlySet<string> installed, IReadOnlyDictionary<string, PlatformAccount> platforms) =>
        items.Select(w => new WishRow(w, IsInstalled(w, installed), PurchaseLogin.Resolve(w, platforms))).ToList();

    // ---------- 筛选和排序 ----------

    public static bool Matches(string searchText, string search)
    {
        var words = search.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(searchText.Contains);
    }

    public static IEnumerable<WishRow> Wishes(IEnumerable<WishRow> rows, string search, string sort)
    {
        var shown = rows.Where(r => Matches(r.SearchText, search));
        return sort switch
        {
            SortByPrice => shown.OrderBy(r => r.PriceSort),
            SortByRecent => shown.OrderByDescending(r => r.Created),
            _ => shown.OrderByDescending(r => r.Priority).ThenByDescending(r => r.Created),
        };
    }

    public static bool MatchesPurchase(WishRow row, string vendor, string state, string search)
    {
        if (vendor.Length > 0 && vendor != AllVendors && row.Item.Vendor != vendor)
            return false;
        if ((state == WithLogin && !row.HasLogin) || (state == WithoutLogin && row.HasLogin))
            return false;
        return Matches(row.SearchText, search);
    }

    /// <summary>厂家筛选的选项：第一项是“全部厂家”。</summary>
    public static IReadOnlyList<string> VendorOptions(IEnumerable<WishRow> purchases) =>
        [AllVendors, .. purchases.Select(p => p.Item.Vendor).Where(v => v.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase)];

    // ---------- 页面文字 ----------

    public static string WishSubtitle(IReadOnlyCollection<WishRow> wishes)
    {
        if (wishes.Count == 0)
            return "记下想买的插件、原因和价格，买了之后转到已购账号";
        var total = Money.Total(wishes.Select(w => (w.Item.Price, w.Item.Currency)));
        return $"{wishes.Count} 款" + (total.Length > 0 ? $"，全买下来约 {total}" : "");
    }

    public static string AccountSubtitle(IReadOnlyCollection<WishRow> purchases)
    {
        var withLogin = purchases.Count(p => p.HasLogin);
        var paid = Money.Total(purchases.Select(p => (p.Item.PaidPrice, p.Item.Currency)));
        return (purchases.Count == 0 ? "" : $"{purchases.Count} 款，{withLogin} 款已填账号，")
            + (paid.Length > 0 ? $"已花 {paid}，" : "")
            + "密码和序列号加密保存在本机";
    }

    public static string WishEmptyText(int total) => total == 0 ? "还没有想买的插件，在上面输入名字加入" : "没有符合搜索的心愿";

    public static string PurchaseEmptyText(int total) => total == 0 ? "还没有已购记录，点右上角“批量添加”" : "没有符合筛选的记录";

    public static string PlatformEmptyText(int total) => total == 0
        ? "还没有厂家账号。\n已购插件填上厂家后，会按厂家出现在这里。"
        : "没有符合搜索的厂家账号";

    public static string WishesStatus(int count) => count == 0 ? "" : $"心愿单 {count} 款";

    public static string PurchasesStatus(int count) => count == 0 ? "" : $"已购 {count} 款";

    public static string QuickAdded(string name, string vendor) =>
        $"已加入心愿单：{name}" + (vendor.Length > 0 ? $"，厂家自动识别为 {vendor}（不对可以直接改）" : "") + "，可以在右边补上原因和价格";

    /// <summary>保存后的状态栏。有填错的项时只说哪些没保存。</summary>
    public static string Saved(string name, IReadOnlyList<string> problems, string guessedVendor, string platform = "") =>
        problems.Count > 0 ? string.Join("，", problems) + "，这一项没有保存"
        : guessedVendor.Length > 0 ? $"已保存 {name}，厂家自动识别为 {guessedVendor}（不对可以直接改）"
        : platform.Length > 0 ? $"已保存 {name}，账号密码存在 {platform} 的厂家账号里"
        : $"已保存 {name}";

    public static string TrashNote((string Name, string Text) remembered) => $"你以前把 {remembered.Name} 移进垃圾桶，后来卸载了。{remembered.Text}";

    /// <summary>心愿详情的“同类”下拉：自动判断，加上已有插件的类别；这一款自己选过的不在里面时补上。</summary>
    public static IReadOnlyList<string> Categories(WishItem item, IEnumerable<PluginRow> plugins)
    {
        var categories = new[] { PurchaseAdvisor.AutoCategory }
            .Concat(PluginComparison.Categories(plugins).Select(c => c.Tag).Where(t => t != PluginComparison.Uncategorized))
            .ToList();
        if (item.Category.Length > 0 && !categories.Contains(item.Category))
            categories.Add(item.Category);
        return categories;
    }

    public static string WishUrl(string text)
    {
        var url = text.Trim();
        return url.Length > 0 && !url.Contains("://", StringComparison.Ordinal) ? "https://" + url : url;
    }

    public static string FormatAmount(decimal? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    // ---------- 字段检查 ----------

    /// <summary>
    /// 心愿详情里填的内容。填错的项保留原值并记进 problems，其余照常保存。厂家另外由 <see cref="VendorFiller"/> 处理。
    /// </summary>
    public static (WishItem Updated, List<string> Problems) ParseWish(WishItem old, WishFields fields)
    {
        var problems = new List<string>();
        var name = fields.Name.Trim();
        if (name.Length == 0)
        {
            name = old.Name;
            problems.Add("插件名不能为空");
        }
        if (!Money.TryParse(fields.Price, out var price))
        {
            price = old.Price;
            problems.Add("价格只能填数字");
        }
        if (!Money.TryParse(fields.Target, out var target))
        {
            target = old.TargetPrice;
            problems.Add("目标价只能填数字");
        }
        if (!Money.TryParse(fields.Lowest, out var lowest))
        {
            lowest = old.LowestPrice;
            problems.Add("历史最低价只能填数字");
        }
        var category = fields.Category ?? PurchaseAdvisor.AutoCategory;
        return (old with
        {
            Name = name,
            Currency = fields.Currency ?? old.Currency,
            Price = price,
            TargetPrice = target,
            LowestPrice = lowest,
            Category = category == PurchaseAdvisor.AutoCategory ? "" : category,
            Priority = fields.MustHave ? WishPriority.MustHave : WishPriority.CanWait,
            Reason = fields.Reason,
            Url = fields.Url.Trim(),
            Note = fields.Note,
        }, problems);
    }

    /// <summary>已购详情里除账号密码以外的内容。账号密码要看显示的是厂家账号还是这一款的，由调用方处理。</summary>
    public static (WishItem Updated, List<string> Problems) ParsePurchase(WishItem old, PurchaseFields fields)
    {
        var problems = new List<string>();
        var name = fields.Name.Trim();
        if (name.Length == 0)
        {
            name = old.Name;
            problems.Add("插件名不能为空");
        }
        if (!Money.TryParse(fields.Paid, out var paid))
        {
            paid = old.PaidPrice;
            problems.Add("实付只能填数字");
        }
        var date = old.PurchasedOn;
        if (string.IsNullOrWhiteSpace(fields.Date))
            date = null;
        else if (DateOnly.TryParse(fields.Date.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            date = parsed;
        else
            problems.Add("日期格式应为 2026-11-06");
        var activations = old.ActivationsLeft;
        if (string.IsNullOrWhiteSpace(fields.Activations))
            activations = null;
        else if (int.TryParse(fields.Activations.Trim(), out var left) && left >= 0)
            activations = left;
        else
            problems.Add("剩余激活次数只能填整数");

        return (old with
        {
            Name = name,
            Serial = old.SecretsLocked ? old.Serial : fields.Serial,
            Currency = fields.Currency ?? old.Currency,
            PaidPrice = paid,
            PurchasedOn = date,
            LicenseKind = (LicenseKind)Math.Max(fields.License, 0),
            ActivatedOn = fields.ActivatedOn.Trim(),
            Invoice = fields.Invoice.Trim(),
            ActivationsLeft = activations,
            Reason = fields.Reason,
            Note = fields.Note,
        }, problems);
    }

    /// <summary>把这台电脑加到“激活在”，已有其他机器时保留；已经在里面就原样返回。</summary>
    public static string AddMachine(WishItem item, string current, string machine)
    {
        current = current.Trim();
        if (LicenseMigration.IsActivatedOn(item with { ActivatedOn = current }, machine))
            return current;
        return current.Length > 0 ? $"{current}，{machine}" : machine;
    }

    // ---------- 账号 ----------

    public static string DescribeLogin(WishItem item, EffectiveLogin login, IEnumerable<WishItem> purchases)
    {
        if (login.Platform.Length == 0)
            return "厂家没填，账号只存在这一款上。填上厂家后就能和同厂家的插件共用账号。";
        var key = VendorCatalog.PlatformKey(login.Platform);
        var sharing = purchases.Count(p => !p.OwnLogin && VendorCatalog.PlatformKey(VendorCatalog.PlatformOf(p.Vendor)) == key);
        if (login.FromPlatform)
        {
            var display = VendorCatalog.DisplayVendor(item.Vendor);
            var same = login.Platform == display ? "" : $"{display} 的插件用 {login.Platform} 账号登录。";
            return same + (login.HasAny
                ? $"这是 {login.Platform} 的厂家账号，{sharing} 款插件共用，在这里修改会一起生效。"
                : $"{login.Platform} 还没有厂家账号，在这里填写后，{sharing} 款同厂家的插件都会使用它。");
        }
        return $"只用于这一款，不影响 {login.Platform} 的厂家账号。";
    }

    /// <summary>改成跟随厂家账号时，厂家账号还空着而这一款自己填过：问要不要把它设为厂家账号。</summary>
    public static bool OfferAsPlatform(WishItem item, PlatformAccount? shared) =>
        (shared == null || (shared.Account.Length == 0 && shared.Password.Length == 0 && !shared.Locked))
        && (item.Account.Length > 0 || item.Password.Length > 0) && !item.SecretsLocked;

    public static string OfferAsPlatformPrompt(string platform) =>
        $"把这一款的账号密码设为 {platform} 的厂家账号吗？同厂家其余跟随厂家账号的插件也会使用它。";

    public static string LoginModeChanged(WishItem item, bool own) =>
        own ? $"{item.Name} 改为单独账号" : $"{item.Name} 改为使用 {VendorCatalog.PlatformOf(item.Vendor)} 的厂家账号";

    /// <summary>详情里账号框改动后要写进厂家账号；没有变化时返回 null。厂家账号解不开时密码不动。</summary>
    public static PlatformAccount? ChangedPlatform(string platform, PlatformAccount? shared, string account, string password)
    {
        var locked = shared?.Locked ?? false;
        if ((shared?.Account ?? "") == account && (locked || (shared?.Password ?? "") == password))
            return null;
        return new PlatformAccount(platform, account, locked ? "" : password, shared?.Note ?? "", locked);
    }

    /// <summary>填过的账号（已购记录和厂家账号里的），用得多的排前面。只有账号，没有密码。</summary>
    public static IReadOnlyList<string> KnownAccounts(IEnumerable<WishItem> items, IEnumerable<PlatformAccount> platforms, params string[] typed) =>
        items.Select(w => w.Account)
            .Concat(platforms.Select(p => p.Account))
            .Concat(typed)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    // ---------- 移动和删除 ----------

    public static WishItem Bought(WishItem item, DateOnly today) => item with { Status = WishStatus.Purchased, PurchasedOn = today, PaidPrice = item.Price };

    public static string BoughtText(WishItem item) => $"已把 {item.Name} 移到已购账号，可以去那里填账号密码";

    public static string BackText(WishItem item) => $"已把 {item.Name} 移回心愿单" + (item.HasLogin ? "，账号密码仍然保留" : "");

    public static string WishDeletePrompt(WishItem item) => $"“{item.Name}”的原因、价格和备注都会删除，无法恢复。";

    public static string PurchaseDeletePrompt(WishItem item) => item.HasLogin
        ? $"“{item.Name}”保存的账号、密码和序列号都会删除，无法恢复。"
        : $"“{item.Name}”的记录会删除，无法恢复。";

    public static string BatchDeletePrompt(IReadOnlyCollection<WishItem> items)
    {
        var withLogin = items.Count(i => i.HasLogin);
        return $"会删除这 {items.Count} 条记录" + (withLogin > 0 ? $"，其中 {withLogin} 条保存的账号密码也一起删除" : "") + "，无法恢复。";
    }

    // ---------- 批量 ----------

    /// <summary>批量填写会覆盖的条数。厂家账号不会被覆盖（不一样时存成单独账号），这里只数单独账号和序列号。</summary>
    public static int Overwritten(IEnumerable<WishItem> selected, string account, string password, string serial) =>
        selected.Count(i =>
            (i.OwnLogin && account.Length > 0 && i.Account.Length > 0 && i.Account != account)
            || (i.OwnLogin && password.Length > 0 && (i.Password.Length > 0 || i.SecretsLocked) && i.Password != password)
            || (serial.Length > 0 && (i.Serial.Length > 0 || i.SecretsLocked) && i.Serial != serial));

    public static string OverwritePrompt(int selected, int overwritten) =>
        $"选中的 {selected} 款里，有 {overwritten} 款已经填了不一样的内容，保存后旧内容无法找回。";

    /// <summary>
    /// 批量填写：账号密码序列号交给 <see cref="PluginDatabase.SetLogins"/>，授权方式和激活在随后写到最新的记录上。
    /// license 为 null、activatedOn 为空表示不改。
    /// </summary>
    public static LoginAssignment? ApplyBatch(PluginDatabase database, IReadOnlyCollection<long> ids, string account, string password, string serial, LicenseKind? license, string activatedOn)
    {
        LoginAssignment? assigned = null;
        if (account.Length > 0 || password.Length > 0 || serial.Length > 0)
            assigned = database.SetLogins(ids, account, password, serial);
        if (license != null || activatedOn.Length > 0)
        {
            // SetLogins 刚改过账号，这里要读最新的，否则会写回旧内容。
            var set = ids.ToHashSet();
            foreach (var item in database.LoadWishes().Where(w => set.Contains(w.Id)))
            {
                database.SaveWish(item with
                {
                    LicenseKind = license ?? item.LicenseKind,
                    ActivatedOn = activatedOn.Length > 0 ? activatedOn : item.ActivatedOn,
                });
            }
        }
        return assigned;
    }

    public static string BatchSaved(int count, LoginAssignment? assigned) =>
        $"已保存到 {count} 款插件" + DescribeAssignment(assigned?.NewPlatforms, assigned?.Own ?? 0);

    public static string DescribeAssignment(IReadOnlyList<string>? newPlatforms, int own)
    {
        var text = newPlatforms is { Count: > 0 } ? $"；新建厂家账号：{string.Join("、", newPlatforms)}" : "";
        if (own > 0)
            text += $"；{own} 款和已有的厂家账号不一样，存成了单独账号";
        return text;
    }

    /// <summary>批量添加：粘贴的文本常常没写厂家，按插件名补上，才能归到厂家账号下。返回状态栏文字。</summary>
    public static string AddPurchases(PluginDatabase database, IEnumerable<PurchaseDraft> drafts, IReadOnlyList<PluginRow> rows)
    {
        var guessed = 0;
        var filled = drafts.Select(d =>
        {
            if (d.Vendor.Length > 0)
                return d;
            var vendor = NoteWishes.GuessVendor(d.Name, rows);
            if (vendor.Length > 0)
                guessed++;
            return d with { Vendor = vendor };
        }).ToList();
        var result = database.UpsertPurchased(filled);
        return $"新增 {result.Added} 款，更新 {result.Updated} 款"
            + (guessed > 0 ? $"，{guessed} 款按插件名自动填了厂家" : "")
            + DescribeAssignment(result.NewPlatforms, 0);
    }

    // ---------- 导出 ----------

    /// <summary>导出的内容：跟随厂家的插件导出厂家账号，这样导入后能重新归到厂家账号下。</summary>
    public static List<WishItem> ExportItems(IEnumerable<WishItem> wishes, IEnumerable<PlatformAccount> platforms)
    {
        var byKey = PurchaseLogin.ByKey(platforms);
        return wishes
            .Select(w => (Item: w, Login: PurchaseLogin.Resolve(w, byKey)))
            .Where(x => x.Login.HasAny || x.Item.Serial.Length > 0 || x.Item.SecretsLocked)
            .Select(x => x.Item with
            {
                Account = x.Login.Account,
                Password = x.Login.Password,
                SecretsLocked = x.Item.SecretsLocked || x.Login.Locked,
            })
            .ToList();
    }

    public static string Exported(int count, IReadOnlyList<string> skipped) =>
        $"已导出 {count - skipped.Count} 款的账号" + (skipped.Count > 0 ? $"，{skipped.Count} 款无法导出：{string.Join("、", skipped)}" : "");
}

/// <summary>心愿详情里的输入框内容。</summary>
public sealed record WishFields(string Name, string Price, string Target, string Lowest, string? Currency, string? Category, bool MustHave, string Reason, string Url, string Note);

/// <summary>已购详情里除账号密码以外的输入框内容。License 是授权方式下拉的序号。</summary>
public sealed record PurchaseFields(string Name, string Serial, string? Currency, string Paid, string Date, int License, string ActivatedOn, string Activations, string Invoice, string Reason, string Note)
{
    public override string ToString() => $"PurchaseFields {{ Name = {Name} }}";
}

/// <summary>
/// 厂家留空时按插件名猜：先找已安装的同名插件，再查厂家目录。同名属于多个厂家时不填。
/// 用户亲手清空的厂家这次运行里不再自动填回去。
/// </summary>
public sealed class VendorFiller
{
    private readonly HashSet<long> _cleared = [];

    /// <param name="guessed">这次是猜出来的（界面要把它填回输入框）。</param>
    public string Fill(WishItem old, string name, string vendor, IEnumerable<PluginRow> rows, out bool guessed)
    {
        guessed = false;
        vendor = vendor.Trim();
        if (vendor.Length > 0)
        {
            _cleared.Remove(old.Id);
            return vendor;
        }
        if (old.Vendor.Length > 0 && name == old.Name)
        {
            _cleared.Add(old.Id);
            return "";
        }
        if (_cleared.Contains(old.Id))
            return "";
        var guess = NoteWishes.GuessVendor(name, rows);
        guessed = guess.Length > 0;
        return guess;
    }
}

/// <summary>厂家账号表的一行：一个账号平台，以及跟随它的已购插件。</summary>
public sealed record PlatformEntry(string Platform, PlatformAccount Account, IReadOnlyList<WishItem> Followers)
{
    public bool IsILok => ILokFill.IsILok(Platform);

    /// <summary>这个平台的插件用 iLok 激活（iLok 那条自己除外）。</summary>
    public bool UsesILok => !IsILok && (VendorCatalog.UsesILok(Platform) || Followers.Any(f => VendorCatalog.UsesILok(f.Vendor)));

    public int Count => Followers.Count;
    public string CountText => Count > 0 ? $"{Count} 款" : "暂无";
    public string AccountText => Account.Account.Length > 0 ? Account.Account : "未填";
    public string PasswordMask => Account.Locked ? "无法解开" : Account.Password.Length > 0 ? "••••••••" : "未填";

    /// <summary>平台下面有哪些品牌，例如 Plugin Alliance 下的 brainworx、SPL。</summary>
    public string VendorsText
    {
        get
        {
            if (IsILok)
                return "iLok 账号，可以用上方“填入 iLok 账号”填到用 iLok 激活的厂家";
            var vendors = Followers.Select(f => VendorCatalog.DisplayVendor(f.Vendor)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var ilok = Followers.Any(f => VendorCatalog.UsesILok(f.Vendor)) || VendorCatalog.UsesILok(Platform) ? "用 iLok 激活 · " : "";
            if (vendors.Count == 0 || (vendors.Count == 1 && vendors[0] == Platform))
                return ilok + (Account.Note.Length > 0 ? Account.Note.ReplaceLineEndings(" ") : "厂家账号");
            return ilok + "含 " + string.Join("、", vendors.Take(4)) + (vendors.Count > 4 ? $" 等 {vendors.Count} 个品牌" : "");
        }
    }

    public string SearchText => string.Join(' ', Platform, Account.Account, Account.Note, VendorsText,
        string.Join(' ', Followers.Select(f => f.Name))).ToLowerInvariant();

    public string Subtitle => IsILok
        ? "在这里填一次 iLok 账号和密码，再点上方“填入 iLok 账号”，就能填到用 iLok 激活的厂家。以后改了密码，再点一次即可同步。"
        : Count > 0
            ? $"{Count} 款插件跟随这个账号。修改后它们一起生效；需要分开的，在插件详情里改成“单独账号”。"
            : "目前没有插件跟随这个账号。";

    public string ProductsText => Count > 0 ? string.Join("、", Followers.Select(f => f.Name)) : "无";

    public string DeletePrompt => $"{Platform} 的账号、密码和备注会删除，无法恢复。"
        + (Count > 0 ? $"\n\n跟随它的 {Count} 款插件会变成“未填”。设为单独账号的插件不受影响。" : "");

    public string Saved => $"已保存 {Platform} 的厂家账号" + (Count > 0 ? $"，{Count} 款插件一起生效" : "");

    public override string ToString() => $"PlatformEntry {{ Platform = {Platform} }}";
}

public static class PlatformListing
{
    public const string ILokFirst = "先在最上面的“iLok”里填好 iLok 账号和密码，再点“填入 iLok 账号”";
    public const string NoILokTargets = "已购插件里还没有用 iLok 激活的厂家";

    /// <summary>已有的厂家账号加上已购插件跟随的平台。iLok 那条总是放在最上面，没填也显示，方便先填它再一键填到各厂家。</summary>
    public static List<PlatformEntry> Build(IEnumerable<WishItem> purchases, IReadOnlyDictionary<string, PlatformAccount> accounts)
    {
        var followers = purchases
            .Where(p => !p.OwnLogin && VendorCatalog.PlatformOf(p.Vendor).Length > 0)
            .GroupBy(p => VendorCatalog.PlatformKey(VendorCatalog.PlatformOf(p.Vendor)))
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var rows = new List<PlatformEntry>();
        foreach (var (key, account) in accounts)
            rows.Add(new PlatformEntry(account.Platform, account, followers.GetValueOrDefault(key) ?? []));
        foreach (var (key, items) in followers)
        {
            if (!accounts.ContainsKey(key))
            {
                var platform = VendorCatalog.PlatformOf(items[0].Vendor);
                rows.Add(new PlatformEntry(platform, new PlatformAccount(platform), items));
            }
        }
        if (!rows.Any(r => r.IsILok))
            rows.Add(new PlatformEntry(ILokFill.Platform, new PlatformAccount(ILokFill.Platform), []));
        return rows
            .OrderBy(r => !r.IsILok)
            .ThenBy(r => r.Account.Account.Length == 0)
            .ThenBy(r => r.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>填入 iLok 账号前的检查：没填 iLok、解不开或没有目标时返回说明，可以填时返回 null。</summary>
    public static string? ILokProblem(PlatformAccount? ilok, IReadOnlyList<PlatformEntry> rows)
    {
        if (ilok == null || ilok.Account.Length == 0)
            return ILokFirst;
        if (ilok.Locked)
            return WishListing.ILokLockedText;
        return rows.Any(r => r.UsesILok) ? null : NoILokTargets;
    }

    /// <summary>每个用 iLok 激活的厂家会怎么改，以及默认要不要勾选。</summary>
    public static List<(PlatformEntry Row, ILokFillResult Result, string Text, bool Checked)> ILokPlan(PlatformAccount ilok, IEnumerable<PlatformEntry> rows) =>
        rows.Where(r => r.UsesILok)
            .Select(r =>
            {
                var result = ILokFill.Apply(ilok, r.Account);
                return (r, result, $"{r.Platform}（{r.Count} 款）：{ILokFill.Describe(result.Action)}",
                    result.Action is ILokFillAction.Filled or ILokFillAction.Synced or ILokFillAction.NoteAdded);
            })
            .ToList();

    public static string ILokPrompt(PlatformAccount ilok) =>
        $"把 iLok 账号 {ilok.Account} 填到下面这些用 iLok 激活的厂家。已经填了别的账号的不会覆盖，只在备注里记下 iLok 账号名；密码不会写进备注。";

    public static string ILokFilled(IReadOnlyList<string> changed) =>
        changed.Count > 0 ? $"已填入 iLok 账号：{string.Join("、", changed)}" : "没有需要改动的厂家";
}
