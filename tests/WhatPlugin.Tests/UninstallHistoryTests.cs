using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>垃圾桶的卸载历史：记录、旧数据迁移、备份，以及插件记录删了以后的提醒。全部用临时数据库，不碰真实文件。</summary>
public sealed class UninstallHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-uh-" + Guid.NewGuid().ToString("N"));

    public UninstallHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string Db(string name = "t") => Path.Combine(_dir, name + ".db");

    private static MergedPlugin Merged(string key, string name, string path, string vendor = "Acme") =>
        new(key, name, vendor, InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile(path, PluginFormat.Vst2, Arch.X64, "1", "", ScanStatus.FileInfoOnly, null)]);

    private static readonly DateTime When = new(2026, 11, 12, 8, 30, 0, DateTimeKind.Utc);

    /// <summary>Bad 在垃圾桶里写了原因和评分，Good 不在垃圾桶里。</summary>
    private static void Trash(PluginDatabase db)
    {
        db.SaveMerged([Merged("bad", "Bad Comp", "bad.dll"), Merged("good", "Good EQ", "good.dll")]);
        var bad = db.LoadPlugins().Single(p => p.Key == "bad");
        db.SaveUserData(bad.Id, bad.User with { Hidden = true, BlacklistReason = "太吃 CPU", Rating = 2, Note = "底噪大" });
    }

    [Fact]
    public void RecordKeepsSnapshotAndSurvivesDeletingThePluginRecord()
    {
        using var db = new PluginDatabase(Db());
        Trash(db);
        var bad = db.LoadPlugins().Single(p => p.Key == "bad");
        db.AddUninstallRecords([UninstallHistory.From(bad, UninstallWay.Official, "  有更好的替代 ", When)]);

        // 卸载后：文件不在了，插件记录被手动删掉。
        db.SaveMerged([Merged("good", "Good EQ", "good.dll")]);
        Assert.True(db.DeleteUninstalledRecord(db.LoadPlugins().Single(p => p.Key == "bad").Id));
        Assert.DoesNotContain(db.LoadPlugins(), p => p.Key == "bad");

        var record = Assert.Single(db.LoadUninstallHistory());
        Assert.Equal(("bad", "Bad Comp", "Acme"), (record.PluginKey, record.Name, record.Vendor));
        Assert.Equal(UninstallWay.Official, record.Way);
        Assert.Equal("有更好的替代", record.Reason);
        Assert.Equal(("太吃 CPU", 2, "底噪大"), (record.TrashReason, record.Rating, record.Note));
        Assert.Equal(When, record.UninstalledUtc!.Value.ToUniversalTime());
        Assert.Equal("官方卸载程序", record.WayText);

        db.SetUninstallReason(record.Id, "换成了别的压缩");
        Assert.Equal("换成了别的压缩", db.LoadUninstallHistory().Single().EffectiveReason);
        db.DeleteUninstallRecord(record.Id);
        Assert.Empty(db.LoadUninstallHistory());
    }

    [Fact]
    public void EmptyReasonFallsBackToTrashReason()
    {
        var record = new UninstallRecord(1, "k", "X", "", UninstallWay.FoundMissing, "", When, "太吃 CPU");
        Assert.Equal("太吃 CPU", record.EffectiveReason);
        Assert.Contains("原因：太吃 CPU", UninstallHistory.Describe(record));
        Assert.Equal("1.2.4 之前卸载，日期未知", (record with { UninstalledUtc = null }).DateText);
    }

    [Fact]
    public void ReminderWorksFromHistoryAloneAfterRecordIsDeleted()
    {
        var history = new List<UninstallRecord>
        {
            new(1, "bad", "Bad Comp", "Acme", UninstallWay.RecycleFiles, "", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), "旧原因"),
            new(2, "bad", "Bad Comp", "Acme", UninstallWay.Official, "有更好的替代", When, "太吃 CPU", 2, "底噪大"),
        };
        var plugins = new List<PluginRow>();

        // 名称带不带厂家前缀都能找到，取最近一条。
        var found = UninstallHistory.Find("Acme Bad Comp", history, plugins);
        Assert.Equal(2, found!.Id);
        Assert.Equal(2, UninstallHistory.Find("bad comp", history, plugins)!.Id);
        Assert.Null(UninstallHistory.Find("Other", history, plugins));

        var text = UninstallHistory.Reminder(null, found);
        Assert.StartsWith(When.ToLocalTime().ToString("yyyy-MM-dd") + " 卸载", text);
        Assert.Contains("卸载原因：有更好的替代", text);
        Assert.Contains("移入原因：太吃 CPU", text);
        Assert.Contains("评分 2 星", text);
        Assert.Contains("感受：底噪大", text);

        Assert.Equal("以前卸载过（日期未知）；当时没有写原因",
            UninstallHistory.Reminder(null, new UninstallRecord(3, "x", "X", "", UninstallWay.Unknown, "", null)));
    }

    [Fact]
    public void InstalledAgainIsNotRemindedButMarkedBack()
    {
        using var db = new PluginDatabase(Db());
        Trash(db);
        var record = UninstallHistory.From(db.LoadPlugins().Single(p => p.Key == "bad"), UninstallWay.RecycleFiles, "", When);
        var plugins = db.LoadPlugins();
        Assert.True(UninstallHistory.IsBack(record, plugins));
        Assert.Null(UninstallHistory.Find("Bad Comp", [record], plugins));
        Assert.False(UninstallHistory.IsBack(record, plugins.Where(p => p.Key != "bad")));
    }

    [Fact]
    public void ScanFindsTrashPluginsThatDisappeared()
    {
        using var db = new PluginDatabase(Db());
        Trash(db);
        var before = db.LoadPlugins().Where(p => p.User.Hidden && !p.IsMissing).Select(p => p.Id).ToHashSet();
        // 在本软件外卸载了 Bad，Good 也不见了但它不在垃圾桶里。
        db.SaveMerged([]);
        var gone = Assert.Single(UninstallHistory.NewlyMissing(before, db.LoadPlugins()));
        Assert.Equal("bad", gone.Key);
    }

    [Fact]
    public void MigrationAddsAlreadyUninstalledTrashPluginsWithUnknownDate()
    {
        using (var db = new PluginDatabase(Db()))
        {
            Trash(db);
            db.SaveMerged([Merged("good", "Good EQ", "good.dll")]); // Bad 已卸载，留在垃圾桶里
            using var connection = new SqliteConnection($"Data Source={Db()}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE uninstall_history; PRAGMA user_version = 14;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new PluginDatabase(Db());
        var record = Assert.Single(reopened.LoadUninstallHistory());
        Assert.Equal(("bad", "Bad Comp", UninstallWay.Unknown), (record.PluginKey, record.Name, record.Way));
        Assert.Null(record.UninstalledUtc);
        Assert.Equal(("太吃 CPU", 2, "底噪大"), (record.TrashReason, record.Rating, record.Note));
        Assert.Equal("方式未知", record.WayText);
    }

    [Fact]
    public void BackupRoundTripsHistoryWithoutDuplicates()
    {
        string json;
        using (var source = new PluginDatabase(Db("a")))
        {
            Trash(source);
            var row = source.LoadPlugins().Single(p => p.Key == "bad");
            source.AddUninstallRecords(
            [
                UninstallHistory.From(row, UninstallWay.Official, "有更好的替代", When),
                new UninstallRecord(0, "old", "Old Verb", "", UninstallWay.Unknown, "", null, "听腻了"),
            ]);
            json = UserDataBackup.Serialize(BackupRestore.Collect(source, includeSecrets: false, out _), null);
        }
        Assert.Contains("\"Version\": 6", json);

        using var db = new PluginDatabase(Db("b"));
        var content = UserDataBackup.Read(json).Content;
        Assert.Equal(2, BackupRestore.Apply(db, content, null, _ => false).Uninstalls);
        Assert.Equal(0, BackupRestore.Apply(db, content, null, _ => false).Uninstalls); // 再导一次不翻倍

        var history = db.LoadUninstallHistory();
        Assert.Equal(2, history.Count);
        var bad = history.Single(h => h.PluginKey == "bad");
        Assert.Equal(("有更好的替代", UninstallWay.Official, "太吃 CPU"), (bad.Reason, bad.Way, bad.TrashReason));
        Assert.Equal(When, bad.UninstalledUtc!.Value.ToUniversalTime());
        Assert.Null(history.Single(h => h.PluginKey == "old").UninstalledUtc);

        // 旧版备份没有卸载历史，照样能读。
        Assert.Empty(UserDataBackup.Read("""{"Version":5,"ExportedUtc":"2026-01-01T00:00:00Z","Plugins":[]}""").Content.Uninstalls!);
    }
}
