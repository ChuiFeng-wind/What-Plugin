using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>
/// Waves 完全卸载工具的界面。全部在临时目录里的假 Waves 上跑：卸载程序不真的启动，
/// “回收”是直接删除，每次删除前都确认路径在临时目录里。不碰真实的 Waves、回收站和注册表。
/// </summary>
public sealed class WavesToolTests : IDisposable
{
    private const string Code = "{B10780BE-614D-4E9D-A677-72C790F14EC7}";
    private const string Squished = "EB08701BD416D9E46A77277C091FE47C";
    private const string UninstallRoot = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pv-waves-" + Guid.NewGuid().ToString("N"));
    private readonly WavesPaths _paths;
    private readonly FileWavesRegistry _registry;
    private readonly Dictionary<string, Dictionary<string, string>> _keys = [];
    private readonly List<InstalledProgram> _programs = [];
    private readonly List<string> _recycled = [];
    private readonly List<string> _launched = [];
    private readonly ScriptedDialogs _dialogs = new();

    public WavesToolTests()
    {
        Directory.CreateDirectory(_root);
        Assert.StartsWith(Path.GetTempPath(), _root, StringComparison.OrdinalIgnoreCase);
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

    private void Touch(string path, string text = "data")
    {
        Assert.True(WavesGuard.IsUnder(path, _root), "测试文件必须在临时目录里：" + path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void SaveKeys() => File.WriteAllText(Path.Combine(_root, "registry.json"), JsonSerializer.Serialize(_keys));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
            Task.FromResult(FileOpExecutor.Execute(new FileOpRequest(FileOpKind.WavesRegistry, targets.Select(t => t.Id).ToList(), roots), null, _paths, _registry)),
        BackupRoot = Path.Combine(_root, "Backup"),
        IsTest = true,
    };

    /// <summary>一套假 Waves：插件、WaveShell、授权、预设、设置，加上 MSI 和 Waves Central 的登记。</summary>
    private void BuildInstall()
    {
        Touch(Path.Combine(Main, "Plug-Ins V14", "Q10.bundle", "q10.dll"));
        Touch(Path.Combine(Main, "WaveShells V14", "shell.dll"));
        Touch(Path.Combine(Main, "Data", "Presets", "p.xps"), "factory preset");
        Touch(Path.Combine(_paths.CommonFiles, "VST3", "WaveShell1-VST3 14.5_x64.vst3"));
        Touch(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat"), "licence");
        Touch(Path.Combine(_paths.RoamingAppData, "Waves Audio", "Preferences", "pref.xml"), "settings");
        Touch(Path.Combine(_paths.Public, "Waves Audio", "Plug-In Settings", "mine.xps"), "my preset");
        Touch(Path.Combine(_paths.CentralFolder, "Uninstall Waves Central.exe"));
        var setup = Path.Combine(_paths.ProgramData, "Caphyon", "Advanced Installer", Code, "Waves 14.5 Setup.exe");
        Touch(setup);

        _programs.AddRange([
            new InstalledProgram("Waves 14 14.5", "Waves 14", "WavesAudio", "14.5", $"{setup} /x {Code} AI_UNINSTALLER_CTP=1", _paths.SearchRoots[0], false),
            new InstalledProgram(Code, "Waves 14", "WavesAudio", "14.5", $"MsiExec.exe /I{Code}", _paths.SearchRoots[0], true),
            new InstalledProgram("{ab507e17-892b-5203-838d-d58d8d09c50f}", "Waves Central", "Waves Audio Ltd", "13.3.2", $"\"{Path.Combine(_paths.CentralFolder, "Uninstall Waves Central.exe")}\" /allusers", "", false),
        ]);
        _keys[@"HKLM\SOFTWARE\Classes\Installer\Products\" + Squished] = new() { ["ProductName"] = "Waves 14" };
        _keys[@"HKLM\SOFTWARE\WavesAudio\Waves 14"] = new() { ["Path"] = _paths.SearchRoots[0] };
        _keys[UninstallRoot + "Waves 14 14.5"] = new() { ["Publisher"] = "WavesAudio", ["UninstallString"] = setup };
        _keys[UninstallRoot + Code] = new() { ["Publisher"] = "WavesAudio", ["UninstallString"] = $"MsiExec.exe /I{Code}" };
        SaveKeys();
    }

    /// <summary>模拟 MSI 卸载：连授权、预设和设置一起删掉，并去掉登记。</summary>
    private void SimulateMsiUninstall()
    {
        _programs.RemoveAll(WavesIdentity.IsMainProduct);
        _keys.Remove(@"HKLM\SOFTWARE\Classes\Installer\Products\" + Squished);
        _keys.Remove(UninstallRoot + "Waves 14 14.5");
        _keys.Remove(UninstallRoot + Code);
        SaveKeys();
        Directory.Delete(Path.Combine(_paths.ProgramDataWaves, "Licenses"), true);
        Directory.Delete(Path.Combine(Main, "Plug-Ins V14"), true);
        File.Delete(Path.Combine(_paths.RoamingAppData, "Waves Audio", "Preferences", "pref.xml"));
    }

    private WavesToolWindow Open(WavesEnvironment env)
    {
        var tool = new WavesToolWindow(env) { Dialogs = _dialogs, Width = 880, Height = 760 };
        tool.Show();
        Wait(tool.ExtraSearch, tool);
        return tool;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Task task, Window window) => WaitUntil(() => task.IsCompleted, window);

    private static void WaitUntil(Func<bool> done, Window window)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "等待超时");
        }
        Settle(window);
    }

    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static Button ButtonOf(Window window, string text) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>().First(b => Equals(b.Content, text));

    [AvaloniaFact]
    public void SurveyShowsWhatWillBeCleanedAndGatesStart()
    {
        if (!OperatingSystem.IsWindows())
            return;
        BuildInstall();
        var tool = Open(Env());

        Assert.Contains("Waves 14 14.5", tool.Summary);
        Assert.Contains("Waves Central", tool.Summary);
        Assert.Contains("主目录：" + _paths.SearchRoots[0], tool.Summary);
        // 标准清理默认保留授权、自己的预设和设置；保留项要备份。
        Assert.False(tool.Categories[WavesCategory.Licenses].IsChecked);
        Assert.Contains("这次要备份", tool.BackupText);
        // 没确认授权前不能开始。
        Assert.False(tool.CanStart);
        tool.License.IsChecked = true;
        Assert.True(tool.CanStart);

        // 完全清理把授权也勾上；回到标准清理再去掉。
        tool.FullPreset.IsChecked = true;
        Assert.True(tool.Categories[WavesCategory.Licenses].IsChecked);
        tool.Categories[WavesCategory.Licenses].IsChecked = false;
        Assert.False(tool.Categories[WavesCategory.Licenses].IsChecked);

        // 预演清单只显示文字，不动文件。
        _dialogs.Answers.Enqueue(null);
        Click(ButtonOf(tool, "查看预演清单"));
        Settle(tool);
        Assert.Contains(Main, Assert.Single(_dialogs.Shown));
        Assert.Empty(_launched);
        Assert.Empty(_recycled);
        Assert.True(File.Exists(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat")));
    }

    [AvaloniaFact]
    public void RunningDawBlocksStart()
    {
        if (!OperatingSystem.IsWindows())
            return;
        BuildInstall();
        var tool = Open(Env("Cubase"));
        tool.License.IsChecked = true;
        Assert.False(tool.CanStart);
    }

    [AvaloniaFact]
    public void CancelledConfirmChangesNothing()
    {
        if (!OperatingSystem.IsWindows())
            return;
        BuildInstall();
        var tool = Open(Env());
        tool.License.IsChecked = true;
        _dialogs.Answers.Enqueue(false);
        Click(ButtonOf(tool, "开始卸载"));
        Settle(tool);
        Assert.Single(_dialogs.Asked);
        Assert.False(tool.Running);
        Assert.False(tool.ChangedSomething);
        Assert.Empty(_launched);
        Assert.Empty(_recycled);
    }

    [AvaloniaFact]
    public void FullRunKeepsLicensesAndPresetsAndReports()
    {
        if (!OperatingSystem.IsWindows())
            return;
        BuildInstall();
        var tool = Open(Env());
        tool.License.IsChecked = true;
        _dialogs.Answers.Enqueue(true);
        Click(ButtonOf(tool, "开始卸载"));
        Settle(tool);
        Assert.True(tool.Running);
        Assert.True(tool.ChangedSomething);
        Assert.Equal("正在卸载 Waves", tool.Heading);

        // 卸载程序“打开”后，在它的窗口里完成卸载，再点继续。
        WaitUntil(() => tool.Prompt != null, tool);
        Assert.Contains("Waves 14", tool.Prompt);
        Assert.Equal("完成", tool.StepState(WavesStep.Backup));
        // 运行中不能关窗口。
        tool.Close();
        Assert.True(tool.IsVisible);
        SimulateMsiUninstall();
        tool.AnswerPrompt(primary: true);

        WaitUntil(() => !tool.Running, tool);
        Assert.Equal("卸载流程已结束", tool.Heading);
        Assert.NotEmpty(tool.Report);
        Assert.Contains(_launched, c => c.Contains(Code, StringComparison.OrdinalIgnoreCase));
        // 授权、设置被 MSI 删掉后从备份放回原位；插件和 WaveShell 清掉了。
        Assert.Equal("licence", File.ReadAllText(Path.Combine(_paths.ProgramDataWaves, "Licenses", "lic.dat")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(_paths.RoamingAppData, "Waves Audio", "Preferences", "pref.xml")));
        Assert.Equal("my preset", File.ReadAllText(Path.Combine(_paths.Public, "Waves Audio", "Plug-In Settings", "mine.xps")));
        Assert.False(File.Exists(Path.Combine(_paths.CommonFiles, "VST3", "WaveShell1-VST3 14.5_x64.vst3")));
        Assert.All(_recycled, p => Assert.True(WavesGuard.IsUnder(p, _root)));

        // 结束后可以关窗口，重新检查回到检查页。
        Click(ButtonOf(tool, "重新检查"));
        Wait(tool.ExtraSearch, tool);
        Assert.Equal("Waves 完全卸载工具", tool.Heading);
        Assert.Equal("", tool.Report);
    }

    [AvaloniaFact]
    public void StoppingAtUninstallerLeavesFilesAlone()
    {
        if (!OperatingSystem.IsWindows())
            return;
        BuildInstall();
        var tool = Open(Env());
        tool.License.IsChecked = true;
        _dialogs.Answers.Enqueue(true);
        Click(ButtonOf(tool, "开始卸载"));
        WaitUntil(() => tool.Prompt != null, tool);
        tool.AnswerPrompt(primary: false);
        WaitUntil(() => !tool.Running, tool);

        Assert.Equal("已停止", tool.Heading);
        // MSI 还登记着：一个文件都不清理。
        Assert.Empty(_recycled);
        Assert.True(File.Exists(Path.Combine(Main, "Plug-Ins V14", "Q10.bundle", "q10.dll")));
    }

    [AvaloniaFact]
    public void BackupFolderCannotBeInsideWaves()
    {
        if (!OperatingSystem.IsWindows())
            return;
        BuildInstall();
        var tool = Open(Env());
        tool.PickFolder = () => Task.FromResult<string?>(Path.Combine(Main, "Data"));
        _dialogs.Answers.Enqueue(null);
        Click(ButtonOf(tool, "更改"));
        Settle(tool);
        Assert.Equal(["不能选这个位置"], _dialogs.Asked);
        Assert.StartsWith(Path.Combine(_root, "Backup"), tool.BackupText);

        var elsewhere = Path.Combine(_root, "MyBackups");
        tool.PickFolder = () => Task.FromResult<string?>(elsewhere);
        Click(ButtonOf(tool, "更改"));
        Settle(tool);
        Assert.StartsWith(Path.Combine(elsewhere, "WhatPlugin_Waves备份"), tool.BackupText);
    }

    [AvaloniaFact]
    public void RendersSurveyAndRun()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var output = Environment.GetEnvironmentVariable("WHATPLUGIN_SCREENSHOTS") ?? _root;
        Directory.CreateDirectory(output);
        void Save(Window w, string name)
        {
            Settle(w);
            var frame = w.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
        }

        BuildInstall();
        var tool = Open(Env());
        tool.License.IsChecked = true;
        Save(tool, "waves-tool");
        _dialogs.Answers.Enqueue(true);
        Click(ButtonOf(tool, "开始卸载"));
        WaitUntil(() => tool.Prompt != null, tool);
        Save(tool, "waves-tool-run");
        SimulateMsiUninstall();
        tool.AnswerPrompt(primary: true);
        WaitUntil(() => !tool.Running, tool);
        Save(tool, "waves-tool-done");
    }
}
