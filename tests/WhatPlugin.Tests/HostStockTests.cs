using System.Text;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>宿主自带插件：从宿主自己的清单读出来，不靠写死的名单。全部在临时文件夹里，不碰本机的宿主文件。</summary>
public sealed class HostStockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-stock-" + Guid.NewGuid().ToString("N"));

    public HostStockTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // 新版 Cubase 才有的插件，名字不在内置名单里。
    private const string FutureUid = "0123456789ABCDEF0123456789ABCDEF";

    private static string Class(int index, string cid, string category, string name, string sub) => $"""
                    <string name="Class {index:D2} - CID" value="{cid}" wide="true"/>
                    <int name="Class {index:D2} - Cardinality" value="2147483647"/>
                    <string name="Class {index:D2} - Category" value="{category}"/>
                    <string name="Class {index:D2} - Name" value="{name}" wide="true"/>
                    <string name="Class {index:D2} - SubCategories" value="{sub}"/>
                    <string name="Class {index:D2} - Vendor" value="" wide="true"/>
        """;

    private static readonly string ModuleCache = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ModuleManager>
           <list name="Entries" type="list">
              <item>
                 <string name="Group" value="Components\AudioCodecs\MP3AudioHandler"/>
                 <member name="Values">
        {Class(0, "2EC8AB41D17540CD97EB392E82D9AB5A", "AudioFileHandler", "MPEG 1 Layer 3", "")}
                 </member>
              </item>
              <item>
                 <string name="Group" value="Components\Cubase Plug-in Set"/>
                 <member name="Values">
        {Class(0, FutureUid, "Audio Module Class", "Future Verb", "Fx|Reverb")}
        {Class(1, "11111111111111111111111111111111", "Component Controller Class", "Future Verb Controller", "Fx|Reverb")}
        {Class(2, "E25E19544351460996CF8E7672886C40", "Audio Module Class", "AutoFilter", "Fx|Filter")}
                 </member>
              </item>
              <item>
                 <string name="Group" value="Components\Some Synth"/>
                 <member name="Values">
        {Class(0, "22222222222222222222222222222222", "Audio Module Class", "Stock Synth", "Instrument|Synth")}
                 </member>
              </item>
           </list>
        </ModuleManager>
        """;

    private HostStock LoadFromTemp()
    {
        var steinberg = Path.Combine(_dir, "Steinberg");
        Directory.CreateDirectory(Path.Combine(steinberg, "Cubase 15_64"));
        File.WriteAllText(Path.Combine(steinberg, "Cubase 15_64", "Cubase Pro Module Cache.xml"), ModuleCache, Encoding.UTF8);
        // 打不开 .cpr 的 Steinberg 软件不算。
        Directory.CreateDirectory(Path.Combine(steinberg, "WaveLab Pro 12"));
        File.WriteAllText(Path.Combine(steinberg, "WaveLab Pro 12", "WaveLab Module Cache.xml"),
            ModuleCache.Replace("Future Verb", "WaveLab Only"), Encoding.UTF8);

        var reaper = Path.Combine(_dir, "REAPER");
        Directory.CreateDirectory(reaper);
        File.WriteAllText(Path.Combine(reaper, "reaper-vstplugins64.ini"), """
            [vstcache]
            reaeq.dll=00AA,1919247729,ReaEQ (Cockos)
            reasynth.dll=00BB,1919251321,ReaSynth (Cockos)!!!VSTi
            Pro-Q 3.dll=00CC,1179758419,Pro-Q 3 (FabFilter)
            """);

        return HostStock.Load(new DawLocations(SteinbergFolder: steinberg, ReaperFolder: reaper, PreSonusFolder: Path.Combine(_dir, "none"),
            FlStudioDatabases: [], CakewalkInventory: () => []));
    }

    [Fact]
    public void ModuleCacheGivesEffectClassesOnly()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ModuleCache));
        var plugins = HostStock.ParseSteinbergModuleCache(stream, "Cubase 15");

        Assert.Equal(["Future Verb", "AutoFilter"], plugins.Select(p => p.Name));
        Assert.All(plugins, p => Assert.Equal(ProjectHost.Cubase, p.Host));
        Assert.Equal(FutureUid, plugins[0].Uid);
    }

    [Theory]
    [InlineData("Cubase 14_64", "Cubase 14")]
    [InlineData("Cubase 15_64", "Cubase 15")]
    [InlineData("Nuendo 13_64", "Nuendo 13")]
    [InlineData("Cubase LE AI Elements 13_64", "Cubase LE AI Elements 13")]
    [InlineData("WaveLab Pro 12", null)]
    [InlineData("Steinberg MediaLib", null)]
    public void OnlyHostsThatOpenCprCount(string folder, string? product) => Assert.Equal(product, HostStock.SteinbergProduct(folder));

    [Fact]
    public void LoadReadsInstalledHostsWithoutAList()
    {
        var stock = LoadFromTemp();

        Assert.True(stock.Contains(ProjectHost.Cubase, "Future Verb"));
        Assert.False(stock.Contains(ProjectHost.Cubase, "WaveLab Only"));
        Assert.False(stock.Contains(ProjectHost.Cubase, "Stock Synth"));
        Assert.True(stock.Contains(ProjectHost.Reaper, "ReaEQ"));
        Assert.False(stock.Contains(ProjectHost.Reaper, "Pro-Q 3"));
        Assert.Equal("Cubase 15", stock.ProductOf("Future Verb"));
        Assert.Equal("REAPER", stock.ProductOf("ReaEQ"));
        Assert.Null(stock.ProductOf("Pro-Q 3"));
    }

    [Fact]
    public void GuidMatchesEvenWhenTheNameDiffers()
    {
        var stock = LoadFromTemp();

        Assert.True(stock.Contains(ProjectHost.Cubase, "Some Localized Name", FutureUid));
        // 别的宿主的工程不按 Cubase 的 GUID 算。
        Assert.False(stock.Contains(ProjectHost.Reaper, "Some Localized Name", FutureUid));
    }

    [Fact]
    public void MissingFoldersGiveAnEmptyCatalog()
    {
        var none = Path.Combine(_dir, "none");
        var stock = HostStock.Load(new DawLocations(none, none, none, [], () => []));
        Assert.Equal(0, stock.Count);
    }

    private static ProjectFileRecord Cpr(params (string Name, string? Uid)[] uses) =>
        new(0, @"D:\p\歌.cpr", ProjectHost.Cubase, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ProjectScanStatus.Ok, null,
            ProjectParser.Version, uses.Select(u => new ProjectPluginUse(u.Name, u.Uid, 1)).ToList());

    [Fact]
    public void NewHostPluginIsStockInsteadOfMissing()
    {
        var stock = LoadFromTemp();
        var file = Cpr(("Future Verb", FutureUid), ("Renamed In Project", FutureUid), ("Not Installed FX", null));

        var withCatalog = ProjectReport.Build([file], [], stock: stock).Projects[0];
        Assert.Equal(1, withCatalog.MissingCount);
        Assert.Equal("Not Installed FX", withCatalog.Plugins.Single(p => p.State == ProjectPluginState.Missing).Name);
        Assert.Equal(2, withCatalog.Plugins.Count(p => p.State == ProjectPluginState.Stock));

        // 没装这个版本的宿主：确实缺。
        Assert.Equal(3, ProjectReport.Build([file], [], stock: HostStock.Empty).Projects[0].MissingCount);
    }

    [Fact]
    public void MixNoteStepOfHostPluginIsNotMissing()
    {
        var stock = LoadFromTemp();
        var chain = new PluginChain(1, "人声", "", [new ChainStep("", "Future Verb"), new ChainStep("", "Not Installed FX"), new ChainStep("", "ReaEQ")]);

        var states = ChainReport.Resolve(chain, [], stock).Select(s => s.State).ToList();

        Assert.Equal([ChainStepState.Stock, ChainStepState.Missing, ChainStepState.Stock], states);
        Assert.Equal(ChainStepState.Stock, ChainReport.Resolve(new ChainPick("", "Future Verb"), [], stock).State);
        Assert.Equal(ChainStepState.Missing, ChainReport.Resolve(new ChainPick("", "Future Verb"), [], HostStock.Empty).State);
    }
}
