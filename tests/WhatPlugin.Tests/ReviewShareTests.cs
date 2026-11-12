using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public sealed class ReviewShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-r-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, PluginUsage> NoUsage = [];
    private static long _nextId;

    public ReviewShareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static PluginRow Row(
        string name,
        int rating = 4,
        string note = "顺滑",
        string[]? tags = null,
        string vendor = "Acme",
        string? key = null,
        PluginKind kind = PluginKind.Effect) =>
        new(++_nextId, key ?? Normalizer.Name(name), name, vendor, kind, ["压缩"], ClassificationSource.Seed, false, [],
            new PluginUserData(rating, note, tags ?? ["人声"], null, null, null, false, "", null));

    private static SharedReview Review(string name, int rating = 5, string note = "好用", string[]? tags = null, string vendor = "Acme") =>
        new(ReviewShare.KeyOf(name, vendor), name, vendor, rating, note, tags ?? ["鼓"]);

    private static ReviewFile File1(string id, string author, params SharedReview[] reviews) => new(id, author, Now, reviews);

    private static MergedPlugin Merged(string key, string name) =>
        new(key, name, "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile(name + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx|EQ", ScanStatus.Loaded, null)]);

    [Fact]
    public void RoundTripKeepsDefaultContentAndLeavesOptionalOut()
    {
        var file = ReviewShare.Build("id1", "小明", [Row("Pro-Q 4", note: "第一行\n第二行")], NoUsage, new ReviewExportOptions(), Now);

        var parsed = ReviewShare.Parse(ReviewShare.Serialize(file));

        Assert.Equal("id1", parsed.AuthorId);
        Assert.Equal("小明", parsed.Author);
        var review = Assert.Single(parsed.Reviews);
        Assert.Equal("Pro-Q 4", review.Name);
        Assert.Equal(4, review.Rating);
        Assert.Equal("第一行\n第二行", review.Note);
        Assert.Equal(["人声"], review.Tags);
        Assert.Null(review.ProjectCount);
        Assert.Null(review.Categories);
        Assert.Null(review.Mix);
    }

    [Fact]
    public void OptionalContentOnlyWhenChecked()
    {
        var row = Row("C4") with { };
        row = row with { User = row.User with { Mix = new MixAttributes(LatencyLevel.Zero) } };
        var usage = new Dictionary<string, PluginUsage> { [row.Key] = new(7, 9, Now, ["秘密工程"]) };

        var file = ReviewShare.Build("id", "A", [row], usage, new ReviewExportOptions(true, true, true), Now);
        var review = Assert.Single(ReviewShare.Parse(ReviewShare.Serialize(file)).Reviews);

        Assert.Equal(7, review.ProjectCount);
        Assert.Equal(["压缩"], review.Categories);
        Assert.Equal(LatencyLevel.Zero, review.Mix?.Latency);
    }

    [Fact]
    public void ExportNeverContainsPrivateContent()
    {
        var row = Row("C4") with
        {
            Files = [new MergedFile(@"D:\私密路径\C4.vst3", PluginFormat.Vst3, Arch.X64, "1", "", ScanStatus.Loaded, null)],
        };
        row = row with { User = row.User with { BlacklistReason = "私密原因", VendorOverride = "私密厂家覆盖" } };
        var usage = new Dictionary<string, PluginUsage> { [row.Key] = new(3, 3, Now, ["私密工程"]) };

        var json = ReviewShare.Serialize(ReviewShare.Build("id", "A", [row], usage, new ReviewExportOptions(true, true, true), Now));

        Assert.DoesNotContain("私密路径", json);
        Assert.DoesNotContain("私密原因", json);
        Assert.DoesNotContain("私密工程", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("price", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstrumentsAndUnreviewedPluginsAreNotExported()
    {
        var file = ReviewShare.Build("id", "A",
            [Row("Synth", kind: PluginKind.Instrument), Row("Empty", rating: 0, note: " ", tags: []), Row("Good")],
            NoUsage, new ReviewExportOptions(), Now);

        Assert.Equal("Good", Assert.Single(file.Reviews).Name);
    }

    [Theory]
    [InlineData("不是 JSON")]
    [InlineData("""{"format":"other","version":1,"authorId":"a","author":"b","reviews":[]}""")]
    [InlineData("""{"format":"whatplugin-review","version":99,"authorId":"a","author":"b","reviews":[]}""")]
    [InlineData("""{"format":"whatplugin-review","version":1,"author":"b","reviews":[]}""")]
    [InlineData("""{"format":"whatplugin-review","version":1,"authorId":"a","author":"  ","reviews":[]}""")]
    [InlineData("""{"format":"whatplugin-review","version":1,"authorId":"a","author":"b"}""")]
    [InlineData("""{"format":"whatplugin-review","version":1,"authorId":"a","author":"b","reviews":[{"name":"x","rating":"高"}]}""")]
    public void InvalidFilesAreRejected(string json) =>
        Assert.Throws<FormatException>(() => ReviewShare.Parse(json));

    [Fact]
    public void HostileContentIsLimited()
    {
        var longNote = new string('长', 50_000);
        var tags = string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"t{i}\""));
        var json = $$$"""
            {"format":"whatplugin-review","version":1,"authorId":"a","author":"坏\u202E人\u0007",
             "reviews":[
               {"name":"Foo","rating":99,"note":"{{{longNote}}}","tags":[{{{tags}}}],"projectCount":-5,"mix":{"latency":42}},
               {"name":"Bar","rating":-3,"note":"<b>x</b>\u0000"},
               {"name":"   ","rating":5},
               {"name":"Nothing"},
               null
             ]}
            """;

        var file = ReviewShare.Parse(json);

        Assert.Equal("坏人", file.Author);
        Assert.Equal(2, file.Reviews.Count);
        var foo = file.Reviews.Single(r => r.Name == "Foo");
        Assert.Equal(5, foo.Rating);
        Assert.Equal(20_000, foo.Note.Length);
        Assert.Equal(50, foo.Tags.Count);
        Assert.Null(foo.ProjectCount);
        Assert.Null(foo.Mix);
        var bar = file.Reviews.Single(r => r.Name == "Bar");
        Assert.Equal(0, bar.Rating);
        // 文字原样保留，界面只当纯文本显示。
        Assert.Equal("<b>x</b>", bar.Note);
    }

    [Fact]
    public void OversizedFileIsRejectedBeforeReading()
    {
        var path = Path.Combine(_dir, "big" + ReviewShare.Extension);
        using (var stream = File.Create(path))
            stream.SetLength(ReviewShare.MaxFileBytes + 1);

        Assert.Throws<FormatException>(() => ReviewShare.Read(path));
    }

    [Fact]
    public void ImportDoesNotTouchOwnReviewAndReimportReplaces()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo")]);
        var row = Assert.Single(db.LoadPlugins());
        db.SaveUserData(row.Id, row.User with { Rating = 2, Note = "我的感受" });

        db.SaveReviewFile(File1("x", "小明", Review("Foo"), Review("Bar")), Now);
        db.SaveReviewFile(File1("y", "小红", Review("Foo", rating: 1)), Now);
        db.SaveReviewFile(File1("x", "小明明", Review("Foo", rating: 3)), Now);

        var mine = Assert.Single(db.LoadPlugins()).User;
        Assert.Equal(2, mine.Rating);
        Assert.Equal("我的感受", mine.Note);

        var sources = db.LoadReviewSources();
        Assert.Equal(new HashSet<string> { "小明明", "小红" }, sources.Select(s => s.Name).ToHashSet());
        Assert.Equal(1, sources.Single(s => s.AuthorId == "x").Count);
        var reviews = db.LoadSharedReviews();
        Assert.Equal(2, reviews.Count);
        Assert.Equal(3, reviews.Single(r => r.Source.AuthorId == "x").Review.Rating);
    }

    [Fact]
    public void RemovingSourceDeletesItsReviewsOnly()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        var source = db.SaveReviewFile(File1("x", "小明", Review("Foo")), Now);
        db.SaveReviewFile(File1("y", "小红", Review("Foo")), Now);

        db.RemoveReviewSource(source.Id);

        Assert.Equal("小红", Assert.Single(db.LoadReviewSources()).Name);
        Assert.Equal("小红", Assert.Single(db.LoadSharedReviews()).Source.Name);
    }

    [Fact]
    public void AllFieldsSurviveTheDatabase()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        var review = Review("Foo") with { ProjectCount = 4, Categories = ["压缩"], Mix = new MixAttributes(Cpu: CpuLevel.Heavy) };

        db.SaveReviewFile(File1("x", "小明", review), Now);

        var loaded = Assert.Single(db.LoadSharedReviews()).Review;
        Assert.Equal(4, loaded.ProjectCount);
        Assert.Equal(["压缩"], loaded.Categories);
        Assert.Equal(CpuLevel.Heavy, loaded.Mix?.Cpu);
    }

    [Fact]
    public void AdoptionCopiesContentAndTracksEdits()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo")]);
        var row = Assert.Single(db.LoadPlugins());
        db.SaveUserData(row.Id, row.User with { BlacklistReason = "保留", Mix = new MixAttributes(LatencyLevel.Low) });
        row = Assert.Single(db.LoadPlugins());

        var (data, adoption) = db.AdoptReview(row, Review("Foo", 5, "好用", ["鼓"]), "小明");

        var saved = Assert.Single(db.LoadPlugins()).User;
        Assert.Equal(5, saved.Rating);
        Assert.Equal("好用", saved.Note);
        Assert.Equal(["鼓"], saved.MyTags);
        Assert.Equal("保留", saved.BlacklistReason);
        Assert.Equal(LatencyLevel.Low, saved.Mix?.Latency);
        Assert.Equal("采用自 小明 的评价", adoption.Describe(data));
        Assert.Equal("基于 小明 的评价修改", adoption.Describe(data with { Note = "好用，但吃 CPU" }));
        Assert.Equal("基于 小明 的评价修改", adoption.Describe(data with { Rating = 3 }));
        Assert.Equal("基于 小明 的评价修改", adoption.Describe(data with { MyTags = ["鼓", "人声"] }));
        Assert.Equal("", adoption.Describe(data with { Rating = 0, Note = "", MyTags = [] }));

        var kept = db.LoadReviewAdoptions()[row.Key];
        Assert.Equal("小明", kept.AuthorName);
        Assert.Equal("采用自 小明 的评价", kept.Describe(saved));
    }

    [Fact]
    public void AdoptionSurvivesRemovingTheSource()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo")]);
        var source = db.SaveReviewFile(File1("x", "小明", Review("Foo")), Now);
        db.AdoptReview(Assert.Single(db.LoadPlugins()), Review("Foo"), "小明");

        db.RemoveReviewSource(source.Id);

        Assert.Equal(5, Assert.Single(db.LoadPlugins()).User.Rating);
        Assert.True(db.LoadReviewAdoptions().ContainsKey("foo"));
    }

    [Fact]
    public void MigratesFromPreviousSchema()
    {
        var path = Path.Combine(_dir, "t.db");
        using (var db = new PluginDatabase(path))
            db.SaveMerged([Merged("foo", "Foo")]);
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE review_sources; DROP TABLE shared_reviews; DROP TABLE review_adoptions; PRAGMA user_version = 8;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new PluginDatabase(path);
        migrated.SaveReviewFile(File1("x", "小明", Review("Foo")), Now);

        Assert.Single(migrated.LoadSharedReviews());
        Assert.Single(migrated.LoadPlugins());
    }

    private static AttributedReview From(long id, string name, SharedReview review) =>
        new(new ReviewSource(id, "id" + id, name, Now, 1), review);

    [Fact]
    public void LibraryMatchesByNormalizedNameAcrossVariants()
    {
        var library = new ReviewLibrary([From(1, "小明", Review("C4 Stereo", vendor: "Waves Audio"))]);

        Assert.Single(library.For(Row("C4", vendor: "Waves")));
        Assert.Single(library.For(Row("c4 (VST3)", vendor: "Waves")));
        Assert.Empty(library.For(Row("C6", vendor: "Waves")));
    }

    [Fact]
    public void LibraryKeepsSameNamedPluginsApart()
    {
        var library = new ReviewLibrary([From(1, "小明", Review("Comp", vendor: "Alpha"))]);

        var alpha = Row("Comp", vendor: "Alpha", key: "comp|alpha");
        var beta = Row("Comp", vendor: "Beta", key: "comp|beta");

        Assert.Single(library.For(alpha));
        Assert.Empty(library.For(beta));
    }

    [Fact]
    public void LibraryPicksOneReviewPerPersonAndSortsByName()
    {
        var library = new ReviewLibrary([
            From(2, "乙", Review("Comp", vendor: "Other")),
            From(1, "甲", Review("Comp", rating: 1, vendor: "Other")),
            From(1, "甲", Review("Comp", rating: 2, vendor: "Acme")),
        ]);

        var reviews = library.For(Row("Comp"));

        Assert.Equal(2, reviews.Count);
        Assert.Equal(2, reviews.Single(r => r.Source.Id == 1).Review.Rating);
        Assert.Equal(new HashSet<string> { "甲", "乙" }, reviews.Select(r => r.Source.Name).ToHashSet());
    }

    [Fact]
    public void UnmatchedReviewAppearsOnceInstalled()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        var source = db.SaveReviewFile(File1("x", "小明", Review("Foo")), Now);
        Assert.Equal(0, new ReviewLibrary(db.LoadSharedReviews()).CountMatched(source, db.LoadPlugins()));

        db.SaveMerged([Merged("foo", "Foo")]);

        Assert.Equal(1, new ReviewLibrary(db.LoadSharedReviews()).CountMatched(source, db.LoadPlugins()));
    }
}
