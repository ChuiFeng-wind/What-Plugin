using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>混音笔记的树、文字、多选、路由和轨道导入（Windows 版和跨平台版共用）。</summary>
public sealed class NoteListingTests
{
    private static readonly NoteFolder[] Folders = [new(1, null, "Mixer A"), new(2, 1, "Vocals"), new(3, null, "Empty")];

    private static PluginChain Chain(long id, string name, long? folder, params string[] plugins) =>
        new(id, name, "", plugins.Select(p => new ChainStep("", p)).ToList(), folder);

    private static readonly PluginChain[] Chains =
    [
        Chain(1, "Lead Vox", 2, "soothe2", "Pro-C 2") with { Tags = ["人声"] },
        Chain(2, "Vox Bus", 2, "Pro-C 2") with { Role = ChannelRole.Group },
        Chain(3, "Kick", null),
        Chain(4, "Snare", 1, "Pro-Q 4"),
    ];

    [Fact]
    public void TreeListsFoldersFirstAndKeepsOnlyMatchesWhenSearching()
    {
        var names = new Dictionary<string, string>();
        var tree = NoteListing.Tree(Chains, Folders, "", names);
        Assert.Equal(["Mixer A", "Empty", "Kick"], tree.Select(n => n.Name));
        Assert.Equal(("3", ""), (tree[0].Count, tree[1].Count));
        Assert.Equal(["Vocals", "Snare"], tree[0].Children.Select(n => n.Name));
        Assert.Equal("Lead Vox  ·  人声", tree[0].Children[0].Children[0].ToolTip);

        var found = NoteListing.Tree(Chains, Folders, "pro-c 人声", names);
        Assert.Equal(["Mixer A"], found.Select(n => n.Name));
        Assert.Equal(["Lead Vox"], found[0].Children.Single().Children.Select(n => n.Name));
    }

    [Fact]
    public void TextsDescribeFoldersNotesAndPaths()
    {
        Assert.Equal("最外层", NoteListing.FolderPath(null, Folders));
        Assert.Equal("Mixer A › Vocals", NoteListing.FolderPath(2, Folders));
        Assert.Equal("Mixer A  ·  3 条笔记，1 个子文件夹", NoteListing.FolderMeta(Folders[0], Chains, Folders));
        Assert.Equal(["Snare", "Lead Vox", "Vox Bus"], NoteListing.FolderNotes(Folders[0], Chains, Folders).Select(r => r.Name));
        Assert.Equal("Mixer A › Vocals  ·  soothe2 → Pro-C 2  ·  人声", NoteListing.FolderNotes(Folders[0], Chains, Folders)[1].Summary);
        Assert.Equal("Mixer A › Vocals  ·  2 步，按从上到下的顺序  ·  1 个备选  ·  首选里 1 款没安装  ·  导入自 Song.rpp",
            NoteListing.ChainMeta(Chains[0] with { Source = "Song.rpp" }, Folders, 2, 1, 1, 0));
        Assert.Equal(["最外层", "Empty"], NoteListing.MoveTargets(Folders, MixNotes.Subtree(1, Folders)).Select(o => o.Text));
        Assert.Equal("未安装的 2 款加入心愿单", NoteListing.WishMissingText(2));
    }

    [Fact]
    public void DeletePromptsOfferToDeleteNotesOnlyWhenThereAreSome()
    {
        var (empty, noOption) = NoteListing.DeleteFolderPrompt(Folders[2], Chains, Folders);
        Assert.Equal("删除空文件夹“Empty”？", empty);
        Assert.Null(noOption);

        var (message, option) = NoteListing.DeleteFolderPrompt(Folders[0], Chains, Folders);
        Assert.Equal("删除文件夹“Mixer A”？里面的 1 条笔记和1 个子文件夹会移到“最外层”，不会删除任何笔记。", message);
        Assert.Equal("同时删除笔记：这个文件夹和子文件夹里的 3 条笔记、1 个子文件夹一起删除，不能恢复", option);

        // 选中文件夹又选中笔记：加标签对文件夹里的也生效。
        var batch = NoteBatch.Of([Folders[1]], [Chains[2]], Folders, Chains);
        Assert.Equal("1 条笔记、1 个文件夹", batch.Describe());
        Assert.Equal(["Kick", "Lead Vox", "Vox Bus"], batch.TagTargets.Select(c => c.Name));
        var (title, text, check) = batch.DeletePrompt();
        Assert.Equal("批量删除", title);
        Assert.Contains("文件夹里的 2 条笔记默认移到上一层", text);
        Assert.Equal("同时删除文件夹里的 2 条笔记（不能恢复）", check);
        Assert.Equal((null as string), NoteBatch.Of([], [Chains[2]], Folders, Chains).DeletePrompt().Option);
    }

