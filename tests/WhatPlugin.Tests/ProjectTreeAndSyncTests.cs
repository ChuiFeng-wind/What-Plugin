using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

using static WhatPlugin.Tests.TestPaths;

namespace WhatPlugin.Tests;

public sealed class ProjectTreeAndSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-t-" + Guid.NewGuid().ToString("N"));
    private static long _nextId;

    public ProjectTreeAndSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static ProjectEntry Project(string folder, string name = "歌", bool missing = false) =>
        new(folder + "\\" + name, name, folder + "\\" + name + ".cpr", folder, ProjectHost.Cubase, DateTime.UtcNow, 1,
            ProjectScanStatus.Ok, null,
            missing ? [new ProjectPluginLine("缺的插件", 1, ProjectPluginState.Missing, null)] : []);

    private static PluginRow Row(string name, string? tag = "压缩", bool hidden = false, bool missing = false, PluginKind kind = PluginKind.Effect) =>
        new(++_nextId, Normalizer.Name(name), name, "Acme", kind, tag == null ? [] : [tag], ClassificationSource.Seed, missing, [],
            new PluginUserData(0, "", [], null, null, null, hidden, "", null));

    private static PluginUsage Used(int projects) => new(projects, projects, DateTime.UtcNow, ["工程"]);

    private static readonly Dictionary<long, IReadOnlySet<string>> NoRemovals = [];

    // ---------- 文件夹树 ----------

    [Fact]
    public void FoldersCountEverythingBelowThem()
    {
        var root = ProjectFolderTree.Build([P(@"D:\工程")],
        [
            Project(P(@"D:\工程\混音\A"), "一"),
            Project(P(@"D:\工程\混音\A"), "二", missing: true),
            Project(P(@"D:\工程\混音\B"), "三"),
            Project(P(@"D:\工程\编曲"), "四"),
            Project(P(@"D:\工程\编曲"), "五"),
        ]).Single();

        Assert.Equal(5, root.ProjectCount);
        Assert.Equal(1, root.MissingProjects);
        var mix = root.Children.Single(c => c.Name == "混音");
        Assert.Equal(3, mix.ProjectCount);
        Assert.Equal(P(@"D:\工程\混音"), mix.Path);
        Assert.Equal("A", mix.Children.Single().Name);
    }

    [Fact]
    public void FolderHoldingOneSongIsNotItsOwnLevel()
    {
        var root = ProjectFolderTree.Build([P(@"D:\工程")],
        [
            Project(P(@"D:\工程\混音\歌一"), "歌一"),
            Project(P(@"D:\工程\混音\歌二"), "歌二"),
        ]).Single();

        var mix = root.Children.Single();
        Assert.Equal("混音", mix.Name);
        Assert.Empty(mix.Children);
    }

    [Fact]
    public void PassThroughFoldersAreMerged()
    {
        var root = ProjectFolderTree.Build([P(@"D:\工程")],
        [
            Project(P(@"D:\工程\客户A\2026"), "一"),
            Project(P(@"D:\工程\客户A\2026"), "二"),
            Project(P(@"D:\工程\其他"), "三"),
        ]).Single();

        var merged = root.Children.Single(c => c.ProjectCount == 2);
        Assert.Equal("客户A / 2026", merged.Name);
        Assert.Equal(P(@"D:\工程\客户A\2026"), merged.Path);
    }

    [Fact]
    public void RootOnlyContainingFolderKeepsTheRootVisible()
    {
        var roots = ProjectFolderTree.Build([P(@"D:\工程\"), P(@"D:\工程\混音"), P(@"E:\备份")],
        [
            Project(P(@"D:\工程\混音"), "一"),
            Project(P(@"E:\备份"), "二"),
            Project(P(@"F:\别处"), "三"),
        ]);

        // 被包含的文件夹并入外层，不在已添加文件夹里的工程自成一组。
        Assert.Equal([P(@"D:\工程"), P(@"E:\备份"), P(@"F:\别处")], roots.Select(r => r.Path).Order());
    }

    [Fact]
    public void ContainsComparesWholeFolderNames()
    {
        Assert.True(ProjectFolderTree.Contains(P(@"D:\混音"), P(@"D:\混音")));
        Assert.True(ProjectFolderTree.Contains(P(@"D:\混音\"), P(@"d:\混音\A")));
        Assert.False(ProjectFolderTree.Contains(P(@"D:\混音"), P(@"D:\混音2")));
        Assert.False(ProjectFolderTree.Contains(P(@"D:\混音\A"), P(@"D:\混音")));
    }
}
