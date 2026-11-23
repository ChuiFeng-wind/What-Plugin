using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>工程页、闲置报告的文字和筛选，以及路由图的排布（Windows 版和跨平台版共用）。</summary>
public sealed class ProjectListingTests
{
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PluginRow Plugin(long id, string name, string vendor = "Acme", bool hidden = false, string reason = "") =>
        new(id, Normalizer.Name(name), name, vendor, PluginKind.Effect, ["EQ"], ClassificationSource.Seed, false,
            [new MergedFile(name + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx", ScanStatus.Loaded, null)],
            PluginUserData.Empty with { Hidden = hidden, BlacklistReason = reason });

    private static ProjectEntry Entry(string name, params ProjectPluginLine[] lines) =>
        new("k-" + name, name, "/p/" + name + ".rpp", "/p", ProjectHost.Reaper, Day, 1, ProjectScanStatus.Ok, null, lines);

    [Fact]
    public void RowsDescribeMissingAndFailedProjects()
    {
        var ok = new ProjectListRow(Entry("Ok", new ProjectPluginLine("Pro-Q 4", 2, ProjectPluginState.Installed, null)));
        var missing = new ProjectListRow(Entry("Gap", new ProjectPluginLine("soothe2", 1, ProjectPluginState.Missing, null), new ProjectPluginLine("Comp", 1, ProjectPluginState.Missing, null)));
        var failed = new ProjectListRow(Entry("Bad") with { Status = ProjectScanStatus.Failed, Error = "坏了" });

        Assert.Equal(("齐全", false), (ok.MissingText, ok.Warn));
        Assert.Equal(("缺 2 款", true), (missing.MissingText, missing.Warn));
        Assert.Equal(("读取失败", true), (failed.MissingText, failed.Warn));
        Assert.Equal("REAPER", ok.HostText);
        Assert.True(ProjectListing.Matches("pro-q  ok", ok.SearchText));
        Assert.False(ProjectListing.Matches("pro-q gap", ok.SearchText));
        Assert.True(ProjectListing.Matches("  ", ok.SearchText));
    }

    [Fact]
    public void DetailSplitsThirdPartyStockAndIgnoredPlugins()
    {
        var remembered = Plugin(9, "soothe2", hidden: true, reason: "发闷") with { IsMissing = true };
        var entry = Entry("Song",
            new ProjectPluginLine("soothe2", 1, ProjectPluginState.Missing, null),
            new ProjectPluginLine("Pro-Q 4", 3, ProjectPluginState.Installed, null),
            new ProjectPluginLine("ReaEQ", 1, ProjectPluginState.Stock, null),
            new ProjectPluginLine("Old Thing", 1, ProjectPluginState.Ignored, null)) with { Versions = 3 };

        var detail = ProjectListing.Detail(entry, new PluginNameMatcher([remembered]));

        Assert.Equal("Song", detail.Title);
        Assert.EndsWith("，共 3 个版本，下面是最新版本", detail.Meta);
        Assert.Null(detail.Error);
        Assert.Equal(["soothe2", "Pro-Q 4"], detail.Lines.Select(l => l.Name));
        Assert.True(detail.Lines[0].ShowRemembered);
        Assert.Contains("发闷", detail.Lines[0].RememberedText);
        Assert.Equal("× 3", detail.Lines[1].InstancesText);
        Assert.Equal("宿主自带（不算缺失）：ReaEQ\n已设为不算缺失：Old Thing", detail.Notes);

        var empty = ProjectListing.Detail(Entry("Empty"), new PluginNameMatcher([]));
        Assert.Equal("这个工程没有用到第三方效果器。", empty.Notes);
    }

    [Fact]
    public void ScanSummaryAndEmptyTexts()
    {
        Assert.Equal("工程读取完成，找到 5 个工程文件，合并为 3 个工程，本次读取 4 个，未变化 1 个，1 个读取失败，移除 2 条已不存在的记录",
            ProjectListing.DescribeScan(new ProjectScanSummary(5, 4, 1, 1, 2, false, TimeSpan.Zero), 3));
        Assert.StartsWith("已停止", ProjectListing.DescribeScan(new ProjectScanSummary(1, 0, 0, 0, 0, true, TimeSpan.Zero), 0));

        Assert.Equal("点右上角“添加文件夹”，选择存放工程的位置", ProjectListing.EmptyText(true, 0, false, 0, false, false, false));
        Assert.Equal("这些文件夹里没有找到工程", ProjectListing.EmptyText(false, 0, false, 0, false, false, false));
        Assert.Equal("没有缺失的插件", ProjectListing.EmptyText(false, 2, true, 0, false, false, false));
        Assert.Equal("所有工程需要的插件都装了", ProjectListing.EmptyText(false, 2, false, 0, true, false, false));
        Assert.Equal("这个文件夹里没有符合条件的工程", ProjectListing.EmptyText(false, 2, false, 1, false, true, true));
        Assert.Equal("没有符合搜索的工程", ProjectListing.EmptyText(false, 2, false, 1, false, true, false));
        Assert.Equal("工程文件只会被读取，不会被修改。", ProjectListing.Status(0, 0, 0));
        Assert.Equal("3 个工程，1 个有缺失，缺失插件 2 种", ProjectListing.Status(3, 1, 2));
    }

    [Fact]
    public void IdleRowsFilterAndDescribe()
    {
        var never = new IdlePlugin(Plugin(1, "Unused") with { User = PluginUserData.Empty with { Rating = 3 } }, IdleReason.NeverUsed, null);
        var old = new IdlePlugin(Plugin(2, "Old Delay", ""), IdleReason.LongUnused, new PluginUsage(2, 3, Day, ["A", "B"]));
        var a = new IdleListRow(never);
        var b = new IdleListRow(old);

        Assert.Equal(("从未出现", 0, "★★★"), (a.LastUsedText, a.ProjectCount, a.RatingText));
        Assert.Equal(("未知", 2), (b.Vendor, b.ProjectCount));
        Assert.True(IdleListing.Matches(a, IdleFilter.NeverUsed, "unus"));
        Assert.False(IdleListing.Matches(b, IdleFilter.NeverUsed, ""));
        Assert.True(IdleListing.Matches(b, IdleFilter.LongUnused, "eq"));
        Assert.Equal("依据 4 个工程：1 款从未出现，1 款超过 24 个月没用", IdleListing.Describe(4, [never, old], 24));
        Assert.Equal("闲置报告依据工程扫描结果", IdleListing.Describe(0, [never], 12));
        Assert.Equal("把选中的 2 款移入垃圾桶…", IdleListing.ButtonText(2));
        Assert.Equal(["6 个月", "12 个月", "24 个月", "36 个月"], IdleListing.MonthOptions.Select(IdleListing.MonthLabel));
    }

    private static MergedPlugin Merged(string key, string name) =>
        new(key, name, "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile(name + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx|EQ", ScanStatus.Loaded, null)]);

