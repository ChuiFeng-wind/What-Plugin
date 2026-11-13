using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>只备份、只导入其中几部分；组合分享文件。</summary>
[Collection("VendorCatalog")]
public sealed class PartialBackupTests : IDisposable
{
    private const string Key = "vst3|fabfilter|proq4";
    private const string FastPassword = "fake-backup-pw-不是真的";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-pb-" + Guid.NewGuid().ToString("N"));

    public PartialBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string Db(string name) => Path.Combine(_dir, name + ".db");

    /// <summary>评价和整理都有的一款插件，加上心愿、笔记、设置、卸载历史。</summary>
    private static void Fill(PluginDatabase db, int rating, string note, bool hidden, string vendor, string chain, string wish, string theme)
    {
        db.ImportUserData([new BackupEntry(Key, "Pro-Q 4", "FabFilter", rating, note, ["人声"], ["EQ"], PluginKind.Effect, vendor, hidden,
            Purchase: PurchaseKind.Paid)]);
        db.AddWish(new WishItem(0, wish, "Acme", Reason: "想要"));
        var c = db.AddChain(chain);
        db.SaveChain(c.Id, chain + "的备注", [new ChainStep(Key, "Pro-Q 4", "")]);
        db.SetSetting("theme", theme);
    }

    private static BackupContent RoundTrip(BackupContent content, string? password = null) =>
        UserDataBackup.Read(UserDataBackup.Serialize(content, password, 10_000)).Content;

    [Fact]
    public void PartialExportOnlyContainsChosenParts()
    {
        using var db = new PluginDatabase(Db("a"));
        Fill(db, 5, "很好用", hidden: true, "FabFilter Pro", "主唱", "Saturn 2", "dark-test");

        var content = RoundTrip(BackupRestore.Collect(db, BackupParts.Reviews | BackupParts.Notes, out _));

        Assert.Equal(BackupParts.Reviews | BackupParts.Notes, content.Parts);
        var entry = Assert.Single(content.Plugins);
        Assert.Equal(5, entry.Rating);
        Assert.Equal("很好用", entry.Note);
        // 整理那部分没带。
        Assert.False(entry.Hidden);
        Assert.Null(entry.VendorOverride);
        Assert.Null(entry.KindOverride);
        Assert.Null(entry.TagsOverride);
        Assert.Null(entry.Purchase);
        Assert.Equal("主唱", Assert.Single(content.Chains).Name);
        Assert.Empty(content.Wishes);
        Assert.Null(content.Settings);
        Assert.Null(content.Reviews);
        Assert.Null(content.Usage);
        Assert.Empty(content.Uninstalls ?? []);
    }

    [Fact]
    public void OrganizeOnlySkipsPluginsWithoutOrganizeData()
    {
        using var db = new PluginDatabase(Db("a"));
        db.ImportUserData([
            new BackupEntry(Key, "Pro-Q 4", "FabFilter", 5, "只有评价", [], null, null, null),
            new BackupEntry("vst3|x|y", "Y", "X", 0, "", [], null, null, null, Hidden: true),
        ]);

        var entries = UserDataBackup.PluginEntries(db.LoadPlugins(), BackupParts.Organize);

        var only = Assert.Single(entries);
        Assert.Equal("vst3|x|y", only.Key);
        Assert.True(only.Hidden);
    }

    [Fact]
    public void ImportingPartialBackupLeavesOtherPartsUntouched()
    {
        BackupContent content;
        using (var source = new PluginDatabase(Db("a")))
        {
            Fill(source, 5, "备份里的评价", hidden: false, "备份里的厂家", "备份里的笔记", "备份里的心愿", "backup-theme");
            content = RoundTrip(BackupRestore.Collect(source, BackupParts.Reviews, out _));
        }

        using var db = new PluginDatabase(Db("b"));
        Fill(db, 2, "本机的评价", hidden: true, "本机的厂家", "本机的笔记", "本机的心愿", "local-theme");
        BackupRestore.Apply(db, content, null, _ => true);

        var plugin = Assert.Single(db.LoadPlugins(), p => p.Key == Key);
        Assert.Equal(5, plugin.User.Rating);
        Assert.Equal("备份里的评价", plugin.User.Note);
        // 整理是本机的。
        Assert.True(plugin.User.Hidden);
        Assert.Equal("本机的厂家", plugin.User.VendorOverride);
        Assert.Equal(PurchaseKind.Paid, plugin.User.Purchase);
        Assert.Equal(["本机的笔记"], db.LoadChains().Select(c => c.Name));
        Assert.Equal(["本机的心愿"], db.LoadWishes().Select(w => w.Name));
        Assert.Equal("local-theme", db.GetSetting("theme"));
    }

    [Fact]
    public void ChoosingPartsOfAFullBackupOnImport()
    {
        BackupContent content;
        using (var source = new PluginDatabase(Db("a")))
        {
            Fill(source, 5, "备份里的评价", hidden: false, "备份里的厂家", "备份里的笔记", "备份里的心愿", "backup-theme");
            content = RoundTrip(BackupRestore.Collect(source, includeSecrets: false, out _));
        }
        Assert.Equal(BackupParts.All & ~BackupParts.Secrets, content.Parts);

        using var db = new PluginDatabase(Db("b"));
        Fill(db, 2, "本机的评价", hidden: true, "本机的厂家", "本机的笔记", "本机的心愿", "local-theme");
        var summary = BackupRestore.Apply(db, content, null, _ => true, BackupParts.Organize | BackupParts.Notes);

        var plugin = Assert.Single(db.LoadPlugins(), p => p.Key == Key);
        Assert.Equal(2, plugin.User.Rating);
        Assert.Equal("本机的评价", plugin.User.Note);
        Assert.False(plugin.User.Hidden);
        Assert.Equal("备份里的厂家", plugin.User.VendorOverride);
        Assert.Equal(["备份里的笔记", "本机的笔记"], db.LoadChains().Select(c => c.Name).Order());
        Assert.Equal(["本机的心愿"], db.LoadWishes().Select(w => w.Name));
        Assert.Equal("local-theme", db.GetSetting("theme"));
        Assert.Equal(0, summary.Settings);
        Assert.Equal(0, summary.Wishes.Added);
    }