    [Fact]
    public void RoutePanelListsOutputsSendsAndIncoming()
    {
        var verb = Chain(5, "Verb", 2, "ValhallaRoom") with { Role = ChannelRole.Effect };
        var chains = Chains.Append(verb).ToList();
        var routes = new[] { new NoteRoute(1, 2, RouteKind.Output), new NoteRoute(1, 5, RouteKind.Send), new NoteRoute(1, 5, RouteKind.Sidechain, "ValhallaRoom") };

        var panel = NoteRouting.Panel(chains[0], chains, routes, Folders);
        // 效果轨不能当输出；当前输出选中。
        Assert.Equal(["主输出", "Vox Bus（编组）"], panel.Outputs.Select(o => o.Text));
        Assert.Equal(1, panel.OutputIndex);
        Assert.Equal(["Verb"], panel.Sends.Select(s => s.Name));
        Assert.Equal([("Verb", "ValhallaRoom")], panel.Sidechains.Select(s => (s.Target.Name, s.Plugin)));

        var incoming = NoteRouting.Panel(verb, chains, routes, Folders);
        Assert.Equal("接入 2 路：Lead Vox（发送）、Lead Vox（侧链 · ValhallaRoom）", incoming.Incoming);
        Assert.Equal(["Vox Bus"], NoteRouting.SendOptions(chains[0], chains, routes).Select(c => c.Name));
        Assert.Equal(["Vox Bus", "Verb"], NoteRouting.SidechainOptions(chains[0], chains).Select(c => c.Name));
    }

    [Fact]
    public void TrackImportPlanKeysRoutesWithinTheImport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pv-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Song X.rpp");
            static string Fx(string name) => $"    <FXCHAIN\n      <VST \"VST3: {name}\" \"x.vst3\" 0 \"\" 1 \"\"\n      >\n    >\n";
            File.WriteAllText(path,
                "<REAPER_PROJECT 0.1 \"7.0\" 1700000000\n"
                + "  <TRACK {A}\n    NAME Drums\n    ISBUS 1 1\n  >\n"
                + "  <TRACK {B}\n    NAME Kick\n    ISBUS 2 -1\n" + Fx("Pro-Q 4 (FabFilter)") + "  >\n"
                + "  <TRACK {C}\n    NAME Pad\n  >\n"
                + ">\n");

            var plan = new TrackImportPlan(path, ProjectParser.ParseTracks(path), []);
            // 没插件的 Pad 不列出；没插件的编组 Drums 因为接了轨道要列出。
            Assert.Equal(["Drums", "Kick"], plan.Candidates.Select(t => t.Name));
            Assert.True(plan.HasRouting);
            Assert.StartsWith("Kick　Pro-Q 4（未安装）", plan.Line(plan.Candidates[1]));

            var notes = plan.Notes([1], withDestinations: true);
            Assert.Equal(["Drums", "Kick"], notes.Select(n => n.Name).Order());
            var kick = notes.First(n => n.Name == "Kick");
            Assert.Equal(notes.First(n => n.Name == "Drums").Key, kick.OutputKey);
            Assert.Equal(["Kick"], plan.Notes([1], withDestinations: false).Select(n => n.Name));

            var folders = TrackImportPlan.FolderOptions(plan.ProjectName, 2, Folders);
            Assert.Equal("“Mixer A › Vocals”下的“Song X”文件夹", folders[0].Text);
            Assert.True(folders[0].Create);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
