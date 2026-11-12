using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public sealed class InsightsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-i-" + Guid.NewGuid().ToString("N"));
    private static long _nextId;

    public InsightsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static PluginRow Row(string name, string[] tags, MixAttributes? mix = null, int rating = 0, bool hidden = false,
        bool missing = false, PluginKind kind = PluginKind.Effect) =>
        new(++_nextId, Normalizer.Name(name), name, "Acme", kind, tags, ClassificationSource.Seed, missing, [],
            new PluginUserData(rating, "", [], null, null, null, hidden, "", mix));

    private static MergedPlugin Merged(string key, string name) =>
        new(key, name, "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["压缩"], ClassificationSource.SdkCategory),
            [new MergedFile(key + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx", ScanStatus.Loaded, null)]);

    private static PluginUsage Used(DateTime when, int projects = 1) => new(projects, projects, when, ["工程"]);

    private static List<string> Recommend(string need, params PluginRow[] rows) =>
        Recommender.Recommend(need, rows, 100).Items.Select(i => i.Plugin.Name).ToList();

    // ---------- 混音属性和推荐 ----------

    [Fact]
    public void LowLatencyIsNotTheDelayEffect()
    {
        var result = Recommender.Recommend("低延迟的人声压缩",
            [Row("Fast Comp", ["压缩", "人声"], new MixAttributes(LatencyLevel.Low)), Row("Echo", ["延迟"])], 100);
        Assert.Equal(["Fast Comp"], result.Items.Select(i => i.Plugin.Name));
        Assert.DoesNotContain("延迟", result.Understood);
        Assert.Contains("低延迟", result.Understood);
    }

    [Fact]
    public void AttributeRanksFilledAndExcludesConflictsButKeepsUnfilled()
    {
        var names = Recommend("低延迟压缩",
            Row("Unfilled", ["压缩"]),
            Row("Slow", ["压缩"], new MixAttributes(LatencyLevel.High)),
            Row("Fast", ["压缩"], new MixAttributes(LatencyLevel.Zero)));
        Assert.Equal(["Fast", "Unfilled"], names);
    }

    [Fact]
    public void ZeroLatencyRejectsLowLatency()
    {
        var names = Recommend("零延迟压缩",
            Row("Low", ["压缩"], new MixAttributes(LatencyLevel.Low)),
            Row("Zero", ["压缩"], new MixAttributes(LatencyLevel.Zero)));
        Assert.Equal(["Zero"], names);
    }

    [Fact]
    public void AttributeOnlyQueryReturnsOnlyMarkedPlugins()
    {
        var result = Recommender.Recommend("不吃CPU",
            [Row("Light", ["EQ"], new MixAttributes(Cpu: CpuLevel.Light)), Row("Blank", ["EQ"]), Row("Heavy", ["EQ"], new MixAttributes(Cpu: CpuLevel.Heavy))], 100);
        Assert.Equal(["Light"], result.Items.Select(i => i.Plugin.Name));
        Assert.Contains("你标记为", string.Join(' ', result.Items[0].Reasons));
        Assert.Contains("2 款填过", result.AttributeHint);
    }

    [Fact]
    public void MarkedSidechainCountsEvenWhenTheNameDoesNotSayIt()
    {
        var names = Recommend("侧链",
            Row("Plain Comp", ["压缩"], new MixAttributes(Sidechain: Tri.Yes)),
            Row("Other Comp", ["压缩"]),
            Row("No SC", ["压缩"], new MixAttributes(Sidechain: Tri.No)));
        Assert.Equal(["Plain Comp"], names);
    }

    [Fact]
    public void NegatedAttributeExcludesMarkedPlugins()
    {
        var names = Recommend("压缩 不要过采样",
            Row("Has", ["压缩"], new MixAttributes(Oversampling: Tri.Yes)),
            Row("Blank", ["压缩"]));
        Assert.Equal(["Blank"], names);
    }

    [Fact]
    public void DelayEffectQueryStillWorks()
    {
        Assert.Equal(["Echo"], Recommend("延迟", Row("Echo", ["延迟"]), Row("Comp", ["压缩"], new MixAttributes(LatencyLevel.Low))));
    }

    [Fact]
    public void MixAttributesPersistAndEmptyBecomesNull()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo")]);
        var row = Assert.Single(db.LoadPlugins());

        var mix = new MixAttributes(LatencyLevel.Zero, CpuLevel.Heavy, Tri.No, Tri.Yes, Tri.Yes);
        db.SaveUserData(row.Id, (row.User with { Mix = mix }).Normalized());
        db.SaveMerged([Merged("foo", "Foo")]);
        Assert.Equal(mix, Assert.Single(db.LoadPlugins()).User.Mix);

        db.SaveUserData(row.Id, (row.User with { Mix = new MixAttributes() }).Normalized());
        Assert.Null(Assert.Single(db.LoadPlugins()).User.Mix);
    }

    [Fact]
    public void MixAttributesKeepAMissingPluginsRecord()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo")]);
        var row = Assert.Single(db.LoadPlugins());
        db.SaveUserData(row.Id, row.User with { Mix = new MixAttributes(LatencyLevel.Low) });
        db.SaveMerged([]);
        db.DeleteMissingWithoutUserData();
        Assert.Single(db.LoadPlugins());
    }

    [Fact]
    public void OldBackupDoesNotWipeMixAttributes()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo")]);
        var row = Assert.Single(db.LoadPlugins());
        var mix = new MixAttributes(LatencyLevel.Low);
        db.SaveUserData(row.Id, row.User with { Mix = mix });

        var old = """{"Version":1,"ExportedUtc":"2026-01-01T00:00:00Z","Plugins":[{"Key":"foo","Name":"Foo","Vendor":"Acme","Rating":5,"Note":"n","MyTags":[]}]}""";
        db.ImportUserData(UserDataBackup.Parse(old));

        var after = Assert.Single(db.LoadPlugins());
        Assert.Equal(5, after.User.Rating);
        Assert.Equal(mix, after.User.Mix);
    }

    // ---------- 迁移 ----------

    [Fact]
    public void MigratesFromVersion6AndKeepsData()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var db = new PluginDatabase(path))
        {
            db.SaveMerged([Merged("foo", "Foo")]);
            db.AddWish(new WishItem(0, "Old Wish", Price: 10m));
        }
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE chain_steps; DROP TABLE chains;
                ALTER TABLE plugins DROP COLUMN mix_json;
                ALTER TABLE wishes DROP COLUMN license_kind;
                ALTER TABLE wishes DROP COLUMN activated_on;
                ALTER TABLE wishes DROP COLUMN activations_left;
                ALTER TABLE wishes DROP COLUMN target_price;
                ALTER TABLE wishes DROP COLUMN lowest_price;
                ALTER TABLE wishes DROP COLUMN category;
                PRAGMA user_version = 6;
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using (var db = new PluginDatabase(path))
        {
            Assert.Null(Assert.Single(db.LoadPlugins()).User.Mix);
            var wish = Assert.Single(db.LoadWishes());
            Assert.Equal(10m, wish.Price);
            Assert.Equal(LicenseKind.Unknown, wish.LicenseKind);
            Assert.Empty(db.LoadChains());
        }
    }

    // ---------- 同类对比 ----------

    [Fact]
    public void ComparisonSortsByUsageAndLeavesOutHiddenMissingAndInstruments()
    {
        var used = Row("Used", ["压缩"]);
        var rated = Row("Rated", ["压缩"], rating: 5);
        var plain = Row("Plain", ["压缩"]);
        var rows = new[] { plain, rated, used, Row("Hidden", ["压缩"], hidden: true), Row("Gone", ["压缩"], missing: true), Row("Synth", ["压缩"], kind: PluginKind.Instrument) };
        var usage = new Dictionary<string, PluginUsage> { [used.Key] = Used(DateTime.UtcNow.AddDays(-5), 7) };

        var compared = PluginComparison.Rows(rows, usage, "压缩");
        Assert.Equal(["Used", "Rated", "Plain"], compared.Select(r => r.Plugin.Name));
        var summary = PluginComparison.Summarize(compared, DateTime.UtcNow);
        Assert.Equal(new ComparisonSummary(3, 1, 2, 2), summary);
    }

    [Fact]
    public void CategoriesCountAndAddUncategorizedLast()
    {
        var categories = PluginComparison.Categories([Row("A", ["压缩"]), Row("B", ["压缩", "EQ"]), Row("C", [])]);
        Assert.Equal(2, categories.Single(c => c.Tag == "压缩").Count);
        Assert.Equal(new ComparisonCategory(PluginComparison.Uncategorized, 1), categories[^1]);
        Assert.Equal(["C"], PluginComparison.Rows([Row("A", ["压缩"]), Row("C", [])], new Dictionary<string, PluginUsage>(), PluginComparison.Uncategorized).Select(r => r.Plugin.Name));
    }

    [Fact]
    public void OldUsageIsNotRecent()
    {
        var old = Row("Old", ["压缩"]);
        var usage = new Dictionary<string, PluginUsage> { [old.Key] = Used(DateTime.UtcNow.AddMonths(-13)) };
        var summary = PluginComparison.Summarize(PluginComparison.Rows([old], usage, "压缩"), DateTime.UtcNow);
        Assert.Equal(0, summary.UsedRecently);
        Assert.Equal(0, summary.NeverUsed);
    }

    // ---------- 插件链 ----------

    [Fact]
    public void ChainKeepsOrderRepeatsAndNotes()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        var chain = db.AddChain("  人声总线链 ");
        Assert.Equal("人声总线链", chain.Name);
        db.SaveChain(chain.Id, "先 EQ", [new ChainStep("b", "B", "第一步"), new ChainStep("a", "A"), new ChainStep("b", "B", "再来一次")]);

        var loaded = Assert.Single(db.LoadChains());
        Assert.Equal("先 EQ", loaded.Note);
        Assert.Equal(["第一步", "", "再来一次"], loaded.Steps.Select(s => s.Note));
        Assert.Equal(["b", "a", "b"], loaded.Steps.Select(s => s.PluginKey));
        Assert.Throws<ArgumentException>(() => db.AddChain("人声总线链"));
        Assert.Throws<ArgumentException>(() => db.AddChain("  "));

        var other = db.AddChain("鼓组");
        Assert.Throws<ArgumentException>(() => db.RenameChain(other.Id, "人声总线链"));

        db.DeleteChain(chain.Id);
        Assert.Equal(["鼓组"], db.LoadChains().Select(c => c.Name));
    }

    [Fact]
    public void ChainStepsResolveStates()
    {
        var installed = Row("Here", ["EQ"]);
        var hidden = Row("Hidden", ["EQ"], hidden: true);
        var synth = Row("Synth", [], kind: PluginKind.Instrument);
        var chain = new PluginChain(1, "c", "", [
            new ChainStep(installed.Key, "Here"),
            new ChainStep(hidden.Key, "Hidden"),
            new ChainStep(synth.Key, "Synth"),
            new ChainStep("gone", "Gone"),
            new ChainStep("old-key", "Here"),
        ]);
        var states = ChainReport.Resolve(chain, [installed, hidden, synth]).Select(s => s.State);
        Assert.Equal([ChainStepState.Installed, ChainStepState.Blacklisted, ChainStepState.Instrument, ChainStepState.Missing, ChainStepState.Installed], states);
    }

    [Fact]
    public void BackupRoundTripsChainsAndMixAndKeepsOldFormatReadable()
    {
        var path = Path.Combine(_dir, "a.db");
        string json;
        using (var db = new PluginDatabase(path))
        {
            db.SaveMerged([Merged("foo", "Foo")]);
            var row = Assert.Single(db.LoadPlugins());
            db.SaveUserData(row.Id, row.User with { Mix = new MixAttributes(LatencyLevel.Zero) });
            var chain = db.AddChain("母带");
            db.SaveChain(chain.Id, "注意", [new ChainStep("foo", "Foo", "轻压")]);
            db.AddWish(new WishItem(0, "W", LicenseKind: LicenseKind.ILok, ActivatedOn: "Studio", ActivationsLeft: 2, TargetPrice: 5m, LowestPrice: 3m, Category: "EQ"));
            json = UserDataBackup.Serialize(db.LoadPlugins(), db.LoadWishes(), db.LoadFavoriteLists(), db.LoadChains());
        }

        using (var db = new PluginDatabase(Path.Combine(_dir, "b.db")))
        {
            db.SaveMerged([Merged("foo", "Foo")]);
            db.ImportUserData(UserDataBackup.Parse(json));
            db.ImportWishes(UserDataBackup.ParseWishes(json));
            Assert.Equal(1, db.ImportChains(UserDataBackup.ParseChains(json)));
            Assert.Equal(1, db.ImportChains(UserDataBackup.ParseChains(json)));

            Assert.Equal(LatencyLevel.Zero, Assert.Single(db.LoadPlugins()).User.Mix?.Latency);
            var chain = Assert.Single(db.LoadChains());
            Assert.Equal("轻压", Assert.Single(chain.Steps).Note);
            var wish = Assert.Single(db.LoadWishes());
            Assert.Equal((LicenseKind.ILok, "Studio", 2, 5m, 3m, "EQ"), (wish.LicenseKind, wish.ActivatedOn, wish.ActivationsLeft!.Value, wish.TargetPrice!.Value, wish.LowestPrice!.Value, wish.Category));
        }
        Assert.Empty(UserDataBackup.ParseChains("""{"Version":1,"ExportedUtc":"2026-01-01T00:00:00Z","Plugins":[]}"""));
    }

    [Fact]
    public void OldWishBackupDoesNotWipeLicense()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.AddWish(new WishItem(0, "W", LicenseKind: LicenseKind.Waves, ActivatedOn: "Studio", TargetPrice: 5m));
        var old = """{"Version":1,"ExportedUtc":"2026-01-01T00:00:00Z","Plugins":[],"Wishes":[{"Name":"W","Reason":"新原因","Status":"Wished","Priority":2,"CreatedUtc":"2026-01-01T00:00:00Z"}]}""";
        db.ImportWishes(UserDataBackup.ParseWishes(old));
        var wish = Assert.Single(db.LoadWishes());
        Assert.Equal("新原因", wish.Reason);
        Assert.Equal(LicenseKind.Waves, wish.LicenseKind);
        Assert.Equal("Studio", wish.ActivatedOn);
        Assert.Equal(5m, wish.TargetPrice);
    }

    // ---------- 换机清单 ----------

    private static WishItem Bought(string name, LicenseKind kind = LicenseKind.Unknown, string on = "", int? left = null, string account = "", string password = "", string serial = "") =>
        new(1, name, "Acme", Status: WishStatus.Purchased, LicenseKind: kind, ActivatedOn: on, ActivationsLeft: left, Account: account, Password: password, Serial: serial);

    [Fact]
    public void ChecklistGroupsByActivationAndNeverContainsSecrets()
    {
        var account = "fake-" + Guid.NewGuid().ToString("N") + "@test.invalid";
        var password = "pw-" + Guid.NewGuid().ToString("N");
        var serial = "SN-" + Guid.NewGuid().ToString("N");
        var text = LicenseMigration.BuildChecklist([
            Bought("Here iLok", LicenseKind.ILok, "笔记本，Studio", 2, account, password, serial),
            Bought("Here Waves", LicenseKind.Waves, "studio"),
            Bought("Elsewhere", LicenseKind.Serial, "笔记本", account: account),
            Bought("Login", LicenseKind.Account, account: account, password: password),
            Bought("Unknown", serial: serial),
            new WishItem(2, "Only Wished", ActivatedOn: "Studio"),
        ], "Studio", new DateTime(2026, 11, 7, 12, 0, 0));

        Assert.DoesNotContain(account, text);
        Assert.DoesNotContain(password, text);
        Assert.DoesNotContain(serial, text);
        Assert.DoesNotContain("Only Wished", text);

        var first = text.IndexOf("先在这台电脑上停用", StringComparison.Ordinal);
        var second = text.IndexOf("激活在其他机器", StringComparison.Ordinal);
        var third = text.IndexOf("没记录激活", StringComparison.Ordinal);
        var fourth = text.IndexOf("还没填授权方式", StringComparison.Ordinal);
        Assert.True(first < text.IndexOf("Here iLok", StringComparison.Ordinal) && text.IndexOf("Here Waves", StringComparison.Ordinal) < second);
        Assert.Contains("iLok License Manager", text);
        Assert.Contains("Waves Central", text);
        Assert.Contains("还剩 2 次", text);
        Assert.True(second < text.IndexOf("Elsewhere", StringComparison.Ordinal) && text.IndexOf("Elsewhere", StringComparison.Ordinal) < third);
        Assert.True(third < text.IndexOf("Login", StringComparison.Ordinal) && text.IndexOf("Login", StringComparison.Ordinal) < fourth);
        Assert.True(fourth < text.IndexOf("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void ChecklistSkipsEmptySections()
    {
        var text = LicenseMigration.BuildChecklist([Bought("A", LicenseKind.ILok, "Studio")], "Studio", DateTime.Now);
        Assert.Contains("先在这台电脑上停用", text);
        Assert.DoesNotContain("激活在其他机器", text);
        Assert.DoesNotContain("还没填授权方式", text);
    }

    // ---------- 购买辅助 ----------

    [Fact]
    public void AdviceCountsOwnedAndIdle()
    {
        var used = Row("Used", ["压缩"]);
        var old = Row("Old", ["压缩"]);
        var never = Row("Never", ["压缩"]);
        var usage = new Dictionary<string, PluginUsage> { [used.Key] = Used(DateTime.UtcNow, 9), [old.Key] = Used(DateTime.UtcNow.AddYears(-2)) };

        var advice = PurchaseAdvisor.Advise("New Thing", "", "压缩", [used, old, never], usage, DateTime.UtcNow)!;
        Assert.Equal(("压缩", 3, 2), (advice.Tag, advice.Owned, advice.IdleOverYear!.Value));
        Assert.Equal(["Used", "Old"], advice.MostUsed);
        var text = PurchaseAdvisor.Describe(advice);
        Assert.Contains("3 款", text);
        Assert.Contains("2 款过去一年", text);
    }

    [Fact]
    public void AdviceDoesNotClaimIdleWithoutProjectData()
    {
        var advice = PurchaseAdvisor.Advise("X", "", "压缩", [Row("A", ["压缩"])], new Dictionary<string, PluginUsage>(), DateTime.UtcNow)!;
        Assert.Null(advice.IdleOverYear);
        Assert.DoesNotContain("过去一年", PurchaseAdvisor.Describe(advice));
    }

    [Fact]
    public void AdviceGuessesCategoryFromNameAndPrefersNarrowest()
    {
        var owned = new[] { Row("A", ["压缩", "动态"]), Row("B", ["压缩", "动态"]), Row("C", ["动态"]) };
        var advice = PurchaseAdvisor.Advise("Super Compressor", "", null, owned, new Dictionary<string, PluginUsage>(), DateTime.UtcNow);
        Assert.Equal("压缩", advice?.Tag);
        Assert.Null(PurchaseAdvisor.Advise("Qwzxv", "", PurchaseAdvisor.AutoCategory, owned, new Dictionary<string, PluginUsage>(), DateTime.UtcNow));
        Assert.Contains("选一个类别", PurchaseAdvisor.Describe(null));
    }

    [Fact]
    public void PriceStatus()
    {
        var item = new WishItem(1, "W", Price: 100m, Currency: "$", TargetPrice: 80m, LowestPrice: 60m);
        Assert.False(PurchaseAdvisor.ReachedTarget(item));
        Assert.Contains("差", PurchaseAdvisor.DescribePrice(item));
        Assert.Contains("20", PurchaseAdvisor.DescribePrice(item));

        var cheap = item with { Price = 60m };
        Assert.True(PurchaseAdvisor.ReachedTarget(cheap));
        Assert.Contains("已到目标价", PurchaseAdvisor.DescribePrice(cheap));
        Assert.Contains("不高于", PurchaseAdvisor.DescribePrice(cheap));

        Assert.False(PurchaseAdvisor.ReachedTarget(item with { Price = null }));
        Assert.Equal("", PurchaseAdvisor.DescribePrice(new WishItem(1, "W")));
    }

    [Fact]
    public void PriceSearchIsAnEscapedWebUrl()
    {
        var url = PurchaseAdvisor.PriceSearchUrl("Pro-Q & 3", "Fab Filter");
        Assert.StartsWith("https://www.bing.com/search?q=", url);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("Pro-Q%20%26%203", url);
    }
}
