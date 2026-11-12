using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public sealed class BlacklistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-b-" + Guid.NewGuid().ToString("N"));

    public BlacklistTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MergedPlugin Merged(string key, string name, string path) =>
        new(key, name, "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile(path, PluginFormat.Vst3, Arch.X64, "1", "Fx|EQ", ScanStatus.Loaded, null)]);

    [Fact]
    public void BlacklistSurvivesRescan()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo", "foo.vst3")]);
        var row = Assert.Single(db.LoadPlugins());

        db.SaveUserData(row.Id, row.User with { Hidden = true, BlacklistReason = "太吃 CPU" });
        db.SaveMerged([Merged("foo", "Foo", "foo.vst3")]);

        var after = Assert.Single(db.LoadPlugins());
        Assert.True(after.User.Hidden);
        Assert.Equal("太吃 CPU", after.User.BlacklistReason);
    }

    [Fact]
    public void LoadEffectsFiltersInstrumentsButKeepsTheirRecords()
    {
        var instrument = Merged("synth", "Synth", "synth.vst3") with
        {
            Classification = new Classification(PluginKind.Instrument, ["合成器"], ClassificationSource.Seed),
        };
        var unknown = Merged("mystery", "Mystery", "mystery.vst3") with
        {
            Classification = new Classification(PluginKind.Unknown, [], ClassificationSource.None),
        };
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo", "foo.vst3"), instrument, unknown]);

        Assert.Equal(["foo", "mystery"], db.LoadEffects().Select(p => p.Key).Order());
        Assert.Equal(3, db.LoadPlugins().Count);
    }

    [Fact]
    public void InstrumentNotesSurviveBackupEvenThoughHidden()
    {
        var instrument = Merged("synth", "Synth", "synth.vst3") with
        {
            Classification = new Classification(PluginKind.Instrument, ["合成器"], ClassificationSource.Seed),
        };
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([instrument]);
        var row = Assert.Single(db.LoadPlugins());
        db.SaveUserData(row.Id, row.User with { Note = "保留我" });

        Assert.Empty(db.LoadEffects());
        Assert.Equal("保留我", Assert.Single(UserDataBackup.Parse(UserDataBackup.Serialize(db.LoadPlugins()))).Note);
    }

    [Fact]
    public void ManualOverrideToEffectBringsMisjudgedPluginBack()
    {
        var instrument = Merged("x", "X", "x.vst3") with
        {
            Classification = new Classification(PluginKind.Instrument, [], ClassificationSource.SdkCategory),
        };
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([instrument]);
        var row = Assert.Single(db.LoadPlugins());
        db.SaveUserData(row.Id, row.User with { KindOverride = PluginKind.Effect });

        Assert.Single(db.LoadEffects());
    }

    [Fact]
    public void ReasonIsOptional()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo", "foo.vst3")]);
        var row = Assert.Single(db.LoadPlugins());

        db.SaveUserData(row.Id, row.User with { Hidden = true });

        var after = Assert.Single(db.LoadPlugins());
        Assert.True(after.User.Hidden);
        Assert.Equal("", after.User.BlacklistReason);
    }

    [Fact]
    public void BlacklistCountsAsUserData()
    {
        Assert.False(PluginUserData.Empty.HasData);
        Assert.True((PluginUserData.Empty with { Hidden = true }).HasData);
        Assert.True((PluginUserData.Empty with { BlacklistReason = "不好用" }).HasData);
    }

    [Fact]
    public void NormalizedKeepsBlacklistAndTrimsReason()
    {
        var data = (PluginUserData.Empty with { Hidden = true, BlacklistReason = "  不好用  " }).Normalized();
        Assert.True(data.Hidden);
        Assert.Equal("不好用", data.BlacklistReason);
    }

    [Fact]
    public void BackupKeepsBlacklistAndAcceptsOlderBackups()
    {
        string json;
        using (var source = new PluginDatabase(Path.Combine(_dir, "a.db")))
        {
            source.SaveMerged([Merged("foo", "Foo", "foo.vst3")]);
            var row = Assert.Single(source.LoadPlugins());
            source.SaveUserData(row.Id, row.User with { Hidden = true, BlacklistReason = "不好用" });
            json = UserDataBackup.Serialize(source.LoadPlugins());
        }

        var entry = Assert.Single(UserDataBackup.Parse(json));
        Assert.True(entry.Hidden);
        Assert.Equal("不好用", entry.BlacklistReason);

        const string older = """{ "version": 1, "exportedUtc": "2026-11-06T00:00:00Z", "plugins": [ { "key": "x", "name": "X", "vendor": "", "rating": 3, "note": "旧备份", "myTags": [] } ] }""";
        var old = Assert.Single(UserDataBackup.Parse(older)).ToUserData();
        Assert.False(old.Hidden);
        Assert.Equal("", old.BlacklistReason);
        Assert.Equal("旧备份", old.Note);
    }

    [Fact]
    public void ImportRestoresBlacklistOnExistingAndNotInstalledPlugins()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("foo", "Foo", "foo.vst3")]);

        db.ImportUserData(
        [
            new BackupEntry("foo", "Foo", "Acme", 0, "", [], null, null, null, true, "太吃 CPU"),
            new BackupEntry("gone", "Gone", "Acme", 0, "", [], null, null, null, true, "已卸载"),
        ]);

        var rows = db.LoadPlugins();
        var foo = rows.Single(r => r.Key == "foo");
        Assert.True(foo.User.Hidden);
        Assert.Equal("太吃 CPU", foo.User.BlacklistReason);
        var gone = rows.Single(r => r.Key == "gone");
        Assert.True(gone.User.Hidden);
        Assert.Equal("已卸载", gone.User.BlacklistReason);
    }
}
