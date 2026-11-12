using System.Text;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>改动静态的厂家目录，不能和其他依赖目录的测试同时跑。</summary>
[CollectionDefinition("VendorCatalog", DisableParallelization = true)]
public sealed class VendorCatalogCollection;

[Collection("VendorCatalog")]
public sealed class PlatformAccountTests : IDisposable
{
    // 测试用的假数据，不是真实账号。
    private const string FakePassword = "fake-platform-pw-不是真的";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-pa-" + Guid.NewGuid().ToString("N"));

    public PlatformAccountTests()
    {
        Directory.CreateDirectory(_dir);
        VendorCatalog.UseForTests(
        [
            new VendorCatalog.Entry("Plugin Alliance", ["Plugin Alliance LLC"], "Plugin Alliance", ["bx_digital V3", "bx_console SSL 4000 E"]),
            new VendorCatalog.Entry("Brainworx", ["Brainworx Audio GmbH", "bx"], "Plugin Alliance", ["bx_masterdesk", "bx_limiter True Peak"]),
            new VendorCatalog.Entry("SPL", ["Sound Performance Lab"], "Plugin Alliance", ["SPL Transient Designer Plus"]),
            new VendorCatalog.Entry("FabFilter", ["FabFilter Software Instruments"], "FabFilter", ["Pro-Q 4", "Pro-Q 3", "Pro-C 2", "Saturn 2", "Timeless 3"]),
            new VendorCatalog.Entry("Waves", ["Waves Audio Ltd"], "Waves", ["C4 Multiband Compressor", "C4", "C1 Compressor", "Renaissance Compressor", "Compressor"], ["Q10", "CLA"]),
            new VendorCatalog.Entry("iZotope", [], "Native Instruments", ["Ozone 11", "Neutron 4"]),
            new VendorCatalog.Entry("Universal Audio", [], "Universal Audio", ["Pultec EQP-1A"], ["UAD", "UADx"]),
            new VendorCatalog.Entry("Output", [], "Output", ["Thermal", "Portal"]),
            new VendorCatalog.Entry("Other Co", [], "Other Co", ["Thermal"]),
            new VendorCatalog.Entry("Slatey", [], "Slatey", ["Virtual Mixer Rack"], UsesILok: true),
        ]);
    }

    public void Dispose()
    {
        VendorCatalog.UseForTests(null);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "t.db");

    // ---------- iLok 一键填入 ----------

    private static PlatformAccount ILok(string password = "fake-ilok-pw") => new(ILokFill.Platform, "me@ilok.invalid", password);

    [Fact]
    public void UsesILok_ComesFromCatalog()
    {
        Assert.True(VendorCatalog.UsesILok("Slatey"));
        Assert.True(VendorCatalog.UsesILok(" slatey "));
        Assert.False(VendorCatalog.UsesILok("FabFilter"));
        Assert.False(VendorCatalog.UsesILok("Unknown Vendor"));
        Assert.False(VendorCatalog.UsesILok(""));
        Assert.True(ILokFill.IsILok("ILOK"));
        Assert.False(ILokFill.IsILok("Slatey"));
    }

    [Fact]
    public void ILokFill_EmptyVendor_GetsAccountAndPassword()
    {
        var result = ILokFill.Apply(ILok(), new PlatformAccount("Slatey"));
        Assert.Equal(ILokFillAction.Filled, result.Action);
        Assert.Equal("me@ilok.invalid", result.Updated.Account);
        Assert.Equal("fake-ilok-pw", result.Updated.Password);
        Assert.Equal("Slatey", result.Updated.Platform);
    }

    [Fact]
    public void ILokFill_SameAccount_SyncsPassword()
    {
        var target = new PlatformAccount("Slatey", "ME@ilok.invalid", "fake-old-pw", "note");
        var result = ILokFill.Apply(ILok("fake-new-pw"), target);
        Assert.Equal(ILokFillAction.Synced, result.Action);
        Assert.Equal("fake-new-pw", result.Updated.Password);
        Assert.Equal("note", result.Updated.Note);

        var again = ILokFill.Apply(ILok("fake-new-pw"), result.Updated);
        Assert.Equal(ILokFillAction.Unchanged, again.Action);
    }

