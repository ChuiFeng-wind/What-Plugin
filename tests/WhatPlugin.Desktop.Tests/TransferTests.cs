using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>左下角的导出、导入，别人分享的评价（采用、对比、移除）。数据库和导出的文件都在临时目录。</summary>
public sealed class TransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;
    private readonly TestEnvironment _env;
    private readonly ScriptedDialogs _dialogs = new();

    public TransferTests()
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

    private string TempFile(string name)
    {
        var path = Path.Combine(_dir, name);
        Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
        return path;
    }

    private PluginRow Plugin(string name) => _database.LoadEffects().First(p => p.Name == name);

    private ChainStep Step(string name) => new(Plugin(name).Key, name);

    /// <summary>三款插件，两款写了评价；一条心愿带密码；一个笔记文件夹里两条笔记，之间有发送。</summary>
    private void Seed()
    {
        _env.AddVst3("Pro-Q 4", "FabFilter");
        _env.AddVst3("Pro-C 2", "FabFilter", "Fx|Dynamics");
        _env.AddVst3("ValhallaRoom", "Valhalla DSP", "Fx|Reverb");
        var scan = Task.Run(() => ScanWorkflow.RunAsync(_database, _env.Scan, new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None)).GetAwaiter().GetResult();
        Assert.NotNull(scan.Summary);

        var proQ = Plugin("Pro-Q 4");
        _database.SaveUserData(proQ.Id, proQ.User with { Rating = 5, Note = "动态 EQ 很顺手", MyTags = ["人声"] });
        var room = Plugin("ValhallaRoom");
        _database.SaveUserData(room.Id, room.User with { Rating = 4, Note = "短混响" });

        _database.AddWish(new WishItem(0, "soothe2", "oeksound", 199, "$", Reason: "人声齿音"));
        _database.AddWish(new WishItem(0, "Pro-MB", "FabFilter", Status: WishStatus.Purchased, Account: "mix@example.com", Password: "pm-password", Serial: "PMB-1"));

        var folder = _database.AddNoteFolder(null, "Vocals");
        var lead = _database.AddChain("Lead Vox", folder.Id);
        _database.SaveChain(lead.Id, "先 EQ 再压", [Step("Pro-Q 4"), Step("Pro-C 2")]);
        var verb = _database.AddChain("Verb", folder.Id);
        _database.SaveChain(verb.Id, "", [Step("ValhallaRoom")]);
        _database.SetRoutes(lead.Id, null, [verb.Id], []);
    }

    /// <summary>另一位分享者对 Pro-Q 4 和一款没装的插件的评价。</summary>
    private ReviewFile OtherReviews(string author = "阿杰", string authorId = "other-author") =>
        new(authorId, author, DateTime.UtcNow,
        [
            new SharedReview(ReviewShare.KeyOf("Pro-Q 4", "FabFilter"), "Pro-Q 4", "FabFilter", 4, "切低频干净", ["母带"], ProjectCount: 12),
            new SharedReview(ReviewShare.KeyOf("Not Installed", "Nobody"), "Not Installed", "Nobody", 3, "", []),
        ]);

    private MainWindow Open(double width = 1440, double height = 900)
    {
        var window = new MainWindow(_database, _dbPath, _env.Scan) { Width = width, Height = height };
        window.Show();
        Settle(window);
        window.Transfer.Dialogs = _dialogs;
        window.Library("Plugins")!.Dialogs = _dialogs;
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

    private static Func<IReadOnlyList<PartOption>, IReadOnlySet<string>?> AllEnabled =>
        options => options.Where(o => o.Enabled).Select(o => o.Id).ToHashSet();

    [AvaloniaFact]
    public void BackupRoundTripRestoresReviewsNotesAndSecrets()
    {
        Seed();
        var window = Open();
        var path = TempFile("backup.json");

        _dialogs.Answers.Enqueue(new ExportChoice(false, new HashSet<string> { "Reviews", "Notes", "Wishes", "Secrets" }, "backup-pass"));
        _dialogs.Answers.Enqueue(path);
        Wait(window.Transfer.ExportAsync(), window);
        Assert.Equal(["导出", "导出备份｜" + TransferListing.BackupFileName(BackupParts.Reviews | BackupParts.Notes | BackupParts.Wishes | BackupParts.Secrets, DateTime.Now)], _dialogs.Asked);
        Assert.StartsWith("已导出 ", window.Status);
        Assert.Contains("2 条混音笔记", window.Status);
        Assert.Contains("已用备份密码加密", window.Status);
        Assert.DoesNotContain("pm-password", File.ReadAllText(path));
        // 选项里的数字从数据库数出来；账号密码挂在心愿单下面。
        var self = _dialogs.Parts[0];
        Assert.Contains("2 款", self.First(o => o.Id == "Reviews").Detail);
        Assert.Equal("Wishes", self.First(o => o.Id == "Secrets").Parent);
        Assert.Equal(TransferListing.ToParts(["Reviews", "Notes", "Wishes", "Secrets"]).ToString(), ((BackupParts)int.Parse(_database.GetSetting(TransferListing.ExportPartsSetting)!)).ToString());

        // 本机内容改掉，再从备份恢复。
        var proQ = Plugin("Pro-Q 4");
        _database.SaveUserData(proQ.Id, proQ.User with { Rating = 1, Note = "" });
        var purchase = _database.LoadWishes().First(w => w.Name == "Pro-MB");
        _database.SaveWish(purchase with { Password = "changed" });

        _dialogs.Asked.Clear();
        _dialogs.Answers.Enqueue(path);
        _dialogs.Answers.Enqueue(AllEnabled);
        _dialogs.Answers.Enqueue("wrong-pass");
        _dialogs.Answers.Enqueue("backup-pass");
        Wait(window.Transfer.ImportAsync(), window);
        Assert.Equal(["导入", "导入备份｜" + TransferListing.ImportBackupMessage, "备份密码", "备份密码"], _dialogs.Asked);
        Assert.Single(_dialogs.PasswordErrors);
        // 备份里没有的部分不能勾。
        Assert.False(_dialogs.Parts[^1].First(o => o.Id == "Settings").Enabled);
        Assert.StartsWith("已导入：", window.Status);
        Assert.Contains("账号密码 1 条", window.Status);

        Assert.Equal(5, Plugin("Pro-Q 4").User.Rating);
        Assert.Equal("动态 EQ 很顺手", Plugin("Pro-Q 4").User.Note);
        Assert.Equal("pm-password", _database.LoadWishes().First(w => w.Name == "Pro-MB").Password);
        // 插件库跟着重新读。
        var library = window.Library("Plugins")!;
        library.Select("Pro-Q 4");
        Assert.Equal(5, library.DetailRating);
    }

    [AvaloniaFact]
    public void SkippingTheBackupPasswordKeepsLocalSecrets()
    {
        Seed();
        var window = Open();
        var path = TempFile("backup.json");
        _dialogs.Answers.Enqueue(new ExportChoice(false, new HashSet<string> { "Wishes", "Secrets" }, "backup-pass"));
        _dialogs.Answers.Enqueue(path);
        Wait(window.Transfer.ExportAsync(), window);

        var purchase = _database.LoadWishes().First(w => w.Name == "Pro-MB");
        _database.SaveWish(purchase with { Password = "local" });
        _dialogs.Answers.Enqueue(path);
        _dialogs.Answers.Enqueue(AllEnabled);
        _dialogs.Answers.Enqueue("");
        Wait(window.Transfer.ImportAsync(), window);
        Assert.StartsWith("已导入：", window.Status);
        Assert.DoesNotContain("账号密码", window.Status);
        Assert.Equal("local", _database.LoadWishes().First(w => w.Name == "Pro-MB").Password);

        // 取消输入密码就整个不导入。
        _dialogs.Answers.Enqueue(path);
        _dialogs.Answers.Enqueue(AllEnabled);
        _dialogs.Answers.Enqueue(null);
        var before = window.Status;
        Wait(window.Transfer.ImportAsync(), window);
        Assert.Equal(before, window.Status);
    }

    [AvaloniaFact]
    public void ShareBundleExportsReviewsAndNotesTogether()
    {
        Seed();
        var window = Open();
        var path = TempFile("share" + ShareBundles.Extension);

        _dialogs.Answers.Enqueue(new ExportChoice(true, new HashSet<string> { TransferListing.ShareReviewsId, TransferListing.ShareNotesId }, ""));
        _dialogs.Answers.Enqueue((Func<IReadOnlyList<PluginRow>, ReviewExportResult?>)(rows => new ReviewExportResult("小明", rows, new ReviewExportOptions(Categories: true))));
        _dialogs.Answers.Enqueue((Func<IReadOnlyList<PluginChain>, NoteExportResult?>)(chains => new NoteExportResult("小明", chains, true)));
        _dialogs.Answers.Enqueue(path);
        Wait(window.Transfer.ExportAsync(), window);
        Assert.Equal(["导出", "导出评价｜", "导出混音笔记｜小明", "导出分享｜" + TransferListing.ShareFileName("小明")], _dialogs.Asked);
        Assert.Equal($"已导出 2 条评价和 2 条混音笔记到 {path}", window.Status);
        Assert.Equal("share", _database.GetSetting(TransferListing.ExportModeSetting));
        Assert.Equal("小明", _database.GetSetting(ReviewListing.AuthorNameSetting));
        var bundle = ShareBundles.Read(path);
        Assert.Equal(2, bundle.Reviews!.Reviews.Count);
        Assert.True(bundle.Notes!.HasRoutes);
        Assert.DoesNotContain("pm-password", File.ReadAllText(path));

        // 自己导出的评价不能再导入，笔记可以。
        _dialogs.Asked.Clear();
        _dialogs.Answers.Enqueue(path);
        _dialogs.Answers.Enqueue(AllEnabled);
        Wait(window.Transfer.ImportAsync(), window);
        var options = _dialogs.Parts[^1];
        Assert.False(options[0].Enabled);
        Assert.Equal("这是你自己导出的评价，不需要导入", options[0].Detail);
        Assert.True(options[1].Enabled);
        Assert.StartsWith("已导入 2 条混音笔记到“来自 小明 的笔记”", window.Status);
        Assert.Contains(_database.LoadNoteFolders(), f => f.Name == "来自 小明 的笔记");
        Assert.Equal(4, _database.LoadChains().Count);
    }

    [AvaloniaFact]
    public void SingleShareFilesUseTheirOwnFormat()
    {
        Seed();
        var window = Open();
        var notes = TempFile("notes" + NoteShare.Extension);
        _dialogs.Answers.Enqueue(new ExportChoice(true, new HashSet<string> { TransferListing.ShareNotesId }, ""));
        _dialogs.Answers.Enqueue((Func<IReadOnlyList<PluginChain>, NoteExportResult?>)(chains => new NoteExportResult("", chains.Take(1).ToList(), false)));
        _dialogs.Answers.Enqueue(notes);
        Wait(window.Transfer.ExportAsync(), window);
        Assert.Equal("导出混音笔记｜" + TransferListing.NoteFileName(""), _dialogs.Asked[^1]);
        Assert.Equal($"已导出 1 条混音笔记到 {notes}", window.Status);

        // 别人的评价文件：确认后单独保存，插件详情里能看到。
        var reviews = TempFile("other" + ReviewShare.Extension);
        File.WriteAllText(reviews, ReviewShare.Serialize(OtherReviews()));
        _dialogs.Answers.Enqueue(reviews);
        _dialogs.Answers.Enqueue(true);
        Wait(window.Transfer.ImportAsync(), window);
        Assert.Contains("其中 1 条对应你已安装的插件", _dialogs.Asked[^1]);
        Assert.Equal("已导入 阿杰 的 2 条评价", window.Status);
        Assert.Equal(5, Plugin("Pro-Q 4").User.Rating);

        // 同一位分享者再导入一次，说明里提醒会替换。
        _dialogs.Answers.Enqueue(reviews);
        _dialogs.Answers.Enqueue(false);
        Wait(window.Transfer.ImportAsync(), window);
        Assert.Contains("会替换之前导入的 2 条“阿杰”的评价", _dialogs.Asked[^1]);

        // 坏文件只在状态栏说明。
        var broken = TempFile("broken" + ReviewShare.Extension);
        File.WriteAllText(broken, "{");
        _dialogs.Answers.Enqueue(broken);
        Wait(window.Transfer.ImportAsync(), window);
        Assert.StartsWith("导入失败：", window.Status);
    }

    [AvaloniaFact]
    public void NothingToShareExplainsWhy()
    {
        var window = Open();
        _dialogs.Answers.Enqueue(new ExportChoice(true, new HashSet<string> { TransferListing.ShareReviewsId }, ""));
        Wait(window.Transfer.ExportAsync(), window);
        Assert.Equal(ReviewListing.NoReviews, window.Status);
        Assert.False(_dialogs.Parts[1][0].Enabled);
        Assert.False(_dialogs.Parts[1][1].Enabled);
    }

    [AvaloniaFact]
    public void SharedReviewCanBeAdoptedComparedAndRemoved()
    {
        Seed();
        _database.SaveReviewFile(OtherReviews(), DateTime.UtcNow);
        _database.SaveReviewFile(OtherReviews("阿杰", "second-author") with { Reviews = [OtherReviews().Reviews[0] with { Rating = 2, Note = "太亮" }] }, DateTime.UtcNow.AddDays(-3));
        var window = Open();
        var library = window.Library("Plugins")!;

        library.Select("Pro-C 2");
        Settle(window);
        Assert.False(library.SharedCardVisible);

        library.Select("Pro-Q 4");
        Settle(window);
        Assert.True(library.SharedCardVisible);
        Assert.Equal(2, library.SharedReviews.Count);
        Assert.True(library.CanAdopt);
        var picker = library.FindControl<ComboBox>("SharedAuthorPicker")!;
        Assert.True(picker.IsVisible);
        // 同名的分享者后面带导入日期。
        Assert.All(picker.ItemsSource!.Cast<string>(), label => Assert.StartsWith("阿杰（", label));

        Wait(library.CompareReviewsAsync(), window);
        Assert.Single(_dialogs.Compared);
        Assert.Equal(2, _dialogs.Compared[0].Others.Count);

        // 已经有自己的评价，采用前先问；不替换就不动。
        var shared = library.SelectedShared!;
        _dialogs.Answers.Enqueue(false);
        Wait(library.AdoptReviewAsync(), window);
        Assert.Equal(5, Plugin("Pro-Q 4").User.Rating);
        _dialogs.Answers.Enqueue(true);
        Wait(library.AdoptReviewAsync(), window);
        Assert.Contains(_dialogs.Asked, a => a.StartsWith("替换自己的评价？｜“Pro-Q 4”已经有你写的评分"));
        Assert.Equal(shared.Review.Rating, Plugin("Pro-Q 4").User.Rating);
        Assert.Equal(shared.Review.Note, Plugin("Pro-Q 4").User.Note);
        Assert.Equal($"采用自 {shared.Source.Name} 的评价", library.AdoptionLabel);
        Assert.False(library.CanAdopt);
        Assert.Equal(ReviewListing.Adopted(shared.Source, Plugin("Pro-Q 4")), window.Status);

        // 改一下自己的评价，角标变成“基于……修改”。
        library.ClickStar(1);
        Settle(window);
        library.Select("Pro-C 2");
        library.Select("Pro-Q 4");
        Settle(window);
        Assert.Equal($"基于 {shared.Source.Name} 的评价修改", library.AdoptionLabel);

        // 设置页移除一位分享者，详情里跟着少一条。
        window.Navigate(MainWindow.SettingsPage.Key);
        Settle(window);
        var settings = window.Settings!;
        settings.Confirm = (_, _, _) => Task.FromResult(true);
        Assert.Equal(2, settings.ReviewSources.Count);
        Assert.Contains("1 条对应已安装的插件", settings.ReviewSources[0].Detail);
        Wait(settings.RemoveReviewSource(settings.ReviewSources[0]), window);
        Assert.Single(settings.ReviewSources);
        Assert.StartsWith("已移除 阿杰 的评价", window.Status);
        window.Navigate("Plugins");
        Settle(window);
        library.Select("Pro-Q 4");
        Settle(window);
        Assert.Single(library.SharedReviews);
    }

    [AvaloniaFact]
    public void ExportDialogChecksPasswordAndParentParts()
    {
        Seed();
        var dialog = new ExportPartsDialog(TransferListing.SelfOptions(_database), TransferListing.ShareOptions(_database, _database.LoadEffects()), shareMode: false);
        dialog.Show();
        Assert.False(dialog.IsShare);
        Assert.True(dialog.PasswordVisible);
        Assert.Equal("导出", dialog.OkText);

        // 不勾心愿单，账号密码也不能勾，密码框收起。
        dialog.SelfList.Selection.Set("Wishes", false);
        Assert.False(dialog.SelfList.Box("Secrets").IsEnabled);
        Assert.False(dialog.PasswordVisible);
        dialog.SelfList.Selection.Set("Wishes", true);
        Assert.True(dialog.SelfList.Box("Secrets").IsChecked);

        dialog.SetPassword("123", "123");
        Assert.Null(dialog.Finish());
        Assert.StartsWith("备份密码至少 6 位", dialog.Error);
        dialog.SetPassword("123456", "1234567");
        Assert.Null(dialog.Finish());
        Assert.Equal("两次输入的备份密码不一样", dialog.Error);

        dialog.IsShare = true;
        Assert.Equal("下一步", dialog.OkText);
        Assert.False(dialog.PasswordVisible);
        Assert.Equal("", dialog.Error);
        dialog.ShareList.Selection.SelectNone();
        Assert.False(dialog.CanFinish);
        dialog.ShareList.Selection.Invert();
        var choice = dialog.Finish();
        Assert.NotNull(choice);
        Assert.True(choice.Share);
        Assert.Equal(new HashSet<string> { "reviews", "notes" }, choice.Parts);
    }

    [AvaloniaFact]
    public void PasswordDialogStaysOpenUntilUnlocked()
    {
        var dialog = new BackupPasswordDialog(attempt => attempt == "right-pass" ? null : "备份密码不对");
        dialog.Show();
        dialog.Password.Secret = "nope";
        dialog.Unlock();
        Assert.Equal("备份密码不对", dialog.Error);
        Assert.True(dialog.IsVisible);
        dialog.Password.Secret = "right-pass";
        dialog.Unlock();
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void NoteExportDialogSelectsByFolder()
    {
        Seed();
        _database.AddChain("Kick", null);
        var dialog = new NoteExportDialog(_database.LoadChains(), _database.LoadNoteFolders(), "小明");
        dialog.Show();
        Assert.Equal("已选 3 条笔记", dialog.Summary);
        var folder = dialog.Rows.First(r => r.IsFolder);
        Assert.Equal(true, folder.Selected);
        Assert.Equal("2/2", folder.Detail);

        dialog.ToggleNote(dialog.Rows.First(r => r.Name == "Verb"), false);
        Assert.Null(folder.Selected);
        Assert.Equal("1/2", folder.Detail);
        dialog.ToggleFolder(folder, false);
        Assert.Equal(false, folder.Selected);
        Assert.Equal("已选 1 条笔记", dialog.Summary);

        // 搜索只列笔记。
        dialog.Search = "Pro-C";
        Assert.Equal(["Lead Vox"], dialog.Shown.Select(r => r.Name));
        dialog.Search = "";
        dialog.ToggleFolder(folder, true);
        Assert.Equal(["Kick", "Lead Vox", "Verb"], dialog.SelectedChains.Select(c => c.Name).Order());
        dialog.Close();
    }

    [AvaloniaFact]
    public void ReviewExportDialogNeedsANickname()
    {
        Seed();
        var dialog = new ReviewExportDialog(_database.LoadEffects().Where(ReviewShare.HasReview), "");
        dialog.Show();
        Assert.Equal("请填写昵称", dialog.Summary);
        Assert.False(dialog.CanFinish);
        dialog.Author = "小明";
        Settle(dialog);
        Assert.Equal("已选 2 款", dialog.Summary);
        Assert.True(dialog.CanFinish);
        dialog.Search = "valhalla";
        Assert.Single(dialog.Shown);
        dialog.SelectVisible(false);
        Assert.Equal("已选 1 款", dialog.Summary);
        dialog.Close();
    }

    /// <summary>导出、导入窗口和插件详情里别人的评价；设置 WHATPLUGIN_SCREENSHOTS 时保存图片。</summary>
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
        _database.SaveReviewFile(OtherReviews(), DateTime.UtcNow);
        var window = Open();
        var library = window.Library("Plugins")!;
        library.Select("Pro-Q 4");
        Save(window, "transfer-shared-review");

        window.Navigate(MainWindow.SettingsPage.Key);
        Settle(window);
        Save(window, "transfer-settings-sources");

        var export = new ExportPartsDialog(TransferListing.SelfOptions(_database), TransferListing.ShareOptions(_database, _database.LoadEffects()), false);
        export.Show();
        Save(export, "transfer-export-self");
        export.IsShare = true;
        Save(export, "transfer-export-share");
        export.Close();

        var path = TempFile("backup.json");
        File.WriteAllText(path, UserDataBackup.Serialize(BackupRestore.Collect(_database, BackupParts.All, out _), "backup-pass"));
        var import = new ImportPartsDialog("导入备份", TransferListing.ImportBackupMessage, TransferListing.BackupOptions(UserDataBackup.Read(File.ReadAllText(path))), "导入", danger: true);
        import.Show();
        Save(import, "transfer-import");
        import.Close();

        var password = new BackupPasswordDialog(_ => "备份密码不对");
        password.Show();
        password.Password.Secret = "nope";
        password.Unlock();
        Save(password, "transfer-password");
        password.Close();

        var reviews = new ReviewExportDialog(_database.LoadEffects().Where(ReviewShare.HasReview), "小明");
        reviews.Show();
        Save(reviews, "transfer-review-export");
        reviews.Close();

        var notes = new NoteExportDialog(_database.LoadChains(), _database.LoadNoteFolders(), "小明");
        notes.Show();
        notes.ToggleNote(notes.Rows.First(r => r.Name == "Verb"), false);
        Save(notes, "transfer-note-export");
        notes.Close();

        var proQ = Plugin("Pro-Q 4");
        var compare = new ReviewCompareDialog(proQ, new ReviewLibrary(_database.LoadSharedReviews()).For(proQ));
        compare.Show();
        Save(compare, "transfer-compare");
        compare.Close();
    }
}
