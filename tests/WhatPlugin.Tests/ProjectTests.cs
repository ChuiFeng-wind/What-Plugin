using System.IO.Compression;
using System.Text;
using WhatPlugin.Core;

using static WhatPlugin.Tests.TestPaths;

namespace WhatPlugin.Tests;

public sealed class ProjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-p-" + Guid.NewGuid().ToString("N"));

    public ProjectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "t.db");

    // ---------- 假的 Cubase 工程：字节结构和真实 .cpr 一致 ----------

    private const string Guid1 = "5653544651337066616266696C746572";

    private static byte[] Key(string key) => [.. BigEndian(key.Length + 1), .. Encoding.ASCII.GetBytes(key), 0];

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Field(string key, byte[] text, bool bom = true)
    {
        byte[] suffix = bom ? [0, 0xEF, 0xBB, 0xBF] : [0];
        return [.. Key(key), 0, 8, .. BigEndian(text.Length + suffix.Length), .. text, .. suffix];
    }

    private static byte[] Field(string key, string text, bool bom = true) => Field(key, Encoding.UTF8.GetBytes(text), bom);

    private static byte[] Record(byte[] name, string? original = null, string slot = "Slot", string pluginClass = "VstCtrlInternalEffect", string guid = Guid1)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Key(slot));
        bytes.AddRange(new byte[] { 0, 2, 0, 6, 0, 0, 0, 6 });
        bytes.AddRange(Key("Plugin isA"));
        bytes.AddRange(new byte[] { 0, 8 });
        bytes.AddRange(BigEndian(pluginClass.Length + 1));
        bytes.AddRange(Encoding.ASCII.GetBytes(pluginClass));
        bytes.Add(0);
        bytes.AddRange(Key("Plugin"));
        bytes.AddRange(new byte[] { 0, 2, 0, 6, 0, 0, 0, 0x16 });
        bytes.AddRange(Key("Plugin UID"));
        bytes.AddRange(new byte[] { 0, 2, 0, 6, 0, 0, 0, 1 });
        bytes.AddRange(Field("GUID", guid));
        bytes.AddRange(Field("Plugin Name", name));
        if (original != null)
            bytes.AddRange(Field("Original Plugin Name", original));
        bytes.AddRange(Key("Audio Input Count"));
        bytes.AddRange(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0, 2 });
        return bytes.ToArray();
    }

    private static byte[] Record(string name, string? original = null, string slot = "Slot", string pluginClass = "VstCtrlInternalEffect") =>
        Record(Encoding.UTF8.GetBytes(name), original, slot, pluginClass);

    private static byte[] Cpr(params byte[][] records) =>
        [.. "RIFF"u8, 0, 0, 0, 0, 0, 0, 0, 0, .. "ROOT"u8, .. records.SelectMany(r => r)];

    private static IReadOnlyList<ProjectPluginUse> ParseCpr(byte[] bytes) =>
        ProjectParser.Parse(ProjectHost.Cubase, new MemoryStream(bytes));

    [Fact]
    public void CubaseReadsInsertsAndCountsInstances()
    {
        var uses = ParseCpr(Cpr(Record("FabFilter Pro-Q 3"), Record("FabFilter Pro-Q 3"), Record("API-2500 Stereo")));

        Assert.Equal(2, uses.Count);
        Assert.Equal(2, uses.Single(u => u.Name == "FabFilter Pro-Q 3").Instances);
        Assert.Equal(Guid1, uses[1].Uid);
    }

    [Fact]
    public void CubaseUsesOriginalNameWhenTrackWasRenamed()
    {
        var uses = ParseCpr(Cpr(Record("主唱压缩", original: "FabFilter Pro-C 2")));

        Assert.Equal("FabFilter Pro-C 2", Assert.Single(uses).Name);
    }

    [Fact]
    public void CubaseDropsInstrumentSlots()
    {
        var uses = ParseCpr(Cpr(Record("violin 1", original: "Kontakt", slot: "Synth Slot"), Record("Pro-R")));

        Assert.Equal("Pro-R", Assert.Single(uses).Name);
    }

    [Fact]
    public void CubaseDropsInstrumentsInOldSynthRack()
    {
        // 老工程：Synth Rack 里是普通 Slot。第一个紧跟容器名，
        // 之后的紧跟上一个音源的输出通道（中间夹着它自己的插入效果）。
        byte[] channelId(string id) => [.. Key("IDString"), 0, 8, .. BigEndian(id.Length + 1), .. Encoding.ASCII.GetBytes(id), 0];
        var uses = ParseCpr(Cpr(
            Key("Synth Rack"), new byte[40],
            Record("HALion Sonic SE"),
            new byte[500], Key("InsertFolder"), new byte[300], Record("Rack Channel Insert"),
            new byte[500], channelId("Synth 2"), new byte[70],
            Record("Second Rack Synth"),
            new byte[500], Key("InsertFolder"), new byte[300], Record("Pro-R")));

        Assert.Equal(["Pro-R", "Rack Channel Insert"], uses.Select(u => u.Name).ToArray());
    }

    [Fact]
    public void AudioChannelIdDoesNotLookLikeSynthRack()
    {
        byte[] channelId = [.. Key("IDString"), 0, 8, .. BigEndian(6), .. "Audio"u8, 0];

        Assert.Equal("Pro-R", Assert.Single(ParseCpr(Cpr(channelId, new byte[70], Record("Pro-R")))).Name);
    }

    [Fact]
    public void CubaseDropsChannelStripModules()
    {
        var uses = ParseCpr(Cpr(Record("EQ", pluginClass: "VstCtrlEQ"), Record("Pro-R")));

        Assert.Equal("Pro-R", Assert.Single(uses).Name);
    }

    [Fact]
    public void CubaseDropsRecordsWithoutPluginClass()
    {
        // 声像器和输入滤波前面没有 Plugin isA。
        byte[] panner =
        [
            .. Key("Plugin UID"), 0, 2, 0, 6, 0, 0, 0, 1,
            .. Field("GUID", Guid1),
            .. Field("Plugin Name", "Standard Panner"),
        ];

        Assert.Empty(ParseCpr(Cpr(panner)));
    }

    [Fact]
    public void CubaseDecodesGbkNames()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var uses = ParseCpr(Cpr(Record(Encoding.GetEncoding(936).GetBytes("某中文插件"))));

        Assert.Equal("某中文插件", Assert.Single(uses).Name);
    }

    [Fact]
    public void CubaseAcceptsLargeFileVariant()
    {
        byte[] bytes = [.. "RIF2"u8, 0, 0, 0, 0, 0, 0, 0, 0, .. "NUND"u8, .. Record("Pro-R")];

        Assert.Equal("Pro-R", Assert.Single(ParseCpr(bytes)).Name);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(-300)]
    [InlineData(-8192)]
    [InlineData(-9000)]
    [InlineData(0)]
    public void CubaseFindsRecordsAcrossReadChunks(int offsetFromBoundary)
    {
        // 读取分块是 16 MB。记录正好落在分块边界附近时不能丢，也不能算两次。
        const int chunk = 16 * 1024 * 1024;
        var padding = new byte[chunk + offsetFromBoundary - 16];
        Array.Fill(padding, (byte)0x55);
        var bytes = Cpr(padding, Record("Pro-R"), new byte[chunk], Record("Pro-L 2"));

        var uses = ParseCpr(bytes);

        Assert.Equal(1, uses.Single(u => u.Name == "Pro-R").Instances);
        Assert.Equal(1, uses.Single(u => u.Name == "Pro-L 2").Instances);
    }

    [Fact]
    public void CubaseRejectsOtherFiles()
    {
        Assert.Throws<InvalidDataException>(() => ParseCpr("not a project at all"u8.ToArray()));
    }

    [Fact]
    public void CubaseSurvivesTruncatedRecord()
    {
        var record = Record("Pro-R");
        var bytes = Cpr(record[..^40]);

        Assert.Empty(ParseCpr(bytes));
    }

    // ---------- 其它宿主：样本是按公开资料构造的 ----------

    private static byte[] Song(params (string Name, string Content)[] entries)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }

    [Fact]
    public void StudioOneReadsThirdPartyEffectsOnly()
    {
        var song = Song(
            ("Devices/audiomixer.xml", """
                <AudioMixer>
                  <Attributes x:id="classInfo" name="Pro-Q 3" classID="{ABC}" subCategory="VST3/Fx/EQ"/>
                  <Attributes x:id="classInfo" name="Pro-Q 3" classID="{ABC}" subCategory="VST3/Fx/EQ"/>
                  <Attributes x:id="classInfo" name="Pro EQ" classID="{DEF}" subCategory="(Native)"/>
                  <Attributes x:id="classInfo" name="Some Synth" classID="{111}" subCategory="VST3/Synth"/>
                  <Attributes x:id="other" name="ignored"/>
                </AudioMixer>
                """),
            ("Devices/audiosynthfolder.xml", """<Folder><Attributes name="Kontakt" subCategory="VST2/Fx"/></Folder>"""));

        var uses = ProjectParser.Parse(ProjectHost.StudioOne, new MemoryStream(song));

        var use = Assert.Single(uses);
        Assert.Equal("Pro-Q 3", use.Name);
        Assert.Equal(2, use.Instances);
    }

    [Fact]
    public void StudioOneReadsRealInsertLayout()
    {
        // 结构取自 Studio One 5.1 保存的真实工程：名字跨行，自带插件的分类是 "(Native)/Dynamics"。
        var song = Song(("Devices/audiomixer.xml", """
            <AudioMixer xmlns:x="internal://schemas/x">
              <ChannelGroup name="AudioOutput">
                <Attributes x:id="Inserts">
                  <Attributes name="FX01">
                    <Attributes x:id="deviceData" name="Limiter"/>
                    <Attributes x:id="ghostData" presetType="fxpreset">
                      <Attributes x:id="classInfo" classID="{61B18D53-26FA-4220-8614-89944A1990EC}" name="Limiter" category="AudioEffect"
                                  subCategory="(Native)/Dynamics"/>
                    </Attributes>
                  </Attributes>
                </Attributes>
              </ChannelGroup>
              <ChannelGroup name="AudioTrack" flags="1">
                <Attributes x:id="Inserts">
                  <Attributes name="FX01">
                    <UID x:id="deviceClassID" uid="{72C4DB71-7A4D-459A-B97E-51745D84B39D}"/>
                    <Attributes x:id="deviceData" name="FabFilter Pro-Q 3"/>
                    <Attributes x:id="ghostData" presetType="vstpreset">
                      <Attributes x:id="classInfo" classID="{72C4DB71-7A4D-459A-B97E-51745D84B39D}" name="FabFilter Pro-Q 3"
                                  category="AudioEffect" subCategory="VST3/EQ"/>
                    </Attributes>
                  </Attributes>
                </Attributes>
              </ChannelGroup>
            </AudioMixer>
            """));

        var use = Assert.Single(ProjectParser.Parse(ProjectHost.StudioOne, new MemoryStream(song)));
        Assert.Equal("FabFilter Pro-Q 3", use.Name);
        Assert.Equal("{72C4DB71-7A4D-459A-B97E-51745D84B39D}", use.Uid);
    }

    [Fact]
    public void StudioOneRejectsZipWithoutDevices()
    {
        var zip = Song(("readme.txt", "hello"));

        Assert.Throws<InvalidDataException>(() => ProjectParser.Parse(ProjectHost.StudioOne, new MemoryStream(zip)));
    }

    [Fact]
    public void StudioOneRejectsNonZip()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProjectParser.Parse(ProjectHost.StudioOne, new MemoryStream("not a zip file"u8.ToArray())));
    }

    [Fact]
    public void ReaperReadsEffectsAndSkipsInstrumentsAndCockos()
    {
        const string rpp = """
            <REAPER_PROJECT 0.1 "7.0" 1700000000
              <TRACK
                <FXCHAIN
                  <VST "VST3: Pro-Q 3 (FabFilter)" "FabFilter Pro-Q 3.vst3" 0 "" 1 ""
                  >
                  <VST "VST: API-2500 Stereo (Waves)" WaveShell.dll 0 "我的压缩" 2 ""
                  >
                  <VST "VSTi: Kontakt (Native Instruments)" Kontakt.dll 0 "" 3 ""
                  >
                  <VST "VST: ReaEQ (Cockos)" reaeq.dll 0 "" 4 ""
                  >
                  <CLAP "CLAP: Surge FX (Surge)" surge.clap 0 ""
                  >
                  <JS "utility/volume" ""
                  >
                >
              >
            >
            """;

        var uses = ProjectParser.Parse(ProjectHost.Reaper, new MemoryStream(Encoding.UTF8.GetBytes(rpp)));

        Assert.Equal(["API-2500 Stereo", "Pro-Q 3", "Surge FX"], uses.Select(u => u.Name).ToArray());
    }

    [Fact]
    public void ReaperRejectsOtherText()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProjectParser.Parse(ProjectHost.Reaper, new MemoryStream("hello"u8.ToArray())));
    }

    private static byte[] Gzip(string text)
    {
        var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return memory.ToArray();
    }

    [Fact]
    public void AbletonReadsPluginNames()
    {
        var als = Gzip("""
            <?xml version="1.0" encoding="UTF-8"?>
            <Ableton MajorVersion="5">
              <PluginDevice><PluginDesc><Vst3PluginInfo Id="0"><Name Value="Pro-Q 3"/></Vst3PluginInfo></PluginDesc></PluginDevice>
              <PluginDevice><PluginDesc><VstPluginInfo Id="0"><Path Value="x"/><PlugName Value="H-Delay Stereo"/></VstPluginInfo></PluginDesc></PluginDevice>
              <PluginDevice><PluginDesc><Vst3PluginInfo Id="1"><Name Value="Pro-Q 3"/></Vst3PluginInfo></PluginDesc></PluginDevice>
              <Eq8><Name Value="NotAPlugin"/></Eq8>
            </Ableton>
            """);

        var uses = ProjectParser.Parse(ProjectHost.Ableton, new MemoryStream(als));

        Assert.Equal(2, uses.Count);
        Assert.Equal(2, uses.Single(u => u.Name == "Pro-Q 3").Instances);
        Assert.Contains(uses, u => u.Name == "H-Delay Stereo");
    }

    [Fact]
    public void AbletonRejectsOtherXml()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProjectParser.Parse(ProjectHost.Ableton, new MemoryStream(Gzip("<Other/>"))));
        Assert.ThrowsAny<Exception>(() =>
            ProjectParser.Parse(ProjectHost.Ableton, new MemoryStream("plain"u8.ToArray())));
    }

    [Theory]
    [InlineData("a.cpr", ProjectHost.Cubase)]
    [InlineData("A.NPR", ProjectHost.Cubase)]
    [InlineData("a.song", ProjectHost.StudioOne)]
    [InlineData("a.rpp", ProjectHost.Reaper)]
    [InlineData("a.als", ProjectHost.Ableton)]
    public void HostIsChosenByExtension(string file, ProjectHost host) => Assert.Equal(host, ProjectParser.HostOf(file));

    [Theory]
    [InlineData("a.bak")]
    [InlineData("a.rpp-bak")]
    [InlineData("a.autosave")]
    [InlineData("a.ptx")]
    public void BackupsAndUnknownFilesAreNotProjects(string file) => Assert.Null(ProjectParser.HostOf(file));

    // ---------- 名称匹配 ----------

    private static PluginRow Row(string name, string vendor = "", PluginKind kind = PluginKind.Effect, bool missing = false, bool hidden = false, PluginFormat format = PluginFormat.Vst3, string? key = null) =>
        new(name.GetHashCode(), key ?? Normalizer.Name(name), name, vendor, kind, [], ClassificationSource.None, missing,
            missing ? [] : [new MergedFile("c:\\" + name, format, Arch.X64, "1", "", ScanStatus.Loaded, null)],
            PluginUserData.Empty with { Hidden = hidden });

    [Theory]
    [InlineData("API-2500 Stereo", "API-2500")]
    [InlineData("RVerb Mono/Stereo", "RVerb")]
    [InlineData("FabFilter Pro-DS (Mono)", "FabFilter Pro-DS")]
    [InlineData("soothe2_x64", "soothe2")]
    [InlineData("TRAX_x64", "TRAX")]
    [InlineData("Nexus.64", "Nexus")]
    [InlineData("Kickstart-64bit", "Kickstart")]
    [InlineData("WhisperOfLoong(64)", "WhisperOfLoong")]
    public void MatcherHandlesFormatAndChannelSpellings(string projectName, string installed)
    {
        var matcher = new PluginNameMatcher([Row(installed)]);

        Assert.Equal(installed, matcher.Find(projectName)?.Name);
    }

    [Fact]
    public void MatcherHandlesVendorPrefixBothWays()
    {
        var matcher = new PluginNameMatcher([Row("FabFilter Pro-Q 3", "FabFilter"), Row("Saturn 2", "FabFilter")]);

        Assert.Equal("FabFilter Pro-Q 3", matcher.Find("Pro-Q 3")?.Name);
        Assert.Equal("Saturn 2", matcher.Find("FabFilter Saturn 2")?.Name);
    }

    [Fact]
    public void MatcherDoesNotGuessBetweenDifferentPlugins()
    {
        var matcher = new PluginNameMatcher([Row("FabFilter Pro-Q 3", "FabFilter")]);

        Assert.Null(matcher.Find("Pro-Q 4"));
        Assert.Null(matcher.Find("Q"));
    }

    // ---------- 分组 ----------

    [Theory]
    [InlineData(@"D:\p\新歌-03.cpr", "新歌")]
    [InlineData(@"D:\p\新歌 (2).cpr", "新歌")]
    [InlineData(@"D:\p\新歌 - 副本.cpr", "新歌")]
    [InlineData(@"D:\p\新歌_v2.cpr", "新歌")]
    [InlineData(@"D:\p\新歌1.cpr", "新歌")]
    [InlineData(@"D:\p\0107.cpr", "0107")]
    [InlineData(@"D:\p\111.cpr", "111")]
    [InlineData(@"D:\p\young in.cpr", "young in")]
    public void SongNameRemovesVersionSuffix(string path, string song) => Assert.Equal(song, ProjectReport.SongName(P(path)));

    [Fact]
    public void OwnProjectFolderGroupsDifferentFileNames()
    {
        Assert.Equal(
            ProjectReport.SongKey(P(@"D:\霁川\霁川 混音.cpr"), ownFolder: true),
            ProjectReport.SongKey(P(@"D:\霁川\霁川 音量平衡.cpr"), ownFolder: true));
        Assert.NotEqual(
            ProjectReport.SongKey(P(@"D:\杂项\一.cpr")),
            ProjectReport.SongKey(P(@"D:\杂项\二.cpr")));
        Assert.Equal("霁川", ProjectReport.SongDisplayName(P(@"D:\霁川\混音.cpr"), ownFolder: true));
    }

    [Fact]
    public void OwnFolderIsDetectedFromHostSubfolders()
    {
        var project = Path.Combine(_dir, "歌", "歌.cpr");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        Assert.False(ProjectReport.IsOwnProjectFolder(project));

        Directory.CreateDirectory(Path.Combine(_dir, "歌", "Audio"));
        Assert.True(ProjectReport.IsOwnProjectFolder(project));
    }

    // ---------- 报告 ----------

    private static ProjectFileRecord File1(string path, DateTime modified, params string[] names) =>
        new(0, path, ProjectParser.HostOf(path)!.Value, 1, modified, ProjectScanStatus.Ok, null, ProjectParser.Version,
            names.Select(n => new ProjectPluginUse(n, null, 1)).ToList());

    private static readonly DateTime Day1 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReportSeparatesInstalledMissingAndStock()
    {
        var report = ProjectReport.Build(
            [File1(P(@"D:\p\歌.cpr"), Day1, "API-2500 Stereo", "Not Installed FX", "Compressor", "StudioEQ")],
            [Row("API-2500", "Waves")]);

        var project = Assert.Single(report.Projects);
        Assert.Equal(1, project.MissingCount);
        Assert.Equal(2, project.EffectCount);
        Assert.Equal(ProjectPluginState.Missing, project.Plugins[0].State);
        Assert.Equal("Not Installed FX", project.Plugins[0].Name);
        Assert.Equal(ProjectPluginState.Installed, project.Plugins[1].State);
        Assert.Equal("API-2500", project.Plugins[1].Name);
        Assert.Equal(2, project.Plugins.Count(p => p.State == ProjectPluginState.Stock));
    }

    [Fact]
    public void InstalledPluginWinsOverStockNameOfSameSpelling()
    {
        var report = ProjectReport.Build([File1(P(@"D:\p\歌.cpr"), Day1, "Compressor")], [Row("Compressor", "Some Vendor")]);

        Assert.Equal(ProjectPluginState.Installed, report.Projects[0].Plugins[0].State);
        Assert.True(report.Usage.ContainsKey(Normalizer.Name("Compressor")));
    }

    [Fact]
    public void StockListDoesNotApplyToOtherHosts()
    {
        var report = ProjectReport.Build([File1(P(@"D:\p\歌.rpp"), Day1, "StudioEQ")], []);

        Assert.Equal(ProjectPluginState.Missing, report.Projects[0].Plugins[0].State);
    }

    [Fact]
    public void InstrumentsNeverAppearInReport()
    {
        var report = ProjectReport.Build(
            [File1(P(@"D:\p\歌.als"), Day1, "Installed Synth", "Omnisphere", "Real FX")],
            [Row("Installed Synth", kind: PluginKind.Instrument), Row("Real FX")]);

        var line = Assert.Single(report.Projects[0].Plugins);
        Assert.Equal("Real FX", line.Name);
        Assert.Empty(report.Missing);
        Assert.Single(report.Usage);
    }

    [Fact]
    public void UninstalledPluginCountsAsMissing()
    {
        var report = ProjectReport.Build([File1(P(@"D:\p\歌.cpr"), Day1, "Old FX")], [Row("Old FX", missing: true)]);

        Assert.Equal(1, report.Projects[0].MissingCount);
        Assert.Equal("Old FX", Assert.Single(report.Missing).Name);
    }

    [Fact]
    public void MissingLooksOnlyAtLatestVersion()
    {
        // 老版本用过的插件在新版本里换掉了，这首歌现在打开不缺它。
        var report = ProjectReport.Build(
            [File1(P(@"D:\p\歌-01.cpr"), Day1, "Old FX"), File1(P(@"D:\p\歌-02.cpr"), Day2, "New FX")],
            [Row("New FX")]);

        var project = Assert.Single(report.Projects);
        Assert.Equal(2, project.Versions);
        Assert.Equal(0, project.MissingCount);
        Assert.Empty(report.Missing);
        Assert.EndsWith("歌-02.cpr", project.Path);
    }

    [Fact]
    public void UsageCountsSongsNotVersionsAndKeepsLatestDate()
    {
        var plugin = Row("Pro-R");
        var report = ProjectReport.Build(
            [
                File1(P(@"D:\p\歌-01.cpr"), Day2, "Pro-R"),
                File1(P(@"D:\p\歌-02.cpr"), Day1, "Pro-R"),
                File1(P(@"D:\p\另一首.cpr"), Day1, "Pro-R"),
            ],
            [plugin]);

        var usage = report.Usage[plugin.Key];
        Assert.Equal(2, usage.Projects);
        Assert.Equal(Day2, usage.LastUsedUtc);
        Assert.Equal("歌", usage.ProjectNames[0]);
    }

    [Fact]
    public void UsageIncludesPluginOnlyInOlderVersion()
    {
        var plugin = Row("Pro-R");
        var report = ProjectReport.Build(
            [File1(P(@"D:\p\歌-01.cpr"), Day1, "Pro-R"), File1(P(@"D:\p\歌-02.cpr"), Day2, "Other")],
            [plugin]);

        Assert.Equal(Day1, report.Usage[plugin.Key].LastUsedUtc);
    }

    [Fact]
    public void IgnoredNameLeavesMissingList()
    {
        var ignored = new HashSet<string> { ProjectReport.IgnoreKey("Custom Thing Stereo") };
        var report = ProjectReport.Build([File1(P(@"D:\p\歌.cpr"), Day1, "Custom Thing Stereo")], [], ignored);

        Assert.Equal(0, report.Projects[0].MissingCount);
        Assert.Equal(ProjectPluginState.Ignored, report.Projects[0].Plugins[0].State);
        Assert.Empty(report.Missing);
    }

    [Fact]
    public void MissingSummaryMergesChannelVariants()
    {
        var report = ProjectReport.Build(
            [File1(P(@"D:\p\一.cpr"), Day1, "Gone FX Stereo"), File1(P(@"D:\p\二.cpr"), Day2, "Gone FX Mono")],
            []);

        var missing = Assert.Single(report.Missing);
        Assert.Equal(2, missing.Projects.Count);
    }

    [Fact]
    public void FailedProjectIsReportedAndKeepsItsError()
    {
        var failed = new ProjectFileRecord(0, P(@"D:\p\坏.cpr"), ProjectHost.Cubase, 1, Day1, ProjectScanStatus.Failed, "坏文件", ProjectParser.Version, []);

        var project = Assert.Single(ProjectReport.Build([failed], []).Projects);

        Assert.Equal(ProjectScanStatus.Failed, project.Status);
        Assert.Equal("坏文件", project.Error);
    }

    // ---------- 闲置 ----------

    [Fact]
    public void IdleListsNeverUsedAndLongUnusedOnly()
    {
        var never = Row("Never");
        var old = Row("Old");
        var recent = Row("Recent");
        var usage = new Dictionary<string, PluginUsage>
        {
            [old.Key] = new(1, 1, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), ["歌"]),
            [recent.Key] = new(1, 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), ["歌"]),
        };

        var idle = ProjectReport.FindIdle([never, old, recent], usage, 12, new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["Never", "Old"], idle.Select(i => i.Plugin.Name).ToArray());
        Assert.Equal(IdleReason.NeverUsed, idle[0].Reason);
        Assert.Equal(IdleReason.LongUnused, idle[1].Reason);
    }

    [Fact]
    public void IdleSkipsPluginsItCannotJudge()
    {
        var idle = ProjectReport.FindIdle(
            [
                Row("Blacklisted", hidden: true),
                Row("Synth", kind: PluginKind.Instrument),
                Row("Gone", missing: true),
                Row("Pro Tools Only", format: PluginFormat.Aax),
                Row("WaveShell1-VST3 14.0"),
                Row("Candidate"),
            ],
            new Dictionary<string, PluginUsage>(), 12, Day2);

        Assert.Equal("Candidate", Assert.Single(idle).Plugin.Name);
    }

    [Fact]
    public void SuggestionsSkipListedAndHiddenAndRankByUse()
    {
        var a = Row("A");
        var b = Row("B");
        var c = Row("C", hidden: true);
        var d = Row("D");
        var usage = new Dictionary<string, PluginUsage>
        {
            [a.Key] = new(1, 1, Day1, []),
            [b.Key] = new(5, 5, Day1, []),
            [c.Key] = new(9, 9, Day1, []),
            [d.Key] = new(7, 7, Day1, []),
        };

        var suggested = ProjectReport.SuggestForList([a, b, c, d], usage, new HashSet<string> { d.Key });

        Assert.Equal(["B", "A"], suggested.Select(s => s.Plugin.Name).ToArray());
    }

    // ---------- 扫描 ----------

    private string WriteProject(string relative, byte[] bytes)
    {
        var path = Path.Combine(_dir, "projects", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string ProjectRoot => Path.Combine(_dir, "projects");

    [Fact]
    public void ScanNeverChangesProjectFiles()
    {
        var path = WriteProject("歌.cpr", Cpr(Record("Pro-R")));
        var before = File.ReadAllBytes(path);
        var modified = new DateTime(2024, 5, 5, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);

        using var database = new PluginDatabase(DbPath);
        database.AddProjectFolder(ProjectRoot);
        ProjectScanner.Scan(database);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void ScanIsIncrementalAndPicksUpChanges()
    {
        var path = WriteProject("歌.cpr", Cpr(Record("Pro-R")));
        using var database = new PluginDatabase(DbPath);
        database.AddProjectFolder(ProjectRoot);

        var first = ProjectScanner.Scan(database);
        Assert.Equal(1, first.Parsed);

        var second = ProjectScanner.Scan(database);
        Assert.Equal(0, second.Parsed);
        Assert.Equal(1, second.Unchanged);

        File.WriteAllBytes(path, Cpr(Record("Pro-R"), Record("Pro-L 2")));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var third = ProjectScanner.Scan(database);
        Assert.Equal(1, third.Parsed);
        Assert.Equal(2, Assert.Single(database.LoadProjectFiles()).Plugins.Count);
    }

    [Fact]
    public void ScanSkipsBackupsAutosavesAndTemplates()
    {
        WriteProject("歌.cpr", Cpr(Record("Pro-R")));
        WriteProject("歌.bak", Cpr(Record("Pro-R")));
        WriteProject(Path.Combine("Auto Saves", "歌-01.cpr"), Cpr(Record("Pro-R")));
        WriteProject(Path.Combine("History", "歌.song"), Song(("Devices/audiomixer.xml", "<a/>")));
        WriteProject(Path.Combine("Project Templates", "模板.cpr"), Cpr(Record("Pro-R")));

        Assert.Single(ProjectScanner.Discover([ProjectRoot]));
    }

    [Fact]
    public void ScanRecordsFailureWithoutStopping()
    {
        WriteProject("坏.cpr", "garbage file"u8.ToArray());
        WriteProject("好.cpr", Cpr(Record("Pro-R")));
        using var database = new PluginDatabase(DbPath);
        database.AddProjectFolder(ProjectRoot);

        var summary = ProjectScanner.Scan(database);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Parsed);
        var files = database.LoadProjectFiles();
        Assert.Contains(files, f => f.Status == ProjectScanStatus.Failed && f.Error != null);
    }

    [Fact]
    public void ScanForgetsDeletedProjectsAndRemovedFoldersButNotFiles()
    {
        var gone = WriteProject("没了.cpr", Cpr(Record("Pro-R")));
        var kept = WriteProject(Path.Combine("子目录", "还在.cpr"), Cpr(Record("Pro-R")));
        using var database = new PluginDatabase(DbPath);
        database.AddProjectFolder(ProjectRoot);
        ProjectScanner.Scan(database);

        File.Delete(gone);
        var summary = ProjectScanner.Scan(database);
        Assert.Equal(1, summary.Removed);

        database.RemoveProjectFolder(ProjectRoot);
        ProjectScanner.Scan(database);
        Assert.Empty(database.LoadProjectFiles());
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void CancelledScanKeepsExistingRecords()
    {
        WriteProject("歌.cpr", Cpr(Record("Pro-R")));
        using var database = new PluginDatabase(DbPath);
        database.AddProjectFolder(ProjectRoot);
        ProjectScanner.Scan(database);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var summary = ProjectScanner.Scan(database, cancel: cancel.Token);

        Assert.True(summary.Cancelled);
        Assert.Single(database.LoadProjectFiles());
    }

    [Fact]
    public void ProjectDataSurvivesReopen()
    {
        WriteProject("歌.cpr", Cpr(Record("Pro-R"), Record("Pro-R")));
        using (var database = new PluginDatabase(DbPath))
        {
            database.AddProjectFolder(ProjectRoot);
            ProjectScanner.Scan(database);
            database.IgnoreProjectName("Custom FX Stereo");
        }

        using var reopened = new PluginDatabase(DbPath);
        Assert.Equal(ProjectRoot, Assert.Single(reopened.LoadProjectFolders()));
        var file = Assert.Single(reopened.LoadProjectFiles());
        Assert.Equal(2, Assert.Single(file.Plugins).Instances);
        Assert.Contains(ProjectReport.IgnoreKey("Custom FX"), reopened.LoadIgnoredProjectNames());

        reopened.ClearIgnoredProjectNames();
        Assert.Empty(reopened.LoadIgnoredProjectNames());
    }

    // ---------- 常用清单 ----------

    [Fact]
    public void FavoriteListsKeepOrderAcrossReopen()
    {
        using (var database = new PluginDatabase(DbPath))
        {
            var list = database.AddFavoriteList("  人声常用 ");
            Assert.Equal("人声常用", list.Name);
            database.SetFavoriteItems(list.Id, ["b", "a", "b", "c"]);
        }

        using var reopened = new PluginDatabase(DbPath);
        var loaded = Assert.Single(reopened.LoadFavoriteLists());
        Assert.Equal(["b", "a", "c"], loaded.PluginKeys.ToArray());
    }

    [Fact]
    public void FavoriteListNameMustBeUniqueAndNotEmpty()
    {
        using var database = new PluginDatabase(DbPath);
        var first = database.AddFavoriteList("人声");
        var second = database.AddFavoriteList("母带");

        Assert.Throws<ArgumentException>(() => database.AddFavoriteList("   "));
        Assert.Throws<ArgumentException>(() => database.AddFavoriteList("人声"));
        Assert.Throws<ArgumentException>(() => database.RenameFavoriteList(second.Id, "人声"));

        database.RenameFavoriteList(first.Id, "人声 2");
        Assert.Contains(database.LoadFavoriteLists(), l => l.Name == "人声 2");
    }

    [Fact]
    public void DeletingFavoriteListRemovesItemsOnly()
    {
        using var database = new PluginDatabase(DbPath);
        var list = database.AddFavoriteList("人声");
        database.SetFavoriteItems(list.Id, ["a"]);

        database.DeleteFavoriteList(list.Id);

        Assert.Empty(database.LoadFavoriteLists());
        var again = database.AddFavoriteList("人声");
        Assert.Empty(Assert.Single(database.LoadFavoriteLists()).PluginKeys);
        Assert.NotEqual(default, again.Id);
    }

    [Fact]
    public void FavoritesRoundTripThroughBackupAndMerge()
    {
        string json;
        using (var database = new PluginDatabase(DbPath))
        {
            var list = database.AddFavoriteList("人声");
            database.SetFavoriteItems(list.Id, ["a", "b"]);
            json = UserDataBackup.Serialize([], [], database.LoadFavoriteLists());
        }

        using var other = new PluginDatabase(Path.Combine(_dir, "other.db"));
        var existing = other.AddFavoriteList("人声");
        other.SetFavoriteItems(existing.Id, ["z"]);

        Assert.Equal(1, other.ImportFavoriteLists(UserDataBackup.ParseFavorites(json)));

        Assert.Equal(["z", "a", "b"], Assert.Single(other.LoadFavoriteLists()).PluginKeys.ToArray());
    }

    [Fact]
    public void OldBackupWithoutFavoritesStillParses()
    {
        var json = UserDataBackup.Serialize([], []).Replace("\"Favorites\"", "\"Ignored\"");

        Assert.Empty(UserDataBackup.ParseFavorites(json));
    }

    [Fact]
    public void OlderDatabaseUpgradesWithoutLosingPlugins()
    {
        using (var database = new PluginDatabase(DbPath))
        {
            var plugin = new MergedPlugin("k", "N", "V", InfoSource.FileName,
                new Classification(PluginKind.Effect, [], ClassificationSource.None), []);
            database.SaveMerged([plugin]);
        }
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = """
                DROP TABLE project_folders; DROP TABLE project_plugins; DROP TABLE project_files;
                DROP TABLE project_ignored; DROP TABLE favorite_items; DROP TABLE favorite_lists;
                ALTER TABLE plugins DROP COLUMN mix_json; ALTER TABLE wishes DROP COLUMN license_kind; ALTER TABLE wishes DROP COLUMN activated_on; ALTER TABLE wishes DROP COLUMN activations_left; ALTER TABLE wishes DROP COLUMN target_price; ALTER TABLE wishes DROP COLUMN lowest_price; ALTER TABLE wishes DROP COLUMN category; PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
        }

        using var upgraded = new PluginDatabase(DbPath);

        Assert.Single(upgraded.LoadPlugins());
        Assert.Empty(upgraded.LoadFavoriteLists());
        Assert.Empty(upgraded.LoadProjectFiles());
    }
}
