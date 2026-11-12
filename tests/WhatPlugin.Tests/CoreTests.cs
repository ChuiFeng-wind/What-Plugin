using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public class NormalizerTests
{
    [Theory]
    [InlineData("Pro-Q 3", "proq3")]
    [InlineData("Pro-Q 3 x64", "proq3")]
    [InlineData("ProQ3_x64", "proq3")]
    [InlineData("Foo (VST3) (x64)", "foo")]
    [InlineData("Foo VST3 x64", "foo")]
    [InlineData("古筝", "古筝")]
    [InlineData("WhisperOfLoong(64)", "whisperofloong")]
    [InlineData("REmatrix Player-64", "rematrixplayer")]
    [InlineData("WaveShell1-VST3 14.0_x64", "waveshell")]
    [InlineData("WaveShell1-AAX 14.5", "waveshell")]
    [InlineData("Plugin 64", "plugin64")]
    public void NameDropsFormatTagsAndPunctuation(string input, string expected) =>
        Assert.Equal(expected, Normalizer.Name(input));

    [Theory]
    [InlineData("iZotope, Inc.", "iZotope")]
    [InlineData("Waves Audio Ltd.", "Waves")]
    [InlineData("Dreamtonics Co., Ltd.", "Dreamtonics")]
    [InlineData("Celemony Software GmbH", "Celemony")]
    [InlineData("IK Multimedia Production", "IK Multimedia")]
    [InlineData("FabFilter", "FabFilter")]
    public void CanonicalVendorUnifiesSpellings(string input, string expected) =>
        Assert.Equal(expected, Normalizer.CanonicalVendor(input));

    [Fact]
    public void NameKeepsTextWhenItIsOnlyATag() => Assert.Equal("vst", Normalizer.Name("VST"));

    [Theory]
    [InlineData("FabFilter", "fabfilter")]
    [InlineData("oeksound Oy", "oeksoundoy")]
    [InlineData("Acme, Inc.", "acme")]
    [InlineData("Acme GmbH", "acme")]
    public void VendorDropsCompanySuffix(string input, string expected) =>
        Assert.Equal(expected, Normalizer.Vendor(input));
}

public class ClassifierTests
{
    [Fact]
    public void SdkCategoryBecomesTagsAndKind()
    {
        var result = Classifier.Classify("Mystery", "Acme", "Fx|Dynamics|EQ", false);
        Assert.Equal(PluginKind.Effect, result.Kind);
        Assert.Equal(["动态", "EQ"], result.Tags);
        Assert.Equal(ClassificationSource.SdkCategory, result.Source);
    }

    [Fact]
    public void KeywordMatchesWholeWordsOnly()
    {
        // "sample" 里含 amp，不能被归为吉他。
        Assert.DoesNotContain("吉他", Classifier.Classify("Sample Thing", "", "", null).Tags);
        Assert.Contains("吉他", Classifier.Classify("Guitar Amp", "", "", null).Tags);
    }

    [Fact]
    public void HyphenatedKeywordStillMatches() =>
        Assert.Contains("去齿音", Classifier.Classify("Acme De-Esser", "", "", null).Tags);

    [Fact]
    public void StereoInNameIsNotAFunction() =>
        Assert.DoesNotContain("声像/空间", Classifier.Classify("Acme Thing Stereo", "", "", null).Tags);

    [Theory]
    [InlineData("FabFilter Pro-DS", "FabFilter", "去齿音")]
    [InlineData("Ample China Zheng", "Ample Sound", "民乐")]
    [InlineData("Ozone 9 Maximizer", "iZotope", "限制器")]
    [InlineData("Ozone 11 Maximizer", "iZotope", "限制器")]
    [InlineData("RX 11 De-ess", "iZotope", "去齿音")]
    public void SeedRefinesCoarseCategories(string name, string vendor, string tag)
    {
        var result = Classifier.Classify(name, vendor, "Fx|Dynamics", false);
        Assert.Equal(ClassificationSource.Seed, result.Source);
        Assert.Contains(tag, result.Tags);
    }

    [Fact]
    public void SeedRespectsVendorConstraint()
    {
        Assert.Equal(ClassificationSource.Seed, Classifier.Classify("Vinyl", "iZotope", "", null).Source);
        Assert.NotEqual(ClassificationSource.Seed, Classifier.Classify("Vinyl", "Somebody Else", "", null).Source);
    }

