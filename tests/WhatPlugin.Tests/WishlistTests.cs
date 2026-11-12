using System.Text;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

[Collection("VendorCatalog")]
public sealed class WishlistTests : IDisposable
{
    // 测试用的假数据，不是真实账号。
    private const string FakePassword = "fake-pw-不是真的-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-w-" + Guid.NewGuid().ToString("N"));

    public WishlistTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "t.db");

    [Fact]
    public void WishKeepsAllFieldsAcrossReopen()
    {
        var bought = new DateOnly(2026, 11, 6);
        using (var db = new PluginDatabase(DbPath))
        {
            var added = db.AddWish(new WishItem(0, "Pro-Q 4", "FabFilter", 1299.5m, "$", 3, "动态 EQ 好用", "https://example.com", "黑五再买"));
            db.SaveWish(added with { Status = WishStatus.Purchased, PurchasedOn = bought, PaidPrice = 899m });
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = new PluginDatabase(DbPath);
        var item = Assert.Single(reopened.LoadWishes());
        Assert.Equal("Pro-Q 4", item.Name);
        Assert.Equal(1299.5m, item.Price);
        Assert.Equal("$", item.Currency);
        Assert.Equal(WishPriority.MustHave, item.Priority);
        Assert.Equal("动态 EQ 好用", item.Reason);
        Assert.Equal("黑五再买", item.Note);
        Assert.Equal(WishStatus.Purchased, item.Status);
        Assert.Equal(bought, item.PurchasedOn);
        Assert.Equal(899m, item.PaidPrice);
    }

    [Fact]
    public void PasswordIsNotStoredInPlainText()
    {
        using (var db = new PluginDatabase(DbPath))
        {
            var added = db.AddWish(new WishItem(0, "Foo", Account: "me@example.com", Password: FakePassword, Serial: "FAKE-SERIAL-9"));
            Assert.Equal(FakePassword, Assert.Single(db.LoadWishes()).Password);
            Assert.Equal("FAKE-SERIAL-9", db.LoadWishes()[0].Serial);
            db.SaveWish(added with { Password = FakePassword + "x" });
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var raw = File.ReadAllBytes(DbPath);
        Assert.Equal(-1, raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes("fake-pw")));
        Assert.Equal(-1, raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes("FAKE-SERIAL")));
    }

    [Fact]
    public void UnreadableSecretIsKeptWhenSaving()
    {
        using var db = new PluginDatabase(DbPath);
        var added = db.AddWish(new WishItem(0, "Foo", Password: FakePassword));

        // 模拟另一个 Windows 账号加密的内容：写入一段解不开的字节。
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE wishes SET password_protected = $bad";
            command.Parameters.AddWithValue("$bad", new byte[] { 1, 2, 3, 4 });
            command.ExecuteNonQuery();
        }

        var locked = Assert.Single(db.LoadWishes());
        Assert.True(locked.SecretsLocked);
        Assert.Equal("", locked.Password);
        db.SaveWish(locked with { Note = "改了备注" });

        var again = Assert.Single(db.LoadWishes());
        Assert.True(again.SecretsLocked);
        Assert.Equal("改了备注", again.Note);
    }

    [Fact]
    public void SecretsNeverReachBackup()
    {
        using var db = new PluginDatabase(DbPath);
        db.AddWish(new WishItem(0, "Foo", Reason: "想要", Status: WishStatus.Purchased,
            Account: "me@example.com", Password: FakePassword, Serial: "FAKE-SERIAL-9"));

        var json = UserDataBackup.Serialize([], db.LoadWishes());

        Assert.Contains("想要", json);
        Assert.DoesNotContain("fake-pw", json);
        Assert.DoesNotContain("FAKE-SERIAL", json);
        Assert.DoesNotContain("me@example.com", json);
        Assert.DoesNotContain("Protected", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupRestoresWishesAndKeepsExistingLogin()
    {
        string json;
        using (var source = new PluginDatabase(Path.Combine(_dir, "a.db")))
        {
            source.AddWish(new WishItem(0, "Foo", "Acme", 99m, Reason: "新原因"));
            source.AddWish(new WishItem(0, "Bar", "Acme", 10m, "$", Status: WishStatus.Purchased));
            json = UserDataBackup.Serialize([], source.LoadWishes());
        }

        using var target = new PluginDatabase(DbPath);
        target.AddWish(new WishItem(0, "Foo", "Acme", Reason: "旧原因", Account: "me", Password: FakePassword));

        var result = target.ImportWishes(UserDataBackup.ParseWishes(json));

        Assert.Equal(new UpsertResult(1, 1), result);
        var rows = target.LoadWishes();
        var foo = rows.Single(w => w.Name == "Foo");
        Assert.Equal("新原因", foo.Reason);
        Assert.Equal("me", foo.Account);
        Assert.Equal(FakePassword, foo.Password);
        Assert.Equal(WishStatus.Purchased, rows.Single(w => w.Name == "Bar").Status);
    }

    [Fact]
    public void OlderBackupHasNoWishes()
    {
        const string older = """{ "version": 1, "exportedUtc": "2026-11-06T00:00:00Z", "plugins": [] }""";
        Assert.Empty(UserDataBackup.ParseWishes(older));
    }

    [Fact]
    public void SetLoginsLeavesBlankFieldsUntouched()
    {
        using var db = new PluginDatabase(DbPath);
        var a = db.AddWish(new WishItem(0, "A", Account: "old", Password: "old-pw", Serial: "S1"));
        var b = db.AddWish(new WishItem(0, "B"));
        var c = db.AddWish(new WishItem(0, "C", Account: "untouched"));

        db.SetLogins([a.Id, b.Id], "new@example.com", "", "");

        var rows = db.LoadWishes();
        var first = rows.Single(w => w.Name == "A");
        Assert.Equal("new@example.com", first.Account);
        Assert.Equal("old-pw", first.Password);
        Assert.Equal("S1", first.Serial);
        Assert.Equal("new@example.com", rows.Single(w => w.Name == "B").Account);
        Assert.Equal("untouched", rows.Single(w => w.Name == "C").Account);
    }

    [Fact]
    public void UpsertAddsUpdatesAndPromotesWithoutDuplicates()
    {
        using var db = new PluginDatabase(DbPath);
        db.AddWish(new WishItem(0, "Pro-Q 4", "FabFilter", 1299m, Reason: "想要"));
        db.AddWish(new WishItem(0, "C4", "Waves", Status: WishStatus.Purchased, Account: "old", Password: "keep-me", OwnLogin: true));

        var result = db.UpsertPurchased(
        [
            new PurchaseDraft("Pro Q4", "FabFilter", "me", "pw"),
            new PurchaseDraft("C4 Stereo", "Waves Audio", "new"),
            new PurchaseDraft("Brand New", "Acme", "me", "pw"),
            new PurchaseDraft("Brand New", "Acme", "me2"),
        ]);

        Assert.Equal((1, 3), (result.Added, result.Updated));
        Assert.Equal(["FabFilter", "Acme"], result.NewPlatforms);
        var rows = db.LoadWishes();
        var platforms = PurchaseLogin.ByKey(db.LoadPlatformAccounts());
        Assert.Equal(3, rows.Count);
        var promoted = rows.Single(w => w.Name == "Pro-Q 4");
        Assert.Equal(WishStatus.Purchased, promoted.Status);
        Assert.Equal("想要", promoted.Reason);
        Assert.Equal(1299m, promoted.Price);
        // 厂家还没有账号：存成厂家账号，这一款跟随它。
        Assert.False(promoted.OwnLogin);
        Assert.Equal(("me", "pw", true), Login(promoted, platforms));
        // 原来就是单独账号的，继续单独，留空的密码不动。
        var c4 = rows.Single(w => w.Name == "C4");
        Assert.True(c4.OwnLogin);
        Assert.Equal("new", c4.Account);
        Assert.Equal("keep-me", c4.Password);
        // 和已有厂家账号不一样的，存成单独账号，不覆盖厂家账号。
        var brandNew = rows.Single(w => w.Name == "Brand New");
        Assert.True(brandNew.OwnLogin);
        Assert.Equal("me2", brandNew.Account);
        Assert.Equal("me", platforms[VendorCatalog.PlatformKey("Acme")].Account);
    }

    private static (string, string, bool) Login(WishItem item, IReadOnlyDictionary<string, PlatformAccount> platforms)
    {
        var login = PurchaseLogin.Resolve(item, platforms);
        return (login.Account, login.Password, login.FromPlatform);
    }

    [Fact]
    public void SameNameFromDifferentVendorsStaysSeparate()
    {
        using var db = new PluginDatabase(DbPath);
        db.AddWish(new WishItem(0, "Comp", "Acme"));

        db.UpsertPurchased([new PurchaseDraft("Comp", "Other", "me")]);

        Assert.Equal(2, db.LoadWishes().Count);
    }

    [Fact]
    public void DeleteRemovesRecords()
    {
        using var db = new PluginDatabase(DbPath);
        var a = db.AddWish(new WishItem(0, "A"));
        db.AddWish(new WishItem(0, "B"));

        db.DeleteWishes([a.Id]);

        Assert.Equal("B", Assert.Single(db.LoadWishes()).Name);
    }

    [Fact]
    public void ToStringHidesSecrets()
    {
        var text = new WishItem(0, "Foo", Password: FakePassword, Serial: "FAKE-SERIAL-9").ToString();
        Assert.DoesNotContain("fake-pw", text);
        Assert.DoesNotContain("FAKE-SERIAL", text);
        Assert.DoesNotContain("fake-pw", new PurchaseDraft("Foo", Password: FakePassword).ToString());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("199", "199")]
    [InlineData("29.99", "29.99")]
    [InlineData("¥1,299", "1299")]
    [InlineData("$ 29", "29")]
    [InlineData("199元", "199")]
    public void MoneyParsesCommonForms(string text, string? expected)
    {
        Assert.True(Money.TryParse(text, out var value));
        Assert.Equal(expected == null ? null : decimal.Parse(expected), value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.2.3")]
    public void MoneyRejectsBadInput(string text) => Assert.False(Money.TryParse(text, out _));

    [Fact]
    public void MoneyFormatsAndTotalsPerCurrency()
    {
        Assert.Equal("¥1,299.5", Money.Format(1299.5m, "¥"));
        Assert.Equal("", Money.Format(null, "¥"));
        Assert.Equal("¥300 + $58", Money.Total([(100m, "¥"), (29m, "$"), (200m, "¥"), (null, "€"), (29m, "$")]));
    }

    [Fact]
    public void ParsesCommaLinesAndKeepsCommasInPassword()
    {
        var result = PurchaseText.Parse("Pro-Q 4, FabFilter, me@example.com, pa,ss，word\r\n\r\nC4，Waves，w@example.com，pw2\nOnly Name\n");

        Assert.Empty(result.BadLines);
        Assert.Equal(3, result.Drafts.Count);
        Assert.Equal(new PurchaseDraft("Pro-Q 4", "FabFilter", "me@example.com", "pa,ss，word"), result.Drafts[0]);
        Assert.Equal(new PurchaseDraft("C4", "Waves", "w@example.com", "pw2"), result.Drafts[1]);
        Assert.Equal(new PurchaseDraft("Only Name"), result.Drafts[2]);
    }

    [Fact]
    public void ReportsBadLinesWithTheirNumbers()
    {
        var result = PurchaseText.Parse("Good\n,Acme,me,pw\n\nA\tB\tC\tD\tE\tF\n");

        Assert.Equal("Good", Assert.Single(result.Drafts).Name);
        Assert.Equal([2, 4], result.BadLines);
    }

    [Fact]
    public void ExportedTextParsesBackExactly()
    {
        WishItem[] items =
        [
            new(1, "Pro-Q 4", "FabFilter", Account: "me", Password: " p,w ", Serial: "S-1"),
            new(2, "Bad\tName", Account: "me"),
            new(3, "Locked", SecretsLocked: true),
        ];

        var text = PurchaseText.Format(items, out var skipped);

        Assert.Equal(["Bad\tName", "Locked"], skipped);
        var draft = Assert.Single(PurchaseText.Parse(text).Drafts);
        Assert.Equal(new PurchaseDraft("Pro-Q 4", "FabFilter", "me", " p,w ", "S-1"), draft);
    }

    [Fact]
    public void MigratesExistingDatabaseWithoutLosingNotes()
    {
        using (var db = new PluginDatabase(DbPath))
        {
            db.SaveMerged([new MergedPlugin("foo", "Foo", "Acme", InfoSource.PluginLoad,
                new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory), [])]);
            var row = Assert.Single(db.LoadPlugins());
            db.SaveUserData(row.Id, row.User with { Note = "留着" });
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE wishes; ALTER TABLE plugins DROP COLUMN mix_json; PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var migrated = new PluginDatabase(DbPath);
        Assert.Equal("留着", Assert.Single(migrated.LoadPlugins()).User.Note);
        Assert.Empty(migrated.LoadWishes());
    }
}