    [Fact]
    public void ILokFill_OtherAccount_OnlyAddsNoteWithoutPassword()
    {
        var target = new PlatformAccount("Slatey", "other@vendor.invalid", "fake-vendor-pw", "第一行");
        var result = ILokFill.Apply(ILok(), target);
        Assert.Equal(ILokFillAction.NoteAdded, result.Action);
        Assert.Equal("other@vendor.invalid", result.Updated.Account);
        Assert.Equal("fake-vendor-pw", result.Updated.Password);
        Assert.Equal("第一行" + Environment.NewLine + "iLok 账号：me@ilok.invalid", result.Updated.Note);
        Assert.DoesNotContain("fake-ilok-pw", result.Updated.Note);

        // 再点一次不重复；iLok 换了账号就替换那一行。
        Assert.Equal(ILokFillAction.Unchanged, ILokFill.Apply(ILok(), result.Updated).Action);
        var changed = ILokFill.Apply(new PlatformAccount("iLok", "new@ilok.invalid", "fake-ilok-pw"), result.Updated);
        Assert.Equal(ILokFillAction.NoteAdded, changed.Action);
        Assert.Equal("第一行" + Environment.NewLine + "iLok 账号：new@ilok.invalid", changed.Updated.Note);
    }

    [Fact]
    public void ILokFill_LockedTargetSkipped_LockedOrEmptyILokRejected()
    {
        var locked = new PlatformAccount("Slatey", "x@vendor.invalid", "", "", Locked: true);
        Assert.Equal(ILokFillAction.Skipped, ILokFill.Apply(ILok(), locked).Action);
        Assert.Throws<ArgumentException>(() => ILokFill.Apply(new PlatformAccount("iLok"), new PlatformAccount("Slatey")));
        Assert.Throws<ArgumentException>(() => ILokFill.Apply(ILok() with { Locked = true }, new PlatformAccount("Slatey")));
    }

    // ---------- 厂家目录 ----------

    [Theory]
    [InlineData("Pro-Q 4", "FabFilter")]
    [InlineData("Pro-Q 2", "FabFilter")]          // 目录里没有的旧版本，按产品系列认
    [InlineData("FabFilter Pro-L 2", "FabFilter")] // 名字带厂家名
    [InlineData("C4", "Waves")]
    [InlineData("C4 Stereo", "Waves")]
    [InlineData("bx_masterdesk", "Brainworx")]
    [InlineData("Output Portal", "Output")]
    [InlineData("Ozone 9 Imager", "iZotope")]      // 系列名 + 版本号 + 模块
    [InlineData("Neutron 3 Compressor", "iZotope")]
    [InlineData("C1 comp-sc", "Waves")]           // 带数字的型号开头
    [InlineData("Q10", "Waves")]                  // 名字就是厂家专用前缀
    [InlineData("CLA-76", "Waves")]
    [InlineData("UAD Pultec HLF-3C", "Universal Audio")]
    public void GuessesVendorFromName(string name, string vendor) =>
        Assert.Equal(vendor, VendorCatalog.Guess(name)?.Vendor);

    [Theory]
    [InlineData("Compressor")]      // 太普通
    [InlineData("Output Gain")]     // Output 是普通英文词，后半截不是它的产品
    [InlineData("Output")]
    [InlineData("1176 Limiter")]    // 纯数字型号各家都有
    [InlineData("Transient Shaper")]
    [InlineData("Totally Unknown")]
    [InlineData("")]
    public void DoesNotGuessFromGenericOrUnknownNames(string name) => Assert.Null(VendorCatalog.Guess(name));

    [Fact]
    public void SameNameFromTwoVendorsIsAmbiguous()
    {
        var guess = VendorCatalog.Guess("Thermal");
        Assert.NotNull(guess);
        Assert.True(guess.IsAmbiguous);
        Assert.Equal("", guess.Vendor);
        Assert.Equal(["Other Co", "Output"], guess.Candidates);
    }

