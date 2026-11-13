using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>工程路由（主输出、发送、编组、效果轨）的读取，笔记路由的保存、导入和备份。</summary>
public sealed class RoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-r-" + Guid.NewGuid().ToString("N"));

    public RoutingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "t.db");

    // ---------- Cubase：记录格式和真实 .cpr 一致 ----------

    private static byte[] Be32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Be64(long value) => [.. Be32((int)(value >> 32)), .. Be32((int)value)];

    private static byte[] Key(string key) => [.. Be32(key.Length + 1), .. Encoding.ASCII.GetBytes(key), 0];

    private static byte[] Int(string key, long value) => [.. Key(key), 0, 1, .. Be64(value)];

    private static byte[] Channel(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        return [.. Key("Name"), 0, 2, 0, 6, 0, 0, 0, 1, .. Key("String"), 0, 8, .. Be32(bytes.Length + 1), .. bytes, 0];
    }

    private static byte[] Field(string key, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [.. Key(key), 0, 8, .. Be32(bytes.Length + 4), .. bytes, 0, 0xEF, 0xBB, 0xBF];
    }

    /// <summary>插入槽里的一个插件，sidechain 不为空时带侧链输入编号（结构取自真实 .cpr）。</summary>
    private static byte[] Plugin(string name, long? sidechain = null)
    {
        var bytes = new List<byte>();
        bytes.AddRange([.. Key("Slot"), 0, 2, 0, 6, 0, 0, 0, 6]);
        bytes.AddRange([.. Key("Plugin isA"), 0, 8, .. Be32("VstCtrlInternalEffect".Length + 1), .. Encoding.ASCII.GetBytes("VstCtrlInternalEffect"), 0]);
        bytes.AddRange([.. Key("Plugin"), 0, 2, 0, 6, 0, 0, 0, 0x16]);
        bytes.AddRange([.. Key("Plugin UID"), 0, 2, 0, 6, 0, 0, 0, 1]);
        bytes.AddRange(Field("GUID", "5653544651337066616266696C746572"));
        bytes.AddRange(Field("Plugin Name", name));
        bytes.AddRange(Int("Audio Input Count", 2));
        if (sidechain is { } bus)
            bytes.AddRange(Int("Sidechain Bus Id 0", bus));
        bytes.AddRange(Int("Audio Output Count", 1));
        return bytes.ToArray();
    }

    /// <summary>一个通道：名称 → 插入 → 发送 → 自己的输入总线 → 主输出。sends 是 (目标总线或侧链编号, 开着)。</summary>
    private static byte[] CubaseChannel(string name, long own, long output, params (long Bus, bool On)[] sends) =>
        CubaseChannel(name, own, output, [], sends);

    private static byte[] CubaseChannel(string name, long own, long output, byte[][] plugins, params (long Bus, bool On)[] sends)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Channel(name));
        bytes.AddRange([.. Key("InsertFolder"), 0, 2, 0, 6, 0, 0, 0, 2]);
        foreach (var plugin in plugins)
            bytes.AddRange(plugin);
        bytes.AddRange(Key("SendFolder"));
        foreach (var (bus, on) in sends)
        {
            if (on)
                bytes.AddRange(Int("On", 1));
            bytes.AddRange(Key("Volume"));
            bytes.AddRange(Int("Value", 25856));
            bytes.AddRange(Key("Output"));
            bytes.AddRange(Int("Value", bus));
        }
        bytes.AddRange(Key("foldbackSendFolder"));
        bytes.AddRange(Key("OwnInputBus"));
        bytes.AddRange(Int("Bus UID", own));
        bytes.AddRange(Key("OutputBusValue"));
        bytes.AddRange(Int("Value", output));
        return bytes.ToArray();
    }

    private static byte[] Cpr(params byte[][] parts) => [.. "RIFF"u8, 0, 0, 0, 0, 0, 0, 0, 0, .. "ROOT"u8, .. parts.SelectMany(p => p)];

    [Fact]
    public void CubaseReadsOutputsSendsAndInfersRoles()
    {
        var bytes = Cpr(
            CubaseChannel("Vox", 101, 201, (301, true), (302, false), (777, true), (501, true), (502, false)),
            CubaseChannel("BV", 102, 201, (301, true)),
            CubaseChannel("Vox Group", 201, 900),
            CubaseChannel("Plate", 301, 900, [Plugin("FabFilter Pro-Q 3", 502), Plugin("FabFilter Pro-C 2", 501)]),
            CubaseChannel("Delay", 302, 900),
            CubaseChannel("Stereo Out", 900, 0));

        var tracks = ProjectParser.ParseTracks(ProjectHost.Cubase, new MemoryStream(bytes));
        var byName = tracks.ToDictionary(t => t.Name);

        Assert.Equal(["Vox", "BV", "Vox Group", "Plate", "Delay", "Stereo Out"], tracks.Select(t => t.Name));
        Assert.Equal(ChannelRole.Track, byName["Vox"].Role);
        Assert.Equal(byName["Vox Group"].Id, byName["Vox"].OutputId);
        Assert.Equal([byName["Plate"].Id], byName["Vox"].SendIds); // 关着的发送和找不到的总线不算
        // 发送的目标是 Plate 上 Pro-C 2 的侧链输入；关着的（Pro-Q 3）不算。
        Assert.Equal([new TrackSidechain(byName["Plate"].Id, "FabFilter Pro-C 2")], byName["Vox"].Sidechains);
        Assert.Equal(["FabFilter Pro-Q 3", "FabFilter Pro-C 2"], byName["Plate"].Plugins.Select(p => p.Name));
        Assert.Empty(byName["BV"].Sidechains);
        Assert.Equal(ChannelRole.Group, byName["Vox Group"].Role);
        Assert.Equal(byName["Stereo Out"].Id, byName["Vox Group"].OutputId);
        Assert.Equal(ChannelRole.Effect, byName["Plate"].Role);
        Assert.Equal(ChannelRole.Effect, byName["Delay"].Role); // 只被关着的发送接到，也是效果轨
        Assert.Equal(ChannelRole.Master, byName["Stereo Out"].Role);
        Assert.Null(byName["Stereo Out"].OutputId);
    }

    // ---------- REAPER ----------

    [Fact]
    public void ReaperFoldersAreGroupsAndReceivesAreSends()
    {
        const string rpp = """
            <REAPER_PROJECT 0.1 "7.0" 1700000000
              <MASTERFXLIST
                <VST "VST3: Pro-L 2 (FabFilter)" "FabFilter Pro-L 2.vst3" 0 "" 1 ""
                >
              >
              <TRACK {A}
                NAME Drums
                ISBUS 1 1
              >
              <TRACK {B}
                NAME Kick
                ISBUS 0 0
              >
              <TRACK {C}
                NAME Snare
                ISBUS 2 -1
              >
              <TRACK {D}
                NAME Vox
                MAINSEND 1 0
              >
              <TRACK {E}
                NAME Verb
                AUXRECV 3 0 1 0 0 0 0 0 0 -1:U 0 -1 ''
                AUXRECV 1 0 1 0 1 0 0 0 0 -1:U 0 -1 ''
              >
            >
            """;

        var tracks = ProjectParser.ParseTracks(ProjectHost.Reaper, new MemoryStream(Encoding.UTF8.GetBytes(rpp)));
        var byName = tracks.ToDictionary(t => t.Name);

        Assert.Equal(ChannelRole.Group, byName["Drums"].Role);
        Assert.Equal("master", byName["Drums"].OutputId);
        Assert.Equal(byName["Drums"].Id, byName["Kick"].OutputId);
        Assert.Equal(byName["Drums"].Id, byName["Snare"].OutputId);
        Assert.Empty(byName["Kick"].SendIds); // 静音的接收不算
        Assert.Equal("master", byName["Vox"].OutputId);
        Assert.Equal([byName["Verb"].Id], byName["Vox"].SendIds);
        Assert.Equal(ChannelRole.Effect, byName["Verb"].Role);
        Assert.Equal(ChannelRole.Master, byName["Master"].Role);
    }

    // ---------- Studio One ----------

    private static byte[] Song(string mixer)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Devices/audiomixer.xml").Open());
            writer.Write(mixer);
        }
        return memory.ToArray();
    }

    [Fact]
    public void StudioOneReadsBusesFxChannelsAndSends()
    {
        var song = Song("""
            <AudioMixer xmlns:x="internal://schemas/x">
              <ChannelGroup name="AudioTrack">
                <AudioTrackChannel name="Channel01" label="Vox" uniqueID="{aaaa-1}">
                  <Attributes x:id="Inserts">
                    <Attributes name="FX01">
                      <Attributes x:id="classInfo" classID="{2}" name="FabFilter Pro-Q 3" category="AudioEffect" subCategory="VST3/EQ"/>
                    </Attributes>
                  </Attributes>
                  <Connection x:id="destination" objectID="{BBBB-2}/Input"/>
                  <Attributes x:id="Sends">
                    <Attributes name="Send01"><Connection x:id="destination" objectID="{CCCC-3}/Input"/></Attributes>
                    <Attributes name="Send02"><Connection x:id="destination" objectID="{EEEE-9}/Input"/></Attributes>
                  </Attributes>
                </AudioTrackChannel>
              </ChannelGroup>
              <ChannelGroup name="AudioGroup">
                <AudioGroupChannel name="Channel01" label="Vox Bus" uniqueID="{BBBB-2}">
                  <Connection x:id="destination" objectID="{DDDD-4}/Input"/>
                </AudioGroupChannel>
              </ChannelGroup>
              <ChannelGroup name="AudioEffect">
                <AudioEffectChannel name="Channel01" label="Reverb" uniqueID="{CCCC-3}">
                  <Connection x:id="destination" objectID="{DDDD-4}/Input"/>
                </AudioEffectChannel>
              </ChannelGroup>
              <ChannelGroup name="AudioOutput">
                <AudioOutputChannel name="Channel01" label="Main" uniqueID="{DDDD-4}"/>
              </ChannelGroup>
            </AudioMixer>
            """);

        var tracks = ProjectParser.ParseTracks(ProjectHost.StudioOne, new MemoryStream(song));
        var byName = tracks.ToDictionary(t => t.Name);

        Assert.Equal(["FabFilter Pro-Q 3"], byName["Vox"].Plugins.Select(p => p.Name));
        Assert.Equal("BBBB-2", byName["Vox"].OutputId);
        Assert.Equal(["CCCC-3"], byName["Vox"].SendIds); // 工程里没有的通道不算
        Assert.Equal(ChannelRole.Group, byName["Vox Bus"].Role);
        Assert.Equal("DDDD-4", byName["Vox Bus"].OutputId);
        Assert.Equal(ChannelRole.Effect, byName["Reverb"].Role);
        Assert.Equal(ChannelRole.Master, byName["Main"].Role);
        Assert.Equal(ChannelRole.Track, byName["Vox"].Role);
    }

    // ---------- Ableton Live ----------

    private static byte[] Gzip(string text)
    {
        var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return memory.ToArray();
    }

    private static IReadOnlyList<ProjectTrack> AbletonSample() => ProjectParser.ParseTracks(ProjectHost.Ableton, new MemoryStream(Gzip("""
        <Ableton MajorVersion="5">
          <LiveSet>
            <Tracks>
              <GroupTrack Id="10">
                <Name><EffectiveName Value="Drums"/></Name>
                <TrackGroupId Value="-1"/>
                <DeviceChain>
                  <AudioInputRouting><Target Value="AudioIn/External/S0"/></AudioInputRouting>
                  <AudioOutputRouting><Target Value="AudioOut/Master"/></AudioOutputRouting>
                  <Mixer><Sends><TrackSendHolder Id="0"><Send><Manual Value="0.0003162277571"/></Send></TrackSendHolder></Sends></Mixer>
                </DeviceChain>
              </GroupTrack>
              <AudioTrack Id="11">
                <Name><EffectiveName Value="Kick"/></Name>
                <TrackGroupId Value="10"/>
                <DeviceChain>
                  <AudioInputRouting><Target Value="AudioIn/External/S0"/></AudioInputRouting>
                  <AudioOutputRouting><Target Value="AudioOut/GroupTrack"/></AudioOutputRouting>
                  <Mixer><Sends><TrackSendHolder Id="0"><Send><Manual Value="0.5"/></Send></TrackSendHolder></Sends></Mixer>
                  <DeviceChain><Devices>
                    <PluginDevice><PluginDesc><Vst3PluginInfo><Name Value="Pro-Q 3"/></Vst3PluginInfo></PluginDesc></PluginDevice>
                  </Devices></DeviceChain>
                </DeviceChain>
              </AudioTrack>
              <ReturnTrack Id="20">
                <Name><EffectiveName Value="A-Reverb"/></Name>
                <DeviceChain>
                  <AudioOutputRouting><Target Value="AudioOut/Master"/></AudioOutputRouting>
                </DeviceChain>
              </ReturnTrack>
            </Tracks>
            <MasterTrack>
              <Name><EffectiveName Value="Master"/></Name>
              <DeviceChain>
                <AudioOutputRouting><Target Value="AudioOut/External/S0"/></AudioOutputRouting>
              </DeviceChain>
            </MasterTrack>
          </LiveSet>
        </Ableton>
        """)));

    [Fact]
    public void AbletonReadsGroupsReturnsAndSends()
    {
        var byName = AbletonSample().ToDictionary(t => t.Name);

        Assert.Equal(ChannelRole.Group, byName["Drums"].Role);
        Assert.Equal("master", byName["Drums"].OutputId); // 不能读成输入路由
        Assert.Empty(byName["Drums"].SendIds); // 旋钮在最小值
        Assert.Equal("10", byName["Kick"].OutputId);
        Assert.Equal(["20"], byName["Kick"].SendIds);
        Assert.Equal(["Pro-Q 3"], byName["Kick"].Plugins.Select(p => p.Name));
        Assert.Equal(ChannelRole.Effect, byName["A-Reverb"].Role);
        Assert.Equal(ChannelRole.Master, byName["Master"].Role);
        Assert.Null(byName["Master"].OutputId);
    }

    [Fact]
    public void ImportBringsAlongDestinationsAndDescribesThem()
    {
        var all = AbletonSample();
        var kick = all.Single(t => t.Name == "Kick");

        var withoutMaster = TrackRouting.WithDestinations(all, [kick], _ => false);
        Assert.Equal(["Drums", "Kick", "A-Reverb"], withoutMaster.Select(t => t.Name));
        var withMaster = TrackRouting.WithDestinations(all, [kick], _ => true);
        Assert.Equal(["Drums", "Kick", "A-Reverb", "Master"], withMaster.Select(t => t.Name));

        Assert.Equal("→ Drums（编组） · 发送 → A-Reverb", TrackRouting.Describe(kick, all));
        var keyed = kick with { Sidechains = [new TrackSidechain("20", "Pro-C 2")] };
        Assert.Equal("→ Drums（编组） · 发送 → A-Reverb · 侧链 → A-Reverb（Pro-C 2）", TrackRouting.Describe(keyed, all));
        Assert.Equal("", TrackRouting.Describe(all.Single(t => t.Name == "A-Reverb"), all)); // 只接主输出不用说明
    }

    // ---------- 数据库 ----------

    [Fact]
    public void ImportedNotesKeepRoutesAndReuseEarlierImports()
    {
        using var db = new PluginDatabase(DbPath);
        var folder = db.AddNoteFolder(null, "歌");
        ImportedNote[] notes =
        [
            new("11", "Kick", "song · Kick", [new ChainStep("", "Pro-Q 3", "")], ChannelRole.Track, "10", ["20"], [("20", "Pro-C 2"), ("99", "Gone")]),
            new("10", "Drums", "song · Drums", [], ChannelRole.Group, "master"),
            new("20", "A-Reverb", "song · A-Reverb", [], ChannelRole.Effect),
        ];

        var first = db.AddImportedNotes(folder.Id, notes);
        Assert.Equal(3, first.Created.Count);
        Assert.Empty(first.Reused);
        var ids = db.LoadChains().ToDictionary(c => c.Name, c => c.Id);
        Assert.Equal(ChannelRole.Group, db.LoadChains().Single(c => c.Name == "Drums").Role);
        Assert.Equal(
            [
                new NoteRoute(ids["Kick"], ids["Drums"], RouteKind.Output),
                new NoteRoute(ids["Kick"], ids["A-Reverb"], RouteKind.Send),
                new NoteRoute(ids["Kick"], ids["A-Reverb"], RouteKind.Sidechain, "Pro-C 2"),
            ],
            db.LoadRoutes());

        // 再导入一次：沿用原来的笔记（思路不动），只更新路由。
        db.SaveChain(ids["Kick"], "我写的思路", [new ChainStep("", "Pro-Q 3", "")]);
        var second = db.AddImportedNotes(folder.Id, [notes[0] with { SendKeys = [], Sidechains = [] }, notes[1], notes[2]]);
        Assert.Empty(second.Created);
        Assert.Equal(3, second.Reused.Count);
        Assert.Equal(3, db.LoadChains().Count);
        Assert.Equal("我写的思路", db.LoadChains().Single(c => c.Name == "Kick").Note);
        Assert.Equal([new NoteRoute(ids["Kick"], ids["Drums"], RouteKind.Output)], db.LoadRoutes());
    }

    [Fact]
    public void RoutesIgnoreSelfAndMissingTargetsAndGoAwayWithTheNote()
    {
        using var db = new PluginDatabase(DbPath);
        var vox = db.AddChain("Vox");
        var bus = db.AddChain("Vox Bus");
        var verb = db.AddChain("Verb");
        db.SetChainRole(bus.Id, ChannelRole.Group);

        db.SetRoutes(vox.Id, bus.Id, [verb.Id, vox.Id, 9999, verb.Id]);
        Assert.Equal([new NoteRoute(vox.Id, bus.Id, RouteKind.Output), new NoteRoute(vox.Id, verb.Id, RouteKind.Send)], db.LoadRoutes());
        Assert.Equal(ChannelRole.Group, db.LoadChains().Single(c => c.Id == bus.Id).Role);

        db.SetRoutes(vox.Id, null, [verb.Id]); // 改回主输出
        Assert.Equal([new NoteRoute(vox.Id, verb.Id, RouteKind.Send)], db.LoadRoutes());

        // 同一条通道上两个插件都可以接侧链；只改输出和发送时侧链不动。
        db.SetRoutes(vox.Id, null, [verb.Id], [(verb.Id, "Pro-C 2"), (verb.Id, "Pro-Q 3"), (verb.Id, " "), (vox.Id, "Self")]);
        db.SetRoutes(vox.Id, bus.Id, [verb.Id]);
        Assert.Equal(
            [
                new NoteRoute(vox.Id, bus.Id, RouteKind.Output),
                new NoteRoute(vox.Id, verb.Id, RouteKind.Send),
                new NoteRoute(vox.Id, verb.Id, RouteKind.Sidechain, "Pro-C 2"),
                new NoteRoute(vox.Id, verb.Id, RouteKind.Sidechain, "Pro-Q 3"),
            ],
            db.LoadRoutes());
        db.SetRoutes(vox.Id, null, [verb.Id], []);
        Assert.DoesNotContain(db.LoadRoutes(), r => r.Kind == RouteKind.Sidechain);

        db.DeleteChain(verb.Id);
        Assert.Empty(db.LoadRoutes());
        var reused = db.AddChain("New"); // id 可能复用，旧路由不能接到新笔记上
        Assert.DoesNotContain(db.LoadRoutes(), r => r.To == reused.Id || r.From == reused.Id);
    }

    [Fact]
    public void MigrationFromV13AddsRoleAndRoutes()
    {
        using (var db = new PluginDatabase(DbPath))
            db.AddChain("Old note");
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE chain_routes;
                ALTER TABLE chains DROP COLUMN role;
                PRAGMA user_version = 13;
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new PluginDatabase(DbPath);
        var note = Assert.Single(migrated.LoadChains());
        Assert.Equal(("Old note", ChannelRole.Track), (note.Name, note.Role));
        Assert.Empty(migrated.LoadRoutes());
        var bus = migrated.AddChain("Bus");
        migrated.SetRoutes(note.Id, bus.Id, []);
        Assert.Single(migrated.LoadRoutes());
    }

    // ---------- 备份 ----------

    [Fact]
    public void BackupKeepsRolesAndRoutesByFolderAndName()
    {
        string json;
        using (var db = new PluginDatabase(DbPath))
        {
            var song = db.EnsureFolderPath(["歌", "人声"]);
            var vox = db.AddChain("Vox", song);
            var bus = db.AddChain("Vox Bus", song);
            var verb = db.AddChain("Plate", db.EnsureFolderPath(["共用"]));
            db.SetChainRole(bus.Id, ChannelRole.Group);
            db.SetChainRole(verb.Id, ChannelRole.Effect);
            db.SetRoutes(vox.Id, bus.Id, [verb.Id], [(verb.Id, "Pro-C 2")]);
            var entries = BackupRestore.ChainEntries(db.LoadChains(), db.LoadNoteFolders(), db.LoadRoutes());
            json = UserDataBackup.Serialize(new BackupContent([], [], [], entries, [], null, null), null);
        }
        Assert.Contains("\"Version\": 7", json);

        using var restored = new PluginDatabase(Path.Combine(_dir, "b.db"));
        Assert.Equal(3, restored.ImportChains(UserDataBackup.ParseChains(json)));
        var ids = restored.LoadChains().ToDictionary(c => c.Name);
        Assert.Equal(ChannelRole.Group, ids["Vox Bus"].Role);
        Assert.Equal(ChannelRole.Effect, ids["Plate"].Role);
        Assert.Equal(ChannelRole.Track, ids["Vox"].Role);
        Assert.Equal(
            [
                new NoteRoute(ids["Vox"].Id, ids["Vox Bus"].Id, RouteKind.Output),
                new NoteRoute(ids["Vox"].Id, ids["Plate"].Id, RouteKind.Send),
                new NoteRoute(ids["Vox"].Id, ids["Plate"].Id, RouteKind.Sidechain, "Pro-C 2"),
            ],
            restored.LoadRoutes());

        // 旧备份（没有路由字段）照常恢复，不动已有路由。
        Assert.Equal(1, restored.ImportChains([new ChainBackupEntry("Vox", "新思路", [], ["歌", "人声"])]));
        Assert.Equal(3, restored.LoadRoutes().Count);
    }
}