    [Fact]
    public void NotChoosingAnyPluginPartKeepsPluginsAsTheyAre()
    {
        BackupContent content;
        using (var source = new PluginDatabase(Db("a")))
        {
            Fill(source, 5, "备份", hidden: false, "备份", "笔记", "心愿", "t");
            content = RoundTrip(BackupRestore.Collect(source, includeSecrets: false, out _));
        }
        using var db = new PluginDatabase(Db("b"));
        var summary = BackupRestore.Apply(db, content, null, _ => true, BackupParts.Notes);

        Assert.Equal(0, summary.Plugins.Updated + summary.Plugins.Added);
        Assert.DoesNotContain(db.LoadPlugins(), p => p.Key == Key);
        Assert.Single(db.LoadChains());
    }

    [Fact]
    public void SecretsNeedWishesAndAreSkippedWithoutThem()
    {
        Assert.Equal(BackupParts.Notes, (BackupParts.Notes | BackupParts.Secrets).Valid());
        Assert.Equal(BackupParts.Wishes | BackupParts.Secrets, (BackupParts.Wishes | BackupParts.Secrets).Valid());

        using var db = new PluginDatabase(Db("a"));
        db.AddWish(new WishItem(0, "Pro-C 2", "FabFilter", Status: WishStatus.Purchased, Account: "fake@example.invalid", Password: "fake-pw"));
        var content = BackupRestore.Collect(db, BackupParts.Notes | BackupParts.Secrets, out _);
        Assert.Null(content.Secrets);
        Assert.Empty(content.Wishes);
        // 不含账号密码时不需要备份密码。
        Assert.DoesNotContain("fake-pw", UserDataBackup.Serialize(content, null));
    }

    [Fact]
    public void BackupsBeforeVersion7CountAsFull()
    {
        const string json = """{ "Version": 6, "ExportedUtc": "2026-01-01T00:00:00Z", "Plugins": [] }""";
        Assert.Equal(BackupParts.All, UserDataBackup.Read(json).Content.Parts);
    }

    [Fact]
    public void FileRecordsItsParts()
    {
        using var db = new PluginDatabase(Db("a"));
        var json = UserDataBackup.Serialize(BackupRestore.Collect(db, BackupParts.Reviews | BackupParts.Notes, out _), null);
        Assert.Contains("\"Version\": 7", json);
        Assert.Contains("\"Parts\": \"Reviews, Notes\"", json);
    }

    [Fact]
    public void ShareBundleRoundTripsBothParts()
    {
        var reviews = new ReviewFile("id1", "阿风", new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
            [new SharedReview(Key, "Pro-Q 4", "FabFilter", 5, "好用", ["人声"])]);
        var notes = new NoteFile("阿风", new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
            [new ChainBackupEntry("主唱", "备注", [new ChainStep(Key, "Pro-Q 4", "切低频")], ["人声"])]);

        var path = Path.Combine(_dir, "a" + ShareBundles.Extension);
        File.WriteAllText(path, ShareBundles.Serialize(new ShareBundle(reviews, notes), DateTime.UtcNow));
        var back = ShareBundles.Read(path);

        Assert.Equal(ImportKind.Share, ImportKinds.Of(path));
        Assert.Equal("阿风", back.Reviews!.Author);
        Assert.Equal("好用", Assert.Single(back.Reviews.Reviews).Note);
        var note = Assert.Single(back.Notes!.Notes);
        Assert.Equal("主唱", note.Name);
        Assert.Equal(["人声"], note.Folder);
    }

    [Fact]
    public void ShareBundleWithOnlyNotesAndBadFiles()
    {
        var notes = new NoteFile("", DateTime.UtcNow, [new ChainBackupEntry("鼓", null, [new ChainStep(Key, "Pro-Q 4", "")])]);
        var back = ShareBundles.Parse(ShareBundles.Serialize(new ShareBundle(null, notes), DateTime.UtcNow));
        Assert.Null(back.Reviews);
        Assert.Single(back.Notes!.Notes);

        Assert.Throws<ArgumentException>(() => ShareBundles.Serialize(new ShareBundle(null, null), DateTime.UtcNow));
        Assert.Throws<FormatException>(() => ShareBundles.Parse("不是 JSON"));
        Assert.Throws<FormatException>(() => ShareBundles.Parse("""{ "Format": "whatplugin-review" }"""));
        Assert.Throws<FormatException>(() => ShareBundles.Parse("""{ "Format": "whatplugin-share", "Version": 1 }"""));
        Assert.Throws<FormatException>(() => ShareBundles.Parse("""{ "Format": "whatplugin-share", "Version": 99, "Notes": {} }"""));
        // 里面的笔记部分坏了，整份不认。
        Assert.Throws<FormatException>(() => ShareBundles.Parse("""{ "Format": "whatplugin-share", "Version": 1, "Notes": { "Format": "x" } }"""));
    }
}
