using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

internal static class TestSecrets
{
    /// <summary>测试不能碰测试机的默认钥匙串，非 Windows 上换成测试用的加密。Windows 上测的就是 DPAPI。</summary>
    [ModuleInitializer]
    internal static void UseTestSecretsOffWindows()
    {
        if (!OperatingSystem.IsWindows())
            SecretProtector.Backend = new TestSecretBackend();
    }

    private sealed class TestSecretBackend : ISecretBackend
    {
        private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

        public byte[] Protect(byte[] plain)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);
            return [.. nonce, .. tag, .. cipher];
        }

        public byte[] Unprotect(byte[] protectedBytes)
        {
            if (protectedBytes.Length < 28)
                throw new CryptographicException("太短");
            var plain = new byte[protectedBytes.Length - 28];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(protectedBytes[..12], protectedBytes[28..], protectedBytes[12..28], plain);
            return plain;
        }
    }
}

/// <summary>心愿单和已购账号：快速加入、详情保存、标记已买、厂家账号、批量填写、导出和 iLok。数据库和文件都在临时目录。</summary>
public sealed class WishesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-wishes-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;
    private readonly TestEnvironment _env;
    private readonly ScriptedDialogs _dialogs = new();

    public WishesTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        Assert.StartsWith(Path.GetTempPath(), _dbPath, StringComparison.OrdinalIgnoreCase);
        _database = new PluginDatabase(_dbPath);
        _env = new TestEnvironment(Path.Combine(_dir, "env"));
    }

    public void Dispose()
    {
        SecureClipboard.ClearPending();
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private void ScanPlugins()
    {
        _env.AddVst3("Pro-Q 4", "FabFilter");
        _env.AddVst3("Pro-C 2", "FabFilter", "Fx|Dynamics");
        _env.AddVst3("ValhallaRoom", "Valhalla DSP", "Fx|Reverb");
        var scan = Task.Run(() => ScanWorkflow.RunAsync(_database, _env.Scan, new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None)).GetAwaiter().GetResult();
        Assert.NotNull(scan.Summary);
    }

    /// <summary>心愿单两款，已购三款（两款 brainworx 跟随 Plugin Alliance，一款 Eventide 用 iLok）。</summary>
    private void Seed()
    {
        ScanPlugins();
        _database.AddWish(new WishItem(0, "soothe2", "oeksound", 199, "$", WishPriority.MustHave, "人声齿音和共振", TargetPrice: 149));
        _database.AddWish(new WishItem(0, "Ozone 11", "iZotope", 1999, Reason: "母带"));
        _database.AddWish(new WishItem(0, "bx_console SSL 4000 E", "brainworx", Status: WishStatus.Purchased, PaidPrice: 299, PurchasedOn: new DateOnly(2025, 3, 1)));
        _database.AddWish(new WishItem(0, "bx_limiter", "brainworx", Status: WishStatus.Purchased, PaidPrice: 99, PurchasedOn: new DateOnly(2025, 6, 18)));
        _database.AddWish(new WishItem(0, "H3000 Factory", "Eventide", Status: WishStatus.Purchased, LicenseKind: LicenseKind.ILok, Serial: "EV-123"));
        _database.SavePlatformAccount(new PlatformAccount("Plugin Alliance", "mix@example.com", "pa-password"));
    }

    private MainWindow Open(double width = 1440, double height = 900)
    {
        var window = new MainWindow(_database, _dbPath, _env.Scan) { Width = width, Height = height };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Task task, Window window)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "操作没有结束");
        }
        task.GetAwaiter().GetResult();
        Settle(window);
    }

    private WishesView Wishes(MainWindow window)
    {
        window.Navigate("Wishes");
        Settle(window);
        var page = window.Wishes!;
        page.Dialogs = _dialogs;
        return page;
    }

    private AccountsView Accounts(MainWindow window)
    {
        window.Navigate("Accounts");
        Settle(window);
        var page = window.Accounts!;
        page.Dialogs = _dialogs;
        return page;
    }

    private static T Control<T>(UserControl page, string name) where T : Control => page.FindControl<T>(name) ?? throw new InvalidOperationException("找不到 " + name);

    private WishItem Saved(string name) => _database.LoadWishes().Single(w => w.Name == name);

    // ---------- 心愿单 ----------

    [AvaloniaFact]
    public void QuickAddGuessesVendorAndSelectsTheNewWish()
    {
        ScanPlugins();
        var window = Open();
        var page = Wishes(window);

        Wait(page.QuickAdd("  "), window);
        Assert.Equal("请先输入插件名", window.Status);

        Wait(page.QuickAdd("Pro-Q 4"), window);
        Assert.Equal("已加入心愿单：Pro-Q 4，厂家自动识别为 FabFilter（不对可以直接改），可以在右边补上原因和价格", window.Status);
        Assert.Equal("Pro-Q 4", page.Selected?.Name);
        Assert.True(page.Selected!.IsInstalled);
        Assert.Equal("1", window.NavCount("Wishes"));
        Assert.Equal("FabFilter", Control<AutoCompleteBox>(page, "VendorBox").Text);
        // 只是显示，不算修改。
        Assert.False(page.HasPendingSave);
        Assert.NotEmpty(page.AdviceText);
    }

    [AvaloniaFact]
    public void EditingSavesAfterTheDelayAndKeepsBadFieldsUnchanged()
    {
        Seed();
        var window = Open();
        var page = Wishes(window);
        Assert.Equal(["soothe2", "Ozone 11"], page.Shown.Select(r => r.Name));
        page.Select("soothe2");
        Settle(window);
        Assert.False(page.HasPendingSave);
        Assert.Equal("还差 $50 到目标价", Control<TextBlock>(page, "PriceHint").Text);

        Control<TextBox>(page, "PriceBox").Text = "139";
        Control<TextBox>(page, "LowestBox").Text = "很便宜";
        Control<TextBox>(page, "ReasonBox").Text = "齿音";
        Settle(window);
        Assert.True(page.HasPendingSave);
        page.Flush();
        Settle(window);
        Assert.Equal("历史最低价只能填数字，这一项没有保存", window.Status);
        var saved = Saved("soothe2");
        Assert.Equal((139m, (decimal?)null, "齿音"), (saved.Price, saved.LowestPrice, saved.Reason));
        Assert.True(page.Selected!.ReachedTarget);
        Assert.Equal("已到目标价", Control<TextBlock>(page, "PriceHint").Text);
        Assert.False(page.HasPendingSave);

        // 搜索和排序。
        Control<ComboBox>(page, "SortBox").SelectedItem = WishListing.SortByPrice;
        Settle(window);
        Assert.Equal(["soothe2", "Ozone 11"], page.Shown.Select(r => r.Name));
        Control<TextBox>(page, "SearchBox").Text = "母带";
        Settle(window);
        Assert.Equal(["Ozone 11"], page.Shown.Select(r => r.Name));
    }

    [AvaloniaFact]
    public void MarkingBoughtMovesTheWishToAccountsAndBack()
    {
        Seed();
        var window = Open();
        var page = Wishes(window);
        page.Select("Ozone 11");
        Settle(window);
        page.MarkBought();
        Settle(window);
        Assert.Equal("已把 Ozone 11 移到已购账号，可以去那里填账号密码", window.Status);
        var bought = Saved("Ozone 11");
        Assert.Equal((WishStatus.Purchased, 1999m, DateOnly.FromDateTime(DateTime.Today)), (bought.Status, bought.PaidPrice!.Value, bought.PurchasedOn!.Value));
        Assert.Equal(("1", "4"), (window.NavCount("Wishes"), window.NavCount("Accounts")));

        var accounts = Accounts(window);
        Assert.Contains(accounts.Rows, r => r.Name == "Ozone 11");
        accounts.Select("Ozone 11");
        Settle(window);
        accounts.MoveBackToWishes();
        Settle(window);
        Assert.Equal("已把 Ozone 11 移回心愿单", window.Status);
        Assert.Equal(WishStatus.Wished, Saved("Ozone 11").Status);

        var wishes = Wishes(window);
        Assert.Contains(wishes.Rows, r => r.Name == "Ozone 11");
        wishes.Select("Ozone 11");
        Settle(window);
        _dialogs.Answers.Enqueue(true);
        Wait(wishes.DeleteShownAsync(), window);
        Assert.Equal("已删除 Ozone 11", window.Status);
        Assert.DoesNotContain(_database.LoadWishes(), w => w.Name == "Ozone 11");
        Assert.Contains("删除这款心愿？", _dialogs.Asked.Last());
    }

    // ---------- 已购账号 ----------

    [AvaloniaFact]
    public void PlatformLoginIsSharedAndOwnLoginStaysSeparate()
    {
        Seed();
        var window = Open();
        var page = Accounts(window);
        Assert.Equal("3 款，3 款已填账号，已花 ¥398，密码和序列号加密保存在本机", WishListing.AccountSubtitle(page.Rows));
        page.Select("bx_limiter");
        Settle(window);
        Assert.Equal("mix@example.com", Control<TextBox>(page, "AccountBox").Text);
        Assert.Equal("pa-password", Control<SecretBox>(page, "PasswordBox").Secret);
        Assert.False(Control<SecretBox>(page, "PasswordBox").IsRevealed);
        Assert.Contains("2 款插件共用", Control<TextBlock>(page, "LoginHint").Text);
        Assert.False(page.HasPendingSave);

        // 改的是厂家账号：两款一起生效。
        Control<TextBox>(page, "AccountBox").Text = "new@example.com";
        Control<TextBox>(page, "ActivatedOnBox").Text = "Studio";
        Settle(window);
        page.Flush();
        Settle(window);
        Assert.Equal("已保存 bx_limiter，账号密码存在 Plugin Alliance 的厂家账号里", window.Status);
        Assert.Equal("new@example.com", _database.LoadPlatformAccounts().Single(p => p.Platform == "Plugin Alliance").Account);
        Assert.Equal(["new@example.com（厂家账号）", "new@example.com（厂家账号）"],
            page.Rows.Where(r => r.Item.Vendor == "brainworx").Select(r => r.AccountText));
        Assert.Equal("Studio", Saved("bx_limiter").ActivatedOn);

        // 改成单独账号：厂家账号不动。
        Wait(page.SetLoginOwn(true), window);
        Assert.Equal("bx_limiter 改为单独账号", window.Status);
        Assert.True(Saved("bx_limiter").OwnLogin);
        Assert.Equal("", Control<TextBox>(page, "AccountBox").Text);
        Control<TextBox>(page, "AccountBox").Text = "solo@example.com";
        Settle(window);
        page.Flush();
        Assert.Equal("solo@example.com", Saved("bx_limiter").Account);
        Assert.Equal("new@example.com", _database.LoadPlatformAccounts().Single(p => p.Platform == "Plugin Alliance").Account);
        Assert.Equal(["solo@example.com", "new@example.com"], page.KnownAccounts);

        page.MachineName = "Mac-Studio";
        page.AddThisMachine();
        Settle(window);
        Assert.Equal("Studio，Mac-Studio", Control<TextBox>(page, "ActivatedOnBox").Text);
        page.AddThisMachine();
        Assert.Equal("Studio，Mac-Studio", Control<TextBox>(page, "ActivatedOnBox").Text);
    }

    [AvaloniaFact]
    public void BatchFillAsksBeforeOverwritingAndBatchAddGuessesVendors()
    {
        Seed();
        var window = Open();
        var page = Accounts(window);
        page.Select("bx_limiter", "H3000 Factory");
        Settle(window);
        Assert.True(Control<ScrollViewer>(page, "BatchPanel").IsVisible);
        Assert.Equal("已选 2 款", Control<TextBlock>(page, "BatchTitle").Text);

        Wait(page.BatchApplyAsync(), window);
        Assert.Equal("至少填一项", window.Status);

        // H3000 已有序列号，换成别的要先确认；取消就什么都不改。
        page.SetBatch("", "", "EV-999");
        _dialogs.Answers.Enqueue(false);
        Wait(page.BatchApplyAsync(), window);
        Assert.Equal("EV-123", Saved("H3000 Factory").Serial);
        Assert.Contains("有 1 款已经填了不一样的内容", _dialogs.Asked.Last());

        page.SetBatch("ilok-user", "pw", "", license: 1);
        Wait(page.BatchApplyAsync(), window);
        Assert.StartsWith("已保存到 2 款插件；新建厂家账号：Eventide", window.Status);
        Assert.Equal(LicenseKind.ILok, Saved("bx_limiter").LicenseKind);

        _dialogs.Answers.Enqueue((IReadOnlyList<PurchaseDraft>?)[new PurchaseDraft("Pro-C 2"), new PurchaseDraft("bx_limiter", "brainworx", "", "", "SN-9")]);
        Wait(page.BatchAddAsync(), window);
        Assert.Equal("新增 1 款，更新 1 款，1 款按插件名自动填了厂家", window.Status);
        Assert.Equal("FabFilter", Saved("Pro-C 2").Vendor);
        Assert.Equal("SN-9", Saved("bx_limiter").Serial);
        Assert.Equal("4", window.NavCount("Accounts"));

        page.Select("Pro-C 2", "bx_limiter");
        Settle(window);
        _dialogs.Answers.Enqueue(true);
        Wait(page.BatchDeleteAsync(), window);
        Assert.Equal("已删除 2 条记录", window.Status);
        Assert.Contains("其中 1 条保存的账号密码也一起删除", _dialogs.Asked.Last());
    }

    [AvaloniaFact]
    public void PlatformsViewFillsILokAndClearsAccounts()
    {
        Seed();
        var window = Open();
        var page = Accounts(window);
        page.ShowPlatforms(true);
        Settle(window);
        Assert.Equal(["iLok", "Plugin Alliance", "Eventide"], page.Platforms.Select(p => p.Platform));

        Wait(page.ILokFillAsync(), window);
        Assert.Equal(PlatformListing.ILokFirst, window.Status);
        Assert.Equal("iLok", page.ShownPlatform?.Platform);

        Control<TextBox>(page, "PlatformAccountBox").Text = "me-ilok";
        Control<SecretBox>(page, "PlatformPassword").Secret = "ilok-pw";
        Control<TextBox>(page, "PlatformNote").Text = "主力 iLok";
        Settle(window);
        page.Flush();
        Settle(window);
        Assert.Equal("已保存 iLok 的厂家账号", window.Status);

        _dialogs.Answers.Enqueue(((IReadOnlyList<int>?)[0], false));
        Wait(page.ILokFillAsync(), window);
        Assert.Equal(["Eventide（1 款）：空着，会填入 iLok 账号和密码"], _dialogs.Options.Last());
        Assert.Equal("已填入 iLok 账号：Eventide", window.Status);
        var eventide = _database.LoadPlatformAccounts().Single(p => p.Platform == "Eventide");
        Assert.Equal(("me-ilok", "ilok-pw"), (eventide.Account, eventide.Password));

        page.SelectPlatform("Plugin Alliance");
        Settle(window);
        Assert.Contains("2 款插件跟随这个账号", Control<TextBlock>(page, "PlatformSubtitle").Text);
        _dialogs.Answers.Enqueue(true);
        Wait(page.DeletePlatformAsync(), window);
        Assert.Equal("已清空 Plugin Alliance 的厂家账号", window.Status);
        Assert.DoesNotContain(_database.LoadPlatformAccounts(), p => p.Platform == "Plugin Alliance");
    }

    [AvaloniaFact]
    public void ExportWarnsFirstAndChecklistCanBeSaved()
    {
        Seed();
        var window = Open();
        var page = Accounts(window);
        var file = Path.Combine(_dir, "账号.txt");
        Assert.StartsWith(Path.GetTempPath(), file, StringComparison.OrdinalIgnoreCase);

        _dialogs.Answers.Enqueue(false);
        Wait(page.ExportAsync(), window);
        Assert.False(File.Exists(file));

        _dialogs.Answers.Enqueue(true);
        _dialogs.Answers.Enqueue(file);
        Wait(page.ExportAsync(), window);
        Assert.Contains(WishListing.ExportWarning, _dialogs.Asked[^2]);
        Assert.Equal("已导出 3 款的账号", window.Status);
        var text = File.ReadAllText(file);
        Assert.Contains("bx_limiter\tbrainworx\tmix@example.com\tpa-password\t", text);
        Assert.Contains("H3000 Factory\tEventide\t\t\tEV-123", text);

        page.MachineName = "Mac-Studio";
        _dialogs.Answers.Enqueue("saved.txt");
        Wait(page.ChecklistAsync(), window);
        Assert.Contains("H3000 Factory", _dialogs.Shown.Last());
    }

    // ---------- 截图 ----------

    [AvaloniaFact]
    public void Screenshots()
    {
        var output = Environment.GetEnvironmentVariable("WHATPLUGIN_SCREENSHOTS");
        if (output != null)
            Directory.CreateDirectory(output);
        void Save(Window window, string name)
        {
            Settle(window);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(output ?? _dir, name + ".png"), new PngBitmapEncoderOptions());
        }

        Seed();
        var window = Open();
        var wishes = Wishes(window);
        wishes.Select("soothe2");
        Save(window, "wishes");

        var accounts = Accounts(window);
        accounts.Select("bx_limiter");
        Save(window, "accounts");
        accounts.Select("bx_limiter", "H3000 Factory");
        Save(window, "accounts-batch");
        accounts.ShowPlatforms(true);
        Settle(window);
        accounts.SelectPlatform("Plugin Alliance");
        Save(window, "accounts-platforms");

        var dialog = new BatchAddDialog(_database.LoadEffects(), _database.LoadWishes().Where(w => w.Status == WishStatus.Purchased).ToList(), _dialogs);
        dialog.Show();
        dialog.Rows[0].Selected = true;
        dialog.SelectVisible();
        Save(dialog, "accounts-batch-add");
        Assert.Equal("已选 3 款", dialog.Summary);
        dialog.UsePaste("Pro-Q 4, FabFilter\na\tb\tc\td\te\tf");
        Assert.Equal("可识别 1 款，第 2 行无法识别", dialog.Summary);
        dialog.Close();
    }
}
