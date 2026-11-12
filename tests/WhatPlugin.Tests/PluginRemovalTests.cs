using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public sealed class PluginRemovalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-rm-" + Guid.NewGuid().ToString("N"));
    private static long _nextId;

    public PluginRemovalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MergedFile File1(string path, PluginFormat format = PluginFormat.Vst2, ScanStatus status = ScanStatus.FileInfoOnly) =>
        new(path, format, Arch.X64, "1", "", status, null);

    private static PluginRow Row(string name, string vendor = "Acme", params MergedFile[] files) =>
        new(++_nextId, Normalizer.Name(name), name, vendor, PluginKind.Effect, [], ClassificationSource.None, false, files,
            new PluginUserData(0, "", [], null, null, null, true, "", null));

    private static InstalledProgram Program(string name, string publisher = "Acme", string location = "", bool msi = false) =>
        new("k-" + name, name, publisher, "1.0", "\"C:\\x\\unins000.exe\"", location, msi);

    private static readonly Dictionary<string, PluginUsage> NoUsage = [];

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "假插件");
        return path;
    }

    // ---------- 卸载程序匹配 ----------

    [Fact]
    public void PluginUnderInstallLocationIsExact()
    {
        var row = Row("Foo", "Acme", File1(@"D:\Apps\FooSuite\Foo.dll"));
        var match = UninstallMatcher.Find(row, [Program("Foo Suite", "Acme Inc.", @"D:\Apps\FooSuite\")]);
        Assert.Equal("Foo Suite", match.Exact?.Name);
    }

    [Fact]
    public void ProgramNameCanStandInForPublisherUnderInstallLocation()
    {
        var row = Row("wdm2vst", "O Deus Audio", File1(@"D:\Apps\Tools\wdm2vst.dll"));
        var match = UninstallMatcher.Find(row, [Program("O Deus WDM2VST v1.1", "Team X", @"D:\Apps\Tools\")]);
        Assert.Equal("O Deus WDM2VST v1.1", match.Exact?.Name);
    }

    [Fact]
    public void AnotherVendorsProgramRegisteredOnASharedFolderIsNeverOffered()
    {
        // 真实电脑上的情况：SoundToys 把用户的公共 VST 文件夹登记成了自己的安装目录。
        var row = Row("Pro-Q 3", "FabFilter", File1(@"F:\vst64\FabFilter\Pro-Q 3.dll"));
        var match = UninstallMatcher.Find(row, [Program("SoundToys 5", "SoundToys", @"F:\vst64\")]);
        Assert.Null(match.Exact);
        Assert.Empty(match.Possible);
    }

    [Fact]
    public void SharedFolderProgramStillMatchesItsOwnPlugin()
    {
        var row = Row("EchoBoy", "SoundToys", File1(@"F:\vst64\EchoBoy.dll"));
        var match = UninstallMatcher.Find(row, [Program("SoundToys 5", "SoundToys", @"F:\vst64\")]);
        Assert.Equal("SoundToys 5", match.Exact?.Name);
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\Common Files")]
    [InlineData(@"C:\Program Files\Common Files\VST3")]
    [InlineData(@"C:\Program Files\Common Files\Avid\Audio")]
    public void GeneralInstallLocationsAreNotEvidence(string location)
    {
        var row = Row("Foo", "Whoever", File1(Path.Combine(location, "Foo.dll")));
        Assert.Null(UninstallMatcher.Find(row, [Program("Something", "Other", location)]).Exact);
    }

    [Theory]
    [InlineData("Ample Guitar M", "Ample Guitar M 3.5.2", "Ample Sound")]
    [InlineData("Ample Guitar M", "Ample Sound Ample Guitar M", "Nobody")]
    [InlineData("Foo Comp", "Foo Comp version 1.2.0 (x64)", "Ample Sound Inc.")]
    [InlineData("Foo Comp", "Foo Comp 2", "Ample Sound")]
    public void NameWithPublisherIsExact(string plugin, string program, string publisher)
    {
        var row = Row(plugin, "Ample Sound");
        Assert.Equal(program, UninstallMatcher.Find(row, [Program(program, publisher)]).Exact?.Name);
    }

    [Fact]
    public void SameNameFromAnotherPublisherIsNotExact()
    {
        var row = Row("Comp", "Acme");
        Assert.Null(UninstallMatcher.Find(row, [Program("Comp", "Zeta Systems")]).Exact);
    }

    [Fact]
    public void VendorOnlyIsPossibleNeverExact()
    {
        var row = Row("Pro-Q 4", "FabFilter");
        var match = UninstallMatcher.Find(row, [Program("FabFilter Total Bundle", "FabFilter"), Program("Notepad++", "Don")]);
        Assert.Null(match.Exact);
        Assert.Equal("FabFilter Total Bundle", Assert.Single(match.Possible).Name);
    }

    [Theory]
    [InlineData("\"C:\\A B\\unins000.exe\" /S", "C:\\A B\\unins000.exe", "/S")]
    [InlineData("C:\\A B\\uninstall.exe /x /y", "C:\\A B\\uninstall.exe", "/x /y")]
    [InlineData("MsiExec.exe /X{B10780BE}", "MsiExec.exe", "/X{B10780BE}")]
    [InlineData("\"C:\\A\\u.exe\"", "C:\\A\\u.exe", "")]
    [InlineData("rundll32 foo.dll,Bar", "rundll32", "foo.dll,Bar")]
    public void UninstallCommandsParse(string command, string file, string args)
    {
        var parsed = UninstallCommand.Parse(command);
        Assert.Equal(file, parsed?.FileName);
        Assert.Equal(args, parsed?.Arguments);
    }

    // ---------- 方案 ----------

    [Theory]
    [InlineData("Waves", "C4", true)]
    [InlineData("Waves Audio", "C4", true)]
    [InlineData("Acme", "WaveShell1-VST3 14.5", true)]
    [InlineData("Waves Factory", "Cassette", false)]
    [InlineData("Steinberg", "WaveLab Exporter", false)]
    public void WavesDetection(string vendor, string name, bool expected)
    {
        var row = Row(name, vendor, File1($@"D:\x\{name}.dll"));
        Assert.Equal(expected, RemovalPlanner.IsWaves(row));
    }

    [Fact]
    public void PlanSkipsFilesSharedWithUnselectedPlugins()
    {
        var a = Row("A", "Acme", File1(@"D:\x\Shell.dll"), File1(@"D:\x\A.dll"));
        var b = Row("B", "Acme", File1(@"D:\x\Shell.dll"));

        var only = Assert.Single(RemovalPlanner.Plan([a], [a, b], [], NoUsage, _ => true, _ => 7));
        Assert.Equal(@"D:\x\A.dll", Assert.Single(only.Files).Path);
        Assert.Equal(@"D:\x\Shell.dll", Assert.Single(only.SharedFiles));

        var both = RemovalPlanner.Plan([a, b], [a, b], [], NoUsage, _ => true, _ => 7);
        Assert.Empty(both[0].SharedFiles);
        Assert.Equal(2, both[0].Files.Count);
    }

    [Fact]
    public void PlanIgnoresMissingFilesAndListsBundleMates()
    {
        var a = Row("Alpha", "Acme", File1(@"D:\Suite\Alpha.dll"), File1(@"D:\Suite\gone.dll"));
        var b = Row("Beta", "Acme", File1(@"D:\Suite\Beta.dll"));
        var usage = new Dictionary<string, PluginUsage> { [a.Key] = new(4, 4, DateTime.UtcNow, ["工程"]) };

        var plan = Assert.Single(RemovalPlanner.Plan([a], [a, b], [Program("Suite", "Acme", @"D:\Suite")], usage, p => !p.Contains("gone"), _ => 1));

        Assert.Equal("Suite", plan.Exact?.Name);
        Assert.Equal(@"D:\Suite\Alpha.dll", Assert.Single(plan.Files).Path);
        Assert.Equal("Beta", Assert.Single(plan.AlsoInProgram));
        Assert.Equal(4, plan.Projects);
    }

    // ---------- 删除格式 ----------

    [Fact]
    public void FormatRemovalKeepsLastUsableVersion()
    {
        var vst3 = File1(@"D:\x\A.vst3", PluginFormat.Vst3, ScanStatus.Loaded);
        var vst2 = File1(@"D:\x\A.dll");
        var row = Row("A", "Acme", vst3, vst2);

        Assert.Null(RemovalPlanner.CheckFormatRemoval(row, [vst2.Path], [row], _ => true));
        Assert.Contains("最后一个", RemovalPlanner.CheckFormatRemoval(row, [vst2.Path, vst3.Path], [row], _ => true));
        // 剩下的文件不在磁盘上，也算没有可用版本。
        Assert.Contains("最后一个", RemovalPlanner.CheckFormatRemoval(row, [vst2.Path], [row], p => p != vst3.Path));
        Assert.Contains("还没有选择", RemovalPlanner.CheckFormatRemoval(row, [], [row], _ => true));
    }

    [Fact]
    public void FailedVersionDoesNotCountAsUsable()
    {
        var bad = File1(@"D:\x\A.vst3", PluginFormat.Vst3, ScanStatus.LoadFailed);
        var good = File1(@"D:\x\A.dll");
        var row = Row("A", "Acme", bad, good);

        Assert.Contains("最后一个", RemovalPlanner.CheckFormatRemoval(row, [good.Path], [row], _ => true));
        Assert.Null(RemovalPlanner.CheckFormatRemoval(row, [bad.Path], [row], _ => true));
    }

    [Fact]
    public void FormatRemovalRefusesWavesSharedAndForeignFiles()
    {
        var waves = Row("C4", "Waves", File1(@"D:\x\WaveShell.dll"), File1(@"D:\x\WaveShell.vst3", PluginFormat.Vst3));
        Assert.Contains("Waves", RemovalPlanner.CheckFormatRemoval(waves, [@"D:\x\WaveShell.dll"], [waves], _ => true));

        var a = Row("A", "Acme", File1(@"D:\x\Shell.dll"), File1(@"D:\x\A.vst3", PluginFormat.Vst3));
        var b = Row("B", "Acme", File1(@"D:\x\Shell.dll"));
        Assert.Contains("共用", RemovalPlanner.CheckFormatRemoval(a, [@"D:\x\Shell.dll"], [a, b], _ => true));
        Assert.Contains("不属于", RemovalPlanner.CheckFormatRemoval(a, [@"C:\Windows\explorer.dll"], [a, b], _ => true));
    }

    // ---------- 白名单 ----------

    [Fact]
    public void GuardAcceptsPluginInsideRoot()
    {
        var dll = Touch(@"root\Sub\Foo.dll");
        Assert.Null(RemovalGuard.Reject(dll, [Path.Combine(_dir, "root")]));
    }

    [Fact]
    public void GuardRejectsEverythingElse()
    {
        var root = Path.Combine(_dir, "root");
        var dll = Touch(@"root\Foo.dll");
        var text = Touch(@"root\Foo.txt");
        var outside = Touch(@"other\Foo.dll");

        Assert.NotNull(RemovalGuard.Reject(outside, [root]));
        Assert.NotNull(RemovalGuard.Reject(text, [root]));
        Assert.NotNull(RemovalGuard.Reject(Path.Combine(root, "gone.dll"), [root]));
        Assert.NotNull(RemovalGuard.Reject("Foo.dll", [root]));
        Assert.NotNull(RemovalGuard.Reject(root + @"\..\other\Foo.dll", [root]));
        Assert.NotNull(RemovalGuard.Reject(@"\\server\share\Foo.dll", [@"\\server\share"]));
        Assert.NotNull(RemovalGuard.Reject(root, [root]));
        // 用户文件夹不能把整个驱动器或系统目录变成可操作范围。
        Assert.NotNull(RemovalGuard.Reject(dll, [Path.GetPathRoot(dll)!]));
        var system = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        Assert.NotNull(RemovalGuard.Reject(system, [Environment.SystemDirectory]));
        Assert.NotNull(RemovalGuard.Reject(system, [Path.GetPathRoot(system)!]));
    }

    [Fact]
    public void GuardRejectsPathsThroughJunctions()
    {
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Foo.dll"), "x");
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "link");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
            process!.WaitForExit();
        Assert.True(Directory.Exists(link));

        Assert.NotNull(RemovalGuard.Reject(Path.Combine(link, "Foo.dll"), [root]));

        Directory.Delete(link);
    }

    // ---------- 回收站 ----------

    private static byte[] InfoV2(string original, DateTime deleted)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2L);
        writer.Write(123L);
        writer.Write(deleted.ToFileTimeUtc());
        writer.Write(original.Length + 1);
        writer.Write(Encoding.Unicode.GetBytes(original + "\0"));
        return stream.ToArray();
    }

    private static byte[] InfoV1(string original, DateTime deleted)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1L);
        writer.Write(123L);
        writer.Write(deleted.ToFileTimeUtc());
        var path = new byte[520];
        Encoding.Unicode.GetBytes(original).CopyTo(path, 0);
        writer.Write(path);
        return stream.ToArray();
    }

    private string FakeBin(string original, string id, DateTime deleted, bool v1 = false)
    {
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "$I" + id), v1 ? InfoV1(original, deleted) : InfoV2(original, deleted));
        File.WriteAllText(Path.Combine(bin, "$R" + id), id);
        return bin;
    }

    [Fact]
    public void FindsLatestRecordInBothFormats()
    {
        var path = @"D:\x\Foo.dll";
        var early = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bin = FakeBin(path, "AAA.dll", early, v1: true);
        FakeBin(path, "BBB.dll", early.AddDays(1));
        FakeBin(@"D:\x\Other.dll", "CCC.dll", early.AddDays(2));

        var entry = RecycleBin.Find(path.ToUpperInvariant(), bin);

        Assert.Equal(path, entry?.OriginalPath);
        Assert.EndsWith("$RBBB.dll", entry?.RecycledPath);
        Assert.Null(RecycleBin.Find(@"D:\x\None.dll", bin));
    }

    [Fact]
    public void BrokenInfoFilesAreIgnored()
    {
        var bin = FakeBin(@"D:\x\Foo.dll", "AAA.dll", DateTime.UtcNow);
        File.WriteAllBytes(Path.Combine(bin, "$Ishort"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(bin, "$Ibadlen"), [.. BitConverter.GetBytes(2L), .. new byte[16], .. BitConverter.GetBytes(int.MaxValue)]);

        Assert.NotNull(RecycleBin.Find(@"D:\x\Foo.dll", bin));
    }

    [Fact]
    public void RestorePutsFileBackAndRefusesToOverwrite()
    {
        var root = Path.Combine(_dir, "root");
        var path = Path.Combine(root, "Sub", "Foo.dll");
        var bin = FakeBin(path, "AAA.dll", DateTime.UtcNow);
        var request = new FileOpRequest(FileOpKind.Restore, [path], [root]);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "占位");
        var refused = Assert.Single(FileOpExecutor.Execute(request, bin));
        Assert.False(refused.Ok);
        Assert.Equal("占位", File.ReadAllText(path));

        File.Delete(path);
        Directory.Delete(Path.GetDirectoryName(path)!);
        Assert.True(Assert.Single(FileOpExecutor.Execute(request, bin)).Ok);
        Assert.Equal("AAA.dll", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(bin));

        Assert.Equal("回收站里已经没有这个文件", Assert.Single(FileOpExecutor.Execute(request, bin)).Error);
    }

    [Fact]
    public void RestoreRefusesPathsOutsideRoots()
    {
        var bin = FakeBin(@"C:\Windows\evil.dll", "AAA.dll", DateTime.UtcNow);

        var result = Assert.Single(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.Restore, [@"C:\Windows\evil.dll"], [Path.Combine(_dir, "root")]), bin));

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(bin, "$RAAA.dll")));
    }

    [Fact]
    public void RealRecycleBinRoundTrip()
    {
        var root = Path.Combine(_dir, "root");
        var dll = Touch(@"root\Bundle\Foo.dll");

        var sent = Assert.Single(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.Recycle, [dll], [root])));

        Assert.True(sent.Ok, sent.Error);
        Assert.False(sent.Unconfirmed);
        Assert.False(File.Exists(dll));

        var back = Assert.Single(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.Restore, [dll], [root])));

        Assert.True(back.Ok, back.Error);
        Assert.Equal("假插件", File.ReadAllText(dll));
    }

    [Fact]
    public void RecycleRejectsWithoutTouchingFile()
    {
        var text = Touch(@"root\notes.txt");

        var result = Assert.Single(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.Recycle, [text], [Path.Combine(_dir, "root")])));

        Assert.False(result.Ok);
        Assert.True(File.Exists(text));
    }

    // ---------- 管道和提权判断 ----------

    [Fact]
    public async Task HelperAppliesGuardToRequestsFromPipe()
    {
        var outside = Touch(@"other\Foo.dll");
        var pipe = "WhatPlugin-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var helper = Task.Run(() => FileOpRunner.Serve(pipe));
        var results = await FileOpRunner.ExchangeAsync(server, new FileOpRequest(FileOpKind.Recycle, [outside], [Path.Combine(_dir, "root")]), timeout.Token);

        Assert.Equal(0, await helper);
        Assert.False(Assert.Single(results).Ok);
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData("other-pipe")]
    [InlineData(@"WhatPlugin-..\..\x")]
    [InlineData("")]
    public void HelperRefusesForeignPipeNames(string name) => Assert.Equal(2, FileOpRunner.Serve(name));

    [Fact]
    public void ElevationOnlyForProtectedFolders()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        Assert.True(FileOpRunner.NeedsElevation(Path.Combine(common, "VST3", "Foo.vst3")));
        Assert.False(FileOpRunner.NeedsElevation(@"F:\vst64\Foo.dll"));
    }

    // ---------- 数据库 ----------

    private static MergedPlugin Merged(string key, string name, string path) =>
        new(key, name, "Acme", InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, ["EQ"], ClassificationSource.SdkCategory),
            [new MergedFile(path, PluginFormat.Vst2, Arch.X64, "1", "", ScanStatus.FileInfoOnly, null)]);

    [Fact]
    public void RemovedFilesAreRemembered()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        var when = new DateTime(2026, 11, 7, 1, 2, 3, DateTimeKind.Utc);

        db.SaveRemovedFile("foo", "Foo", @"D:\x\Foo.dll", PluginFormat.Vst2, 42, when);
        db.SaveRemovedFile("foo", "Foo", @"d:\X\foo.DLL", PluginFormat.Vst2, 43, when);

        var file = Assert.Single(db.LoadRemovedFiles());
        Assert.Equal(43, file.Size);
        Assert.Equal(when, file.RemovedUtc);
        db.DeleteRemovedFile(@"D:\X\FOO.dll");
        Assert.Empty(db.LoadRemovedFiles());
    }

    [Fact]
    public void ForgetUninstalledKeepsVerdictsInTrashAndDropsEmptyRows()
    {
        using var db = new PluginDatabase(Path.Combine(_dir, "t.db"));
        db.SaveMerged([Merged("rated", "Rated", "a.dll"), Merged("reason", "Reasoned", "b.dll"), Merged("empty", "Empty", "c.dll"), Merged("kept", "Kept", "d.dll")]);
        foreach (var row in db.LoadPlugins())
            db.SaveUserData(row.Id, row.User with
            {
                Hidden = true,
                BlacklistReason = row.Key is "rated" or "reason" ? "太吃 CPU" : "",
                Rating = row.Key == "rated" ? 2 : 0,
            });
        var ids = db.LoadPlugins().Select(r => r.Id).ToList();
        // kept 的文件还在，其余三款已经卸载。
        db.SaveMerged([Merged("kept", "Kept", "d.dll")]);

        db.ForgetUninstalled(ids);

        var rows = db.LoadPlugins().ToDictionary(r => r.Key);
        Assert.False(rows.ContainsKey("empty"));
        // 写过原因或评分的留在垃圾桶里，原因不清空。
        Assert.True(rows["rated"].User.Hidden);
        Assert.Equal(("太吃 CPU", 2), (rows["rated"].User.BlacklistReason, rows["rated"].User.Rating));
        Assert.True(rows["reason"].User.Hidden);
        Assert.Equal("太吃 CPU", rows["reason"].User.BlacklistReason);
        Assert.True(TrashMemory.IsRemembered(rows["reason"]));
        Assert.False(TrashMemory.IsRemembered(rows["kept"]));
        Assert.Equal("移入原因：太吃 CPU；评分 2 星", TrashMemory.Describe(rows["rated"]));

        // 手动删除记录：文件还在的不删。
        Assert.False(db.DeleteUninstalledRecord(rows["kept"].Id));
        Assert.True(db.DeleteUninstalledRecord(rows["reason"].Id));
        Assert.DoesNotContain(db.LoadPlugins(), r => r.Key == "reason");

        // 又装回来了：仍在垃圾桶里，扫描后能认出来。
        var before = db.LoadPlugins().Where(TrashMemory.IsRemembered).Select(r => r.Id).ToHashSet();
        db.SaveMerged([Merged("kept", "Kept", "d.dll"), Merged("rated", "Rated", "a.dll")]);
        var returned = Assert.Single(TrashMemory.Returned(before, db.LoadPlugins()));
        Assert.Equal("rated", returned.Key);
        Assert.True(returned.User.Hidden);
    }

    [Fact]
    public void TrashMemoryFindsUninstalledByNameButNotWhileInstalled()
    {
        var gone = Row("Pro-R 2", "FabFilter") with { IsMissing = true, User = PluginUserData.Empty with { Hidden = true, BlacklistReason = "不好用" } };
        var restored = Row("Valhalla Room") with { IsMissing = true, User = PluginUserData.Empty };
        Assert.Same(gone, TrashMemory.Find("Pro-R 2", [gone, restored]));
        // 带厂家前缀也能认出来。
        Assert.Same(gone, TrashMemory.Find("FabFilter Pro-R 2", [gone, restored]));
        // 只是缺失、没进过垃圾桶的不算。
        Assert.Null(TrashMemory.Find("Valhalla Room", [gone, restored]));
        // 另一个格式还装着，不提醒。
        Assert.Null(TrashMemory.Find("Pro-R 2", [gone, Row("Pro-R 2", "FabFilter")]));
        Assert.Null(TrashMemory.Find(" ", [gone]));
    }

    [Fact]
    public void MigratesFromSchemaNine()
    {
        var path = Path.Combine(_dir, "t.db");
        using (var db = new PluginDatabase(path))
            db.SaveMerged([Merged("foo", "Foo", "a.dll")]);
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE removed_files; PRAGMA user_version = 9;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var migrated = new PluginDatabase(path);
        migrated.SaveRemovedFile("foo", "Foo", "a.dll", PluginFormat.Vst2, 1, DateTime.UtcNow);

        Assert.Single(migrated.LoadRemovedFiles());
        Assert.Single(migrated.LoadPlugins());
    }
}
