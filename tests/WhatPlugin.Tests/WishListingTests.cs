using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>心愿单和已购账号的文字、筛选、字段检查和厂家账号（Windows 版和跨平台版共用）。</summary>
public sealed class WishListingTests
{
    private static readonly Dictionary<string, PlatformAccount> NoPlatforms = [];

    private static WishRow Row(WishItem item, bool installed = false) => new(item, installed, PurchaseLogin.Resolve(item, NoPlatforms));

    [Fact]
    public void WishRowsDescribeThemselvesAndSortByPriorityPriceOrRecent()
    {
        var now = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Row(new WishItem(1, "Pro-Q 4", "FabFilter", 1299, Priority: WishPriority.CanWait, CreatedUtc: now.AddDays(-3), TargetPrice: 1500), installed: true),
            Row(new WishItem(2, "soothe2", Reason: "人声\n共振", Priority: WishPriority.MustHave, CreatedUtc: now.AddDays(-5))),
            Row(new WishItem(3, "Ozone 11", "iZotope", 29.99m, "$", CreatedUtc: now.AddDays(-1))),
        };
        var proq = rows[0];
        Assert.Equal(("¥1,299", "到目标价", "我还能忍"), (proq.PriceText, proq.ReachedTarget ? "到目标价" : "", proq.PriorityText));
        Assert.Equal(("厂家未填", "未定价", "人声 共振"), (rows[1].VendorText, rows[1].PriceText, rows[1].ReasonLine));
        Assert.Equal("还没写为什么想买", proq.ReasonLine);

        Assert.Equal(["soothe2", "Ozone 11", "Pro-Q 4"], WishListing.Wishes(rows, "", WishListing.SortByPriority).Select(r => r.Name));
        Assert.Equal(["Ozone 11", "Pro-Q 4", "soothe2"], WishListing.Wishes(rows, "", WishListing.SortByPrice).Select(r => r.Name));
        Assert.Equal(["Ozone 11", "Pro-Q 4", "soothe2"], WishListing.Wishes(rows, "", WishListing.SortByRecent).Select(r => r.Name));
        Assert.Equal(["soothe2"], WishListing.Wishes(rows, " 共振 ", WishListing.SortByRecent).Select(r => r.Name));

