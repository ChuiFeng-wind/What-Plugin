using System.Text.Json;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>全部在临时假目录里运行，文件“回收”用直接删除代替，不碰真实的回收站和注册表。</summary>
public sealed class WavesCleanupTests : IDisposable
{
    private const string Code = "{B10780BE-614D-4E9D-A677-72C790F14EC7}";
    private const string Squished = "EB08701BD416D9E46A77277C091FE47C";
    private const string UninstallRoot = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "WavesTest" + Guid.NewGuid().ToString("N"));
    private readonly WavesPaths _paths;
    private readonly FileWavesRegistry _registry;
    private readonly Dictionary<string, Dictionary<string, string>> _keys = [];
    private readonly List<InstalledProgram> _programs = [];
    private readonly List<string> _recycled = [];
    private readonly List<string> _deletedKeys = [];
    private readonly List<string> _launched = [];

    public WavesCleanupTests()
    {
        Directory.CreateDirectory(_root);
        _paths = WavesPaths.UnderTestRoot(_root);
        _registry = new FileWavesRegistry(Path.Combine(_root, "registry.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Main => Path.Combine(_paths.SearchRoots[0], "Waves");

    private string Touch(string path, string text = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void SaveKeys() => File.WriteAllText(Path.Combine(_root, "registry.json"), JsonSerializer.Serialize(_keys));

    private WavesEnvironment Env(string? process = null) => new()
    {
        Paths = _paths,
        Registry = _registry,
        Programs = () => _programs.ToList(),
        Processes = () => process == null ? [] : [process],
        Launch = (command, _) =>
        {
            _launched.Add(command);
            return null;
        },
        Recycle = (paths, roots) => Task.FromResult<IReadOnlyList<FileOpResult>>(paths.Select(path =>
        {
            Assert.True(WavesGuard.IsUnder(path, _root), "测试试图删除临时目录以外的路径：" + path);
            var rejected = WavesGuard.Reject(path, roots, _paths);
            if (rejected != null)
                return new FileOpResult(path, false, rejected);
            _recycled.Add(path);
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            else
                File.Delete(path);
            return new FileOpResult(path, true);
        }).ToList()),
        DeleteRegistry = (targets, roots) =>
        {
            _deletedKeys.AddRange(targets.Select(t => t.Id));
            return Task.FromResult(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.WavesRegistry, targets.Select(t => t.Id).ToList(), roots), null, _paths, _registry));
        },
        BackupRoot = Path.Combine(_root, "Backup"),
        IsTest = true,
    };

    /// <summary>照用户电脑的真实结构搭一套假 Waves。</summary>
    private void BuildInstall()
    {
        Touch(Path.Combine(Main, "Plug-Ins V14", "Q10.bundle", "q10.dll"));
        Touch(Path.Combine(Main, "WaveShells V14", "shell.dll"));
        Touch(Path.Combine(Main, "Data", "Presets", "p.xps"), "factory preset");
        Touch(Path.Combine(Main, "Data", "IR1Impulses", "ir.wir"), "impulse");
        Touch(Path.Combine(_paths.CommonFiles, "VST3", "WaveShell1-VST3 14.5_x64.vst3"));
        Touch(Path.Combine(_paths.ProgramFiles, "VSTPlugins", "WaveShell1-VST 14.5_x64.dll"));
        Touch(Path.Combine(_paths.CommonFiles, "Avid", "Audio", "Plug-Ins", "WaveShell1-AAX 14.5.aaxplugin", "x.dll"));
        Touch(Path.Combine(_paths.CommonFilesX86, "WPAPI", "WaveShell1-WPAPI_2 14.5.bundle", "y.dll"));
        Touch(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat"), "licence");
        Touch(Path.Combine(_paths.ProgramDataWaves, "Preset Browser", "V14", "a.css"));
        Touch(Path.Combine(_paths.ProgramDataWaves, "Modules", "m.dll"));
        Touch(Path.Combine(_paths.LocalAppData, "Waves Audio", "log.txt"));
        Touch(Path.Combine(_paths.RoamingAppData, "Waves Audio", "Preferences", "pref.xml"), "settings");
        Touch(Path.Combine(_paths.RoamingAppData, "Waves Central", "cache.json"));
        Touch(Path.Combine(_paths.Public, "Waves Audio", "Plug-In Settings", "mine.xps"), "my preset");
        Directory.CreateDirectory(Path.Combine(_paths.Public, "Waves Audio", "Plug-In Settings", "EmptyFolder"));
        Touch(Path.Combine(_paths.CentralFolder, "Uninstall Waves Central.exe"));
        Touch(Path.Combine(_paths.CommonPrograms, "Waves Central.lnk"));
        var setup = Touch(Path.Combine(_paths.ProgramData, "Caphyon", "Advanced Installer", Code, "Waves 14.5 Setup.exe"));

        _programs.AddRange([
            new InstalledProgram("Waves 14 14.5", "Waves 14", "WavesAudio", "14.5", $"{setup} /x {Code} AI_UNINSTALLER_CTP=1", _paths.SearchRoots[0], false),
            new InstalledProgram(Code, "Waves 14", "WavesAudio", "14.5", $"MsiExec.exe /I{Code}", _paths.SearchRoots[0], true),
            new InstalledProgram("{ab507e17-892b-5203-838d-d58d8d09c50f}", "Waves Central", "Waves Audio Ltd", "13.3.2", $"\"{Path.Combine(_paths.CentralFolder, "Uninstall Waves Central.exe")}\" /allusers", "", false),
        ]);

        _keys[@"HKLM\SOFTWARE\Classes\Installer\Products\" + Squished] = new() { ["ProductName"] = "Waves 14" };
        _keys[@"HKLM\SOFTWARE\WavesAudio\Waves 14"] = new() { ["Path"] = _paths.SearchRoots[0] };
        _keys[@"HKLM\SOFTWARE\Caphyon\Advanced Installer\LZMA\" + Code + @"\14.5"] = new() { ["AI_ExePath"] = "x" };
        _keys[UninstallRoot + "Waves 14 14.5"] = new() { ["Publisher"] = "WavesAudio", ["UninstallString"] = setup };
        _keys[UninstallRoot + Code] = new() { ["Publisher"] = "WavesAudio", ["UninstallString"] = $"MsiExec.exe /I{Code}" };
        _keys[@"HKLM\SOFTWARE\{ab507e17-892b-5203-838d-d58d8d09c50f}"] = new() { ["ShortcutName"] = "Waves Central" };
        _keys[@"HKLM\SOFTWARE\{11111111-2222-3333-4444-555555555555}"] = new() { ["ShortcutName"] = "Something Else" };
        _keys[@"HKLM\SOFTWARE\Classes\CEN\shell\open\command"] = new() { [""] = "Waves Central.exe %1" };
        _keys[@"HKCU\Software\Classes\waves-central"] = new() { [""] = "URL:waves-central" };
        _keys[WavesRegistryGuard.TraceKeys[0]] = new() { ["Enabled"] = "1" };
        _keys[WavesRegistryGuard.TraceValueKeys[0]] = new()
        {
            [Path.Combine(Main, "Plug-Ins V14", "Tracker.exe")] = "1",
            [@"F:\TestDeepseek\NCM\artifacts\waves-shell-test\app.exe"] = "2",
        };
        SaveKeys();
    }

    /// <summary>模拟 MSI 卸载：连授权和预设一起删掉，并去掉所有登记。</summary>
    private void SimulateMsiUninstall()
    {
        _programs.RemoveAll(WavesIdentity.IsMainProduct);
        _keys.Remove(@"HKLM\SOFTWARE\Classes\Installer\Products\" + Squished);
        _keys.Remove(UninstallRoot + "Waves 14 14.5");
        _keys.Remove(UninstallRoot + Code);
        SaveKeys();
        Directory.Delete(Path.Combine(_paths.ProgramDataWaves, "Licenses"), true);
        Directory.Delete(Path.Combine(Main, "Data", "Presets"), true);
        Directory.Delete(Path.Combine(Main, "Plug-Ins V14"), true);
        File.Delete(Path.Combine(_paths.RoamingAppData, "Waves Audio", "Preferences", "pref.xml"));
    }

    private void SimulateCentralUninstall()
    {
        _programs.RemoveAll(WavesIdentity.IsCentral);
        Directory.Delete(_paths.CentralFolder, true);
    }

    private sealed class FakeUi(Action<WavesProduct> uninstall) : IWavesRunUi
    {
        public List<string> Log { get; } = [];
        public UninstallerWait Wait { get; set; } = UninstallerWait.Continue;
        public Func<WavesProduct, StillInstalledChoice> Still { get; set; } = _ => StillInstalledChoice.Stop;

        public void Step(WavesStep step, WavesStepState state, string detail)
        {
            if (state != WavesStepState.Running)
                Log.Add($"{step}:{state}");
        }

        public Task<UninstallerWait> WaitForUninstaller(WavesProduct product, string? launchError)
        {
            if (Wait == UninstallerWait.Continue)
                uninstall(product);
            return Task.FromResult(Wait);
        }

        public Task<StillInstalledChoice> ProductStillInstalled(WavesProduct product, bool canSkip) => Task.FromResult(Still(product));
    }

    private FakeUi WorkingUi() => new(product =>
    {
        if (product.IsCentral)
            SimulateCentralUninstall();
        else
            SimulateMsiUninstall();
    });

    private WavesRun Run(WavesSurvey survey, IEnumerable<WavesCategory> clean, FakeUi ui, IReadOnlyList<WavesTarget>? extras = null) =>
        new(Env(), survey, WavesRun.Normalize(clean), extras ?? [], Path.Combine(_root, "Backup"), ui);

    // ---------- 识别 ----------

    [WindowsFact]
    public void SquishedCodeMatchesTheRealMsiRecord() => Assert.Equal(Squished, WavesIdentity.SquishedCode(Code));

    [WindowsTheory]
    [InlineData("Waves 14", "WavesAudio", true)]
    [InlineData("Waves Complete", "Waves Inc.", true)]
    [InlineData("Waves Factory", "Waves Factory Ltd", false)]
    [InlineData("WaveLab 12", "Steinberg", false)]
    [InlineData("Waves 14", "Someone Else", false)]
    [InlineData("Waves Central", "Waves Audio Ltd", false)]
    public void MainProductNeedsBothNameAndPublisher(string name, string publisher, bool expected) =>
        Assert.Equal(expected, WavesIdentity.IsMainProduct(new InstalledProgram("k", name, publisher, "", "x", "", false)));

    [WindowsTheory]
    [InlineData("WaveShell1-VST3 14.5_x64.vst3", true)]
    [InlineData("WaveShell1-AAX 14.5.aaxplugin", true)]
    [InlineData("WaveShell1-WPAPI_2 14.5.bundle", true)]
    [InlineData("WaveShells V14", false)]
    [InlineData("waves-shell-test", false)]
    [InlineData("WaveShell.txt", false)]
    public void WaveShellNameRule(string name, bool expected) => Assert.Equal(expected, WavesIdentity.IsWaveShellName(name));

    [WindowsFact]
    public void SurveySortsEveryKnownLocationIntoItsCategory()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());

        Assert.Equal([Main], survey.MainRoots);
        WavesCategory Of(params string[] parts) => survey.Targets.Single(t => t.Path == Path.Combine(parts)).Category;
        Assert.Equal(WavesCategory.PluginBody, Of(Main, "Plug-Ins V14"));
        Assert.Equal(WavesCategory.FactoryPresets, Of(Main, "Data", "Presets"));
        Assert.Equal(WavesCategory.Data, Of(Main, "Data", "IR1Impulses"));
        Assert.Equal(WavesCategory.Licenses, Of(_paths.ProgramDataWaves, "Licenses"));
        Assert.Equal(WavesCategory.FactoryPresets, Of(_paths.ProgramDataWaves, "Preset Browser"));
        Assert.Equal(WavesCategory.Background, Of(_paths.ProgramDataWaves, "Modules"));
        Assert.Equal(WavesCategory.UserPresets, Of(_paths.Public, "Waves Audio"));
        Assert.Equal(WavesCategory.Settings, Of(_paths.RoamingAppData, "Waves Audio"));
        Assert.Equal(WavesCategory.Central, Of(_paths.CentralFolder));
        Assert.Equal(WavesCategory.Installer, Of(_paths.ProgramData, "Caphyon", "Advanced Installer", Code));
        Assert.Equal(4, survey.Targets.Count(t => t.Category == WavesCategory.WaveShell));
    }

    [WindowsFact]
    public void VisibleAndHiddenMsiEntriesBecomeOneProductWithAnUninstallFallback()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var main = Assert.Single(survey.MainProducts);
        Assert.Equal(Code, main.MsiCode);
        Assert.Contains("Waves 14.5 Setup.exe", main.Command);
        Assert.Equal($"MsiExec.exe /X{Code}", main.Fallback);
        Assert.Equal(2, main.UninstallKeys.Count);
        Assert.Null(survey.Central?.MsiCode);
    }

    [WindowsFact]
    public void LookalikesAreNeverTargets()
    {
        BuildInstall();
        // 用户电脑上真实存在的误伤对象。
        var kontakt = Touch(Path.Combine(_paths.SearchRoots[0], "Kontakt", "Single Cycles", "Waves", "a.wav"));
        var steam = Touch(Path.Combine(_paths.SearchRoots[1], "Steam", "Presets", "Waves", "a.ini"));
        var test = Touch(Path.Combine(_paths.SearchRoots[1], "TestDeepseek", "artifacts", "waves-shell-test", "app.exe"));
        var noSignature = Touch(Path.Combine(_paths.SearchRoots[1], "Waves", "my-samples.wav"));
        var lookalike = Touch(Path.Combine(_paths.CommonFiles, "VST3", "WaveLab Tool.vst3"));

        var survey = WavesSurveyor.Scan(Env());
        var extras = WavesSurveyor.FindExtraShells(Env(), survey);
        var all = survey.Targets.Select(t => t.Path).Concat(extras.Select(e => e.Path)).ToList();
        foreach (var path in new[] { kontakt, steam, test, noSignature, lookalike })
            Assert.DoesNotContain(all, t => path.Equals(t, StringComparison.OrdinalIgnoreCase) || WavesGuard.IsUnder(path, t));
        Assert.DoesNotContain(survey.MainRoots, r => r.StartsWith(_paths.SearchRoots[1]));
    }

    [WindowsFact]
    public void ExtraShellsAreFoundOutsideStandardFoldersAndBundlesStayWhole()
    {
        BuildInstall();
        var loose = Touch(Path.Combine(_paths.SearchRoots[1], "vst64", "WaveShell1-VST 14.0_x64.dll"));
        var bundle = Path.Combine(_paths.SearchRoots[1], "old", "WaveShell1-WPAPI_2 14.0.bundle");
        Touch(Path.Combine(bundle, "Contents", "WaveShell1-inner.dll"));

        var survey = WavesSurveyor.Scan(Env());
        var extras = WavesSurveyor.FindExtraShells(Env(), survey).Select(e => e.Path).ToList();
        Assert.Contains(loose, extras);
        Assert.Contains(bundle, extras);
        Assert.Equal(2, extras.Count);
    }

    [WindowsFact]
    public void DawAndCentralBlockButBackgroundServicesOnlyWarn()
    {
        var cubase = WavesSurveyor.Scan(Env("Cubase14"));
        Assert.Equal(["Cubase14"], cubase.RunningBlockers);
        Assert.Equal(["Waves Central"], WavesSurveyor.Scan(Env("Waves Central")).RunningBlockers);
        var server = WavesSurveyor.Scan(Env("WavesLocalServer"));
        Assert.Empty(server.RunningBlockers);
        Assert.Equal(["WavesLocalServer"], server.RunningBackground);
    }

    [WindowsFact]
    public void UninstallEntryWithoutProgramOrMsiIsStaleAndOnlyThenRemovable()
    {
        var stale = "Waves 13 13.0";
        _programs.Add(new InstalledProgram(stale, "Waves 13", "WavesAudio", "13", @"D:\gone\Setup.exe /x", "", false));
        _keys[UninstallRoot + stale] = new() { ["Publisher"] = "WavesAudio", ["UninstallString"] = @"D:\gone\Setup.exe /x" };
        var other = "Other App";
        _keys[UninstallRoot + other] = new() { ["Publisher"] = "Other", ["UninstallString"] = @"D:\gone\Setup.exe /x" };
        SaveKeys();

        var survey = WavesSurveyor.Scan(Env());
        Assert.Empty(survey.Products);
        Assert.Contains(survey.RegistryTargets, t => t.Key == UninstallRoot + stale);
        Assert.Null(WavesRegistryGuard.Reject(UninstallRoot + stale, null, [], _paths, _registry));
        Assert.NotNull(WavesRegistryGuard.Reject(UninstallRoot + other, null, [], _paths, _registry));
    }

    [WindowsFact]
    public void TestEnvironmentNeverAcceptsAMainFolderOutsideItsRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "WavesOutside" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(outside, "Waves");
        Touch(Path.Combine(real, "Plug-Ins V14", "a.dll"));
        try
        {
            _programs.Add(new InstalledProgram("Waves 14 14.5", "Waves 14", "WavesAudio", "14.5", "x", outside, false));
            var survey = WavesSurveyor.Scan(Env(), [real]);
            Assert.Empty(survey.MainRoots);
            Assert.DoesNotContain(survey.Targets, t => t.Path.StartsWith(outside, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    // ---------- 白名单 ----------

    [WindowsFact]
    public void GuardRefusesAnythingOutsideWavesLocations()
    {
        BuildInstall();
        var roots = new[] { Main };
        Assert.Null(WavesGuard.Reject(Path.Combine(Main, "Plug-Ins V14"), roots, _paths));
        Assert.Null(WavesGuard.Reject(Path.Combine(_paths.ProgramDataWaves, "Modules"), roots, _paths));
        Assert.Null(WavesGuard.Reject(Path.Combine(_paths.CommonFiles, "VST3", "WaveShell1-VST3 14.5_x64.vst3"), roots, _paths));

        var other = Touch(Path.Combine(_paths.SearchRoots[0], "Other", "file.dll"));
        Assert.NotNull(WavesGuard.Reject(other, roots, _paths));
        var kontakt = Touch(Path.Combine(_paths.SearchRoots[0], "Kontakt", "Waves", "a.wav"));
        Assert.NotNull(WavesGuard.Reject(kontakt, roots, _paths));
        Assert.NotNull(WavesGuard.Reject(Path.Combine(_paths.ProgramData, "Caphyon"), roots, _paths));
        var windows = Touch(Path.Combine(_paths.Windows, "Installer", "WaveShell1.dll"));
        Assert.NotNull(WavesGuard.Reject(windows, roots, _paths));
        Assert.NotNull(WavesGuard.Reject(@"\\server\share\WaveShell1.dll", roots, _paths));
        Assert.NotNull(WavesGuard.Reject(Path.Combine(Main, "..", "Other", "file.dll"), roots, _paths));
    }

    [WindowsFact]
    public void MainRootMustBeCalledWaves()
    {
        var folder = Path.Combine(_paths.SearchRoots[0], "NotWaves");
        var file = Touch(Path.Combine(folder, "a.dll"));
        Assert.False(WavesGuard.IsValidMainRoot(folder, _paths));
        Assert.False(WavesGuard.IsValidMainRoot(@"D:\", _paths));
        Assert.NotNull(WavesGuard.Reject(file, [folder], _paths));
    }

    [WindowsFact]
    public void GuardRefusesLinks()
    {
        var real = Path.Combine(_root, "Elsewhere");
        Directory.CreateDirectory(real);
        Touch(Path.Combine(real, "important.txt"));
        Directory.CreateDirectory(Main);
        var link = Path.Combine(Main, "Linked");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (IOException)
        {
            return; // 没有创建链接的权限时跳过。
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        Assert.Equal("路径里有链接", WavesGuard.Reject(link, [Main], _paths));
    }

    [WindowsTheory]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node", "不在允许的注册表位置")]
    [InlineData(@"HKLM\SOFTWARE", "不在允许的注册表位置")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Installer\Products\EB08701BD416D9E46A77277C091FE47C", "不处理 Windows Installer 记录")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders", "不处理 Windows Installer 记录")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\x", "不在允许的注册表位置")]
    [InlineData(@"HKCU\Software\Corel\PCU\installedsoftware\{CDFBE096-A945-4576-8127-6E86FCA53B33}", "不在允许的注册表位置")]
    public void RegistryGuardRefusesEverythingNotListed(string key, string reason) =>
        Assert.Equal(reason, WavesRegistryGuard.Reject(key, null, [Main], _paths, _registry));

    [WindowsFact]
    public void RegistryGuardChecksOwnershipOfGuidKeysAndLzma()
    {
        BuildInstall();
        Assert.Null(WavesRegistryGuard.Reject(@"HKLM\SOFTWARE\{ab507e17-892b-5203-838d-d58d8d09c50f}", null, [Main], _paths, _registry));
        Assert.NotNull(WavesRegistryGuard.Reject(@"HKLM\SOFTWARE\{11111111-2222-3333-4444-555555555555}", null, [Main], _paths, _registry));
        var lzma = @"HKLM\SOFTWARE\Caphyon\Advanced Installer\LZMA\" + Code;
        Assert.NotNull(WavesRegistryGuard.Reject(lzma, null, [Main], _paths, _registry));
        Assert.NotNull(WavesRegistryGuard.Reject(@"HKLM\SOFTWARE\Caphyon", null, [Main], _paths, _registry));
    }

    [WindowsFact]
    public void TraceValuesMustPointIntoWavesFolders()
    {
        var key = WavesRegistryGuard.TraceValueKeys[0];
        Assert.Null(WavesRegistryGuard.Reject(key, Path.Combine(Main, "Plug-Ins V14", "a.exe"), [Main], _paths, _registry));
        Assert.NotNull(WavesRegistryGuard.Reject(key, @"F:\TestDeepseek\waves-shell-test\a.exe", [Main], _paths, _registry));
        Assert.NotNull(WavesRegistryGuard.Reject(@"HKCU\Software\Other", Path.Combine(Main, "a.exe"), [Main], _paths, _registry));
    }

    // ---------- 备份 ----------

    [WindowsFact]
    public void BackupRestoresDeletedFilesAndEmptyFoldersAndMarksDone()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var kept = survey.Targets.Where(t => t.Category is WavesCategory.UserPresets or WavesCategory.Licenses).ToList();
        var folder = WavesBackup.Create(kept, survey.MainRoots, Path.Combine(_root, "Backup"));
        Assert.Equal([folder], WavesBackup.FindUnrestored(Path.Combine(_root, "Backup")));

        var preset = Path.Combine(_paths.Public, "Waves Audio", "Plug-In Settings", "mine.xps");
        Directory.Delete(Path.Combine(_paths.Public, "Waves Audio"), true);
        File.WriteAllText(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat"), "changed");

        Assert.Empty(WavesBackup.Restore(folder, _paths));
        Assert.Equal("my preset", File.ReadAllText(preset));
        Assert.True(Directory.Exists(Path.Combine(_paths.Public, "Waves Audio", "Plug-In Settings", "EmptyFolder")));
        Assert.Equal("licence", File.ReadAllText(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat")));
        Assert.Empty(WavesBackup.FindUnrestored(Path.Combine(_root, "Backup")));
    }

    [WindowsFact]
    public void CorruptBackupIsReportedAndStaysUnrestored()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var kept = survey.Targets.Where(t => t.Category == WavesCategory.UserPresets).ToList();
        var folder = WavesBackup.Create(kept, survey.MainRoots, Path.Combine(_root, "Backup"));
        File.WriteAllText(Path.Combine(folder, "files", "000", "Plug-In Settings", "mine.xps"), "tampered");
        Directory.Delete(Path.Combine(_paths.Public, "Waves Audio"), true);

        Assert.NotEmpty(WavesBackup.Restore(folder, _paths));
        Assert.Single(WavesBackup.FindUnrestored(Path.Combine(_root, "Backup")));
    }

    [WindowsFact]
    public void RestoreRefusesManifestPathsOutsideWavesLocations()
    {
        var folder = Path.Combine(_root, "Backup", "x");
        var victim = Path.Combine(_root, "victim");
        Directory.CreateDirectory(Path.Combine(folder, "files", "000"));
        File.WriteAllText(Path.Combine(folder, "files", "000", "evil.txt"), "evil");
        var manifest = new WavesBackupManifest(DateTime.UtcNow, false, [], [
            new WavesBackupItem(victim, WavesCategory.Settings, true, "000", [new WavesBackupFile("evil.txt", 4, "00")], []),
        ]);
        File.WriteAllText(Path.Combine(folder, WavesBackup.ManifestName), JsonSerializer.Serialize(manifest));

        Assert.NotEmpty(WavesBackup.Restore(folder, _paths));
        Assert.False(Directory.Exists(victim));
    }

    // ---------- 主流程 ----------

    [WindowsFact]
    public async Task StandardCleanKeepsWhatMsiDeletedAndCleansTheRest()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var ui = WorkingUi();

        var result = await Run(survey, WavesCategories.Standard, ui).RunAsync();

        Assert.True(result.Finished);
        // MSI 删掉的保留项都回来了。
        Assert.Equal("licence", File.ReadAllText(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat")));
        Assert.Equal("factory preset", File.ReadAllText(Path.Combine(Main, "Data", "Presets", "p.xps")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(_paths.RoamingAppData, "Waves Audio", "Preferences", "pref.xml")));
        Assert.True(File.Exists(Path.Combine(Main, "Data", "IR1Impulses", "ir.wir")));
        Assert.True(File.Exists(Path.Combine(_paths.Public, "Waves Audio", "Plug-In Settings", "mine.xps")));
        // Waves Central 没有动。
        Assert.True(File.Exists(Path.Combine(_paths.CentralFolder, "Uninstall Waves Central.exe")));
        Assert.Single(_launched);
        Assert.True(_registry.KeyExists(@"HKCU\Software\Classes\waves-central"));
        // 该清的都清了。
        Assert.False(Directory.Exists(Path.Combine(Main, "WaveShells V14")));
        Assert.False(File.Exists(Path.Combine(_paths.CommonFiles, "VST3", "WaveShell1-VST3 14.5_x64.vst3")));
        Assert.False(Directory.Exists(Path.Combine(_paths.ProgramDataWaves, "Modules")));
        Assert.False(_registry.KeyExists(@"HKLM\SOFTWARE\WavesAudio"));
        Assert.False(_registry.KeyExists(@"HKLM\SOFTWARE\Caphyon\Advanced Installer\LZMA\" + Code));
        Assert.Contains("已经查不到了", result.Report);
    }

    [WindowsFact]
    public async Task FullCleanRemovesEverythingListed()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var result = await Run(survey, WavesCategories.Full, WorkingUi()).RunAsync();

        Assert.True(result.Finished);
        Assert.False(Directory.Exists(Main));
        Assert.False(Directory.Exists(_paths.ProgramDataWaves));
        Assert.False(Directory.Exists(Path.Combine(_paths.Public, "Waves Audio")));
        Assert.False(Directory.Exists(Path.Combine(_paths.RoamingAppData, "Waves Central")));
        Assert.False(File.Exists(Path.Combine(_paths.CommonPrograms, "Waves Central.lnk")));
        Assert.False(_registry.KeyExists(@"HKLM\SOFTWARE\{ab507e17-892b-5203-838d-d58d8d09c50f}"));
        Assert.False(_registry.KeyExists(@"HKLM\SOFTWARE\Classes\CEN"));
        Assert.False(_registry.KeyExists(WavesRegistryGuard.TraceKeys[0]));
        Assert.Equal(2, _launched.Count);

        // 不属于 Waves 的都还在。
        Assert.True(_registry.KeyExists(@"HKLM\SOFTWARE\{11111111-2222-3333-4444-555555555555}"));
        var traces = _registry.ValueNames(WavesRegistryGuard.TraceValueKeys[0]);
        Assert.Equal([@"F:\TestDeepseek\NCM\artifacts\waves-shell-test\app.exe"], traces);
    }

    [WindowsFact]
    public async Task KeptCategoriesAreIndividuallyRespected()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var clean = WavesCategories.Full.Except([WavesCategory.Licenses, WavesCategory.Central]).ToList();

        await Run(survey, clean, WorkingUi()).RunAsync();

        Assert.Equal("licence", File.ReadAllText(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat")));
        Assert.True(Directory.Exists(_paths.CentralFolder));
        Assert.False(Directory.Exists(Path.Combine(Main, "Data", "IR1Impulses")));
        Assert.False(Directory.Exists(Path.Combine(_paths.RoamingAppData, "Waves Audio")));
    }

    [WindowsFact]
    public async Task RegistryIsExportedBeforeItIsDeleted()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var result = await Run(survey, WavesCategories.Standard, WorkingUi()).RunAsync();

        var exports = Directory.GetFiles(Path.Combine(result.BackupFolder, "registry"));
        Assert.Contains(exports, f => File.ReadAllText(f) == @"HKLM\SOFTWARE\WavesAudio");
    }

    [WindowsFact]
    public async Task IfMsiStaysRegisteredNothingIsCleanedAndNothingKeptIsLost()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        // 卸载程序半途失败：删了授权但登记还在。
        var ui = new FakeUi(_ => Directory.Delete(Path.Combine(_paths.ProgramDataWaves, "Licenses"), true));

        var result = await Run(survey, WavesCategories.Standard, ui).RunAsync();

        Assert.False(result.Finished);
        Assert.Empty(_recycled);
        Assert.Empty(_deletedKeys);
        Assert.Contains("UninstallMain:Failed", ui.Log);
        Assert.Equal("licence", File.ReadAllText(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat")));
        Assert.Contains("修复工具", result.Report);
    }

    [WindowsFact]
    public async Task RetryUsesMsiexecUninstallThenContinues()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var attempts = 0;
        var ui = new FakeUi(product =>
        {
            if (product.IsCentral)
                return;
            if (++attempts == 2)
                SimulateMsiUninstall();
        })
        {
            Still = _ => StillInstalledChoice.RetryAsAdmin,
        };

        var result = await Run(survey, WavesCategories.Standard, ui).RunAsync();

        Assert.True(result.Finished);
        Assert.Equal($"MsiExec.exe /X{Code}", _launched[1]);
    }

    [WindowsFact]
    public async Task StoppingAtTheUninstallerLeavesFilesAlone()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var ui = WorkingUi();
        ui.Wait = UninstallerWait.Stop;

        var result = await Run(survey, WavesCategories.Full, ui).RunAsync();

        Assert.False(result.Finished);
        Assert.Empty(_recycled);
        Assert.Empty(_deletedKeys);
        Assert.True(Directory.Exists(Path.Combine(Main, "Plug-Ins V14")));
    }

    [WindowsFact]
    public async Task SkippedCentralKeepsItsFilesAndRegistry()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var ui = new FakeUi(product =>
        {
            if (!product.IsCentral)
                SimulateMsiUninstall();
        })
        {
            Still = _ => StillInstalledChoice.Skip,
        };

        var result = await Run(survey, WavesCategories.Full, ui).RunAsync();

        Assert.True(result.Finished);
        Assert.True(Directory.Exists(_paths.CentralFolder));
        Assert.True(_registry.KeyExists(@"HKCU\Software\Classes\waves-central"));
    }

    [WindowsFact]
    public async Task ExtrasOnlyMoveWhenTicked()
    {
        BuildInstall();
        var ticked = Touch(Path.Combine(_paths.SearchRoots[1], "vst64", "WaveShell1-VST 14.0_x64.dll"));
        var unticked = Touch(Path.Combine(_paths.SearchRoots[1], "old", "WaveShell1-VST 13.0_x64.dll"));
        var survey = WavesSurveyor.Scan(Env());
        var extras = WavesSurveyor.FindExtraShells(Env(), survey).Where(e => e.Path == ticked).ToList();

        await Run(survey, WavesCategories.Standard, WorkingUi(), extras).RunAsync();

        Assert.False(File.Exists(ticked));
        Assert.True(File.Exists(unticked));
    }

    [WindowsFact]
    public void LockedCategoriesCannotBeLeftOut()
    {
        var set = WavesRun.Normalize([WavesCategory.Licenses]);
        Assert.Contains(WavesCategory.PluginBody, set);
        Assert.Contains(WavesCategory.WaveShell, set);
        Assert.Contains(WavesCategory.Installer, set);
    }

    [WindowsFact]
    public void DescribeListsPathsAndWhatIsNeverTouched()
    {
        BuildInstall();
        var survey = WavesSurveyor.Scan(Env());
        var text = WavesSurveyor.Describe(survey, WavesRun.Normalize(WavesCategories.Standard));
        Assert.Contains("只读", text);
        Assert.Contains(Path.Combine(Main, "Data", "Presets"), text);
        Assert.Contains("先备份，卸载后恢复", text);
        Assert.Contains("MSI 记录", text);
    }
}
