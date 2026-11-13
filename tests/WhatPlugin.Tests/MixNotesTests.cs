using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>混音笔记（文件夹、标签、备选、工程导入、批量删除）、适合音色和 v12 迁移。</summary>
public sealed class MixNotesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-n-" + Guid.NewGuid().ToString("N"));

    public MixNotesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "t.db");

    private static long _nextId;

    private static PluginRow Row(string name, string vendor = "Acme", PluginKind kind = PluginKind.Effect, bool missing = false, string[]? tags = null) =>
        new(++_nextId, Normalizer.Name(name), name, vendor, kind, tags ?? ["压缩"], ClassificationSource.Seed, missing,
            missing ? [] : [new MergedFile("c:\\" + name, PluginFormat.Vst3, Arch.X64, "1", "", ScanStatus.Loaded, null)],
            PluginUserData.Empty);

    private static MergedPlugin Merged(string key, string name, params string[] tags) =>
        new(key, name, "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, tags.Length > 0 ? tags : ["压缩"], ClassificationSource.SdkCategory),
            [new MergedFile(key + ".vst3", PluginFormat.Vst3, Arch.X64, "1", "Fx", ScanStatus.Loaded, null)]);

    // ---------- 按轨道读取工程：Cubase 的字节结构取自真实 .cpr ----------

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Key(string key) => [.. BigEndian(key.Length + 1), .. Encoding.ASCII.GetBytes(key), 0];

    private static byte[] Field(string key, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [.. Key(key), 0, 8, .. BigEndian(bytes.Length + 4), .. bytes, 0, 0xEF, 0xBB, 0xBF];
    }

    private static byte[] Channel(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        return [.. Key("Name"), 0, 2, 0, 6, 0, 0, 0, 1, .. Key("String"), 0, 8, .. BigEndian(bytes.Length + 1), .. bytes, 0];
    }

    private static byte[] InsertFolder() => [.. Key("hasAudioInserts"), 0, 1, 0, 0, 0, 1, .. Key("InsertFolder"), 0, 2, 0, 6, 0, 0, 0, 2];

    private static byte[] Plugin(string name, string slot = "Slot")
    {
        var bytes = new List<byte>();
        bytes.AddRange(Key(slot));
        bytes.AddRange(new byte[] { 0, 2, 0, 6, 0, 0, 0, 6 });
        bytes.AddRange(Key("Plugin isA"));
        bytes.AddRange(new byte[] { 0, 8 });
        bytes.AddRange(BigEndian("VstCtrlInternalEffect".Length + 1));
        bytes.AddRange(Encoding.ASCII.GetBytes("VstCtrlInternalEffect"));
        bytes.Add(0);
        bytes.AddRange(Key("Plugin"));
        bytes.AddRange(new byte[] { 0, 2, 0, 6, 0, 0, 0, 0x16 });
        bytes.AddRange(Key("Plugin UID"));
        bytes.AddRange(new byte[] { 0, 2, 0, 6, 0, 0, 0, 1 });
        bytes.AddRange(Field("GUID", "5653544651337066616266696C746572"));
        bytes.AddRange(Field("Plugin Name", name));
        bytes.AddRange(Key("Audio Input Count"));
        bytes.AddRange(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0, 2 });
        return bytes.ToArray();
    }

    private static byte[] Cpr(params byte[][] parts) => [.. "RIFF"u8, 0, 0, 0, 0, 0, 0, 0, 0, .. "ROOT"u8, .. parts.SelectMany(p => p)];

    private static List<(string Name, string[] Plugins)> Tracks(ProjectHost host, byte[] bytes) =>
        ProjectParser.ParseTracks(host, new MemoryStream(bytes)).Select(t => (t.Name, t.Plugins.Select(p => p.Name).ToArray())).ToList();

    [Fact]
    public void CubaseTracksKeepSlotOrderPerChannel()
    {
        var bytes = Cpr(
            Channel("人声"), InsertFolder(), Plugin("FabFilter Pro-Q 3"), Plugin("API-2500 Stereo"),
            Channel("鼓"), InsertFolder(), Plugin("PuigTec EQP1A Stereo"),
            Channel("Stereo Out"), InsertFolder());

        var tracks = Tracks(ProjectHost.Cubase, bytes);

        Assert.Equal(3, tracks.Count);
        Assert.Equal(["人声", "鼓", "Stereo Out"], tracks.Select(t => t.Name));
        Assert.Equal(["FabFilter Pro-Q 3", "API-2500 Stereo"], tracks[0].Plugins);
        Assert.Equal(["PuigTec EQP1A Stereo"], tracks[1].Plugins);
        Assert.Empty(tracks[2].Plugins);
    }

    [Fact]
    public void CubaseTracksSkipInstrumentsAndMergeInstrumentTrackDuplicates()
    {
        // 乐器轨在工程里出现两次：先是带音源槽的一份，再是音频通道，插件在后一份上。
        var bytes = Cpr(
            Channel("Piano"), InsertFolder(), Plugin("Pianoteq 6 STAGE", slot: "Synth Slot"),
            Channel("Piano"), InsertFolder(), Plugin("FabFilter Pro-Q 3"),
            Plugin("Orphan Before Folder"));

        var track = Assert.Single(Tracks(ProjectHost.Cubase, bytes));
        Assert.Equal("Piano", track.Name);
        Assert.Equal(["FabFilter Pro-Q 3", "Orphan Before Folder"], track.Plugins);
    }

    [Fact]
    public void CubaseTracksIgnorePluginsBeforeAnyInsertFolder()
    {
        var bytes = Cpr(Channel("Input"), Plugin("Not An Insert"), InsertFolder(), Plugin("Real Insert"));

        Assert.Equal(["Real Insert"], Assert.Single(Tracks(ProjectHost.Cubase, bytes)).Plugins);
        Assert.Throws<InvalidDataException>(() => Tracks(ProjectHost.Cubase, "not a project"u8.ToArray()));
    }

    [Fact]
    public void ReaperTracksReadNamesFxChainsAndMaster()
    {
        const string rpp = """
            <REAPER_PROJECT 0.1 "7.0" 1700000000
              <MASTERFXLIST
                <VST "VST3: Pro-L 2 (FabFilter)" "FabFilter Pro-L 2.vst3" 0 "" 1 ""
                >
              >
              <TRACK {A}
                NAME "Lead Vox"
                <FXCHAIN_REC
                  <VST "VST3: Input Thing (Acme)" x.vst3 0 "" 9 ""
                  >
                >
                <FXCHAIN
                  <VST "VST3: Pro-Q 3 (FabFilter)" "FabFilter Pro-Q 3.vst3" 0 "" 1 ""
                  >
                  <VST "VSTi: Kontakt (Native Instruments)" Kontakt.dll 0 "" 3 ""
                  >
                  <VST "VST: ReaEQ (Cockos)" reaeq.dll 0 "" 4 ""
                  >
                >
                <ITEM
                  <TAKEFX
                    <VST "VST3: Clip Thing (Acme)" y.vst3 0 "" 5 ""
                    >
                  >
                >
              >
              <TRACK {B}
                NAME Bass
              >
            >
            """;

        var tracks = ProjectParser.ParseTracks(ProjectHost.Reaper, new MemoryStream(Encoding.UTF8.GetBytes(rpp)));

        Assert.Equal(["Lead Vox", "Bass", "Master"], tracks.Select(t => t.Name));
        Assert.Equal(["Pro-Q 3", "ReaEQ"], tracks[0].Plugins.Select(p => p.Name));
        Assert.True(tracks[0].Plugins[1].Stock);
        Assert.Empty(tracks[1].Plugins);
        Assert.Equal(["Pro-L 2"], tracks[2].Plugins.Select(p => p.Name));
    }

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
    public void StudioOneTracksReadChannelLabelsAndInserts()
    {
        // 结构取自 Studio One 5.1 的真实工程。
        var song = Song("""
            <AudioMixer xmlns:x="internal://schemas/x">
              <ChannelGroup name="AudioOutput">
                <AudioOutputChannel name="Channel01" label="主要">
                  <Attributes x:id="Inserts">
                    <Attributes name="FX01">
                      <Attributes x:id="classInfo" classID="{1}" name="Limiter" category="AudioEffect" subCategory="(Native)/Dynamics"/>
                    </Attributes>
                  </Attributes>
                </AudioOutputChannel>
              </ChannelGroup>
              <ChannelGroup name="AudioTrack">
                <AudioTrackChannel name="Channel01" label="音轨 1">
                  <Attributes x:id="Inserts">
                    <Attributes name="FX01">
                      <Attributes x:id="classInfo" classID="{2}" name="FabFilter Pro-Q 3" category="AudioEffect" subCategory="VST3/EQ"/>
                    </Attributes>
                    <Attributes name="FX02">
                      <Attributes x:id="classInfo" classID="{3}" name="MV2 Mono" category="AudioEffect" subCategory="VST3/Dynamics"/>
                    </Attributes>
                  </Attributes>
                </AudioTrackChannel>
                <AudioTrackChannel name="Channel02" label="空轨">
                  <Attributes x:id="Inserts"/>
                </AudioTrackChannel>
              </ChannelGroup>
            </AudioMixer>
            """);

        var tracks = ProjectParser.ParseTracks(ProjectHost.StudioOne, new MemoryStream(song));

        Assert.Equal(["主要", "音轨 1", "空轨"], tracks.Select(t => t.Name));
        Assert.True(Assert.Single(tracks[0].Plugins).Stock);
        Assert.Equal(["FabFilter Pro-Q 3", "MV2 Mono"], tracks[1].Plugins.Select(p => p.Name));
        Assert.Empty(tracks[2].Plugins);
    }

    private static byte[] Gzip(string text)
    {
        var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return memory.ToArray();
    }

    [Fact]
    public void AbletonTracksReadEffectiveNamesAndPlugins()
    {
        var als = Gzip("""
            <Ableton MajorVersion="5">
              <LiveSet>
                <Tracks>
                  <AudioTrack Id="1">
                    <Name><EffectiveName Value="Vox"/><UserName Value="Vox"/></Name>
                    <DeviceChain><DeviceChain><Devices>
                      <PluginDevice><PluginDesc><Vst3PluginInfo><Name Value="Pro-Q 3"/></Vst3PluginInfo></PluginDesc></PluginDevice>
                      <PluginDevice><PluginDesc><VstPluginInfo><PlugName Value="API-2500 Stereo"/></VstPluginInfo></PluginDesc></PluginDevice>
                    </Devices></DeviceChain></DeviceChain>
                  </AudioTrack>
                  <MidiTrack Id="2">
                    <Name><EffectiveName Value="2-MIDI"/></Name>
                  </MidiTrack>
                </Tracks>
                <MasterTrack>
                  <Name><EffectiveName Value="Master"/></Name>
                  <DeviceChain><Devices>
                    <PluginDevice><PluginDesc><Vst3PluginInfo><Name Value="Pro-L 2"/></Vst3PluginInfo></PluginDesc></PluginDevice>
                  </Devices></DeviceChain>
                </MasterTrack>
              </LiveSet>
            </Ableton>
            """);

        var tracks = ProjectParser.ParseTracks(ProjectHost.Ableton, new MemoryStream(als));

        Assert.Equal(["Vox", "2-MIDI", "Master"], tracks.Select(t => t.Name));
        Assert.Equal(["Pro-Q 3", "API-2500 Stereo"], tracks[0].Plugins.Select(p => p.Name));
        Assert.Equal(["Pro-L 2"], tracks[2].Plugins.Select(p => p.Name));
    }

    [Fact]
    public void TrackImportMatchesLibrarySkipsInstrumentsAndStock()
    {
        var api = Row("API-2500", "Waves");
        var synth = Row("Kontakt", kind: PluginKind.Instrument);
        var matcher = new PluginNameMatcher([api, synth]);
        var track = new ProjectTrack("Vox", [
            new TrackPlugin("API-2500 Stereo"),
            new TrackPlugin("Kontakt"),
            new TrackPlugin("Compressor"),
            new TrackPlugin("ReaEQ", Stock: true),
            new TrackPlugin("UADx LA-2A Silver Compressor"),
        ]);

        var steps = TrackImport.Steps(ProjectHost.Cubase, track, matcher, skipStock: true);

        Assert.Equal([(api.Key, "API-2500"), ("", "UADx LA-2A Silver Compressor")], steps.Select(s => (s.PluginKey, s.PluginName)));
        Assert.Equal(4, TrackImport.Steps(ProjectHost.Cubase, track, matcher, skipStock: false).Count);
        Assert.Equal(ChainStepState.Missing, ChainReport.Resolve(steps[1].Primary, [api]).State);
        Assert.Equal("Mix v3 · Vox", TrackImport.Source(Path.Combine("C:", "Mix v3.cpr"), "Vox"));
    }

    [Fact]
    public void ImportedNotesGetUniqueNamesInTheirFolder()
    {
        using var db = new PluginDatabase(DbPath);
        var folder = db.EnsureFolderPath(["工程 A"]);
        db.AddChain("Vox", folder);

        var created = db.AddImportedChains(folder, [
            ("Vox", "A · Vox", [new ChainStep("k", "K")]),
            ("Vox", "A · Vox", []),
            ("Bass", "A · Bass", []),
        ]);

        Assert.Equal(["Vox (2)", "Vox (3)", "Bass"], created.Select(c => c.Name));
        var loaded = db.LoadChains().Single(c => c.Name == "Vox (2)");
        Assert.Equal(("A · Vox", folder), (loaded.Source, loaded.FolderId));
        Assert.Equal("K", Assert.Single(loaded.Steps).PluginName);
    }

    // ---------- 文件夹、标签、备选 ----------

    [Fact]
    public void FoldersMoveDeleteAndRejectCycles()
    {
        using var db = new PluginDatabase(DbPath);
        var mixer = db.AddNoteFolder(null, "混音师");
        var drums = db.AddNoteFolder(mixer.Id, "鼓组");
        var kick = db.AddChain("Kick", drums.Id);
        db.AddChain("鼓组", null);
        Assert.Throws<ArgumentException>(() => db.AddNoteFolder(mixer.Id, "鼓组"));

        // 不能移到自己的子文件夹里。
        Assert.Throws<ArgumentException>(() => db.MoveNoteFolder(mixer.Id, drums.Id));

        db.MoveChain(kick.Id, mixer.Id);
        Assert.Equal(mixer.Id, db.LoadChains().Single(c => c.Name == "Kick").FolderId);

        // 删除文件夹：里面的内容移到上一层，同名的加序号。
        db.MoveChain(kick.Id, drums.Id);
        db.AddNoteFolder(null, "鼓组");
        db.DeleteNoteFolder(mixer.Id);
        var folders = db.LoadNoteFolders();
        Assert.Equal(["鼓组", "鼓组 (2)"], folders.Select(f => f.Name).Order());
        Assert.All(folders, f => Assert.Null(f.ParentId));
        Assert.Equal(drums.Id, db.LoadChains().Single(c => c.Name == "Kick").FolderId);
        Assert.Equal(["鼓组 (2)"], MixNotes.PathOf(drums.Id, folders));
    }

    [Fact]
    public void ChainTagsAndAlternatesRoundTrip()
    {
        using var db = new PluginDatabase(DbPath);
        var chain = db.AddChain("Lead");
        db.SetChainTags(chain.Id, ["人声", "  ", "人声", "我的标签"]);
        db.SaveChain(chain.Id, "", [
            new ChainStep("eq", "EQ", "先切低频", [new ChainPick("eq2", "EQ 2", "没装 EQ 时"), new ChainPick("eq3", "EQ 3")]),
            new ChainStep("comp", "Comp"),
        ]);

        var loaded = Assert.Single(db.LoadChains());
        Assert.Equal(["人声", "我的标签"], loaded.TagList);
        Assert.Equal(["eq2", "eq3"], loaded.Steps[0].Backups.Select(b => b.PluginKey));
        Assert.Equal("没装 EQ 时", loaded.Steps[0].Backups[0].Note);
        Assert.Empty(loaded.Steps[1].Backups);
        Assert.Equal(["eq", "eq2", "eq3"], loaded.Steps[0].AllPicks.Select(p => p.PluginKey));
    }

    [Fact]
    public void BackupKeepsNoteFoldersTagsAndAlternates()
    {
        string json;
        using (var db = new PluginDatabase(DbPath))
        {
            var folder = db.EnsureFolderPath(["混音师", "鼓组"]);
            var chain = db.AddChain("Kick", folder, ["鼓"], "工程 · Kick");
            db.SaveChain(chain.Id, "笔记", [new ChainStep("a", "A", "", [new ChainPick("b", "B", "备选")])]);
            var entries = BackupRestore.ChainEntries(db.LoadChains(), db.LoadNoteFolders());
            json = UserDataBackup.Serialize(new BackupContent([], [], [], entries, [], null, null), null);
        }

        using var restored = new PluginDatabase(Path.Combine(_dir, "b.db"));
        Assert.Equal(1, restored.ImportChains(UserDataBackup.ParseChains(json)));
        var loaded = Assert.Single(restored.LoadChains());
        Assert.Equal(["混音师", "鼓组"], MixNotes.PathOf(loaded.FolderId, restored.LoadNoteFolders()));
        Assert.Equal(["鼓"], loaded.TagList);
        Assert.Equal(("工程 · Kick", "笔记"), (loaded.Source, loaded.Note));
        Assert.Equal("备选", Assert.Single(Assert.Single(loaded.Steps).Backups).Note);
    }

    [Fact]
    public void OldBackupWishPrioritiesMapToTwoLevels()
    {
        const string json = """
            {"Version":2,"ExportedUtc":"2026-01-01T00:00:00Z","Plugins":[],
             "Wishes":[{"Name":"Must","Priority":3},{"Name":"Normal","Priority":2},{"Name":"Low","Priority":1}]}
            """;

        var wishes = UserDataBackup.Read(json).Content.Wishes;

        Assert.Equal([WishPriority.MustHave, WishPriority.CanWait, WishPriority.CanWait], wishes.Select(w => w.Priority));
        Assert.Equal(("我还能忍", "势在必得"), (WishPriority.Text(WishPriority.CanWait), WishPriority.Text(WishPriority.MustHave)));
    }

    // ---------- 批量删除、连笔记一起删文件夹、导入文件的种类 ----------

    [Fact]
    public void DeletingFolderWithNotesRemovesSubtreeAndRoutesOnly()
    {
        using var db = new PluginDatabase(DbPath);
        var mixer = db.AddNoteFolder(null, "混音师");
        var drums = db.AddNoteFolder(mixer.Id, "鼓组");
        var other = db.AddNoteFolder(null, "通用");
        var kick = db.AddChain("Kick", drums.Id);
        var bus = db.AddChain("Drum Bus", mixer.Id);
        var vocal = db.AddChain("Vocal", other.Id);
        var loose = db.AddChain("Loose");
        db.SaveChain(kick.Id, "", [new ChainStep("k", "K")]);
        // 外面的笔记发送到要删的笔记：路由要跟着删，否则 id 被新笔记复用时会连错。
        db.SetRoutes(vocal.Id, null, [bus.Id]);
        db.SetRoutes(kick.Id, bus.Id, []);

        Assert.Equal(2, db.DeleteNoteFolderWithNotes(mixer.Id));

        Assert.Equal(["通用"], db.LoadNoteFolders().Select(f => f.Name));
        Assert.Equal(["Loose", "Vocal"], db.LoadChains().Select(c => c.Name).Order());
        Assert.Empty(db.LoadRoutes());
        // 不存在的文件夹什么都不做。
        Assert.Equal(0, db.DeleteNoteFolderWithNotes(mixer.Id));
        Assert.Equal(2, db.LoadChains().Count);
    }

    [Fact]
    public void DeleteChainsRemovesOnlyTheGivenNotes()
    {
        using var db = new PluginDatabase(DbPath);
        var a = db.AddChain("A");
        var b = db.AddChain("B");
        var c = db.AddChain("C");
        db.SaveChain(a.Id, "", [new ChainStep("k", "K")]);

        Assert.Equal(2, db.DeleteChains([a.Id, c.Id, a.Id, 9999]));

        Assert.Equal(["B"], db.LoadChains().Select(n => n.Name));
        Assert.Equal(b.Id, db.LoadChains().Single().Id);
        Assert.Equal(0, db.DeleteChains([]));
    }

    [Fact]
    public void ImportKindFollowsTheFileExtension()
    {
        Assert.Equal(ImportKind.Backup, ImportKinds.Of(@"c:\x\WhatPlugin-备份-20261114.json"));
        Assert.Equal(ImportKind.Reviews, ImportKinds.Of("阿吹-插件评价" + ReviewShare.Extension));
        Assert.Equal(ImportKind.Reviews, ImportKinds.Of("A" + ReviewShare.Extension.ToUpperInvariant()));
        Assert.Equal(ImportKind.Notes, ImportKinds.Of(@"d:\share\混音笔记" + NoteShare.Extension));
        // 认不出的当备份读，由备份的读取报格式错误。
        Assert.Equal(ImportKind.Backup, ImportKinds.Of("notes.txt"));
        Assert.Equal(ImportKind.Backup, ImportKinds.Of("没有扩展名"));
    }

    // ---------- 适合音色、分类栏和厂家候选 ----------

    [Fact]
    public void TimbresComeFromTagsAndStayOutOfFunctionTags()
    {
        var row = Row("Amp Thing", tags: ["失真/饱和", "吉他"]);

        Assert.Equal(["失真/饱和"], row.Tags);
        Assert.Equal(["吉他"], row.Timbres);

        var overridden = row with { User = row.User with { TimbresOverride = ["人声"] } };
        Assert.Equal(["人声"], overridden.Timbres);
        Assert.Contains("人声", overridden.KnowledgeTags);
    }

    [Fact]
    public void LibraryCategoriesListFunctionsThenTimbresWithCounts()
    {
        var rows = new[]
        {
            Row("A", tags: ["压缩", "人声"]),
            Row("B", tags: ["压缩"]),
            Row("C", tags: []),
        };

        var categories = LibraryCategories.Build(rows).ToList();

        var all = categories[0];
        Assert.Equal((LibraryCategoryKind.All, 3), (all.Kind, all.Count));
        var comp = categories.Single(c => c.Kind == LibraryCategoryKind.Function && c.Label == "压缩");
        Assert.Equal(2, comp.Count);
        Assert.True(comp.Matches(rows[1]));
        Assert.False(comp.Matches(rows[2]));
        var vocal = categories.Single(c => c.Kind == LibraryCategoryKind.Timbre && c.Label == "人声");
        Assert.Equal(1, vocal.Count);
        Assert.True(categories.IndexOf(comp) < categories.IndexOf(vocal));
        Assert.True(categories.Single(c => c.Kind == LibraryCategoryKind.NoFunction).Matches(rows[2]));
    }

    [Fact]
    public void VendorSuggestionsPutPrefixMatchesFirst()
    {
        var suggestions = VendorCatalog.Suggest("m", ["Mystery Audio", "Hammer Labs"]);

        Assert.Contains("Mystery Audio", suggestions);
        // 以 M 开头的都排在只是包含 m 的前面。
        var starts = suggestions.Select(v => v.StartsWith("m", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(starts.OrderByDescending(x => x), starts);
        Assert.Contains("Hammer Labs", suggestions);
        Assert.Empty(VendorCatalog.Suggest("  ", ["Anything"]));
        Assert.Equal(3, VendorCatalog.Suggest("a", [], max: 3).Count);
    }

    // ---------- v12 迁移 ----------

    [Fact]
    public void Version12MigrationMovesTimbresRemapsWishesAndKeepsChains()
    {
        using (var db = new PluginDatabase(DbPath))
        {
            db.SaveMerged([Merged("amp", "Amp")]);
            db.AddWish(new WishItem(0, "Must"));
            db.AddWish(new WishItem(0, "Low"));
            var old = db.AddChain("Old Chain");
            db.SaveChain(old.Id, "旧笔记", [new ChainStep("amp", "Amp", "一步")]);
        }
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection("Data Source=" + DbPath))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // 退回 v11 的样子：音色混在分类改动里，想要程度三级，插件链名称唯一、没有文件夹和备选。
            // 这里只是把库退回旧结构，先关掉外键，免得删表时连带删掉步骤；迁移本身是在外键打开时跑的。
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                UPDATE plugins SET tags_override_json = '["失真/饱和","吉他"]', timbres_override_json = NULL;
                UPDATE wishes SET priority = 3 WHERE name = 'Must';
                UPDATE wishes SET priority = 2 WHERE name = 'Low';
                DROP TABLE note_folders;
                CREATE TABLE chains_old (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, note TEXT NOT NULL DEFAULT '');
                INSERT INTO chains_old (id, name, note) SELECT id, name, note FROM chains;
                DROP TABLE chains;
                ALTER TABLE chains_old RENAME TO chains;
                ALTER TABLE chain_steps DROP COLUMN alternates_json;
                ALTER TABLE plugins DROP COLUMN timbres_override_json;
                PRAGMA user_version = 11;
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var upgraded = new PluginDatabase(DbPath);
        var row = Assert.Single(upgraded.LoadPlugins());
        Assert.Equal(["失真/饱和"], row.User.TagsOverride);
        Assert.Equal(["吉他"], row.User.TimbresOverride);
        var wishes = upgraded.LoadWishes();
        Assert.Equal(WishPriority.MustHave, wishes.Single(w => w.Name == "Must").Priority);
        Assert.Equal(WishPriority.CanWait, wishes.Single(w => w.Name == "Low").Priority);
        var chain = Assert.Single(upgraded.LoadChains());
        Assert.Equal(("Old Chain", "旧笔记", (long?)null), (chain.Name, chain.Note, chain.FolderId));
        Assert.Equal("一步", Assert.Single(chain.Steps).Note);
        // 名称唯一的限制去掉了：不同文件夹里可以同名。
        var folder = upgraded.AddNoteFolder(null, "F");
        upgraded.AddChain("Old Chain", folder.Id);
        Assert.Equal(2, upgraded.LoadChains().Count);
    }

}
