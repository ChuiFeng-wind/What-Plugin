using System.Text;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;
using Xunit;

using static WhatPlugin.Tests.TestPaths;

namespace WhatPlugin.Tests;

public class HostPluginCacheTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <Vst2xPlugins>
           <list name="Entries" type="list">
              <item>
                 <string name="Group" value="Version"/>
                 <member name="Values"><int name="Number" value="1"/></member>
              </item>
              <item>
                 <string name="Group" value="Vst2xPlug\F:\vst64\Serum_x64.dll"/>
                 <member name="Values"><member name="Vst2xPlugInfo">
                    <string name="subCategory" value="Instrument"/>
                    <string name="name" value="Serum_x64"/>
                    <string name="vendor" value="Xfer Records"/>
                    <string name="vendorVersion" value="1.3"/>
                 </member></member>
              </item>
              <item>
                 <string name="Group" value="Vst2xPlug\F:\vst64\Sub\Pro-C 2.dll"/>
                 <member name="Values"><member name="Vst2xPlugInfo">
                    <string name="subCategory" value="Fx|Mastering"/>
                    <string name="name" value="FabFilter Pro-C 2"/>
                    <string name="vendor" value="FabFilter"/>
                 </member></member>
              </item>
              <item>
                 <string name="Group" value="Vst2xPlug\C:\Program Files\Vstplugins\WaveShell1-VST 14.5_x64.dll"/>
                 <member name="Values"><member name="Vst2xPlugInfo">
                    <string name="subCategory" value="Vst2xLegacyShell"/>
                 </member></member>
              </item>
              <item>
                 <string name="Group" value="Vst2xPlug\F:\vst64\Broken.dll"/>
                 <member name="Values"><member name="Vst2xPlugInfo"/></member>
              </item>
           </list>
        </Vst2xPlugins>
        """;

    private static List<(string Path, HostPluginInfo? Info)> ParseSample() =>
        HostPluginCache.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample.TrimStart())));

    [Fact]
    public void ParsesInstrumentAndEffect()
    {
        var rows = ParseSample().ToDictionary(r => r.Path, r => r.Info);
        Assert.Equal(new HostPluginInfo("Serum_x64", "Xfer Records", "1.3", "Instrument"), rows[@"F:\vst64\Serum_x64.dll"]);
        Assert.Equal("FabFilter Pro-C 2", rows[@"F:\vst64\Sub\Pro-C 2.dll"]!.Name);
    }

    [Fact]
    public void KeepsOnlyMainCategory()
    {
        // 厂家填的细分类不可靠，不能盖过 VST3 的分类或名称判断。
        var rows = ParseSample().ToDictionary(r => r.Path, r => r.Info);
        Assert.Equal("Fx", rows[@"F:\vst64\Sub\Pro-C 2.dll"]!.Category);
    }

    [Fact]
    public void ShellsAndFailedFilesHaveNoInfo()
    {
        var rows = ParseSample().ToDictionary(r => r.Path, r => r.Info);
        Assert.Null(rows[@"C:\Program Files\Vstplugins\WaveShell1-VST 14.5_x64.dll"]);
        Assert.Null(rows[@"F:\vst64\Broken.dll"]);
        Assert.DoesNotContain(rows.Keys, k => k == "Version");
    }

    [Fact]
    public void LoadReadsCubaseAndNuendoFoldersOnly()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var host in new[] { "Cubase 14_64", "Nuendo 13_64", "Dorico 5" })
            {
                Directory.CreateDirectory(Path.Combine(root, host));
                File.WriteAllText(Path.Combine(root, host, "Vst2xPlugin Infos Test.xml"), Sample.TrimStart().Replace("Serum_x64.dll", host + ".dll"));
            }
            var loaded = HostPluginCache.Load(root);
            Assert.Contains(@"F:\vst64\Cubase 14_64.dll", loaded.Keys);
            Assert.Contains(@"F:\vst64\Nuendo 13_64.dll", loaded.Keys);
            Assert.DoesNotContain(@"F:\vst64\Dorico 5.dll", loaded.Keys);
            Assert.True(loaded.ContainsKey(@"f:\VST64\cubase 14_64.DLL"), "路径不区分大小写");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BrokenCacheIsIgnored()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Cubase 14_64"));
            File.WriteAllText(Path.Combine(root, "Cubase 14_64", "Vst2xPlugin Infos Cubase.xml"), "<not closed");
            Assert.Empty(HostPluginCache.Load(root));
            Assert.Empty(HostPluginCache.SuggestFolders(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SuggestsOnlyTopFolders()
    {
        var folders = HostPluginCache.TopFolders(
        [
            P(@"F:\vst64\A.dll"),
            P(@"F:\VST64\Sub\B.dll"),
            P(@"F:\vst64\Sub\Deep\C.dll"),
            P(@"F:\vst642\D.dll"),
            P(@"E:\Lonely\E.dll"),
        ], minimumPlugins: 1);
        Assert.Equal([P(@"E:\Lonely"), P(@"F:\vst64"), P(@"F:\vst642")], folders);
    }

    [Fact]
    public void InsideDoesNotMatchSimilarPrefix()
    {
        Assert.True(HostPluginCache.IsInside(P(@"F:\vst64\a.dll"), P(@"F:\VST64")));
        Assert.True(HostPluginCache.IsInside(P(@"F:\vst64\a.dll"), P(@"F:\vst64\")));
        Assert.False(HostPluginCache.IsInside(P(@"F:\vst642\a.dll"), P(@"F:\vst64")));
        Assert.False(HostPluginCache.IsInside(P(@"F:\vst64"), P(@"F:\vst64")));
    }

    [Fact]
    public void HostInfoReplacesFileInfo()
    {
        var file = new PluginClassInfo("Serum_x64", "文件夹厂家", "", "", null, InfoSource.FileName);
        var merged = StaticInspector.WithHostInfo(file, new HostPluginInfo("Serum", "Xfer Records", "1.3", "Instrument"));
        Assert.Equal("Serum", merged.Name);
        Assert.Equal("Xfer Records", merged.Vendor);
        Assert.True(merged.IsInstrument);
        Assert.Same(file, StaticInspector.WithHostInfo(file, null));
    }

    [Fact]
    public void HostInstrumentBeatsSeed()
    {
        // 资料库认为是效果器的名字，只要插件自己说是音源，就不能显示。
        var seed = Classifier.SeedEntries.First(s => s.Kind != "instrument" && s.DisplayName != null && Classifier.FindSeed(s.DisplayName, "") == s);
        Assert.Equal(PluginKind.Instrument, Classifier.Classify(seed.DisplayName!, "", "Instrument", true).Kind);
    }

    [Theory]
    [InlineData("FabFilter Pro-C 2 (Mono)", "FabFilter Pro-C 2")]
    [InlineData("FabFilter Volcano 2 (Mono SC)", "FabFilter Volcano 2")]
    [InlineData("FabFilter Timeless 2 (SC)", "FabFilter Timeless 2")]
    [InlineData("Nexus.64", "Nexus")]
    [InlineData("WhisperOfLoong(64)", "WhisperOfLoong(32)")]
    public void VariantsFoldIntoOnePlugin(string variant, string main) =>
        Assert.Equal(Normalizer.Name(main), Normalizer.Name(variant));

    [Theory]
    [InlineData("TR5 De Esser", "去齿音")]
    [InlineData("BREVERB 2-64", "混响")]
    [InlineData("Guitar Rig 6", "吉他")]
    public void KeywordsCoverCommonVst2Names(string name, string tag) =>
        Assert.Contains(tag, Classifier.ClassifyWithoutSeed(name, "Fx", false).Tags);
}

public class ScanScopeTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory().FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, true);
    }

    private PluginDatabase OpenDatabase() => new(Path.Combine(_folder, "test.db"));

    private static FileScanResult Scan(string path, PluginFormat format, string name) => new(
        path, format, Arch.X64, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ScanStatus.FileInfoOnly, null,
        [new PluginClassInfo(name, "", "", "", null, InfoSource.FileName)]);

    private static ScanService Service(PluginDatabase database) =>
        new(database, new ScannerRunner(Path.Combine(AppContext.BaseDirectory, "no-scanner.exe"), TimeSpan.FromSeconds(1)));

    [Fact]
    public async Task FormatScopeLeavesOtherFormatsAlone()
    {
        using var database = OpenDatabase();
        var vst3 = Path.Combine(_folder, "gone", "A.vst3");
        var vst2 = Path.Combine(_folder, "gone", "B.dll");
        database.SaveFileScan(Scan(vst3, PluginFormat.Vst3, "A"));
        database.SaveFileScan(Scan(vst2, PluginFormat.Vst2, "B"));

        var empty = Directory.CreateDirectory(Path.Combine(_folder, "empty")).FullName;
        var summary = await Service(database).ScanAsync(
            [(empty, PluginFormat.Vst2)], new ScanOptions(Formats: [PluginFormat.Vst2]), null, CancellationToken.None);

        Assert.Equal(1, summary.Removed);
        var left = database.LoadFileScans();
        Assert.True(left.ContainsKey(vst3), "只扫 VST2 时不能动 VST3 的结果");
        Assert.False(left.ContainsKey(vst2));
    }

    [Fact]
    public async Task OfflineFolderKeepsItsPlugins()
    {
        using var database = OpenDatabase();
        var offline = Path.Combine(_folder, "硬盘没插");
        var file = Path.Combine(offline, "B.dll");
        database.SaveFileScan(Scan(file, PluginFormat.Vst2, "B"));

        var summary = await Service(database).ScanAsync(
            [(offline, PluginFormat.Vst2)], new ScanOptions(Formats: [PluginFormat.Vst2]), null, CancellationToken.None);

        Assert.Equal(0, summary.Removed);
        Assert.True(database.LoadFileScans().ContainsKey(file));
    }

    [Fact]
    public async Task RemovedFolderForgetsItsPlugins()
    {
        using var database = OpenDatabase();
        var file = Path.Combine(_folder, "用户移除的", "B.dll");
        database.SaveFileScan(Scan(file, PluginFormat.Vst2, "B"));

        var summary = await Service(database).ScanAsync(
            [], new ScanOptions(Formats: [PluginFormat.Vst2]), null, CancellationToken.None);

        Assert.Equal(1, summary.Removed);
    }
}

public class SettingsStorageTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory().FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void FoldersAndSettingsSurviveReopen()
    {
        var path = Path.Combine(_folder, "test.db");
        using (var database = new PluginDatabase(path))
        {
            database.AddVst2Folder(P(@"F:\vst64\"));
            database.AddVst2Folder(P(@"f:\VST64"));
            database.AddVst2Folder(P(@"E:\More"));
            database.SetSetting("theme", "paper");
            database.SetSetting("theme", "mist");
        }
        using (var database = new PluginDatabase(path))
        {
            Assert.Equal([P(@"E:\More"), P(@"F:\vst64")], database.LoadVst2Folders());
            Assert.Equal("mist", database.GetSetting("theme"));
            Assert.Null(database.GetSetting("nothing"));
            database.RemoveVst2Folder(P(@"F:\vst64"));
            Assert.Equal([P(@"E:\More")], database.LoadVst2Folders());
        }
    }

    [Fact]
    public void ForgettingMissingKeepsPluginsWithUserData()
    {
        using var database = new PluginDatabase(Path.Combine(_folder, "test.db"));
        database.SaveMerged(PluginMerger.Merge(new[] { "Plain", "Noted", "Listed", "Blocked", "Reasoned", "Present" }.Select(name => new FileScanResult(
            P(@"F:\vst64\") + name + ".dll", PluginFormat.Vst2, Arch.X64, 1, DateTime.UnixEpoch, ScanStatus.FileInfoOnly, null,
            [new PluginClassInfo(name, "", "", "", false, InfoSource.ModuleInfo)]))));
        var rows = database.LoadPlugins().ToDictionary(p => p.Name);
        database.SaveUserData(rows["Noted"].Id, rows["Noted"].User with { Note = "好用" });
        database.SaveUserData(rows["Blocked"].Id, rows["Blocked"].User with { Hidden = true });
        // 只剩移入原因（旧版本卸载时清掉了垃圾桶标记）的也要留着。
        database.SaveUserData(rows["Reasoned"].Id, rows["Reasoned"].User with { BlacklistReason = "太吃 CPU" });
        var list = database.AddFavoriteList("常用");
        database.SetFavoriteItems(list.Id, [rows["Listed"].Key]);

        database.SaveMerged(PluginMerger.Merge([new FileScanResult(
            @"F:st64Present.dll", PluginFormat.Vst2, Arch.X64, 1, DateTime.UnixEpoch, ScanStatus.FileInfoOnly, null,
            [new PluginClassInfo("Present", "", "", "", false, InfoSource.ModuleInfo)])]));

        Assert.Equal(1, database.DeleteMissingWithoutUserData());
        var left = database.LoadPlugins().Select(p => p.Name).Order().ToList();
        Assert.Equal(["Blocked", "Listed", "Noted", "Present", "Reasoned"], left);
    }

    [Fact]
    public void UpgradesVersion5Database()
    {
        var path = Path.Combine(_folder, "old.db");
        using (var database = new PluginDatabase(path))
            database.AddProjectFolder(P(@"D:\Work"));
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE vst2_folders; DROP TABLE app_settings; ALTER TABLE plugins DROP COLUMN mix_json; ALTER TABLE wishes DROP COLUMN license_kind; ALTER TABLE wishes DROP COLUMN activated_on; ALTER TABLE wishes DROP COLUMN activations_left; ALTER TABLE wishes DROP COLUMN target_price; ALTER TABLE wishes DROP COLUMN lowest_price; ALTER TABLE wishes DROP COLUMN category; PRAGMA user_version = 5;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using (var database = new PluginDatabase(path))
        {
            Assert.Equal([P(@"D:\Work")], database.LoadProjectFolders());
            Assert.Empty(database.LoadVst2Folders());
            database.SetSetting("theme", "forest");
            Assert.Equal("forest", database.GetSetting("theme"));
        }
    }
}

public class ThemePaletteTests
{
    public static TheoryData<string> Ids
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var palette in ThemePalettes.All)
                data.Add(palette.Id);
            return data;
        }
    }

    [Fact]
    public void AllPalettesDefineTheSameBrushes()
    {
        var expected = ThemePalettes.Find(null).Colors.Keys.ToHashSet();
        Assert.Equal(21, expected.Count);
        foreach (var palette in ThemePalettes.All)
            Assert.True(expected.SetEquals(palette.Colors.Keys), palette.Id);
    }

    [Fact]
    public void UnknownThemeFallsBackToDefault()
    {
        Assert.Equal(ThemePalettes.DefaultId, ThemePalettes.Find("不存在").Id);
        Assert.Equal(ThemePalettes.DefaultId, ThemePalettes.Find(null).Id);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void TextIsReadable(string id)
    {
        var p = ThemePalettes.Find(id);
        foreach (var background in new[] { "Bg0", "Bg1", "Surface", "SurfaceHover", "SurfaceActive", "Input" })
        {
            Assert.True(ThemePalettes.Contrast(p["Text"], p[background]) >= 7, $"Text on {background}");
            Assert.True(ThemePalettes.Contrast(p["TextDim"], p[background]) >= 4.5, $"TextDim on {background}");
        }
        foreach (var background in new[] { "Bg0", "Bg1", "Surface", "SurfaceHover" })
        {
            Assert.True(ThemePalettes.Contrast(p["Muted"], p[background]) >= 4, $"Muted on {background}");
            Assert.True(ThemePalettes.Contrast(p["Accent"], p[background]) >= 3, $"Accent on {background}");
            Assert.True(ThemePalettes.Contrast(p["Danger"], p[background]) >= 3, $"Danger on {background}");
            Assert.True(ThemePalettes.Contrast(p["Good"], p[background]) >= 3, $"Good on {background}");
        }
        foreach (var accent in new[] { "Accent", "AccentHover", "AccentPressed" })
            Assert.True(ThemePalettes.Contrast(p["OnAccent"], p[accent]) >= 4.5, $"OnAccent on {accent}");
        Assert.True(ThemePalettes.Contrast(p["Text"], p["AccentSoft"]) >= 7, "Text on AccentSoft");
        Assert.True(ThemePalettes.Contrast(p["Accent"], p["AccentSoft"]) >= 3, "Accent on AccentSoft");
        Assert.True(ThemePalettes.Contrast(p["Danger"], p["DangerSoft"]) >= 3, "Danger on DangerSoft");
        Assert.True(ThemePalettes.Contrast(p["Good"], p["GoodSoft"]) >= 3, "Good on GoodSoft");
    }
}