    [Theory]
    [InlineData("Brainworx Audio GmbH", "Plugin Alliance")]
    [InlineData("brainworx", "Plugin Alliance")]
    [InlineData("SPL", "Plugin Alliance")]
    [InlineData("FabFilter Software Instruments", "FabFilter")]
    [InlineData("Acme Audio Inc.", "Acme Audio")] // 目录里没有：平台就是厂家自己
    [InlineData("", "")]
    public void FindsAccountPlatform(string vendor, string platform) =>
        Assert.Equal(platform, VendorCatalog.PlatformOf(vendor));

    [Fact]
    public void CatalogFillsVendorOnlyWhenPluginHasNone()
    {
        var file = new FileScanResult(@"C:\fake\a.vst3", PluginFormat.Vst3, Arch.X64, 1, DateTime.UtcNow, ScanStatus.ModuleInfo, null, []);
        PluginClassInfo Class(string name, string vendor, InfoSource source) => new(name, vendor, "1", "Fx", false, source);

        var merged = PluginMerger.Merge(
        [
            file with { Path = @"C:\fake\1.vst3", Classes = [Class("Pro-Q 3", "", InfoSource.FileName)] },
            file with { Path = @"C:\fake\2.vst3", Classes = [Class("Saturn 2", "Plugins", InfoSource.FileName)] },
            file with { Path = @"C:\fake\3.vst3", Classes = [Class("Timeless 3", "Someone Else", InfoSource.ModuleInfo)] },
        ]);

        Assert.Equal("FabFilter", merged.Single(p => p.Name == "Pro-Q 3").Vendor);
        Assert.Equal("FabFilter", merged.Single(p => p.Name == "Saturn 2").Vendor);   // 文件夹名猜的厂家让位给目录
        Assert.Equal("Someone Else", merged.Single(p => p.Name == "Timeless 3").Vendor); // 插件自己写的厂家优先
    }

    // ---------- 厂家账号 ----------

    [Fact]
    public void PlatformPasswordIsEncryptedAndSurvivesReopen()
    {
        using (var db = new PluginDatabase(DbPath))
            db.SavePlatformAccount(new PlatformAccount("Plugin Alliance", "me@example.com", FakePassword, "注册邮箱"));
        SqliteConnection.ClearAllPools();

        Assert.Equal(-1, File.ReadAllBytes(DbPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes("fake-platform-pw")));
        using var reopened = new PluginDatabase(DbPath);
        var account = Assert.Single(reopened.LoadPlatformAccounts());
        Assert.Equal(("Plugin Alliance", "me@example.com", FakePassword, "注册邮箱"), (account.Platform, account.Account, account.Password, account.Note));
    }

    [Fact]
    public void SavingAnEmptyPlatformAccountRemovesIt()
    {
        using var db = new PluginDatabase(DbPath);
        db.SavePlatformAccount(new PlatformAccount("Waves", "me"));
        db.SavePlatformAccount(new PlatformAccount("Waves"));
        Assert.Empty(db.LoadPlatformAccounts());
    }

    [Fact]
    public void BrandsOfOnePlatformShareOneLogin()
    {
        using var db = new PluginDatabase(DbPath);
        var bx = db.AddWish(new WishItem(0, "bx_masterdesk", "Brainworx Audio GmbH", Status: WishStatus.Purchased));
        var spl = db.AddWish(new WishItem(0, "SPL Transient Designer Plus", "SPL", Status: WishStatus.Purchased));
        var own = db.AddWish(new WishItem(0, "bx_digital V3", "Plugin Alliance", Status: WishStatus.Purchased, Account: "second", OwnLogin: true));

        var result = db.SetLogins([bx.Id], "me@example.com", FakePassword, "BX-SERIAL");

        Assert.Equal(["Plugin Alliance"], result.NewPlatforms);
        var platforms = PurchaseLogin.ByKey(db.LoadPlatformAccounts());
        var rows = db.LoadWishes();
        var bxRow = rows.Single(w => w.Id == bx.Id);
        Assert.Equal("BX-SERIAL", bxRow.Serial);    // 序列号仍按插件存
        Assert.Equal("", bxRow.Account);             // 账号存在厂家账号里
        var splLogin = PurchaseLogin.Resolve(rows.Single(w => w.Id == spl.Id), platforms);
        Assert.Equal(("me@example.com", FakePassword, true, "Plugin Alliance"), (splLogin.Account, splLogin.Password, splLogin.FromPlatform, splLogin.Platform));
        var ownLogin = PurchaseLogin.Resolve(rows.Single(w => w.Id == own.Id), platforms);
        Assert.Equal(("second", false), (ownLogin.Account, ownLogin.FromPlatform));
    }

