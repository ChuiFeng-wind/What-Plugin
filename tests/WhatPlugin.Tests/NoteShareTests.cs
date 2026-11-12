using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>混音笔记的分享文件（导出、读取、导入）和左侧导航的自定义顺序。</summary>
public sealed class NoteShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-ns-" + Guid.NewGuid().ToString("N"));

    public NoteShareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "t.db");

    private static ChainStep Step(string plugin, string note = "", params string[] alternates) =>
        new(Normalizer.Name(plugin), plugin, note, alternates.Select(a => new ChainPick(Normalizer.Name(a), a, "备选思路")).ToList());

    /// <summary>导出方：混音师 › 鼓组 下面两条笔记，Kick 发送到 Verb，Verb 侧链到 Kick；外加一条没选中的笔记。</summary>
    private static (NoteFile File, PluginChain Hidden) Exported(PluginDatabase db, bool includeRoutes = true)
    {
        var drums = db.EnsureFolderPath(["混音师", "鼓组"]);
        var kick = db.AddChain("Kick", drums, ["鼓"], source: @"D:\工程\歌.cpr › Kick");
        db.SaveChain(kick.Id, "整体思路", [Step("Pro-Q 3", "切低频", "Kirchhoff-EQ"), Step("Pro-C 2")]);
        db.SetChainRole(kick.Id, ChannelRole.Track);
        var verb = db.AddChain("Verb", drums);
        db.SaveChain(verb.Id, "", [Step("Valhalla Room")]);
        db.SetChainRole(verb.Id, ChannelRole.Effect);
        var hidden = db.AddChain("Private", drums);
        db.SetRoutes(kick.Id, hidden.Id, [verb.Id]);
        db.SetRoutes(verb.Id, null, [], [(kick.Id, "Valhalla Room")]);

        var chains = db.LoadChains().Where(c => c.Id != hidden.Id).ToList();
        var file = NoteShare.Build(" 阿风 ", chains, db.LoadNoteFolders(), db.LoadRoutes(), includeRoutes, new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc));
        return (file, hidden);
    }

    [Fact]
    public void ExportKeepsStepsFoldersAndRoutesButNotSourceOrUnselectedTargets()
    {
        using var db = new PluginDatabase(DbPath);
        var (file, _) = Exported(db);
        var json = NoteShare.Serialize(file);

        Assert.DoesNotContain("工程", json);
        Assert.DoesNotContain(".cpr", json);
        Assert.DoesNotContain("Private", json);

        var read = NoteShare.Parse(json);
        Assert.Equal("阿风", read.Author);
        Assert.Equal(2, read.Notes.Count);
        var kick = read.Notes.Single(n => n.Name == "Kick");
        Assert.Equal(["混音师", "鼓组"], kick.Folder!);
        Assert.Equal(["鼓"], kick.Tags!);
        Assert.Equal("整体思路", kick.Note);
        Assert.Null(kick.Source);
        Assert.Null(kick.Output); // 输出指向没导出的笔记，丢掉
        Assert.Equal("Verb", Assert.Single(kick.Sends!).Name);
        Assert.Equal("切低频", kick.Steps![0].Note);
        Assert.Equal("Kirchhoff-EQ", Assert.Single(kick.Steps[0].Backups).PluginName);
        Assert.Equal("备选思路", kick.Steps[0].Backups[0].Note);
        var verb = read.Notes.Single(n => n.Name == "Verb");
        Assert.Equal(ChannelRole.Effect, verb.Role);
        Assert.Equal("Valhalla Room", Assert.Single(verb.Sidechains!).Plugin);
        Assert.True(read.HasRoutes);
        Assert.Equal(["Pro-Q 3", "Kirchhoff-EQ", "Pro-C 2", "Valhalla Room"], read.Picks.Select(p => p.PluginName));
    }

    [Fact]
    public void ExportWithoutRoutesDropsAllRouting()
    {
        using var db = new PluginDatabase(DbPath);
        var (file, _) = Exported(db, includeRoutes: false);
        var read = NoteShare.Parse(NoteShare.Serialize(file));
        Assert.False(read.HasRoutes);
        Assert.Equal(ChannelRole.Effect, read.Notes.Single(n => n.Name == "Verb").Role);
    }

    [Fact]
    public void ImportGoesIntoNewFolderWithoutTouchingOwnNotesAndReconnectsRoutes()
    {
        string json;
        using (var source = new PluginDatabase(Path.Combine(_dir, "source.db")))
            json = NoteShare.Serialize(Exported(source).File);
        var file = NoteShare.Parse(json);

        using var db = new PluginDatabase(DbPath);
        var own = db.AddChain("Kick", db.EnsureFolderPath(["混音师", "鼓组"]));
        db.SaveChain(own.Id, "我自己的", [Step("SSL Comp")]);
        db.AddNoteFolder(null, NoteShare.RootFolderName(file)); // 以前导入过一次

        var (root, count) = db.ImportSharedNotes(NoteShare.RootFolderName(file), file.Notes);
        Assert.Equal(2, count);
        Assert.Equal("来自 阿风 的笔记 (2)", root.Name);

        var chains = db.LoadChains();
        var folders = db.LoadNoteFolders();
        var mine = chains.Single(c => c.Id == own.Id);
        Assert.Equal("我自己的", mine.Note);
        Assert.Equal("SSL Comp", Assert.Single(mine.Steps).PluginName);

        var kick = chains.Single(c => c.Name == "Kick" && c.Id != own.Id);
        Assert.Equal(["来自 阿风 的笔记 (2)", "混音师", "鼓组"], MixNotes.PathOf(kick.FolderId, folders));
        Assert.Equal("", kick.Source);
        Assert.Equal(["鼓"], kick.TagList);
        Assert.Equal("Kirchhoff-EQ", kick.Steps[0].Backups.Single().PluginName);
        var verb = chains.Single(c => c.Name == "Verb");
        Assert.Equal(ChannelRole.Effect, verb.Role);

        var routes = db.LoadRoutes();
        Assert.Contains(routes, r => r.From == kick.Id && r.To == verb.Id && r.Kind == RouteKind.Send);
        Assert.Contains(routes, r => r.From == verb.Id && r.To == kick.Id && r.Kind == RouteKind.Sidechain && r.Plugin == "Valhalla Room");
        Assert.DoesNotContain(routes, r => r.From == own.Id || r.To == own.Id);
    }

    [Fact]
    public void ImportNumbersDuplicateNamesInsideTheFile()
    {
        var file = new NoteFile("", DateTime.UtcNow,
        [
            new ChainBackupEntry("Vox", "a", [Step("Pro-Q 3")]),
            new ChainBackupEntry("vox", "b", [Step("Pro-C 2")]),
        ]);
        using var db = new PluginDatabase(DbPath);
        var (root, count) = db.ImportSharedNotes(NoteShare.RootFolderName(file), file.Notes);
        Assert.Equal("导入的笔记", root.Name);
        Assert.Equal(2, count);
        Assert.Equal(["Vox", "vox (2)"], db.LoadChains().Where(c => c.FolderId == root.Id).Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\":\"whatplugin-review\",\"version\":1,\"notes\":[]}")]
    [InlineData("{\"format\":\"whatplugin-notes\",\"version\":99,\"notes\":[]}")]
    [InlineData("{\"format\":\"whatplugin-notes\",\"version\":1}")]
    public void ParseRejectsOtherFiles(string json) =>
        Assert.Throws<FormatException>(() => NoteShare.Parse(json));

    [Fact]
    public void ParseCleansUntrustedContent()
    {
        var steps = string.Join(",", Enumerable.Range(0, 300).Select(i => $"{{\"plugin\":\"P{i}\"}}"));
        var folders = string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"F{i}\""));
        var json = $$"""
            {"format":"whatplugin-notes","version":1,"author":"\u202Eevil\u0007",
             "notes":[
               {"name":"  ","steps":[{"plugin":"X"}]},
               null,
               {"name":"Lead\u0000","role":"Nonsense","folder":[{{folders}}],"steps":[{"plugin":""},{{steps}}],"tags":["a","A","b"]}
             ]}
            """;
        var file = NoteShare.Parse(json);
        Assert.Equal("evil", file.Author);
        var note = Assert.Single(file.Notes);
        Assert.Equal("Lead", note.Name);
        Assert.Null(note.Role);
        Assert.Equal(8, note.Folder!.Count);
        Assert.Equal(199, note.Steps!.Count); // 空插件名那一步算在 200 的上限里，丢掉
        Assert.Equal(["a", "b"], note.Tags!);
    }

    // ---------- 左侧导航顺序 ----------

    private static readonly string[] Defaults = ["Plugins", "Projects", "Lists", "Chains", "Idle"];

    [Fact]
    public void NavOrderAppliesSavedOrderAndInsertsNewItemsAfterTheirDefaultNeighbour()
    {
        Assert.Equal(Defaults, NavOrder.Apply(Defaults, NavOrder.Parse(null)));
        Assert.Equal(["Chains", "Plugins", "Projects", "Lists", "Idle"],
            NavOrder.Apply(Defaults, NavOrder.Parse("Chains,Plugins,Projects,Gone,Idle")));
        // Lists 是新加的，放在默认顺序里它前面的 Projects 后面。
        Assert.Equal(["Idle", "Plugins", "Projects", "Lists", "Chains"],
            NavOrder.Apply(Defaults, NavOrder.Parse(" Idle , Plugins,Projects,Chains,Idle")));
    }

    [Fact]
    public void NavOrderMovesItems()
    {
        Assert.Equal(["Projects", "Plugins", "Lists", "Chains", "Idle"], NavOrder.Move(Defaults, "Projects", -1));
        Assert.Null(NavOrder.Move(Defaults, "Plugins", -1));
        Assert.Null(NavOrder.Move(Defaults, "Idle", 1));
        Assert.Null(NavOrder.Move(Defaults, "Missing", 1));
        Assert.Equal(["Idle", "Plugins", "Projects", "Lists", "Chains"], NavOrder.MoveTo(Defaults, "Idle", 0));
        Assert.Equal(["Projects", "Lists", "Chains", "Idle", "Plugins"], NavOrder.MoveTo(Defaults, "Plugins", 99));
        Assert.Null(NavOrder.MoveTo(Defaults, "Lists", 2));
    }

    [Fact]
    public void NavOrderKeepsUninstalledExtensionPositionWhenSaving()
    {
        var saved = NavOrder.Parse("Plugins,ext:成就,Projects,Lists");
        var merged = NavOrder.Merge(saved, ["Projects", "Plugins", "Lists"]);
        Assert.Equal(["Projects", "Plugins", "ext:成就", "Lists"], merged);
        Assert.Equal("Projects,Plugins,ext:成就,Lists", NavOrder.Serialize(merged));
        // 装回来时回到 Plugins 后面。
        Assert.Equal(["Projects", "Plugins", "ext:成就", "Lists"],
            NavOrder.Apply(["Plugins", "Projects", "Lists", "ext:成就"], NavOrder.Parse(NavOrder.Serialize(merged))));
    }

    [Fact]
    public void NavOrderIsStoredInSettings()
    {
        using (var db = new PluginDatabase(DbPath))
            db.SetSetting(NavOrder.Setting, NavOrder.Serialize(["Idle", "Plugins"]));
        using var reopened = new PluginDatabase(DbPath);
        Assert.Equal(["Idle", "Plugins", "Projects", "Lists", "Chains"],
            NavOrder.Apply(Defaults, NavOrder.Parse(reopened.GetSetting(NavOrder.Setting))));
    }
}
