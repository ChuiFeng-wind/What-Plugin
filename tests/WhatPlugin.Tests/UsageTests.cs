using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>主程序的使用记录、购买方式和发票号、备份第 4 版。不依赖成就包，公开仓库里也能跑。数据都是临时目录里的假数据。</summary>
[Collection("VendorCatalog")]
public sealed class UsageTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-usage-" + Guid.NewGuid().ToString("N"));

    public UsageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "t.db");

    private static MergedPlugin Merged(string key, string name) =>
        new(key, name, "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile(key + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx|EQ", ScanStatus.Loaded, null)]);

    // ---------- 主程序：字段、迁移、使用记录、备份 ----------

    [Fact]
    public void PurchaseKindAndInvoiceRoundTrip()
    {
        using var db = new PluginDatabase(DbPath);
        db.SaveMerged([Merged("k1", "Alpha")]);
        var row = db.LoadPlugins().Single();
        db.SaveUserData(row.Id, row.User with { Purchase = PurchaseKind.Free });
        Assert.Equal(PurchaseKind.Free, db.LoadPlugins().Single().User.Purchase);
        Assert.True(db.LoadPlugins().Single().User.HasData);

        var wish = db.AddWish(new WishItem(0, "Beta", Status: WishStatus.Purchased, Invoice: " INV-001 "));
        Assert.Equal("INV-001", db.LoadWishes().Single(w => w.Id == wish.Id).Invoice);
        db.SaveWish(wish with { Invoice = "INV-002" });
        Assert.Equal("INV-002", db.LoadWishes().Single().Invoice);
    }

    [Fact]
    public void PaidPluginsAreBackfilledIntoPurchasesOnce()
    {
        using var db = new PluginDatabase(DbPath);
        db.SaveMerged([Merged("k1", "Alpha"), Merged("k2", "Beta"), Merged("k3", "Gamma"), Merged("k4", "Delta")]);
        foreach (var row in db.LoadPlugins().Where(p => p.Name != "Delta"))
            db.SaveUserData(row.Id, row.User with { Purchase = PurchaseKind.Paid });
        // Beta 已经在已购里，Gamma 还在心愿单。
        db.UpsertPurchased([new PurchaseDraft("Beta", "Acme", "me@example.com")]);
        db.AddWish(new WishItem(0, "Gamma", "Acme", Reason: "想要"));

        Assert.Equal(2, db.BackfillPurchases());

        var wishes = db.LoadWishes();
        Assert.Equal(["Alpha", "Beta", "Gamma"], wishes.Where(w => w.Status == WishStatus.Purchased).Select(w => w.Name).Order());
        Assert.Equal("想要", wishes.Single(w => w.Name == "Gamma").Reason);
        Assert.DoesNotContain(wishes, w => w.Name == "Delta");
        // 原来填的账号不动。
        Assert.Equal("me@example.com", PurchaseLogin.Resolve(wishes.Single(w => w.Name == "Beta"), PurchaseLogin.ByKey(db.LoadPlatformAccounts())).Account);

        // 只做一次：删掉以后不会再加回来。
        db.DeleteWishes([wishes.Single(w => w.Name == "Alpha").Id]);
        Assert.Equal(0, db.BackfillPurchases());
        Assert.DoesNotContain(db.LoadWishes(), w => w.Name == "Alpha");
    }

    [Fact]
    public void OldBackupWithoutPurchaseKeepsLocalMark()
    {
        using var db = new PluginDatabase(DbPath);
        db.SaveMerged([Merged("k1", "Alpha")]);
        var row = db.LoadPlugins().Single();
        db.SaveUserData(row.Id, row.User with { Purchase = PurchaseKind.Paid });
        db.ImportUserData([new BackupEntry("k1", "Alpha", "Acme", 3, "备注", [], null, null, null)]);
        var after = db.LoadPlugins().Single();
        Assert.Equal(PurchaseKind.Paid, after.User.Purchase);
        Assert.Equal(3, after.User.Rating);

        db.ImportUserData([new BackupEntry("k1", "Alpha", "Acme", 3, "备注", [], null, null, null, Purchase: PurchaseKind.Free)]);
        Assert.Equal(PurchaseKind.Free, db.LoadPlugins().Single().User.Purchase);
    }

    [Fact]
    public void MigrationFromV12AddsColumnsAndKeepsData()
    {
        using (var db = new PluginDatabase(DbPath))
        {
            db.SaveMerged([Merged("k1", "Alpha")]);
            db.AddWish(new WishItem(0, "Beta", Status: WishStatus.Purchased, PaidPrice: 99m));
        }
        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = """
                ALTER TABLE plugins DROP COLUMN purchase;
                ALTER TABLE wishes DROP COLUMN invoice;
                DROP TABLE usage_events;
                DROP TABLE achievements;
                PRAGMA user_version = 12;
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var upgraded = new PluginDatabase(DbPath);
        Assert.Equal(PurchaseKind.Unknown, upgraded.LoadPlugins().Single().User.Purchase);
        Assert.Equal(99m, upgraded.LoadWishes().Single().PaidPrice);
        Assert.Equal("", upgraded.LoadWishes().Single().Invoice);
        upgraded.RecordUsage(UsageKinds.BackupExport);
        Assert.Single(upgraded.LoadUsage());
        Assert.Empty(upgraded.LoadAchievements());
    }

    [Fact]
    public void LaunchIsRecordedOncePerDayWithGap()
    {
        using var db = new PluginDatabase(DbPath);
        var first = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        db.RecordLaunch(first);
        db.RecordLaunch(first.AddHours(1));
        db.RecordLaunch(first.AddDays(200));
        var launches = db.LoadUsage(UsageKinds.Launch);
        Assert.Equal(2, launches.Count);
        Assert.Equal(-1, launches[0].Value);
        Assert.Equal(199, launches[1].Value);
    }

    [Fact]
    public void DisabledUsageRecordsNothing()
    {
        using var db = new PluginDatabase(DbPath);
        db.UsageEnabled = false;
        db.RecordLaunch();
        db.RecordAction();
        db.RecordUsage(UsageKinds.BackupExport);
        db.RecordWishCount(3);
        db.RecordProjectMissing("song", 2);
        Assert.Equal(0, db.CountUsageEvents());
        Assert.Null(db.GetSetting(PluginDatabase.LastLaunchSetting));
    }

    [Fact]
    public void ActionsAggregatePerHourAndCountsOnlyOnChange()
    {
        using var db = new PluginDatabase(DbPath);
        var at = new DateTime(2026, 1, 1, 3, 10, 0, DateTimeKind.Local).ToUniversalTime();
        db.RecordAction(at);
        db.RecordAction(at.AddMinutes(20));
        db.RecordAction(at.AddHours(2));
        var actions = db.LoadUsage(UsageKinds.Action);
        Assert.Equal(2, actions.Count);
        Assert.Equal(2, actions[0].Value);
        Assert.Equal(3, actions[0].LocalHour);

        db.RecordWishCount(5);
        db.RecordWishCount(5);
        db.RecordWishCount(0);
        var wishes = db.LoadUsage(UsageKinds.WishCount);
        Assert.Equal(2, wishes.Count);
        Assert.Equal((5L, 0L), (wishes[1].Previous, wishes[1].Value));
    }

    [Fact]
    public void ProjectMissingKeepsLastTwoAndHidesPath()
    {
        using var db = new PluginDatabase(DbPath);
        const string song = @"d:\songs|新歌";
        db.RecordProjectMissing(song, 3);
        db.RecordProjectMissing(song, 3);
        db.RecordProjectMissing(song, 1);
        db.RecordProjectMissing(song, 0);
        var events = db.LoadUsage(UsageKinds.ProjectMissing);
        Assert.Equal(2, events.Count);
        Assert.Equal((1L, 0L), (events[1].Previous, events[1].Value));
        Assert.DoesNotContain(events, e => e.Subject.Contains("songs") || e.Subject.Contains("新歌"));
    }

    [Fact]
    public void ClearUsageKeepsAchievements()
    {
        using var db = new PluginDatabase(DbPath);
        db.RecordUsage(UsageKinds.BackupExport);
        db.UnlockAchievements(["ACH-028"], Now);
        db.ClearUsage();
        Assert.Equal(0, db.CountUsageEvents());
        Assert.Contains("ACH-028", db.LoadAchievements().Keys);
    }

    [Fact]
    public void DataChangedFiresForUserDataButNotForUsage()
    {
        using var db = new PluginDatabase(DbPath);
        var changes = 0;
        db.DataChanged += () => changes++;
        db.RecordUsage(UsageKinds.BackupExport);
        db.UnlockAchievements(["ACH-028"], Now);
        Assert.Equal(0, changes);
        db.AddWish(new WishItem(0, "Alpha"));
        Assert.True(changes > 0);
    }

    [Fact]
    public void BackupV4CarriesUsageAndAchievements()
    {
        using var source = new PluginDatabase(DbPath);
        source.SaveMerged([Merged("k1", "Alpha")]);
        var row = source.LoadPlugins().Single();
        source.SaveUserData(row.Id, row.User with { Purchase = PurchaseKind.Free });
        source.AddWish(new WishItem(0, "Beta", Status: WishStatus.Purchased, Invoice: "INV-9"));
        source.RecordLaunch(Now.AddDays(-3));
        source.RecordUsage(UsageKinds.BackupExport, utc: Now.AddDays(-2));
        source.UnlockAchievements(["ACH-028"], Now.AddDays(-2));

        var json = UserDataBackup.Serialize(BackupRestore.Collect(source, includeSecrets: false, out _), null);
        Assert.Contains("\"Version\": 7", json);

        using var target = new PluginDatabase(Path.Combine(_dir, "target.db"));
        target.UnlockAchievements(["ACH-028"], Now);
        target.RecordLaunch(Now);
        var loaded = UserDataBackup.Read(json);
        var summary = BackupRestore.Apply(target, loaded.Content, null, _ => false);
        Assert.Equal(2, summary.UsageEvents);
        Assert.Equal(Now.AddDays(-2), target.LoadAchievements()["ACH-028"]);
        Assert.Equal(PurchaseKind.Free, target.LoadPlugins().Single().User.Purchase);
        Assert.Equal("INV-9", target.LoadWishes().Single().Invoice);
        // 上次打开的时间用本机的，不用备份里的旧时间。
        Assert.StartsWith(Now.ToString("yyyy-MM-dd"), target.GetSetting(PluginDatabase.LastLaunchSetting));

        // 再导入一次不会重复。
        Assert.Equal(0, BackupRestore.Apply(target, loaded.Content, null, _ => false).UsageEvents);
    }

    [Fact]
    public void OlderBackupVersionsStillLoad()
    {
        const string v3 = """{"Version":3,"ExportedUtc":"2026-01-01T00:00:00Z","Plugins":[]}""";
        var loaded = UserDataBackup.Read(v3);
        Assert.Null(loaded.Content.Usage);
    }
}
