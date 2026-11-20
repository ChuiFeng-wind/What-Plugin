using System.Text;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>
/// 其他 DAW 的 VST2 记录。全部用临时文件夹和假的 Cakewalk 清单，不读这台电脑真实的 AppData 和注册表。
/// </summary>
[Collection("VendorCatalog")]
public sealed class DawRecordsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pv-daw-").FullName;

    public void Dispose()
    {
        VendorCatalog.UseForTests(null);
        Directory.Delete(_root, recursive: true);
    }

    private DawLocations Locations(IEnumerable<CakewalkEntry>? cakewalk = null) => new(
        SteinbergFolder: Path.Combine(_root, "Steinberg"),
        ReaperFolder: Path.Combine(_root, "REAPER"),
        PreSonusFolder: Path.Combine(_root, "PreSonus"),
        FlStudioDatabases: [Path.Combine(_root, "FL", "Plugin database")],
        CakewalkInventory: () => cakewalk ?? []);

    private const string ReaperSample = """
        [vstcache]
        FabFilter_Pro-Q_3.dll=00A1B2C3D4E5F601,1179669297,FabFilter Pro-Q 3 (FabFilter)
        Serum_x64.dll=00112233AABBCC01,1483109208,Serum (Xfer Records)!!!VSTi
        Broken.dll=00FC1FCB31DAD301
        Pro-Q_3.vst3=00ABCDEF00112201,{5653544650513370726F2D7120330000},Pro-Q 3 (FabFilter)
        Odd.dll=0011,42,Odd, Name (Some (Co), Ltd)
        """;

    [Fact]
    public void Reaper_ParsesNameVendorAndInstrumentMarker()
    {
        var records = DawPluginRecords.ParseReaper(new StringReader(ReaperSample));
        Assert.Equal(3, records.Count); // 没扫成功的和 VST3 不收
        var proq = records.Single(r => r.FileName == "FabFilter_Pro-Q_3.dll");
        Assert.Equal(new HostPluginInfo("FabFilter Pro-Q 3", "FabFilter", "", "Fx"), proq.Info);
        Assert.Null(proq.Path);
        var serum = records.Single(r => r.FileName == "Serum_x64.dll");
        Assert.Equal("Instrument", serum.Info.Category);
        Assert.Equal("Xfer Records", serum.Info.Vendor);
        var odd = records.Single(r => r.FileName == "Odd.dll");
        Assert.Equal("Odd, Name (Some", odd.Info.Name.Split(" (Co")[0]);
    }

    [Fact]
    public void Reaper_MatchesByFileNameWithSpacesAsUnderscores()
    {
        Directory.CreateDirectory(Path.Combine(_root, "REAPER"));
        File.WriteAllText(Path.Combine(_root, "REAPER", "reaper-vstplugins64.ini"), ReaperSample);
        var index = HostPluginCache.LoadAll(Locations());
        Assert.Equal(["REAPER"], index.Daws);
        Assert.Equal("Fx", index.Find(@"D:\Any Folder\FabFilter Pro-Q 3.dll")?.Category);
        Assert.Equal("Instrument", index.Find(@"E:\vst\SERUM_X64.DLL")?.Category);
        Assert.Null(index.Find(@"E:\vst\Broken.dll"));
    }

    [Fact]
    public void FlStudio_TypeKeyWinsOverFolder_AndPathOrNameMatch()
    {
        var withType = DawPluginRecords.ParseFlStudio("ps_name=Serum\r\nps_file_vendorname_0=Xfer Records\r\nps_file_type_0=2\r\nps_file_filename_0=C:\\VST\\Serum_x64.dll\r\n", instrumentByFolder: false);
        Assert.NotNull(withType);
        Assert.Equal("Instrument", withType.Info.Category);
        Assert.Equal(@"C:\VST\Serum_x64.dll", withType.Path);

        var byFolder = DawPluginRecords.ParseFlStudio("ps_name=Valhalla Room\nps_file_vendorname_0=Valhalla DSP\n", instrumentByFolder: false);
        Assert.Equal("Fx", byFolder!.Info.Category);
        Assert.Null(byFolder.Path);
        Assert.Null(DawPluginRecords.ParseFlStudio("ps_file_vendorname_0=Nobody\n", false));

        var effects = Path.Combine(_root, "FL", "Plugin database", "Installed", "Effects", "VST");
        var generators = Path.Combine(_root, "FL", "Plugin database", "Installed", "Generators", "VST");
        Directory.CreateDirectory(effects);
        Directory.CreateDirectory(generators);
        File.WriteAllText(Path.Combine(effects, "Valhalla Room.nfo"), "ps_name=ValhallaRoom_x64\nps_file_vendorname_0=Valhalla DSP\n");
        File.WriteAllText(Path.Combine(generators, "Dexed.nfo"), "ps_name=Dexed\nps_file_vendorname_0=Digital Suburban\n");
        var index = HostPluginCache.LoadAll(Locations());
        Assert.Equal("Fx", index.Find(@"D:\VST\ValhallaRoom_x64.dll")?.Category);
        Assert.Equal("Instrument", index.Find(@"D:\VST\Dexed.dll")?.Category);
    }

    [WindowsFact] // Cakewalk 只有 Windows 版，清单里是 Windows 路径
    public void Cakewalk_NeedsPathAndSynthFlag()
    {
        var synth = DawPluginRecords.ParseCakewalk(new CakewalkEntry("c:/program files/vstplugins/dexed.dll",
            new Dictionary<string, object?> { ["isSynth"] = 1, ["Name"] = "Dexed" }));
        Assert.Equal(@"c:\program files\vstplugins\dexed.dll", synth!.Path);
        Assert.Equal("Instrument", synth.Info.Category);
        Assert.Equal("Dexed", synth.Info.Name);

        var fx = DawPluginRecords.ParseCakewalk(new CakewalkEntry("whatever",
            new Dictionary<string, object?> { ["FullPath"] = @"D:\VST\Ampire.dll", ["isSynth"] = 0 }));
        Assert.Equal("Fx", fx!.Info.Category);
        Assert.Equal("Ampire", fx.Info.Name);

        // 没有能认出的音源标记，就不用这一项。
        Assert.Null(DawPluginRecords.ParseCakewalk(new CakewalkEntry("c:/vst/x.dll", new Dictionary<string, object?> { ["isX64"] = 1 })));
        Assert.Null(DawPluginRecords.ParseCakewalk(new CakewalkEntry("not a path", new Dictionary<string, object?> { ["isSynth"] = 1 })));

        var index = HostPluginCache.LoadAll(Locations([new CakewalkEntry("c:/vst/dexed.dll", new Dictionary<string, object?> { ["isSynth"] = 1 })]));
        Assert.Equal("Instrument", index.Find(@"C:\VST\Dexed.dll")?.Category);
        Assert.Equal(["Cakewalk"], index.DawsUsing(@"c:\vst"));
    }

    [Fact]
    public void StudioOne_OnlyElementsWithPathAndExplicitCategory()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <PluginList>
              <Plugin name="Dexed" vendor="Digital Suburban" path="C:\VST\Dexed.dll" category="AudioSynth"/>
              <Plugin name="Room" path="C:\VST\Room.dll"><Attributes category="Fx|Reverb"/></Plugin>
              <Plugin name="Mystery" path="C:\VST\Mystery.dll" color="red"/>
              <Folder path="C:\VST\Folder.dll" type="Folder"/>
            </PluginList>
            """;
        var records = DawPluginRecords.ParseStudioOne(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        Assert.Equal(2, records.Count);
        Assert.Equal("Instrument", records.Single(r => r.Info.Name == "Dexed").Info.Category);
        Assert.Equal("Digital Suburban", records.Single(r => r.Info.Name == "Dexed").Info.Vendor);
        Assert.Equal("Fx", records.Single(r => r.Info.Name == "Room").Info.Category);
    }

    [Fact]
    public void BrokenOrLockedFiles_AreSkipped()
    {
        var x64 = Path.Combine(_root, "PreSonus", "Studio One 6", "x64");
        Directory.CreateDirectory(x64);
        File.WriteAllText(Path.Combine(x64, "Vstplugins.settings"), "<not closed");
        Directory.CreateDirectory(Path.Combine(_root, "REAPER"));
        var ini = Path.Combine(_root, "REAPER", "reaper-vstplugins64.ini");
        File.WriteAllText(ini, ReaperSample);
        // DAW 正在独占写入时也不能出错。
        using (new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var index = HostPluginCache.LoadAll(Locations());
            Assert.Equal(0, index.Count);
        }
        Assert.True(HostPluginCache.LoadAll(Locations()).Count > 0);
    }

    [Fact]
    public void NewestRecordWins_WhenDawsDisagree()
    {
        var cubase = Path.Combine(_root, "Steinberg", "Cubase 14_64");
        Directory.CreateDirectory(cubase);
        var xml = Path.Combine(cubase, "Vst2xPlugin Infos Cubase.xml");
        File.WriteAllText(xml, """
            <?xml version="1.0" encoding="utf-8"?>
            <Vst2xPlugins><list name="Entries" type="list"><item>
              <string name="Group" value="Vst2xPlug\F:\vst64\Hybrid.dll"/>
              <member name="Values"><member name="Vst2xPlugInfo">
                <string name="subCategory" value="Instrument"/><string name="name" value="Hybrid"/>
              </member></member>
            </item></list></Vst2xPlugins>
            """);
        Directory.CreateDirectory(Path.Combine(_root, "REAPER"));
        var ini = Path.Combine(_root, "REAPER", "reaper-vstplugins64.ini");
        File.WriteAllText(ini, "[vstcache]\nHybrid.dll=0011,7,Hybrid (Co)\n");

        File.SetLastWriteTimeUtc(xml, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(ini, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var index = HostPluginCache.LoadAll(Locations());
        // 路径精确匹配优先：Cubase 的完整路径记录先于 REAPER 的文件名记录。
        Assert.Equal("Instrument", index.Find(@"F:\vst64\Hybrid.dll")?.Category);
        // 另一个位置的同名文件只能按文件名对上，取较新的 REAPER。
        Assert.Equal("Fx", index.Find(@"G:\other\Hybrid.dll")?.Category);
        Assert.Equal(["Cubase", "REAPER"], index.Daws);
        Assert.Contains(@"F:\vst64\Hybrid.dll", index.KnownPaths);
    }

    [Fact]
    public void NoDawRecords_EmptyIndex()
    {
        var index = HostPluginCache.LoadAll(Locations());
        Assert.Equal(0, index.Count);
        Assert.Empty(index.Daws);
        Assert.Null(index.Find(@"C:\VST\x.dll"));
    }

    [Fact]
    public void CatalogFallback_OnlyExactProducts()
    {
        VendorCatalog.UseForTests(
        [
            new VendorCatalog.Entry("FabFilter", [], "FabFilter", ["Pro-Q 4", "Pro-C 2"]),
            new VendorCatalog.Entry("Valhalla DSP", [], "Valhalla DSP", ["ValhallaRoom"]),
        ]);
        Assert.True(VendorCatalog.IsKnownEffect("Pro-Q 3"));          // 差版本号
        Assert.True(VendorCatalog.IsKnownEffect("ValhallaRoom_x64"));  // 位数后缀
        Assert.True(VendorCatalog.IsKnownEffect("Pro-C 2 (64 bit)"));
        Assert.False(VendorCatalog.IsKnownEffect("FabFilter Twin 3")); // 只有厂家前缀，可能是音源
        Assert.False(VendorCatalog.IsKnownEffect("Serum_x64"));
    }
}
