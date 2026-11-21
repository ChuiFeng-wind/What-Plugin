using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>插件库页面共用的筛选、排序和编辑规则（Windows 版和跨平台版用同一份）。</summary>
public sealed class PluginLibraryTests
{
    private static long _nextId;

    private static PluginRow Row(string name, string vendor = "Acme", string[]? tags = null, int rating = 0, string note = "",
        bool hidden = false, bool missing = false, PluginFormat format = PluginFormat.Vst3, MixAttributes? mix = null,
        string[]? tagsOverride = null, string[]? timbresOverride = null) =>
        new(++_nextId, Normalizer.Name(name), name, vendor, PluginKind.Effect, tags ?? ["压缩"], ClassificationSource.Seed, missing,
            [new MergedFile(name + "." + format, format, Arch.X64, "1", "Fx", ScanStatus.Loaded, null)],
            new PluginUserData(rating, note, [], tagsOverride, null, null, hidden, "", mix, timbresOverride));

    [Fact]
    public void LibraryAndTrashShowSeparatePlugins()
    {
        var kept = Row("Pro-C 2");
        var trashed = Row("Old Comp", hidden: true);
        Assert.True(new LibraryFilter().Matches(kept));
        Assert.False(new LibraryFilter().Matches(trashed));
        Assert.True(new LibraryFilter(Blacklist: true).Matches(trashed));
        Assert.False(new LibraryFilter(Blacklist: true).Matches(kept));
    }

    [Fact]
    public void SearchNeedsEveryWordAndLooksAtNotesAndMixAttributes()
    {
        var row = Row("Pro-C 2", "FabFilter", note: "鼓组很好用", mix: new MixAttributes(Latency: LatencyLevel.Zero));
        Assert.True(new LibraryFilter(Search: "fabfilter 鼓组").Matches(row));
        Assert.True(new LibraryFilter(Search: "  PRO-C ").Matches(row));
        Assert.False(new LibraryFilter(Search: "fabfilter 混响").Matches(row));
        Assert.True(new LibraryFilter(Search: new MixAttributes(Latency: LatencyLevel.Zero).LatencyText).Matches(row));
    }

    [Fact]
    public void VendorFormatAndRatingFilters()
    {
        var unknownVendor = Row("Mystery", vendor: "", rating: 5, format: PluginFormat.Clap);
        var rated = Row("Pro-Q 4", "FabFilter", rating: 2, note: "切低频");
        Assert.True(new LibraryFilter(Vendor: "未知").Matches(unknownVendor));
        Assert.False(new LibraryFilter(Vendor: "FabFilter").Matches(unknownVendor));
        Assert.True(new LibraryFilter(Format: PluginFormat.Clap).Matches(unknownVendor));
        Assert.False(new LibraryFilter(Format: PluginFormat.Clap).Matches(rated));

        Assert.True(new LibraryFilter(Rating: RatingFilter.HighRated).Matches(unknownVendor));
        Assert.False(new LibraryFilter(Rating: RatingFilter.HighRated).Matches(rated));
        Assert.True(new LibraryFilter(Rating: RatingFilter.LowRated).Matches(rated));
        Assert.True(new LibraryFilter(Rating: RatingFilter.HasNote).Matches(rated));
        Assert.False(new LibraryFilter(Rating: RatingFilter.Unrated).Matches(rated));
        Assert.True(new LibraryFilter(Rating: RatingFilter.Unrated).Matches(Row("New")));
        Assert.Equal(Enum.GetValues<RatingFilter>().Length, LibraryFilter.RatingLabels.Count);
    }

    [Fact]
    public void AuIsOnlyOfferedOnTheMac()
    {
        Assert.Equal(AppPlatform.IsMac, LibraryFilter.FormatsOnThisSystem.Contains(PluginFormat.Au));
        Assert.Contains(PluginFormat.Vst3, LibraryFilter.FormatsOnThisSystem);
    }