    [Fact]
    public void DifferentLoginForAnExistingPlatformBecomesOwnLogin()
    {
        using var db = new PluginDatabase(DbPath);
        db.SavePlatformAccount(new PlatformAccount("FabFilter", "main@example.com", FakePassword));
        var same = db.AddWish(new WishItem(0, "Pro-Q 4", "FabFilter", Status: WishStatus.Purchased));
        var other = db.AddWish(new WishItem(0, "Pro-C 2", "FabFilter", Status: WishStatus.Purchased));

        var first = db.SetLogins([same.Id], "main@example.com", "", "");
        var second = db.SetLogins([other.Id], "second@example.com", "", "");

        Assert.Equal((0, 1, 0), (first.NewPlatforms.Count, first.Shared, first.Own));
        Assert.Equal((0, 0, 1), (second.NewPlatforms.Count, second.Shared, second.Own));
        var rows = db.LoadWishes();
        Assert.False(rows.Single(w => w.Id == same.Id).OwnLogin);
        Assert.True(rows.Single(w => w.Id == other.Id).OwnLogin);
        Assert.Equal("main@example.com", Assert.Single(db.LoadPlatformAccounts()).Account);
    }

    [Fact]
    public void ItemWithoutVendorKeepsItsOwnLogin()
    {
        using var db = new PluginDatabase(DbPath);
        var item = db.AddWish(new WishItem(0, "Mystery", Status: WishStatus.Purchased));

        var result = db.SetLogins([item.Id], "me", FakePassword, "");

        Assert.Equal(1, result.Own);
        Assert.Empty(db.LoadPlatformAccounts());
        var row = Assert.Single(db.LoadWishes());
        Assert.Equal(("me", FakePassword, true), (row.Account, row.Password, row.OwnLogin));
    }

    [Fact]
    public void UpgradeKeepsFilledLoginsAsOwnAndOthersFollow()
    {
        using (var db = new PluginDatabase(DbPath))
        {
            db.AddWish(new WishItem(0, "Filled", "FabFilter", Status: WishStatus.Purchased, Account: "me"));
            db.AddWish(new WishItem(0, "Password Only", "FabFilter", Status: WishStatus.Purchased, Password: FakePassword));
            db.AddWish(new WishItem(0, "Empty", "FabFilter", Status: WishStatus.Purchased, Serial: "ONLY-SERIAL"));
        }
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection("Data Source=" + DbPath))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE platform_accounts;
                ALTER TABLE wishes DROP COLUMN own_login;
                PRAGMA user_version = 10;
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var upgraded = new PluginDatabase(DbPath);
        var rows = upgraded.LoadWishes();
        Assert.True(rows.Single(w => w.Name == "Filled").OwnLogin);
        Assert.True(rows.Single(w => w.Name == "Password Only").OwnLogin);
        Assert.False(rows.Single(w => w.Name == "Empty").OwnLogin);
        Assert.Equal("me", rows.Single(w => w.Name == "Filled").Account);
        Assert.Empty(upgraded.LoadPlatformAccounts());
    }

    [Fact]
    public void LockedPlatformPasswordIsNotOverwritten()
    {
        using var db = new PluginDatabase(DbPath);
        db.SavePlatformAccount(new PlatformAccount("Waves", "me", FakePassword));
        db.SavePlatformAccount(new PlatformAccount("Waves", "renamed", "", "", Locked: true));
        var account = Assert.Single(db.LoadPlatformAccounts());
        Assert.Equal(("renamed", FakePassword), (account.Account, account.Password));
    }

    [Fact]
    public void ToStringHidesPlatformSecrets()
    {
        var account = new PlatformAccount("Waves", "me@example.com", FakePassword);
        Assert.DoesNotContain(FakePassword, account.ToString());
        Assert.DoesNotContain("me@example.com", account.ToString());
        var login = new EffectiveLogin("me@example.com", FakePassword, false, true, "Waves");
        Assert.DoesNotContain(FakePassword, login.ToString());
    }
}