        Assert.Equal("3 款，全买下来约 ¥1,299 + $29.99", WishListing.WishSubtitle(rows));
        Assert.Equal("记下想买的插件、原因和价格，买了之后转到已购账号", WishListing.WishSubtitle([]));
        Assert.Equal("没有符合搜索的心愿", WishListing.WishEmptyText(3));
    }

    [Fact]
    public void InstalledMatchesNameWithOrWithoutVendor()
    {
        var installed = new HashSet<string> { Normalizer.Name("Pro-Q 4"), Normalizer.Vendor("Valhalla DSP") + Normalizer.Name("Room") };
        Assert.True(WishListing.IsInstalled(new WishItem(1, "pro-q 4"), installed));
        Assert.True(WishListing.IsInstalled(new WishItem(2, "Room", "Valhalla DSP"), installed));
        Assert.False(WishListing.IsInstalled(new WishItem(3, "Room"), installed));
    }

    [Fact]
    public void PurchaseFiltersUseVendorStateAndSearchButNeverThePassword()
    {
        var withLogin = Row(new WishItem(1, "Pro-C 2", "FabFilter", Status: WishStatus.Purchased, Account: "me@example.com", Password: "secret-pass", PaidPrice: 899, OwnLogin: true));
        var serialOnly = Row(new WishItem(2, "Ozone 11", "iZotope", Status: WishStatus.Purchased, Serial: "SN-1"));
        var empty = Row(new WishItem(3, "Decapitator", "Soundtoys", Status: WishStatus.Purchased));
        WishRow[] rows = [withLogin, serialOnly, empty];

        Assert.Equal([WishListing.AllVendors, "FabFilter", "iZotope", "Soundtoys"], WishListing.VendorOptions(rows));
        Assert.Equal(["Pro-C 2", "Ozone 11"], rows.Where(r => WishListing.MatchesPurchase(r, "", WishListing.WithLogin, "")).Select(r => r.Name));
        Assert.Equal(["Decapitator"], rows.Where(r => WishListing.MatchesPurchase(r, WishListing.AllVendors, WishListing.WithoutLogin, "")).Select(r => r.Name));
        Assert.Equal(["Ozone 11"], rows.Where(r => WishListing.MatchesPurchase(r, "iZotope", WishListing.AllStates, "")).Select(r => r.Name));
        Assert.Single(rows, r => WishListing.MatchesPurchase(r, "", "", "example"));
        Assert.DoesNotContain(rows, r => WishListing.MatchesPurchase(r, "", "", "secret-pass"));

        Assert.Equal(("me@example.com", "••••••••", "未填", "未填"), (withLogin.AccountText, withLogin.PasswordMask, empty.AccountText, empty.PasswordMask));
        Assert.Equal("3 款，2 款已填账号，已花 ¥899，密码和序列号加密保存在本机", WishListing.AccountSubtitle(rows));
        Assert.Equal("密码和序列号加密保存在本机", WishListing.AccountSubtitle([]));
    }

    [Fact]
    public void ParsingKeepsOldValuesForBadFieldsAndSavesTheRest()
    {
        var old = new WishItem(7, "Pro-Q 4", "FabFilter", 1299, TargetPrice: 999, Category: "均衡");
        var (wish, problems) = WishListing.ParseWish(old, new WishFields(" ", "¥1,099", "abc", "", "$", PurchaseAdvisor.AutoCategory, true, "动态均衡", " pro-q.com ", "等黑五"));
        Assert.Equal(["插件名不能为空", "目标价只能填数字"], problems);
        Assert.Equal(("Pro-Q 4", 1099m, 999m, (decimal?)null, "$", "", WishPriority.MustHave, "pro-q.com"),
            (wish.Name, wish.Price, wish.TargetPrice, wish.LowestPrice, wish.Currency, wish.Category, wish.Priority, wish.Url));
        Assert.Equal("插件名不能为空，目标价只能填数字，这一项没有保存", WishListing.Saved(wish.Name, problems, ""));
        Assert.Equal("已保存 Pro-Q 4，厂家自动识别为 FabFilter（不对可以直接改）", WishListing.Saved("Pro-Q 4", [], "FabFilter"));
        Assert.Equal("已保存 Pro-C 2，账号密码存在 FabFilter 的厂家账号里", WishListing.Saved("Pro-C 2", [], "", "FabFilter"));

        var bought = new WishItem(8, "Ozone 11", Status: WishStatus.Purchased, PurchasedOn: new DateOnly(2026, 1, 2), ActivationsLeft: 2, Serial: "OLD", SecretsLocked: true);
        var (purchase, purchaseProblems) = WishListing.ParsePurchase(bought, new PurchaseFields("Ozone 11", "NEW", "¥", "1,200", "昨天", 1, " Studio ", "-1", " INV-9 ", "", ""));
        Assert.Equal(["日期格式应为 2026-11-06", "剩余激活次数只能填整数"], purchaseProblems);
        // 解不开的序列号不能被界面上的空白覆盖。
        Assert.Equal(("OLD", 1200m, new DateOnly(2026, 1, 2), 2, LicenseKind.ILok, "Studio", "INV-9"),
            (purchase.Serial, purchase.PaidPrice, purchase.PurchasedOn!.Value, purchase.ActivationsLeft!.Value, purchase.LicenseKind, purchase.ActivatedOn, purchase.Invoice));
        Assert.Null(WishListing.ParsePurchase(bought, new PurchaseFields("Ozone 11", "", "¥", "", "", 0, "", "", "", "", "")).Updated.PurchasedOn);

        Assert.Equal("https://example.com/buy", WishListing.WishUrl(" example.com/buy "));
        Assert.Equal("http://a.b", WishListing.WishUrl("http://a.b"));
        Assert.Equal("Mac-A，Studio PC", WishListing.AddMachine(bought, "Mac-A", "Studio PC"));
        Assert.Equal("Studio PC", WishListing.AddMachine(bought, "Studio PC", "Studio PC"));
    }

    [Fact]
    public void VendorIsGuessedFromInstalledPluginsButNotRefilledAfterClearing()
    {
        var rows = new[] { Plugin("Pro-Q 4", "FabFilter") };
        var filler = new VendorFiller();
        var item = new WishItem(5, "Pro-Q 4");

        Assert.Equal("FabFilter", filler.Fill(item, "Pro-Q 4", "", rows, out var guessed));
        Assert.True(guessed);
        var saved = item with { Vendor = "FabFilter" };
        // 用户把厂家清空：这次不再填回去，改名后也不填。
        Assert.Equal("", filler.Fill(saved, "Pro-Q 4", " ", rows, out guessed));
        Assert.False(guessed);
        Assert.Equal("", filler.Fill(saved with { Vendor = "" }, "Pro-Q 4", "", rows, out guessed));
        Assert.Equal("Eventide", filler.Fill(saved, "Pro-Q 4", "Eventide", rows, out guessed));
        Assert.False(guessed);
    }

    [Fact]
    public void LoginTextsOverwriteCountsAndKnownAccounts()
    {
        var bx = new WishItem(1, "bx_console", "brainworx", Status: WishStatus.Purchased);
        var spl = new WishItem(2, "SPL Twin Tube", "SPL", Status: WishStatus.Purchased);
        var own = new WishItem(3, "bx_limiter", "brainworx", Status: WishStatus.Purchased, OwnLogin: true, Account: "other@example.com", Password: "x");
        var platforms = PurchaseLogin.ByKey([new PlatformAccount("Plugin Alliance", "pa@example.com", "pw")]);
        WishItem[] purchases = [bx, spl, own];

        Assert.Equal("这是 Plugin Alliance 的厂家账号，2 款插件共用，在这里修改会一起生效。",
            WishListing.DescribeLogin(bx, PurchaseLogin.Resolve(bx, platforms), purchases));
        Assert.Equal("只用于这一款，不影响 Plugin Alliance 的厂家账号。", WishListing.DescribeLogin(own, PurchaseLogin.Resolve(own, platforms), purchases));
        var unknown = new WishItem(4, "Mystery");
        Assert.StartsWith("厂家没填", WishListing.DescribeLogin(unknown, PurchaseLogin.Resolve(unknown, platforms), purchases));

        // 跟随厂家的不算覆盖；单独账号的账号不一样才算；序列号都算。
        Assert.Equal(1, WishListing.Overwritten(purchases, "new@example.com", "", ""));
        Assert.Equal(0, WishListing.Overwritten(purchases, "other@example.com", "", ""));
        Assert.Equal(0, WishListing.Overwritten(purchases, "", "", "SN"));
        Assert.Equal(1, WishListing.Overwritten([spl with { Serial = "OLD" }], "", "", "SN"));

        Assert.Equal(["pa@example.com", "other@example.com", "typed@example.com"],
            WishListing.KnownAccounts(purchases, platforms.Values, "typed@example.com", " PA@example.com ", ""));
        Assert.True(WishListing.OfferAsPlatform(own, null));
        Assert.False(WishListing.OfferAsPlatform(own, platforms.Values.First()));
        Assert.Null(WishListing.ChangedPlatform("Plugin Alliance", platforms.Values.First(), "pa@example.com", "pw"));
        Assert.Equal("new", WishListing.ChangedPlatform("Plugin Alliance", platforms.Values.First(), "pa@example.com", "new")!.Password);
        var locked = new PlatformAccount("Plugin Alliance", "pa@example.com", "", Locked: true);
        Assert.Null(WishListing.ChangedPlatform("Plugin Alliance", locked, "pa@example.com", "typed"));

        Assert.Equal("；新建厂家账号：Plugin Alliance、FabFilter；2 款和已有的厂家账号不一样，存成了单独账号",
            WishListing.DescribeAssignment(["Plugin Alliance", "FabFilter"], 2));
        Assert.Equal("会删除这 2 条记录，其中 1 条保存的账号密码也一起删除，无法恢复。", WishListing.BatchDeletePrompt([bx, own]));
    }

    [Fact]
    public void ExportUsesThePlatformLoginAndSkipsLockedOnes()
    {
        var bx = new WishItem(1, "bx_console", "brainworx", Status: WishStatus.Purchased);
        var plain = new WishItem(2, "Decapitator", "Soundtoys", Status: WishStatus.Purchased);
        var locked = new WishItem(3, "Ozone 11", "iZotope", Status: WishStatus.Purchased, OwnLogin: true, SecretsLocked: true);
        var items = WishListing.ExportItems([bx, plain, locked], [new PlatformAccount("Plugin Alliance", "pa@example.com", "pw")]);
        Assert.Equal(["bx_console", "Ozone 11"], items.Select(i => i.Name));
        Assert.Equal(("pa@example.com", "pw"), (items[0].Account, items[0].Password));
        var text = PurchaseText.Format(items, out var skipped);
        Assert.Equal(["Ozone 11"], skipped);
        Assert.Contains("bx_console\tbrainworx\tpa@example.com\tpw\t", text);
        Assert.Equal("已导出 1 款的账号，1 款无法导出：Ozone 11", WishListing.Exported(items.Count, skipped));
        Assert.Contains(AppPlatform.IsMac ? "钥匙串" : "Windows 账号", WishListing.ExportWarning);
        Assert.Contains(WishListing.ForeignKeyOwner, WishListing.PurchaseLockedText);
    }

    [Fact]
    public void PlatformsListILokFirstAndFillOnlyILokVendors()
    {
        var purchases = new[]
        {
            new WishItem(1, "bx_console", "brainworx", Status: WishStatus.Purchased),
            new WishItem(2, "H3000", "Eventide", Status: WishStatus.Purchased),
            new WishItem(3, "Blackhole", "Eventide", Status: WishStatus.Purchased),
            new WishItem(4, "Own", "Eventide", Status: WishStatus.Purchased, OwnLogin: true),
        };
        var accounts = PurchaseLogin.ByKey([new PlatformAccount("FabFilter", "ff@example.com"), new PlatformAccount("Plugin Alliance", "", "", "注册邮箱 a")]);
        var rows = PlatformListing.Build(purchases, accounts);
        Assert.Equal(["iLok", "FabFilter", "Eventide", "Plugin Alliance"], rows.Select(r => r.Platform));
        var eventide = rows[2];
        Assert.Equal(("2 款", "未填", "用 iLok 激活 · 厂家账号", "Blackhole、H3000"), (eventide.CountText, eventide.AccountText, eventide.VendorsText, eventide.ProductsText));
        Assert.True(eventide.UsesILok);
        Assert.False(rows[0].UsesILok);
        // brainworx 在目录里显示为 Plugin Alliance：只有一个品牌时显示备注。
        Assert.Equal("注册邮箱 a", rows[3].VendorsText);
        Assert.Contains("2 款插件跟随这个账号", eventide.Subtitle);
        Assert.Contains("跟随它的 2 款插件会变成“未填”", eventide.DeletePrompt);

        Assert.Equal(PlatformListing.ILokFirst, PlatformListing.ILokProblem(rows[0].Account, rows));
        var ilok = new PlatformAccount("iLok", "me-ilok", "pw");
        Assert.Equal(WishListing.ILokLockedText, PlatformListing.ILokProblem(ilok with { Locked = true }, rows));
        Assert.Equal(PlatformListing.NoILokTargets, PlatformListing.ILokProblem(ilok, rows.Where(r => r.Platform != "Eventide").ToList()));
        Assert.Null(PlatformListing.ILokProblem(ilok, rows));
        var plan = PlatformListing.ILokPlan(ilok, rows);
        Assert.Equal([("Eventide（2 款）：空着，会填入 iLok 账号和密码", true)], plan.Select(p => (p.Text, p.Checked)));
        Assert.Equal("已填入 iLok 账号：Eventide", PlatformListing.ILokFilled(["Eventide"]));
    }

    [Fact]
    public void BatchApplyAndPasteAddWriteToTheDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pv-wish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "t.db");
        Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
        try
        {
            using (var database = new PluginDatabase(path))
            {
                var a = database.AddWish(new WishItem(0, "bx_console", "brainworx", Status: WishStatus.Purchased));
                var b = database.AddWish(new WishItem(0, "Ozone 11", "iZotope", Status: WishStatus.Purchased, ActivatedOn: "Old PC"));
                var assigned = WishListing.ApplyBatch(database, [a.Id, b.Id], "me@example.com", "pw", "", LicenseKind.ILok, "");
                Assert.Equal(["Native Instruments", "Plugin Alliance"], assigned!.NewPlatforms.Order());
                var saved = database.LoadWishes().ToDictionary(w => w.Name);
                Assert.Equal((LicenseKind.ILok, "Old PC"), (saved["Ozone 11"].LicenseKind, saved["Ozone 11"].ActivatedOn));
                Assert.Equal("me@example.com", PurchaseLogin.Resolve(saved["bx_console"], PurchaseLogin.ByKey(database.LoadPlatformAccounts())).Account);
                Assert.Null(WishListing.ApplyBatch(database, [a.Id], "", "", "", null, "Studio"));
                Assert.Equal("Studio", database.LoadWishes().First(w => w.Id == a.Id).ActivatedOn);

                // 粘贴的没写厂家：按已安装的同名插件补上。
                var status = WishListing.AddPurchases(database, PurchaseText.Parse("Pro-Q 4\nOzone 11, iZotope, , , SN-2").Drafts, [Plugin("Pro-Q 4", "FabFilter")]);
                Assert.Equal("新增 1 款，更新 1 款，1 款按插件名自动填了厂家", status);
                Assert.Equal("FabFilter", database.LoadWishes().First(w => w.Name == "Pro-Q 4").Vendor);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static long _nextId;

    private static PluginRow Plugin(string name, string vendor) =>
        new(++_nextId, Normalizer.Name(name), name, vendor, PluginKind.Effect, ["均衡"], ClassificationSource.Seed, false,
            [new MergedFile("c:\\" + name, PluginFormat.Vst3, Arch.X64, "1", "", ScanStatus.Loaded, null)], PluginUserData.Empty);
}
