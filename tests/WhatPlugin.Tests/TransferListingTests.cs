using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>导出、导入的勾选规则、文件名、说明文字，以及别人评价的显示文字。两个界面共用这些。</summary>
public sealed class TransferListingTests
{
    private static readonly PartOption[] Options =
    [
        new("Reviews", "插件评价"),
        new("Wishes", "心愿单和已购账号"),
        new("Secrets", "账号、密码和序列号", Parent: "Wishes"),
        new("Usage", "使用记录", Enabled: false),
        new("Settings", "设置", Checked: false),
    ];

    [Fact]
    public void ChildPartFollowsItsParentAndRemembersItsOwnState()
    {
        var selection = new PartSelection(Options);
        var changes = 0;
        selection.Changed += () => changes++;
        Assert.Equal(new HashSet<string> { "Reviews", "Wishes", "Secrets" }, selection.CheckedIds);
        Assert.False(selection.IsEnabled("Usage"));

        selection.Set("Wishes", false);
        Assert.False(selection.IsEnabled("Secrets"));
        Assert.False(selection.IsChecked("Secrets"));
        // 父项没勾时子项点不动。
        selection.Set("Secrets", false);
        selection.Set("Wishes", true);
        Assert.True(selection.IsChecked("Secrets"));
        Assert.Equal(2, changes);

        // 反选按子项自己想要的状态算：父项被取消时子项仍记着“要”。
        selection.Set("Wishes", false);
        selection.Invert();
        Assert.Equal(new HashSet<string> { "Wishes", "Settings" }, selection.CheckedIds);
        selection.SelectNone();
        Assert.False(selection.Any);
        selection.SelectAll();
        Assert.Equal(new HashSet<string> { "Reviews", "Wishes", "Secrets", "Settings" }, selection.CheckedIds);
    }

    [Fact]
    public void BackupPasswordNeedsLengthAndConfirmation()
    {
        Assert.StartsWith("备份密码至少 6 位", TransferListing.CheckPassword("12345", "12345"));
        Assert.Equal("两次输入的备份密码不一样", TransferListing.CheckPassword("123456", "123465"));
        Assert.Null(TransferListing.CheckPassword("123456", "123456"));
        Assert.True(TransferListing.NeedsPassword(false, new HashSet<string> { "Secrets" }));
        Assert.False(TransferListing.NeedsPassword(true, new HashSet<string> { "Secrets" }));
        Assert.False(TransferListing.NeedsPassword(false, new HashSet<string> { "Wishes" }));
    }

    [Fact]
    public void FileNamesAreSafeAndSayWhatIsInside()
    {
        var day = new DateTime(2026, 11, 24);
        Assert.Equal("WhatPlugin-备份-20261124.json", TransferListing.BackupFileName(BackupParts.All & ~BackupParts.Secrets, day));
        Assert.Equal("WhatPlugin-部分备份-20261124.json", TransferListing.BackupFileName(BackupParts.Reviews, day));
        Assert.Equal("a_b_c-插件评价" + ReviewShare.Extension, TransferListing.ReviewFileName("a/b:c"));
        Assert.Equal("混音笔记" + NoteShare.Extension, TransferListing.NoteFileName(""));
        Assert.Equal("小明-评价和混音笔记" + ShareBundles.Extension, TransferListing.ShareFileName("小明"));
        Assert.Equal(BackupParts.Reviews | BackupParts.Wishes, TransferListing.ToParts(["Reviews", "Wishes"]));
        Assert.Contains(ShareBundles.Extension, TransferListing.ImportExtensions);
    }

    [Fact]
    public void ExportedTextNamesTheOtherAccountForLockedSecrets()
    {
        var content = new BackupContent([], [], [], [], [], null, null, new BackupSecrets([new WishSecretEntry(0, "a", "b", null)], []));
        var text = TransferListing.Exported(content, BackupParts.Wishes | BackupParts.Secrets, locked: 2);
        Assert.StartsWith("已导出 0 条心愿和已购，账号密码 1 条、厂家账号 0 个已用备份密码加密", text);
        Assert.Contains($"2 条密码是{WishListing.ForeignKeyOwner}加密的", text);
        Assert.EndsWith("，没有包含账号密码", TransferListing.Exported(content with { Secrets = null }, BackupParts.Notes, 0));
    }

