using System.Text;
using System.Text.Json;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public sealed class HealthTests
{
    private static long _nextId;

    private static MergedFile File(string path, PluginFormat format = PluginFormat.Vst3, Arch arch = Arch.X64, string version = "1.0",
        ScanStatus status = ScanStatus.Loaded, string? error = null) =>
        new(path, format, arch, version, "", status, error);

    private static PluginRow Row(string name, MergedFile[] files, bool missing = false, bool hidden = false, PluginKind kind = PluginKind.Effect) =>
        new(++_nextId, Normalizer.Name(name), name, "Acme", kind, [], ClassificationSource.Seed, missing, files,
            new PluginUserData(0, "", [], null, null, null, hidden, "", null));

    private static List<HealthKind> Kinds(HealthReport report, string name) =>
        report.Issues.Where(i => i.Plugin.Name == name).Select(i => i.Kind).ToList();

    [Fact]
    public void FindsEachKindOfProblem()
    {
        var report = PluginHealth.Build([
            Row("Healthy", [File(@"C:\a\Healthy.vst3")]),
            Row("Gone", [], missing: true),
            Row("Crash", [File(@"C:\a\Crash.vst3", status: ScanStatus.LoadFailed, error: "缺少依赖")]),
            Row("Slow", [File(@"C:\a\Slow.vst3", status: ScanStatus.TimedOut)]),
            Row("Old32", [File(@"C:\x86\Old32.dll", PluginFormat.Vst2, Arch.X86)]),
            Row("Both32", [File(@"C:\a\Both32.vst3"), File(@"C:\x86\Both32.vst3", arch: Arch.X86)]),
            Row("V2", [File(@"F:\v\V2.dll", PluginFormat.Vst2, status: ScanStatus.FileInfoOnly)]),
            Row("Twin", [File(@"C:\a\Twin.vst3"), File(@"D:\b\Twin.vst3")]),
            Row("Both", [File(@"C:\a\Both.vst3"), File(@"F:\v\Both.dll", PluginFormat.Vst2, status: ScanStatus.FileInfoOnly)]),
            Row("Stale", [File(@"C:\a\Stale.vst3", version: "2.0.0"), File(@"F:\v\Stale.dll", PluginFormat.Vst2, version: "1.5.0", status: ScanStatus.FileInfoOnly)]),
        ], []);

        Assert.Empty(Kinds(report, "Healthy"));
        Assert.Equal([HealthKind.Missing], Kinds(report, "Gone"));
        Assert.Equal([HealthKind.ReadFailed], Kinds(report, "Crash"));
        Assert.Contains("缺少依赖", report.Issues.Single(i => i.Plugin.Name == "Crash").Detail);
        Assert.Contains("超时", report.Issues.Single(i => i.Plugin.Name == "Slow").Detail);
        Assert.Contains(HealthKind.Only32Bit, Kinds(report, "Old32"));
        Assert.Equal([HealthKind.Leftover32Bit], Kinds(report, "Both32"));
        Assert.Equal([HealthKind.OnlyVst2], Kinds(report, "V2"));
        Assert.Equal([HealthKind.Duplicate], Kinds(report, "Twin"));
        Assert.Equal([HealthKind.BothFormats], Kinds(report, "Both"));
        Assert.Contains(HealthKind.VersionMismatch, Kinds(report, "Stale"));
    }

    [Fact]
    public void MonoVariantAndDifferentArchAreNotDuplicates()
    {
        var report = PluginHealth.Build([
            Row("Micro", [File(@"F:\v\Micro.dll", PluginFormat.Vst2, status: ScanStatus.FileInfoOnly), File(@"F:\v\Micro (Mono).dll", PluginFormat.Vst2, status: ScanStatus.FileInfoOnly)]),
            Row("Arch", [File(@"C:\Program Files\Arch.vst3"), File(@"C:\Program Files (x86)\Arch.vst3", arch: Arch.X86)]),
        ], []);
        Assert.DoesNotContain(report.Issues, i => i.Kind == HealthKind.Duplicate);
    }

    [Theory]
    [InlineData("1.18", "1.1.8.0", false)]
    [InlineData("5, 3, 1, 18", "5.3.1", false)]
    [InlineData("3.3.0", "3.3.0.0", false)]
    [InlineData("2.0.0", "1.5.0", true)]
    [InlineData("14.0.83.84", "14.5.8.189", true)]
    public void VersionSpellingsAreNotMismatches(string a, string b, bool expected) =>
        Assert.Equal(expected, PluginHealth.HasVersionMismatch([a, b]));

    [Fact]
    public void SkipsInstrumentsAndBlacklistedPlugins()
    {
        var report = PluginHealth.Build([
            Row("Synth", [File(@"C:\x86\Synth.dll", arch: Arch.X86)], kind: PluginKind.Instrument),
            Row("Banned", [File(@"C:\x86\Banned.dll", arch: Arch.X86)], hidden: true),
        ], [new HostBlacklistEntry(@"C:\x86\Synth.dll", "Cubase 14")]);
        Assert.Empty(report.Issues);
        // 音源文件只算数量，不会出现名字。
        Assert.Equal(1, report.UnmatchedBlacklistFiles);
    }

    [Fact]
    public void MatchesHostBlacklistByPathAndSaysWhetherAnotherFormatWorks()
    {
        var report = PluginHealth.Build([
            Row("Saved", [File(@"F:\V\Saved.dll", PluginFormat.Vst2, status: ScanStatus.FileInfoOnly), File(@"C:\a\Saved.vst3")]),
            Row("Stuck", [File(@"F:\v\Stuck.dll", PluginFormat.Vst2, status: ScanStatus.FileInfoOnly)]),
        ], [new HostBlacklistEntry(@"f:\v\saved.dll", "Cubase 14"), new HostBlacklistEntry(@"F:\v\Stuck.dll", "Cubase 14"), new HostBlacklistEntry(@"F:\v\Stuck.dll", "Cubase 13")]);

        var saved = report.Issues.Single(i => i.Plugin.Name == "Saved" && i.Kind == HealthKind.HostBlacklisted);
        Assert.Contains("VST3 可用", saved.Detail);
        var stuck = report.Issues.Single(i => i.Plugin.Name == "Stuck" && i.Kind == HealthKind.HostBlacklisted);
        Assert.Contains("没有别的可用文件", stuck.Detail);
        Assert.Contains("Cubase 13", stuck.Detail);
        Assert.Equal(2, report.NeedsAttention);
    }

    [Fact]
    public void ParsesCubaseVst2BlacklistStructure()
    {
        // 结构取自 Cubase 14 的真实文件。FileType 里的 Name 不能被当成文件名。
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Vst2xBlacklist><list name="Entries" type="list"><item>
              <string name="Group" value="BlackList"/>
              <member name="Values"><member name="BlackList"><list name="entries" type="list"><item>
                <obj class="FNPath" name="path" ID="1">
                  <string name="Name" value="BREVERB 2.dll" wide="true"/>
                  <string name="Path" value="F:\vst64\" wide="true"/>
                  <member name="FileType"><string name="Name" value="Module" wide="true"/></member>
                </obj>
                <int name="reason" value="2"/>
              </item></list></member></member>
            </item></list></Vst2xBlacklist>
            """;
        Assert.Equal([@"F:\vst64\BREVERB 2.dll"], HostBlacklist.ParseVst2(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [Fact]
    public void Vst3BlacklistToleratesEmptyAndFindsPaths()
    {
        Assert.Empty(HostBlacklist.ParseVst3(new MemoryStream(Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8"?><plugins />"""))));
        var xml = """<plugins><plugin path="C:\a\Bad.vst3" name="Bad"/><plugin><file>D:\b\Worse.VST3</file></plugin></plugins>""";
        Assert.Equal([@"C:\a\Bad.vst3", @"D:\b\Worse.VST3"], HostBlacklist.ParseVst3(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }

    [Fact]
    public void LoadReadsHostFoldersAndIgnoresDamagedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pv-h-" + Guid.NewGuid().ToString("N"));
        var host = Directory.CreateDirectory(Path.Combine(root, "Cubase 14_64")).FullName;
        try
        {
            System.IO.File.WriteAllText(Path.Combine(host, "Vst2xPlugin Blacklist Cubase.xml"),
                """<a><obj class="FNPath"><string name="Name" value="X.dll"/><string name="Path" value="C:\p\"/></obj></a>""");
            System.IO.File.WriteAllText(Path.Combine(host, "Vst2xPlugin Blacklist Other.xml"), "<broken");
            var entry = Assert.Single(HostBlacklist.Load(root));
            Assert.Equal((@"C:\p\X.dll", "Cubase 14"), (entry.Path, entry.Host));
            Assert.Empty(HostBlacklist.Load(Path.Combine(root, "nope")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateGuideGroupsVendorsAndSkipsInstruments()
    {
        var waves = Row("A", [File(@"C:\a.vst3")]) with { AutoVendor = "Waves" };
        var rows = new[]
        {
            waves,
            Row("B", [File(@"C:\b.vst3")]) with { AutoVendor = "Waves" },
            Row("C", [File(@"C:\c.vst3")]) with { AutoVendor = "Tiny Co" },
            Row("Synth", [File(@"C:\s.vst3")], kind: PluginKind.Instrument) with { AutoVendor = "SynthCo" },
        };
        var updates = UpdateGuide.Build(rows);
        Assert.Equal(["Waves", "Tiny Co"], updates.Select(u => u.Vendor));
        Assert.Equal("Waves Central", updates[0].Tool);
        Assert.Equal("厂家官网", updates[1].Tool);
        Assert.StartsWith("https://www.bing.com/search?q=", UpdateGuide.SearchUrl("Fab Filter", "Pro-Q 3"));
        Assert.DoesNotContain(" ", UpdateGuide.SearchUrl("Fab Filter", "Pro-Q 3"));
    }

    [Fact]
    public void LookupDumpExcludesInstrumentsHiddenAndItselfAndCarriesChains()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pv-l-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            MergedPlugin Merged(string key, string name, PluginKind kind = PluginKind.Effect) => new(key, name, "Acme", InfoSource.PluginLoad,
                new Classification(kind, kind == PluginKind.Effect ? ["压缩"] : [], ClassificationSource.SdkCategory),
                [new MergedFile(key + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx", ScanStatus.Loaded, null)]);
            using (var db = new PluginDatabase(Path.Combine(dir, "t.db")))
            {
                db.SaveMerged([Merged("comp", "Comp"), Merged("synth", "Synth", PluginKind.Instrument), Merged("ban", "Ban"), Merged("self", LookupExport.OwnPluginName)]);
                var rows = db.LoadPlugins();
                var comp = rows.Single(r => r.Key == "comp");
                db.SaveUserData(comp.Id, comp.User with { Rating = 4, Note = "人声很顺", Mix = new MixAttributes(LatencyLevel.Zero, Sidechain: Tri.Yes) });
                var ban = rows.Single(r => r.Key == "ban");
                db.SaveUserData(ban.Id, ban.User with { Hidden = true });
                var chain = db.AddChain("人声");
                db.SaveChain(chain.Id, "注意", [new ChainStep("comp", "Comp", "轻压"), new ChainStep("synth", "Synth"), new ChainStep("gone", "Gone")]);

                using var dump = JsonDocument.Parse(LookupExport.Dump(db));
                var root = dump.RootElement;
                Assert.True(root.GetProperty("ok").GetBoolean());
                Assert.True(root.GetProperty("theme").TryGetProperty("Accent", out _));
                var plugin = Assert.Single(root.GetProperty("plugins").EnumerateArray());
                Assert.Equal("Comp", plugin.GetProperty("name").GetString());
                Assert.Equal("人声很顺", plugin.GetProperty("note").GetString());
                Assert.Contains("无延迟", plugin.GetProperty("mix").GetString());
                var steps = root.GetProperty("chains")[0].GetProperty("steps").EnumerateArray().ToList();
                Assert.Equal(["Comp", "Gone"], steps.Select(s => s.GetProperty("name").GetString()));
                Assert.Equal("未安装", steps[1].GetProperty("state").GetString());
                Assert.DoesNotContain("\\u", LookupExport.Dump(db));

                using var recommend = JsonDocument.Parse(LookupExport.Recommend(db, "压缩"));
                var item = Assert.Single(recommend.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("comp", item.GetProperty("key").GetString());
                Assert.Contains("找到 1 款", recommend.RootElement.GetProperty("summary").GetString());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ScanSkipsOwnPluginAndProjectsDoNotReportItMissing()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pv-d-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "What Plugin Lookup.vst3"));
            Directory.CreateDirectory(Path.Combine(root, "Other.vst3"));
            System.IO.File.WriteAllText(Path.Combine(root, "Other2.vst3"), "");
            System.IO.File.WriteAllText(Path.Combine(root, "What Plugin Lookup.dll"), "");
            var found = PluginDiscovery.Discover([(root, PluginFormat.Vst3)]).Select(t => Path.GetFileName(t.Path)).ToList();
            Assert.Contains("Other.vst3", found);
            Assert.DoesNotContain(found, f => f.StartsWith("What Plugin Lookup", StringComparison.Ordinal));
            Assert.True(StockPlugins.Contains(ProjectHost.StudioOne, "What Plugin Lookup"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublishesCliPathOnlyForTheDefaultDatabase()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pv-c-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var pointer = Path.Combine(data, "cli-path.txt");
            var db = Path.Combine(data, "plugins.db");

            LookupExport.PublishCliPath(db, db, app);
            Assert.False(System.IO.File.Exists(pointer)); // 工具不存在时不写

            var cli = Path.Combine(app, "WhatPlugin.Cli.exe");
            System.IO.File.WriteAllText(cli, "");
            LookupExport.PublishCliPath(Path.Combine(root, "test.db"), db, app);
            Assert.False(System.IO.File.Exists(pointer)); // 测试数据库不写

            LookupExport.PublishCliPath(db, db, app);
            Assert.Equal(cli, System.IO.File.ReadAllText(pointer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopiesOldDataOnceAndKeepsOriginal()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pv-m-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var legacy = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "old")).FullName, "plugins.db");
            var current = Path.Combine(root, "new", "plugins.db");
            Assert.False(PluginDatabase.MigrateLegacyData(current, legacy)); // 没有旧数据
            System.IO.File.WriteAllText(legacy, "old");
            Assert.True(PluginDatabase.MigrateLegacyData(current, legacy));
            Assert.Equal("old", System.IO.File.ReadAllText(current));
            Assert.Equal("old", System.IO.File.ReadAllText(legacy));

            System.IO.File.WriteAllText(current, "mine");
            Assert.False(PluginDatabase.MigrateLegacyData(current, legacy)); // 新数据不能被盖掉
            Assert.Equal("mine", System.IO.File.ReadAllText(current));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FallsBackToOldDatabaseWhenCopyFails()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pv-f-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var legacy = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "old")).FullName, "plugins.db");
            System.IO.File.WriteAllText(legacy, "old");
            // 新位置的上级是一个文件，建不了文件夹，复制必然失败。
            var blocker = Path.Combine(root, "blocked");
            System.IO.File.WriteAllText(blocker, "");
            Assert.Equal(legacy, PluginDatabase.ResolveDefaultPath(Path.Combine(blocker, "plugins.db"), legacy));

            var current = Path.Combine(root, "new", "plugins.db");
            Assert.Equal(current, PluginDatabase.ResolveDefaultPath(current, legacy));
            Assert.True(System.IO.File.Exists(current));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SecretsSavedBeforeTheRenameStillDecrypt()
    {
        // 用改名前的熵值加密，模拟老数据库里的密码。
        var blob = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes("fake-value"),
            Encoding.UTF8.GetBytes("PluginVault.secret.v1"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        Assert.Equal("fake-value", SecretProtector.TryUnprotect(blob));
    }

    [Fact]
    public void RecognisesOwnPlugin()
    {
        Assert.True(LookupExport.IsOwnPlugin(@"C:\VST3\What Plugin Lookup.vst3"));
        Assert.True(LookupExport.IsOwnPlugin("what plugin lookup"));
        Assert.True(LookupExport.IsOwnPlugin(@"C:\VST3\PluginVault Lookup.vst3"));
        Assert.False(LookupExport.IsOwnPlugin(@"C:\VST3\Pro-Q 3.vst3"));
    }
}