    [Fact]
    public void SeedEntriesAreWellFormed()
    {
        Assert.NotEmpty(Classifier.SeedEntries);
        Assert.All(Classifier.SeedEntries, e =>
        {
            Assert.Contains(e.Kind, new[] { "effect", "instrument" });
            Assert.NotEmpty(e.Tags);
            Assert.All(e.Tags, t => Assert.True(Classifier.TagCatalog.Contains(t) || Classifier.InstrumentTags.Contains(t) || Classifier.TimbresIn([t]).Count > 0, t));
        });
    }

    [Fact]
    public void CamelCaseNamesAreSplitForKeywords() =>
        Assert.Contains("混响", Classifier.Classify("BigReverb", "", "", null).Tags);

    [Fact]
    public void OnlyPluginInfoCanMarkInstrumentNotTheName()
    {
        // 没有 SDK 信息时，名字不足以判定为乐器，否则 Drum Bus 这类效果器会被隐藏。
        Assert.Equal(PluginKind.Unknown, Classifier.Classify("Nice Piano", "", "", null).Kind);
        Assert.Equal(PluginKind.Instrument, Classifier.Classify("Nice Piano", "", "Instrument", null).Kind);
        Assert.Equal(PluginKind.Effect, Classifier.Classify("Nice Piano", "", "Fx", null).Kind);
    }

    [Fact]
    public void ReportedKindWinsOverKeyword() =>
        Assert.Equal(PluginKind.Effect, Classifier.Classify("Piano Reverb", "", "Fx", false).Kind);

    [Fact]
    public void NoInformationStaysUnknown()
    {
        var result = Classifier.Classify("Zzyzx", "", "", null);
        Assert.Equal(PluginKind.Unknown, result.Kind);
        Assert.Empty(result.Tags);
        Assert.Equal(ClassificationSource.None, result.Source);
    }
}

public class MergerTests
{
    private static FileScanResult File(string path, PluginFormat format, ScanStatus status, params PluginClassInfo[] classes) =>
        new(path, format, Arch.X64, 1, DateTime.UnixEpoch, status, null, classes);

    private static PluginClassInfo Class(string name, string vendor, InfoSource source, string category = "") =>
        new(name, vendor, "1.0", category, null, source);

    [Fact]
    public void SameProductAcrossFormatsBecomesOnePlugin()
    {
        var merged = PluginMerger.Merge([
            File("a.vst3", PluginFormat.Vst3, ScanStatus.Loaded, Class("Pro-Q 3", "FabFilter", InfoSource.PluginLoad, "Fx|EQ")),
            File("a.dll", PluginFormat.Vst2, ScanStatus.FileInfoOnly, Class("ProQ3_x64", "", InfoSource.FileName)),
        ]);

        var plugin = Assert.Single(merged);
        Assert.Equal("Pro-Q 3", plugin.Name);
        Assert.Equal("FabFilter", plugin.Vendor);
        Assert.Equal(2, plugin.Files.Count);
        Assert.Contains("EQ", plugin.Classification.Tags);
    }

