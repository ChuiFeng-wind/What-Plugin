using System.Text;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>Mac 插件文件的纯解析：样例都是手工构造或用 Python 的 plistlib 生成的，不需要 Mac。</summary>
public sealed class MacBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-mac-" + Guid.NewGuid().ToString("N"));

    public MacBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IReadOnlyList<Arch> Archs(params byte[] bytes) => MachO.Architectures(new MemoryStream(bytes));

    [Fact]
    public void MachO_ThinBinaries()
    {
        // 64 位 Mach-O 在文件里是小端：CF FA ED FE，然后 cputype。
        Assert.Equal([Arch.X64], Archs(0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01, 0x03, 0, 0, 0));
        Assert.Equal([Arch.Arm64], Archs(0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01, 0x00, 0, 0, 0));
        Assert.Equal([Arch.X86], Archs(0xCE, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x00));
    }

    [Fact]
    public void MachO_UniversalBinaryListsEveryArchitecture()
    {
        var fat = new List<byte> { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 };
        fat.AddRange([0x01, 0, 0, 0x07, 0, 0, 0, 3, 0, 0, 0x40, 0, 0, 0, 0x10, 0, 0, 0, 0, 14]); // x86_64
        fat.AddRange([0x01, 0, 0, 0x0C, 0, 0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0x10, 0, 0, 0, 0, 14]); // arm64
        Assert.Equal([Arch.X64, Arch.Arm64], Archs([.. fat]));
    }

    [Fact]
    public void MachO_RejectsOtherFiles()
    {
        // Java class 文件也以 CAFEBABE 开头，后面是版本号（这里 52），不能当成 52 个架构。
        Assert.Empty(Archs(0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 52));
        Assert.Empty(Archs("MZ"u8.ToArray()));                 // Windows 程序
        Assert.Empty(Archs(0xCF, 0xFA, 0xED));                   // 截断
        Assert.Empty(Archs(0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2, 1, 2)); // 通用二进制但表不完整
        Assert.Empty(MachO.Architectures(Path.Combine(_dir, "missing")));
    }

    private const string AudioUnitPlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleExecutable</key>
            <string>FakeComp</string>
            <key>CFBundleIdentifier</key>
            <string>com.example.fakecomp</string>
            <key>CFBundleName</key>
            <string>FakeComp</string>
            <key>CFBundleShortVersionString</key>
            <string>2.1.0</string>
            <key>AudioComponents</key>
            <array>
                <dict>
                    <key>manufacturer</key>
                    <string>ExAu</string>
                    <key>name</key>
                    <string>Example Audio: FakeComp</string>
                    <key>sandboxSafe</key>
                    <true/>
                    <key>subtype</key>
                    <string>fkcp</string>
                    <key>type</key>
                    <string>aufx</string>
                    <key>version</key>
                    <integer>131328</integer>
                </dict>
                <dict>
                    <key>name</key>
                    <string>Example Audio: FakeSynth 合成器</string>
                    <key>type</key>
                    <string>aumu</string>
                    <key>subtype</key>
                    <string>fksy</string>
                    <key>manufacturer</key>
                    <string>ExAu</string>
                    <key>version</key>
                    <integer>65536</integer>
                </dict>
            </array>
            <key>Ratio</key>
            <real>1.5</real>
            <key>Blob</key>
            <data>AAEC</data>
            <key>When</key>
            <date>2026-01-02T03:04:05Z</date>
            <key>Big</key>
            <integer>5000000000</integer>
        </dict>
        </plist>
        """;

    // 同样的内容，用 Python 的 plistlib.dumps(..., fmt=FMT_BINARY) 生成。
    private const string AudioUnitBinaryPlist =
        "YnBsaXN0MDDZAQIDBAUGBwgJCh0eHyAfISIjXxAPQXVkaW9Db21wb25lbnRzU0JpZ1RCbG9iXxASQ0ZCdW5kbGVFeGVjdXRhYmxlXxASQ0ZCdW5kbGVJZGVudGlmaWVyXENGQnVuZGxlTmFtZV8QGkNGQnVuZGxlU2hvcnRWZXJzaW9uU3RyaW5nVVJhdGlvVFdoZW6iCxjWDA0ODxAREhMUFRYXXG1hbnVmYWN0dXJlclRuYW1lW3NhbmRib3hTYWZlV3N1YnR5cGVUdHlwZVd2ZXJzaW9uVEV4QXVfEBdFeGFtcGxlIEF1ZGlvOiBGYWtlQ29tcAlUZmtjcFRhdWZ4EgACAQDVDA0PEBESGRobHG8QHABFAHgAYQBtAHAAbABlACAAQQB1AGQAaQBvADoAIABGAGEAawBlAFMAeQBuAHQAaAAgVAhiEFZoVGZrc3lUYXVtdRIAAQAAEwAAAAEqBfIAQwABAlhGYWtlQ29tcF8QFGNvbS5leGFtcGxlLmZha2Vjb21wVTIuMS4wIz/4AAAAAAAAM0HHg7aSgAAAAAgAGwAtADEANgBLAGAAbQCKAJAAlQCYAKUAsgC3AMMAywDQANgA3QD3APgA/QECAQcBEgFNAVIBVwFcAWUBaQFyAYkBjwGYAAAAAAAAAgEAAAAAAAAAJAAAAAAAAAAAAAAAAAAAAaE=";

    public static TheoryData<string> BothFormats => ["xml", "binary"];

    private static byte[] Sample(string format) =>
        format == "xml" ? Encoding.UTF8.GetBytes(AudioUnitPlist) : Convert.FromBase64String(AudioUnitBinaryPlist);

    [Theory]
    [MemberData(nameof(BothFormats))]
    public void PropertyList_ReadsAllValueTypes(string format)
    {
        var root = Assert.IsType<Dictionary<string, object?>>(PropertyList.Read(Sample(format)));
        Assert.Equal("com.example.fakecomp", root["CFBundleIdentifier"]);
        Assert.Equal(1.5, root["Ratio"]);
        Assert.Equal(new byte[] { 0, 1, 2 }, root["Blob"]);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ((DateTime)root["When"]!).ToUniversalTime());
        Assert.Equal(5_000_000_000L, root["Big"]);
        var units = Assert.IsType<List<object?>>(root["AudioComponents"]);
        Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(units[0])["sandboxSafe"]);
    }

    [Theory]
    [MemberData(nameof(BothFormats))]
    public void BundleInfo_ListsAudioUnits(string format)
    {
        var bundle = Path.Combine(_dir, "FakeComp.component");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
        File.WriteAllBytes(Path.Combine(bundle, "Contents", "Info.plist"), Sample(format));

        var info = MacBundleInfo.Read(bundle);
        Assert.NotNull(info);
        Assert.Equal("com.example.fakecomp", info.Identifier);
        Assert.Equal("2.1.0", info.Version);
        Assert.Equal(Path.Combine(bundle, "Contents", "MacOS", "FakeComp"), info.ExecutablePath(bundle));

        Assert.Collection(info.AudioUnits,
            fx =>
            {
                Assert.Equal(("FakeComp", "Example Audio", "aufx", "fkcp", "ExAu", 131328L), (fx.Name, fx.Vendor, fx.Type, fx.SubType, fx.Manufacturer, fx.Version));
                Assert.False(fx.IsInstrument);
            },
            synth =>
            {
                Assert.Equal("FakeSynth 合成器", synth.Name); // 二进制里非 ASCII 的文字存成 UTF-16
                Assert.True(synth.IsInstrument);
            });
    }

    [Fact]
    public void PropertyList_BadInputReturnsNull()
    {
        Assert.Null(PropertyList.Read(Encoding.UTF8.GetBytes("<html><body/></html>")));
        Assert.Null(PropertyList.Read(Encoding.UTF8.GetBytes("<plist><dict><key>a</key><widget/></dict></plist>")));
        Assert.Null(PropertyList.Read("bplist00"u8.ToArray()));

        // 二进制格式里文件尾指向越界的位置，不能崩。
        var broken = Convert.FromBase64String(AudioUnitBinaryPlist);
        broken[^1] = 0xFF;
        Assert.Null(PropertyList.Read(broken));

        Assert.Null(MacBundleInfo.Read(Path.Combine(_dir, "NoSuch.vst3")));
    }

    [Fact]
    public void PropertyList_DoesNotFetchTheDtd()
    {
        // DOCTYPE 指向一个不存在的本机地址：如果解析器去取它，会超时或报错。
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist SYSTEM "http://127.0.0.1:1/never.dtd">
            <plist version="1.0"><string>ok</string></plist>
            """;
        Assert.Equal("ok", PropertyList.Read(Encoding.UTF8.GetBytes(xml)));
    }

    // ---------- 发现和静态信息：在临时目录里摆出 Mac 的插件目录结构 ----------

    private static readonly byte[] ThinX64 = [0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01, 0x03, 0, 0, 0];

    private static byte[] Universal()
    {
        var fat = new List<byte> { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 };
        fat.AddRange([0x01, 0, 0, 0x07, 0, 0, 0, 3, 0, 0, 0x40, 0, 0, 0, 0x10, 0, 0, 0, 0, 14]);
        fat.AddRange([0x01, 0, 0, 0x0C, 0, 0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0x10, 0, 0, 0, 0, 14]);
        return [.. fat];
    }

    private static string Plist(string executable, string version) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict>
            <key>CFBundleExecutable</key><string>{executable}</string>
            <key>CFBundleShortVersionString</key><string>{version}</string>
        </dict></plist>
        """;

    private string Bundle(string relative, byte[] plist, string executable, byte[] binary)
    {
        var bundle = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllBytes(Path.Combine(bundle, "Contents", "Info.plist"), plist);
        File.WriteAllBytes(Path.Combine(bundle, "Contents", "MacOS", executable), binary);
        return bundle;
    }

    [Fact]
    public void Discovery_FindsMacBundlesAndTheirArchitecture()
    {
        var au = Bundle(Path.Combine("Components", "FakeComp.component"), Sample("xml"), "FakeComp", Universal());
        var vst2 = Bundle(Path.Combine("VST", "厂家", "Old.vst"), Encoding.UTF8.GetBytes(Plist("OldBin", "1.2")), "OldBin", ThinX64);
        var vst3 = Bundle(Path.Combine("VST3", "New.vst3"), Encoding.UTF8.GetBytes(Plist("New", "3.0")), "New", ThinX64);

        var found = PluginDiscovery.Discover(
        [
            (Path.Combine(_dir, "Components"), PluginFormat.Au),
            (Path.Combine(_dir, "VST"), PluginFormat.Vst2),
            (Path.Combine(_dir, "VST3"), PluginFormat.Vst3),
        ]).ToDictionary(t => t.Path);

        Assert.Equal(3, found.Count);
        Assert.Equal(PluginFormat.Au, found[au].Format);
        // 通用二进制里两种架构都有，取本机能加载的那种。
        Assert.Equal(StaticInspector.NativeArch, found[au].Arch);
        Assert.Equal((PluginFormat.Vst2, Arch.X64), (found[vst2].Format, found[vst2].Arch));
        Assert.Equal((PluginFormat.Vst3, Arch.X64), (found[vst3].Format, found[vst3].Arch));

        Assert.Equal(Path.Combine(vst2, "Contents", "MacOS", "OldBin"), StaticInspector.ResolveBinary(vst2, PluginFormat.Vst2));

        // 没有 Windows 版本信息时，版本取 Info.plist，厂家取上一层文件夹。
        var info = StaticInspector.FromFileInfo(found[vst2]);
        Assert.Equal(("Old", "厂家", "1.2"), (info.Name, info.Vendor, info.Version));
    }

    [Fact]
    public void AudioUnits_ComeFromInfoPlistWithoutLoading()
    {
        var bundle = Bundle("FakeComp.component", Convert.FromBase64String(AudioUnitBinaryPlist), "FakeComp", ThinX64);

        var units = StaticInspector.ReadAudioUnits(bundle);

        Assert.NotNull(units);
        Assert.Collection(units,
            fx => Assert.Equal(("FakeComp", "Example Audio", "2.1.0", "Fx", (bool?)false, InfoSource.ModuleInfo),
                (fx.Name, fx.Vendor, fx.Version, fx.SdkCategory, fx.IsInstrument, fx.Source)),
            synth => Assert.Equal(("FakeSynth 合成器", "1.0.0", (bool?)true), (synth.Name, synth.Version, synth.IsInstrument)));

        // 没有 AudioComponents 的老式 AU 读不出来，交给文件信息。
        var old = Bundle("Old.component", Encoding.UTF8.GetBytes(Plist("Old", "1.0")), "Old", ThinX64);
        Assert.Null(StaticInspector.ReadAudioUnits(old));
        Assert.Null(StaticInspector.ReadAudioUnits(Path.Combine(_dir, "missing.component")));
    }

    [Fact]
    public async Task Scan_RecordsAudioUnitsWithoutTheScanner()
    {
        var root = Path.Combine(_dir, "Components");
        Bundle(Path.Combine("Components", "FakeComp.component"), Sample("xml"), "FakeComp", ThinX64);
        Bundle(Path.Combine("Components", "Old.component"), Encoding.UTF8.GetBytes(Plist("Old", "4.5")), "Old", ThinX64);

        using (var db = new PluginDatabase(Path.Combine(_dir, "t.db")))
        {
            // 扫描器不存在：AU 根本不该用到它。
            var service = new ScanService(db, new ScannerRunner(Path.Combine(_dir, "missing"), TimeSpan.FromSeconds(5)));
            var summary = await service.ScanAsync([(root, PluginFormat.Au)], new ScanOptions(Full: true), null, CancellationToken.None);

            Assert.Equal((1, 1, 0), (summary.ModuleInfo, summary.FileInfoOnly, summary.LoadFailed));
            var plugins = db.LoadPlugins();
            Assert.Contains(plugins, p => p.Name == "FakeComp" && p.Vendor == "Example Audio" && p.Kind == PluginKind.Effect);
            Assert.Contains(plugins, p => p.Name == "FakeSynth 合成器" && p.Kind == PluginKind.Instrument);
            Assert.Contains(plugins, p => p.Name == "Old");
            Assert.All(plugins, p => Assert.All(p.Files, f => Assert.Equal(PluginFormat.Au, f.Format)));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void AudioUnitVersionIsMajorMinorBugfix() =>
        Assert.Equal("10.2.3", StaticInspector.AudioUnitVersion(0x000A0203));
}