    private static PluginRow Row(string name, string vendor, int rating = 0) =>
        new(1, Normalizer.Name(name), name, vendor, PluginKind.Effect, [], default, false, [], PluginUserData.Empty with { Rating = rating });

    private static ReviewFile File(string authorId, params SharedReview[] reviews) => new(authorId, "阿杰", DateTime.UtcNow, reviews);

    private static SharedReview Review(string name, string vendor, int rating = 4) =>
        new(ReviewShare.KeyOf(name, vendor), name, vendor, rating, "好用", ["人声"]);

    [Fact]
    public void ShareImportChecksOwnFilesAndDescribesMatches()
    {
        var source = new ReviewSource(1, "other", "阿杰", DateTime.UtcNow, 3);
        var check = new ShareImport("me", [source], [Row("Pro-Q 4", "FabFilter")]);
        Assert.Equal("这是你自己导出的评价，不需要导入", check.CheckReviews(File("me", Review("Pro-Q 4", "FabFilter"))));
        Assert.Equal("这份文件里没有可用的评价", check.CheckReviews(File("someone")));
        // 本机还没导出过评价时，任何文件都不算自己的。
        Assert.Null(new ShareImport(null, [], []).CheckReviews(File("", Review("A", "B"))));

        var text = check.DescribeReviews(File("other", Review("Pro-Q 4", "FabFilter"), Review("X", "Y")));
        Assert.Contains("共 2 条，其中 1 条对应你已安装的插件", text);
        Assert.Contains("会替换之前导入的 3 条“阿杰”的评价", text);
        Assert.Contains("已有另一位也叫“阿杰”的分享者", check.DescribeReviews(File("third", Review("X", "Y"))));

        var bundle = new ShareBundle(File("me", Review("Pro-Q 4", "FabFilter")), null);
        var options = check.Options(bundle);
        Assert.All(options, o => Assert.False(o.Enabled));
        Assert.Equal("这份分享文件里没有能导入的内容：这是你自己导出的评价，不需要导入；这份文件里没有", ShareImport.NothingText(options));
        Assert.Equal("来自“阿杰”的分享。勾选要导入的内容。别人的评价单独存放，笔记放进新的文件夹，都不会改动你自己的内容。", ShareImport.BundleMessage(bundle));
    }

    [Fact]
    public void SharedReviewTexts()
    {
        var older = new ReviewSource(1, "a", "阿杰", new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc), 1);
        var newer = new ReviewSource(2, "b", "阿杰", new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc), 1);
        var alone = new ReviewSource(3, "c", "小红", DateTime.UtcNow, 1);
        Assert.Equal("阿杰（2026-01-02 导入）", ReviewListing.AuthorLabel(older, [older, newer, alone]));
        Assert.Equal("阿杰（2026-03-04）", ReviewListing.ShortLabel(newer, [older, newer]));
        Assert.Equal("小红", ReviewListing.AuthorLabel(alone, [older, alone]));

        Assert.Equal("★★★☆☆", ReviewListing.Stars(3));
        Assert.Equal("没有打分", ReviewListing.Stars(0));
        var review = Review("Pro-Q 4", "FabFilter") with
        {
            ProjectCount = 12,
            Categories = ["EQ"],
            Mix = new MixAttributes(LatencyLevel.Zero, CpuLevel.Light, Tri.Yes, Tri.Unknown, Tri.No),
        };
        Assert.Equal("用在 12 个工程 · 分类：EQ · 无延迟 · CPU 轻 · 线性相位：有 · 过采样：没有", ReviewListing.Extras(review));
        Assert.Equal("标签：人声", ReviewListing.TagsLine(review));

        var own = PluginUserData.Empty with { Rating = 4, Note = "好用 ", MyTags = ["人声"] };
        Assert.True(ReviewListing.SameAsOwn(own, review));
        Assert.False(ReviewListing.SameAsOwn(own with { Rating = 5 }, review));
        Assert.Equal("请填写昵称", ReviewListing.ExportSummary(" ", 3));
        Assert.Equal("已选 3 款", ReviewListing.ExportSummary("小明", 3));
        Assert.Equal("导出", ReviewListing.ExportButton(0));
        Assert.Equal("已选 2 条笔记", ReviewListing.NoteSummary(2));
    }
}