    [Fact]
    public void DifferentVendorsWithSameNameStaySeparate()
    {
        var merged = PluginMerger.Merge([
            File("a.vst3", PluginFormat.Vst3, ScanStatus.Loaded, Class("Comp", "Acme", InfoSource.PluginLoad)),
            File("b.vst3", PluginFormat.Vst3, ScanStatus.Loaded, Class("Comp", "Globex", InfoSource.PluginLoad)),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged.Select(p => p.Key).Distinct().Count());
    }

    [Fact]
    public void OneFileWithManyClassesBecomesManyPlugins()
    {
        var merged = PluginMerger.Merge([
            File("suite.vst3", PluginFormat.Vst3, ScanStatus.Loaded,
                Class("Alpha", "Acme", InfoSource.PluginLoad),
                Class("Beta", "Acme", InfoSource.PluginLoad)),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.All(merged, p => Assert.Equal("suite.vst3", Assert.Single(p.Files).Path));
    }

    [Fact]
    public void VendorSpellingsMergeAndDisplayCanonically()
    {
        var plugin = Assert.Single(PluginMerger.Merge([
            File("a.vst3", PluginFormat.Vst3, ScanStatus.Loaded, Class("Melodyne", "Celemony", InfoSource.PluginLoad)),
            File("a.aaxplugin", PluginFormat.Aax, ScanStatus.FileInfoOnly, Class("Melodyne", "Celemony Software GmbH", InfoSource.FileInfo)),
        ]));
        Assert.Equal("Celemony", plugin.Vendor);
        Assert.Equal(2, plugin.Files.Count);
    }

    [Fact]
    public void ChannelVariantsBecomeOneEntryWithCleanName()
    {
        var plugin = Assert.Single(PluginMerger.Merge([
            File("shell.vst3", PluginFormat.Vst3, ScanStatus.Loaded,
                Class("C4 Mono", "Waves", InfoSource.PluginLoad),
                Class("C4 Stereo", "Waves", InfoSource.PluginLoad)),
        ]));
        Assert.Equal("C4", plugin.Name);
        Assert.Equal("shell.vst3", Assert.Single(plugin.Files).Path);
        Assert.Contains("多段", plugin.Classification.Tags);
    }

    [Theory]
    [InlineData("NX 5.1/Stereo", "NX")]
    [InlineData("Abbey Road Studio 3 7.1/Stereo", "Abbey Road Studio 3")]
    [InlineData("B360 Mono/Quad", "B360")]
    [InlineData("CLA Vocals Mono/Stereo", "CLA Vocals")]
    [InlineData("Foo 2.5", "Foo 2.5")]
    [InlineData("Stereo", "Stereo")]
    [InlineData("bx_stereomaker", "bx_stereomaker")]
    public void ChannelVariantStripping(string input, string expected) =>
        Assert.Equal(expected, Normalizer.WithoutChannelVariant(input));

    [Fact]
    public void WaveShellVersionsBecomeOneEntryWithReadableName()
    {
        var plugin = Assert.Single(PluginMerger.Merge([
            File("a.vst3", PluginFormat.Vst3, ScanStatus.TimedOut, Class("WaveShell1-VST3 14.0_x64", "Waves Audio Ltd.", InfoSource.FileInfo)),
            File("b.dll", PluginFormat.Vst2, ScanStatus.FileInfoOnly, Class("WaveShell1-VST 14.5_x64", "Waves Audio Ltd.", InfoSource.FileInfo)),
        ]));
        Assert.Equal("Waves", plugin.Vendor);
        Assert.StartsWith("WaveShell", plugin.Name);
        Assert.Equal(2, plugin.Files.Count);
    }

    [Fact]
    public void SeedSuppliesNameAndVendorForUnreadableFiles()
    {
        var plugin = Assert.Single(PluginMerger.Merge([
            File("a.aaxplugin", PluginFormat.Aax, ScanStatus.FileInfoOnly, Class("uaudio_neve_1073", "", InfoSource.FileName)),
        ]));
        Assert.Equal("UAD Neve 1073", plugin.Name);
        Assert.Equal("Universal Audio", plugin.Vendor);
    }

    [Fact]
    public void KeyStaysStableWhenLoadStartsSucceeding()
    {
        var failed = PluginMerger.Merge([File("a.vst3", PluginFormat.Vst3, ScanStatus.LoadFailed, Class("Foo", "", InfoSource.FileName))]);
        var loaded = PluginMerger.Merge([File("a.vst3", PluginFormat.Vst3, ScanStatus.Loaded, Class("Foo", "Acme", InfoSource.PluginLoad))]);
        Assert.Equal(failed[0].Key, loaded[0].Key);
    }
}

public class ModuleInfoTests
{
    [Fact]
    public void ReadsOnlyAudioClassesAndToleratesJson5()
    {
        const string json = """
            {
              "Name": "Thing",
              "Factory Info": { "Vendor": "Acme", },
              "Classes": [
                { "CID": "1", "Category": "Audio Module Class", "Name": "Thing", "Version": "2.0", "Sub Categories": ["Fx", "Reverb"], },
                { "CID": "2", "Category": "Component Controller Class", "Name": "Thing Controller" },
              ],
            }
            """;
        var plugin = Assert.Single(StaticInspector.ParseModuleInfo(json)!);
        Assert.Equal("Thing", plugin.Name);
        Assert.Equal("Acme", plugin.Vendor);
        Assert.Equal("Fx|Reverb", plugin.SdkCategory);
        Assert.False(plugin.IsInstrument);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "Classes": [ { "Category": "Component Controller Class" } ] }""")]
    public void ReturnsNullWhenUnusable(string json) => Assert.Null(StaticInspector.ParseModuleInfo(json));
}

public class ScannerOutputTests
{
    [Fact]
    public void ParsesScannerJson()
    {
        var result = ScannerRunner.ParseOutput("""[{"name":"X","vendor":"V","version":"1","category":"Instrument|Synth","isInstrument":true,"inputs":0,"outputs":2,"uid":1}]""");
        Assert.Equal(LoadOutcome.Success, result.Outcome);
        Assert.True(Assert.Single(result.Classes).IsInstrument);
    }

    [Theory]
    [InlineData("[]", LoadOutcome.NoPlugins)]
    [InlineData("garbage", LoadOutcome.Failed)]
    [InlineData("""[{"name":"X"}]""", LoadOutcome.Failed)]
    public void BadOutputIsReported(string json, LoadOutcome expected) =>
        Assert.Equal(expected, ScannerRunner.ParseOutput(json).Outcome);
}

public sealed class DiscoveryAndDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-test-" + Guid.NewGuid().ToString("N"));

    public DiscoveryAndDatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void BundleCountsAsOneTargetAndVst3FolderDllsAreIgnored()
    {
        var vendor = Path.Combine(_dir, "Acme");
        var bundleBinary = Path.Combine(vendor, "Thing.vst3", "Contents", "x86_64-win");
        Directory.CreateDirectory(bundleBinary);
        System.IO.File.WriteAllText(Path.Combine(bundleBinary, "Thing.vst3"), "x");
        System.IO.File.WriteAllText(Path.Combine(vendor, "Single.vst3"), "x");
        System.IO.File.WriteAllText(Path.Combine(vendor, "dependency.dll"), "x");

        var targets = PluginDiscovery.Discover([(_dir, PluginFormat.Vst3)]);

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal(PluginFormat.Vst3, t.Format));
        Assert.Contains(targets, t => t.Path.EndsWith("Thing.vst3", StringComparison.OrdinalIgnoreCase) && Directory.Exists(t.Path));
    }

    [Fact]
    public void Vst2FolderIgnoresDllWithoutEntryPoint()
    {
        System.IO.File.WriteAllText(Path.Combine(_dir, "notplugin.dll"), "not a pe file");
        Assert.Empty(PluginDiscovery.Discover([(_dir, PluginFormat.Vst2)]));
    }

    [Fact]
    public void VendorFromFolderUsesFirstLevelOnly()
    {
        Assert.Equal("Acme", StaticInspector.VendorFromFolder(Path.Combine(_dir, "Acme", "Sub", "a.vst3"), _dir));
        Assert.Equal("", StaticInspector.VendorFromFolder(Path.Combine(_dir, "a.vst3"), _dir));
    }

    [Fact]
    public void MissingPluginKeepsRowAndReturnsAfterRescan()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        var plugin = new MergedPlugin("foo", "Foo", "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile("foo.vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx|EQ", ScanStatus.Loaded, null)]);

        db.SaveMerged([plugin]);
        var id = Assert.Single(db.LoadPlugins()).Id;

        db.SaveMerged([]);
        var missing = Assert.Single(db.LoadPlugins());
        Assert.True(missing.IsMissing);
        Assert.Empty(missing.Files);
        Assert.Equal(id, missing.Id);

        db.SaveMerged([plugin]);
        var back = Assert.Single(db.LoadPlugins());
        Assert.False(back.IsMissing);
        Assert.Equal(id, back.Id);
        Assert.Equal(["EQ"], back.Tags);
    }

    [Fact]
    public async Task FailedRescanOfUnchangedFileKeepsPreviousPlugins()
    {
        var root = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Shell.vst3");
        System.IO.File.WriteAllText(file, "x");
        var info = new FileInfo(file);

        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveFileScan(new FileScanResult(file, PluginFormat.Vst3, Arch.Unknown, info.Length, info.LastWriteTimeUtc, ScanStatus.Loaded, null,
        [
            new PluginClassInfo("Alpha", "Acme", "1", "Fx", false, InfoSource.PluginLoad),
            new PluginClassInfo("Beta", "Acme", "1", "Fx", false, InfoSource.PluginLoad),
        ]));

        // 扫描器路径不存在，这次读取一定失败。
        var service = new ScanService(db, new ScannerRunner(Path.Combine(_dir, "missing.exe"), TimeSpan.FromSeconds(5)));
        await service.ScanAsync([(root, PluginFormat.Vst3)], new ScanOptions(Full: true), null, CancellationToken.None);

        Assert.Equal(["Alpha", "Beta"], db.LoadPlugins().Select(p => p.Name).Order());
        Assert.Equal(ScanStatus.Loaded, db.LoadFileScans()[file].Status);
    }

    [Fact]
    public async Task FailedScanOfChangedFileFallsBackToFileInfo()
    {
        var root = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Shell.vst3");
        System.IO.File.WriteAllText(file, "x");

        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveFileScan(new FileScanResult(file, PluginFormat.Vst3, Arch.Unknown, 999, DateTime.UnixEpoch, ScanStatus.Loaded, null,
            [new PluginClassInfo("Alpha", "Acme", "1", "Fx", false, InfoSource.PluginLoad)]));

        var service = new ScanService(db, new ScannerRunner(Path.Combine(_dir, "missing.exe"), TimeSpan.FromSeconds(5)));
        await service.ScanAsync([(root, PluginFormat.Vst3)], new ScanOptions(), null, CancellationToken.None);

        var scan = db.LoadFileScans()[file];
        Assert.Equal(ScanStatus.LoadFailed, scan.Status);
        Assert.Equal("Shell", Assert.Single(scan.Classes).Name);
    }

    private static MergedPlugin Sample(string key = "foo", string vendor = "Acme", params string[] tags) =>
        new(key, "Foo", vendor, InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, tags.Length > 0 ? tags : ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile(key + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx|EQ", ScanStatus.Loaded, null)]);

    [Fact]
    public void RescanKeepsUserDataAndStillRefreshesAutoResults()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Sample()]);
        var id = Assert.Single(db.LoadPlugins()).Id;
        var mine = new PluginUserData(4, "人声很顺，但吃 CPU", ["人声", "常用"], ["去齿音"], PluginKind.Instrument, "My Vendor");
        db.SaveUserData(id, mine);

        db.SaveMerged([Sample(vendor: "Acme Two", tags: "混响")]);

        var row = Assert.Single(db.LoadPlugins());
        Assert.Equal(4, row.User.Rating);
        Assert.Equal("人声很顺，但吃 CPU", row.User.Note);
        Assert.Equal(["人声", "常用"], row.User.MyTags);
        // 手动修改优先，自动结果照常更新，恢复自动时能拿到最新值。
        Assert.Equal(["去齿音"], row.Tags);
        Assert.Equal(["混响"], row.AutoTags);
        Assert.Equal("My Vendor", row.Vendor);
        Assert.Equal("Acme Two", row.AutoVendor);
        Assert.Equal(PluginKind.Instrument, row.Kind);
        Assert.Equal(ClassificationSource.Manual, row.TagSource);
    }

    [Fact]
    public void ClearingOverridesFallsBackToAutoResults()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Sample()]);
        var id = Assert.Single(db.LoadPlugins()).Id;

        db.SaveUserData(id, new PluginUserData(0, "", [], ["混响"], PluginKind.Instrument, "X"));
        db.SaveUserData(id, PluginUserData.Empty);

        var row = Assert.Single(db.LoadPlugins());
        Assert.Equal(["EQ"], row.Tags);
        Assert.Equal("Acme", row.Vendor);
        Assert.Equal(PluginKind.Effect, row.Kind);
        Assert.Equal(ClassificationSource.SdkCategory, row.TagSource);
    }

    [Fact]
    public void EmptyOverrideMeansNoCategoriesNotAuto()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Sample()]);
        var id = Assert.Single(db.LoadPlugins()).Id;

        db.SaveUserData(id, new PluginUserData(0, "", [], [], null, null));

        Assert.Empty(Assert.Single(db.LoadPlugins()).Tags);
    }

    [Fact]
    public void UserDataIsNormalizedOnSave()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Sample()]);
        var id = Assert.Single(db.LoadPlugins()).Id;

        db.SaveUserData(id, new PluginUserData(99, "  备注  ", [" 人声 ", "人声", ""], null, null, "   "));

        var user = Assert.Single(db.LoadPlugins()).User;
        Assert.Equal(5, user.Rating);
        Assert.Equal("备注", user.Note);
        Assert.Equal(["人声"], user.MyTags);
        Assert.Null(user.VendorOverride);
    }

    [Fact]
    public void Version1DatabaseUpgradesWithoutLosingRows()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = """
                CREATE TABLE file_scans (path TEXT PRIMARY KEY COLLATE NOCASE, format INTEGER NOT NULL, arch INTEGER NOT NULL, size INTEGER NOT NULL,
                    modified_utc TEXT NOT NULL, status INTEGER NOT NULL, error TEXT, classes_json TEXT NOT NULL);
                CREATE TABLE plugins (id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, vendor TEXT NOT NULL, kind INTEGER NOT NULL,
                    tags_json TEXT NOT NULL, tag_source INTEGER NOT NULL, is_missing INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE plugin_files (plugin_id INTEGER NOT NULL, path TEXT NOT NULL COLLATE NOCASE, format INTEGER NOT NULL, arch INTEGER NOT NULL,
                    version TEXT NOT NULL, sdk_category TEXT NOT NULL, status INTEGER NOT NULL, error TEXT, PRIMARY KEY (plugin_id, path));
                INSERT INTO plugins (key, name, vendor, kind, tags_json, tag_source) VALUES ('foo', 'Foo', 'Acme', 1, '["EQ"]', 1);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        for (var open = 0; open < 2; open++)
        {
            using var db = new PluginDatabase(path);
            var row = Assert.Single(db.LoadPlugins());
            Assert.Equal("Foo", row.Name);
            Assert.Equal(["EQ"], row.Tags);
            Assert.Equal(PluginUserData.Empty.Rating, row.User.Rating);
            Assert.Null(row.User.TagsOverride);
        }
    }

    [Fact]
    public void BackupRestoresIntoNewDatabaseIncludingNotYetInstalledPlugins()
    {
        string json;
        using (var source = new PluginDatabase(Path.Combine(_dir, "a.db")))
        {
            source.SaveMerged([Sample("foo"), Sample("bar"), Sample("untouched")]);
            var rows = source.LoadPlugins();
            source.SaveUserData(rows.Single(r => r.Key == "foo").Id, new PluginUserData(5, "好用", ["常用"], ["混响"], PluginKind.Instrument, "V"));
            source.SaveUserData(rows.Single(r => r.Key == "bar").Id, new PluginUserData(1, "难用", [], null, null, null));
            json = UserDataBackup.Serialize(source.LoadPlugins());
        }

        var entries = UserDataBackup.Parse(json);
        Assert.Equal(2, entries.Count);

        using var target = new PluginDatabase(Path.Combine(_dir, "b.db"));
        target.SaveMerged([Sample("foo")]);
        var result = target.ImportUserData(entries);
        Assert.Equal(new ImportResult(1, 1), result);

        var restored = target.LoadPlugins();
        var foo = restored.Single(r => r.Key == "foo");
        Assert.Equal("好用", foo.User.Note);
        Assert.Equal(PluginKind.Instrument, foo.User.KindOverride);
        Assert.Equal(["混响"], foo.Tags);
        Assert.False(foo.IsMissing);

        var bar = restored.Single(r => r.Key == "bar");
        Assert.True(bar.IsMissing);
        Assert.Equal("难用", bar.User.Note);

        // 之后真的装上并扫描到时，自动对上这条记录。
        target.SaveMerged([Sample("foo"), Sample("bar")]);
        var installed = target.LoadPlugins().Single(r => r.Key == "bar");
        Assert.False(installed.IsMissing);
        Assert.Equal("难用", installed.User.Note);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "version": 999, "plugins": [] }""")]
    [InlineData("""{ "version": 1, "plugins": [ { "key": "" } ] }""")]
    public void InvalidBackupIsRejected(string json) =>
        Assert.Throws<FormatException>(() => UserDataBackup.Parse(json));

    [Fact]
    public void ConcurrentScanWritesAndUserSavesDoNotConflict()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Sample()]);
        var id = Assert.Single(db.LoadPlugins()).Id;

        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0)
                db.SaveUserData(id, new PluginUserData(i % 6, "n" + i, [], null, null, null));
            else
                db.SaveMerged([Sample()]);
        });

        Assert.Single(db.LoadPlugins());
    }

    [Fact]
    public void FileScanRoundTrips()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        db.SaveFileScan(new FileScanResult("C:\\插件\\a.vst3", PluginFormat.Vst3, Arch.X64, 42, modified, ScanStatus.TimedOut, "超时",
            [new PluginClassInfo("A", "B", "1", "Fx", false, InfoSource.FileInfo)]));

        var loaded = db.LoadFileScans()["c:\\插件\\A.VST3"];
        Assert.Equal(modified, loaded.ModifiedUtc);
        Assert.Equal(ScanStatus.TimedOut, loaded.Status);
        Assert.Equal("A", Assert.Single(loaded.Classes).Name);
    }
}
