using System.Text.Json;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>完整备份：所有用户输入的内容导出再导入后一致；账号密码只以加密形式出现在文件里。</summary>
[Collection("VendorCatalog")]
public sealed class FullBackupTests : IDisposable
{
    // 测试用的假数据，不是真实账号。
    private const string BackupPassword = "fake-backup-pw-不是真的";
    private const string WishPassword = "fake-wish-pw-778";
    private const string WishSerial = "FAKE-SERIAL-0000-1111";
    private const string WishAccount = "fake-buyer@example.invalid";
    private const string ILokPassword = "fake-ilok-pw-991";
    private const string ILokAccount = "fake-ilok@example.invalid";
    // 测试里用很少的迭代次数，免得慢；格式和正式的一样。
    private const int FastIterations = 10_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-fb-" + Guid.NewGuid().ToString("N"));

    public FullBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string Db(string name) => Path.Combine(_dir, name + ".db");

    private string ProjectFolder => Path.Combine(_dir, "projects");

    private void Fill(PluginDatabase db)
    {
        Directory.CreateDirectory(ProjectFolder);
        db.ImportUserData([new BackupEntry("vst3|fabfilter|proq4", "Pro-Q 4", "FabFilter", 5, "很好用", ["人声"], null, null, null)]);

        var wish = db.AddWish(new WishItem(0, "Pro-C 2", "FabFilter", 899m, Status: WishStatus.Purchased,
            Account: WishAccount, Password: WishPassword, Serial: WishSerial, OwnLogin: true));
        db.AddWish(new WishItem(0, "Saturn 2", "FabFilter", Reason: "想要"));
        Assert.True(wish.Id > 0);
        db.SavePlatformAccount(new PlatformAccount(ILokFill.Platform, ILokAccount, ILokPassword, "主账号"));
        db.SavePlatformAccount(new PlatformAccount("Soundtoys", "fake-st@example.invalid", "fake-st-pw", "iLok 账号：" + ILokAccount));

        var list = db.AddFavoriteList("人声链");
        db.SetFavoriteItems(list.Id, ["vst3|fabfilter|proq4"]);
        db.RecordFavoriteRemoval(list.Id, "vst3|waves|c4");
        var chain = db.AddChain("主唱");
        db.SaveChain(chain.Id, "链备注", [new ChainStep("vst3|fabfilter|proq4", "Pro-Q 4", "切低频")]);

        db.SetSetting("theme", "dark-test");
        db.SetSetting("detail.tagsExpanded", "1");
        db.SetSetting("review.authorName", "测试作者");
        db.SetSetting(BackupRestore.ReviewAuthorIdSetting, "author-original");
        db.AddProjectFolder(ProjectFolder);
        db.AddProjectFolder(Path.Combine(_dir, "不存在的文件夹"));
        db.AddVst2Folder(Path.Combine(_dir, "vst2-missing"));
        db.IgnoreProjectName("Some Rack Plugin");

        db.SaveReviewFile(new ReviewFile("friend-1", "朋友", DateTime.UtcNow,
            [new SharedReview("vst3|fabfilter|proq4", "Pro-Q 4", "FabFilter", 4, "朋友的评价", ["母带"], null, null, null)]),
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        db.ImportReviewAdoptions([new ReviewAdoption("vst3|fabfilter|proq4", "朋友", 4, "朋友的评价", ["母带"])]);
    }

    private string ExportFull(out BackupContent content)
    {
        using var db = new PluginDatabase(Db("a"));
        Fill(db);
        content = BackupRestore.Collect(db, includeSecrets: true, out var locked);
        Assert.Equal(0, locked);
        return UserDataBackup.Serialize(content, BackupPassword, FastIterations);
    }

    [Fact]
    public void FileContainsNoPlainSecrets()
    {
        var json = ExportFull(out _);
        foreach (var secret in new[] { WishPassword, WishSerial, WishAccount, ILokPassword, ILokAccount, "fake-st-pw", BackupPassword })
            Assert.DoesNotContain(secret, json);
        // 其余内容仍是可读的。
        Assert.Contains("很好用", json);
        Assert.Contains("人声链", json);
        Assert.Contains("\"Version\": 7", json);
    }

    [Fact]
    public void RoundTrip_RestoresEverything()
    {
        var json = ExportFull(out _);
        var backup = UserDataBackup.Read(json);
        Assert.True(backup.HasSecrets);
        var secrets = UserDataBackup.DecryptSecrets(backup, BackupPassword);

        using var db = new PluginDatabase(Db("b"));
        var summary = BackupRestore.Apply(db, backup.Content, secrets, Directory.Exists);

        var plugin = Assert.Single(db.LoadPlugins(), p => p.Key == "vst3|fabfilter|proq4");
        Assert.Equal(5, plugin.User.Rating);
        Assert.Equal("很好用", plugin.User.Note);

        var wishes = db.LoadWishes();
        var bought = Assert.Single(wishes, w => w.Name == "Pro-C 2");
        Assert.Equal(WishAccount, bought.Account);
        Assert.Equal(WishPassword, bought.Password);
        Assert.Equal(WishSerial, bought.Serial);
        Assert.True(bought.OwnLogin);
        Assert.Equal("想要", Assert.Single(wishes, w => w.Name == "Saturn 2").Reason);

        var platforms = PurchaseLogin.ByKey(db.LoadPlatformAccounts());
        var ilok = platforms[VendorCatalog.PlatformKey("iLok")];
        Assert.Equal(ILokAccount, ilok.Account);
        Assert.Equal(ILokPassword, ilok.Password);
        Assert.Equal("主账号", ilok.Note);
        Assert.Equal("iLok 账号：" + ILokAccount, platforms[VendorCatalog.PlatformKey("Soundtoys")].Note);
        Assert.Equal(2, summary.Platforms);

        var list = Assert.Single(db.LoadFavoriteLists());
        Assert.Equal("人声链", list.Name);
        Assert.Contains("vst3|waves|c4", db.LoadFavoriteRemovals()[list.Id]);
        var chain = Assert.Single(db.LoadChains());
        Assert.Equal("链备注", chain.Note);
        Assert.Equal("切低频", Assert.Single(chain.Steps).Note);

        Assert.Equal("dark-test", db.GetSetting("theme"));
        Assert.Equal("1", db.GetSetting("detail.tagsExpanded"));
        Assert.Equal("测试作者", db.GetSetting("review.authorName"));
        Assert.Equal("author-original", db.GetSetting(BackupRestore.ReviewAuthorIdSetting));
        Assert.Equal([ProjectFolder], db.LoadProjectFolders());
        Assert.Empty(db.LoadVst2Folders());
        Assert.Equal(2, summary.FoldersMissing);
        Assert.Contains(ProjectReport.IgnoreKey("Some Rack Plugin"), db.LoadIgnoredProjectNames());

        var source = Assert.Single(db.LoadReviewSources());
        Assert.Equal("friend-1", source.AuthorId);
        Assert.Equal("朋友的评价", Assert.Single(db.LoadSharedReviews()).Review.Note);
        Assert.Equal("朋友", db.LoadReviewAdoptions()["vst3|fabfilter|proq4"].AuthorName);
    }

    [Fact]
    public void WrongPassword_Rejected_AndTamperDetected()
    {
        var json = ExportFull(out _);
        var backup = UserDataBackup.Read(json);
        Assert.Throws<BackupPasswordException>(() => UserDataBackup.DecryptSecrets(backup, "fake-wrong-pw"));
        Assert.Throws<BackupPasswordException>(() => UserDataBackup.DecryptSecrets(backup, ""));

        var section = backup.EncryptedSecrets!;
        var data = Convert.FromBase64String(section.Data);
        data[0] ^= 1;
        var tampered = backup with { EncryptedSecrets = section with { Data = Convert.ToBase64String(data) } };
        Assert.Throws<BackupPasswordException>(() => UserDataBackup.DecryptSecrets(tampered, BackupPassword));
    }

    [Fact]
    public void SkippingSecrets_KeepsLocalLogins()
    {
        var json = ExportFull(out _);
        var backup = UserDataBackup.Read(json);

        using var db = new PluginDatabase(Db("c"));
        db.AddWish(new WishItem(0, "Pro-C 2", "FabFilter", Account: "fake-local@example.invalid", Password: "fake-local-pw", OwnLogin: true));
        db.SavePlatformAccount(new PlatformAccount("iLok", "fake-local-ilok@example.invalid", "fake-local-ilok-pw"));
        BackupRestore.Apply(db, backup.Content, secrets: null, _ => true);

        var item = Assert.Single(db.LoadWishes(), w => w.Name == "Pro-C 2");
        Assert.Equal("fake-local@example.invalid", item.Account);
        Assert.Equal("fake-local-pw", item.Password);
        Assert.True(item.OwnLogin);
        var ilok = Assert.Single(db.LoadPlatformAccounts());
        Assert.Equal("fake-local-ilok-pw", ilok.Password);
    }

    [Fact]
    public void EmptyBackupValues_DoNotWipeLocal_AndOwnAuthorIdKept()
    {
        var json = ExportFull(out _);
        var backup = UserDataBackup.Read(json);
        var secrets = UserDataBackup.DecryptSecrets(backup, BackupPassword);
        // 备份里这条只有序列号。
        secrets = secrets with { Wishes = [new WishSecretEntry(secrets.Wishes!.Single().Index, "", "", WishSerial)] };

        using var db = new PluginDatabase(Db("d"));
        db.AddWish(new WishItem(0, "Pro-C 2", "FabFilter", Account: "fake-local@example.invalid", Password: "fake-local-pw"));
        db.SetSetting(BackupRestore.ReviewAuthorIdSetting, "author-this-pc");
        BackupRestore.Apply(db, backup.Content, secrets, _ => true);

        var item = Assert.Single(db.LoadWishes(), w => w.Name == "Pro-C 2");
        Assert.Equal("fake-local@example.invalid", item.Account);
        Assert.Equal("fake-local-pw", item.Password);
        Assert.Equal(WishSerial, item.Serial);
        Assert.Equal("author-this-pc", db.GetSetting(BackupRestore.ReviewAuthorIdSetting));
    }

    [Fact]
    public void NoSecretsExport_HasNoEncryptedPart()
    {
        using var db = new PluginDatabase(Db("e"));
        Fill(db);
        var content = BackupRestore.Collect(db, includeSecrets: false, out _);
        Assert.Null(content.Secrets);
        var json = UserDataBackup.Serialize(content, null);
        Assert.False(UserDataBackup.Read(json).HasSecrets);
        Assert.DoesNotContain(WishAccount, json);
        Assert.DoesNotContain(ILokAccount, json);
        Assert.Throws<ArgumentException>(() => UserDataBackup.Serialize(content with { Secrets = new BackupSecrets([], []) }, null));
    }

    [Fact]
    public void OldVersion1Backup_StillImports()
    {
        const string json = """
            {"Version":1,"ExportedUtc":"2026-01-01T00:00:00Z",
             "Plugins":[{"Key":"vst3|x|y","Name":"Y","Vendor":"X","Rating":3,"Note":"旧备注","MyTags":[]}],
             "Wishes":[{"Name":"Old Wish","Status":"Wished","CreatedUtc":"2026-01-01T00:00:00Z","Priority":2}]}
            """;
        var backup = UserDataBackup.Read(json);
        Assert.False(backup.HasSecrets);
        Assert.Null(backup.Content.Settings);

        using var db = new PluginDatabase(Db("f"));
        db.AddWish(new WishItem(0, "Old Wish", OwnLogin: true, Account: "fake-keep@example.invalid"));
        var summary = BackupRestore.Apply(db, backup.Content, null, _ => true);
        Assert.Equal(1, summary.Wishes.Updated);
        var wish = Assert.Single(db.LoadWishes());
        Assert.True(wish.OwnLogin);
        Assert.Equal("fake-keep@example.invalid", wish.Account);
        Assert.Equal("旧备注", Assert.Single(db.LoadPlugins()).User.Note);
    }

    [Fact]
    public void NewerReviewImportOnThisPc_IsNotReplaced()
    {
        var json = ExportFull(out _);
        var backup = UserDataBackup.Read(json);
        using var db = new PluginDatabase(Db("g"));
        db.SaveReviewFile(new ReviewFile("friend-1", "朋友", DateTime.UtcNow,
            [new SharedReview("vst3|a|b", "B", "A", 2, "更新的评价", [], null, null, null)]), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        BackupRestore.Apply(db, backup.Content, null, _ => true);
        Assert.Equal("更新的评价", Assert.Single(db.LoadSharedReviews()).Review.Note);
    }

    [Fact]
    public void SecretRecords_DoNotLeakInToString()
    {
        var entry = new WishSecretEntry(0, WishAccount, WishPassword, WishSerial);
        var platform = new PlatformBackupEntry("iLok", ILokAccount, ILokPassword, "x");
        Assert.DoesNotContain(WishPassword, entry.ToString());
        Assert.DoesNotContain(ILokPassword, platform.ToString());
        Assert.DoesNotContain(ILokAccount, platform.ToString());
    }
}