    [Fact]
    public void ACategoryNarrowsTheListAndSortsByUsageThenRating()
    {
        var rows = new[]
        {
            Row("B Comp", rating: 5), Row("A Comp", rating: 5), Row("Busy Comp", rating: 1), Row("Reverb", tags: ["混响"]),
        };
        var category = LibraryCategories.Build(rows).First(c => c.Kind == LibraryCategoryKind.Function && c.Label == "压缩");
        var filter = new LibraryFilter(Category: category);
        var usage = new Dictionary<string, PluginUsage> { ["Busy Comp"] = new(3, 3, DateTime.UtcNow, ["工程"]) };

        var shown = filter.Order(rows.Where(filter.Matches), r => r, r => usage.GetValueOrDefault(r.Name)).Select(r => r.Name);
        Assert.Equal(["Busy Comp", "A Comp", "B Comp"], shown);
    }

    [Fact]
    public void TrashPutsUninstalledPluginsLastThenSortsByVendor()
    {
        var rows = new[]
        {
            Row("Gone", "Aaa", hidden: true, missing: true), Row("Zed", "Beta", hidden: true), Row("Alpha", "Beta", hidden: true),
            Row("First", "Aaa", hidden: true),
        };
        var filter = new LibraryFilter(Blacklist: true);
        Assert.Equal(["First", "Alpha", "Zed", "Gone"], filter.Order(rows, r => r, _ => null).Select(r => r.Name));
        // 插件库选“全部”时保持原来的顺序。
        Assert.Equal(rows, new LibraryFilter().Order(rows, r => r, _ => null));
    }

    [Fact]
    public void LabelsMatchTheWindowsVersion()
    {
        var row = Row("Plain", vendor: "", tags: []);
        Assert.Equal("未知", PluginLabels.Vendor(row));
        Assert.Equal("未分类", PluginLabels.Tags(row));
        Assert.Equal(["未分类"], PluginLabels.TagList(row));
        Assert.Equal("★★★", PluginLabels.Rating(3));
        Assert.Equal("", PluginLabels.Rating(0));
        Assert.Equal("工程里没用过", PluginLabels.UsageSummary(null));
        Assert.StartsWith("2 个工程，最近 ", PluginLabels.UsageSummary(new PluginUsage(2, 4, DateTime.UtcNow, ["a", "b"])));
        Assert.Equal("AU", PluginLabels.Format(PluginFormat.Au));
    }

    [Fact]
    public void TagEditsThatEndUpLikeTheAutoTagsAreNotOverrides()
    {
        var row = Row("Pro-C 2", tags: ["压缩"]);
        var added = PluginEdits.ToggleTag(row, null, "限制器", on: true);
        Assert.Equal(["压缩", "限制器"], added);
        Assert.Null(PluginEdits.ToggleTag(row, added, "限制器", on: false));
        Assert.Equal(["一", "二", "三", "四", "五"], PluginEdits.SplitTags(" 一, 二，三、四;五；"));
    }

    [Fact]
    public void AddingATimbreWordTicksTheTimbreInsteadOfAddingAFunctionTag()
    {
        var row = Row("Pro-C 2", tags: ["压缩"]);
        var (tags, timbres) = PluginEdits.AddTags(row, null, null, "人声、母带");
        Assert.Equal(["压缩", "母带"], tags);
        Assert.Equal(["人声"], timbres);

        var timbre = PluginEdits.ToggleTimbre(row, null, null, "鼓", on: true);
        Assert.Equal(["鼓"], timbre);
        Assert.Null(PluginEdits.ToggleTimbre(row, null, timbre, "鼓", on: false));
    }

    [Fact]
    public void KnownTagsListTheCatalogFirstThenUserTags()
    {
        var rows = new[] { Row("A", tags: ["我的分类"]), Row("B", tags: ["EQ"]) };
        var known = PluginEdits.KnownTags(rows, ["临时"]);
        Assert.Equal(Classifier.TagCatalog, known.Take(Classifier.TagCatalog.Count));
        Assert.Equal(["我的分类", "临时"], known.Skip(Classifier.TagCatalog.Count));
    }
}