    [Fact]
    public void MovingToTrashKeepsAnEarlierReasonWhenNoneIsGiven()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pv-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var db = new PluginDatabase(Path.Combine(dir, "t.db")))
            {
                db.SaveMerged([Merged("a", "A"), Merged("b", "B")]);
                var rows = db.LoadEffects();
                var a = rows.First(r => r.Name == "A");
                db.SaveUserData(a.Id, a.User with { BlacklistReason = "以前写的" });

                IdleListing.MoveToTrash(db, db.LoadEffects(), "  ");

                var after = db.LoadEffects();
                Assert.All(after, r => Assert.True(r.User.Hidden));
                Assert.Equal("以前写的", after.First(r => r.Name == "A").User.BlacklistReason);
                Assert.Equal("", after.First(r => r.Name == "B").User.BlacklistReason);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- 路由图 ----------

    private static PluginChain Note(long id, string name, ChannelRole role = ChannelRole.Track) => new(id, name, "", [], Role: role);

    [Fact]
    public void RouteMapColumnsGoTracksGroupsEffectsMaster()
    {
        var notes = new[]
        {
            Note(1, "Kick"), Note(2, "Snare"), Note(3, "Drums", ChannelRole.Group), Note(4, "All Drums", ChannelRole.Group),
            Note(5, "Vox"), Note(6, "Verb", ChannelRole.Effect), Note(7, "Pad"),
        };
        var routes = new[]
        {
            new NoteRoute(1, 3, RouteKind.Output), new NoteRoute(2, 3, RouteKind.Output), new NoteRoute(3, 4, RouteKind.Output),
            new NoteRoute(5, 6, RouteKind.Send), new NoteRoute(5, 6, RouteKind.Sidechain, "Pro-C 2"),
        };

        var layout = RouteMapLayout.Build(notes, routes);

        Assert.False(layout.IsEmpty);
        // 编组套编组往右排，效果轨一列，补出来的主输出最后一列；没接路由的 Pad 不画。
        Assert.Equal([0, 0, 1, 2, 0, 3, 4], new long[] { 1, 2, 3, 4, 5, 6, RouteMapLayout.ImplicitMaster }.Select(layout.ColumnOf));
        Assert.DoesNotContain(layout.Columns.SelectMany(c => c), n => n.Id == 7);
        Assert.Equal(["Verb"], layout.Node(5).Sends);
        Assert.Equal(["Verb（Pro-C 2）"], layout.Node(5).Sidechains);
        Assert.Equal("主输出", layout.Node(RouteMapLayout.ImplicitMaster).Name);
        // 没有输出的 All Drums、Vox、Verb 接到主输出；输出线排在最后画。
        Assert.Equal(3, layout.Edges.Count(e => e.To == RouteMapLayout.ImplicitMaster));
        Assert.Equal(RouteKind.Output, layout.Edges[^1].Kind);
        Assert.Equal("发送 → Verb\n侧链 → Verb（Pro-C 2）\n点击打开这条笔记", RouteMapLayout.ToolTip(layout.Node(5), []));
    }

    [Fact]
    public void RouteMapUsesTheOnlyMasterNoteAndCentersOnInputs()
    {
        var notes = new[] { Note(1, "A"), Note(2, "B"), Note(3, "Bus", ChannelRole.Group), Note(4, "Stereo Out", ChannelRole.Master) };
        var routes = new[] { new NoteRoute(1, 3, RouteKind.Output), new NoteRoute(2, 3, RouteKind.Output) };

        var layout = RouteMapLayout.Build(notes, routes);
        Assert.DoesNotContain(layout.Columns.SelectMany(c => c), n => n.Id == RouteMapLayout.ImplicitMaster);
        Assert.Contains(layout.Edges, e => e.From == 3 && e.To == 4);

        var tops = layout.Place(_ => 40, 16, 14);
        Assert.Equal(16, tops[1]);
        Assert.Equal(70, tops[2]);
        // 编组对齐到两条输入的中间。
        Assert.Equal((16 + 20 + 70 + 20) / 2.0 - 20, tops[3]);
    }

    [Fact]
    public void RouteMapIsEmptyWithoutRoutes()
    {
        var layout = RouteMapLayout.Build([Note(1, "A"), Note(2, "B")], []);
        Assert.True(layout.IsEmpty);
        Assert.Empty(layout.Edges);
    }
}
