using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

public sealed class WavesCategoryRow(WavesCategory category, string detail, Action changed) : INotifyPropertyChanged
{
    private bool _clean;

    public event PropertyChangedEventHandler? PropertyChanged;

    public WavesCategory Category { get; } = category;
    public string Title => WavesCategories.Label(Category);
    public string Detail { get; } = detail;
    public bool CanChange => !WavesCategories.IsLocked(Category);
    public string AutomationName => "清理 " + Title;
    public string Action => Clean ? "卸载后清理，进回收站" : WavesCategories.KeepAction(Category);

    public bool Clean
    {
        get => _clean;
        set
        {
            if (_clean == value)
                return;
            _clean = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Clean)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Action)));
            changed();
        }
    }
}

public sealed class WavesExtraRow(WavesTarget target) : INotifyPropertyChanged
{
    private bool _selected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public WavesTarget Target { get; } = target;
    public string Path => Target.Path;
    public string Text => $"{Target.Path}（{WavesSurveyor.FormatSize(Target.Size)}）";

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
}

public sealed class WavesStepRow(WavesStep step, string title) : INotifyPropertyChanged
{
    private string _state = "等待";
    private string _detail = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public WavesStep Step { get; } = step;
    public string Title { get; } = title;

    public string State
    {
        get => _state;
        set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    public string Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }
}

/// <summary>Waves 完全卸载工具。预检阶段只读；所有改动都在 <see cref="WavesRun"/> 里按固定顺序进行。</summary>
public partial class WavesToolWindow : Window, IWavesRunUi
{
    private const string TroubleshooterUrl = "https://support.microsoft.com/help/17588/windows-fix-problems-that-block-programs-being-installed-or-removed";

    private readonly WavesEnvironment _env;
    private readonly List<WavesStepRow> _steps =
    [
        new(WavesStep.Backup, "备份保留项"),
        new(WavesStep.UninstallMain, "运行 Waves 卸载程序"),
        new(WavesStep.UninstallCentral, "运行 Waves Central 卸载程序"),
        new(WavesStep.Restore, "恢复保留项"),
        new(WavesStep.CleanFiles, "清理残留文件（进回收站）"),
        new(WavesStep.CleanRegistry, "清理注册表（先导出备份）"),
        new(WavesStep.Verify, "复查"),
    ];

    private WavesSurvey? _survey;
    private List<WavesCategoryRow> _rows = [];
    private List<WavesExtraRow> _extras = [];
    private IReadOnlyList<string> _pending = [];
    private string _backupRoot;
    private bool _applyingPreset;
    private bool _running;
    private CancellationTokenSource? _search;
    private TaskCompletionSource<int>? _prompt;

    public WavesToolWindow(WavesEnvironment env)
    {
        _env = env;
        _backupRoot = env.BackupRoot;
        InitializeComponent();
        WindowEffects.ApplyDialog(this);
        TestModeText.Visibility = env.IsTest ? Visibility.Visible : Visibility.Collapsed;
        StepList.ItemsSource = _steps;
        Closing += OnClosing;
        Loaded += (_, _) => Refresh();
    }

    /// <summary>卸载或恢复动过文件后为 true，主窗口要重新扫描。</summary>
    public bool ChangedSomething { get; private set; }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            return;
        }
        _search?.Cancel();
    }

    private void Refresh()
    {
        _survey = WavesSurveyor.Scan(_env);
        var survey = _survey;

        var chosen = FullRadio.IsChecked == true ? WavesCategories.Full : WavesCategories.Standard;
        var previous = _rows.ToDictionary(r => r.Category, r => r.Clean);
        _rows = Enum.GetValues<WavesCategory>()
            .Where(c => survey.Targets.Any(t => t.Category == c) || survey.RegistryTargets.Any(t => t.Category == c))
            .Select(c => new WavesCategoryRow(c, DescribeCategory(survey, c), UpdateState))
            .ToList();
        _applyingPreset = true;
        foreach (var row in _rows)
            row.Clean = previous.TryGetValue(row.Category, out var clean) ? clean : chosen.Contains(row.Category);
        StandardRadio.IsChecked = FullRadio.IsChecked != true;
        _applyingPreset = false;
        CategoryList.ItemsSource = _rows;

        var lines = new List<string>();
        lines.Add(survey.Products.Count == 0
            ? "卸载程序：没有找到"
            : "卸载程序：" + string.Join("、", survey.Products.Select(p => $"{p.Name} {p.Version}" + (p.MsiCode != null ? "（MSI）" : ""))));
        lines.Add("主目录：" + (survey.MainRoots.Count == 0 ? "没有找到" : string.Join("、", survey.MainRoots)));
        lines.Add($"文件：共 {WavesSurveyor.FormatSize(survey.Targets.Sum(t => t.Size))}；注册表：{survey.RegistryTargets.Count} 项，不含 MSI 自己管理的记录");
        if (survey.RunningBackground.Count > 0)
            lines.Add("正在运行的 Waves 后台程序：" + string.Join("、", survey.RunningBackground) + "。卸载程序可能会要求关闭它们。");
        if (survey.IsEmpty)
            lines.Add("已知位置没有查到 Waves 的内容。");
        SummaryText.Text = string.Join("\n", lines);

        BlockerText.Text = survey.RunningBlockers.Count > 0 ? "请先关闭：" + string.Join("、", survey.RunningBlockers) + "，再点“重新检查”。" : "";
        BlockerText.Visibility = survey.RunningBlockers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _pending = WavesBackup.FindUnrestored(_env.BackupRoot);
        PendingPanel.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_pending.Count > 0)
            PendingText.Text = $"上次卸载有一份备份还没恢复：{_pending[^1]}";

        BackupText.Text = _backupRoot;
        UpdateState();
        StartExtraSearch(survey);
    }

    private static string DescribeCategory(WavesSurvey survey, WavesCategory category)
    {
        var parts = survey.Targets.Where(t => t.Category == category).ToList();
        var keys = survey.RegistryTargets.Count(t => t.Category == category);
        var shown = string.Join("\n", parts.Take(6).Select(t => t.Path));
        if (parts.Count > 6)
            shown += $"\n还有 {parts.Count - 6} 项，完整路径见预演清单";
        var size = parts.Count > 0 ? $"{WavesSurveyor.FormatSize(parts.Sum(p => p.Size))}，{parts.Sum(p => p.Files)} 个文件" : "";
        var registry = keys > 0 ? $"注册表 {keys} 项" : "";
        var head = string.Join("；", new[] { size, registry }.Where(s => s.Length > 0));
        return shown.Length > 0 ? head + "\n" + shown : head;
    }

    private async void StartExtraSearch(WavesSurvey survey)
    {
        _search?.Cancel();
        var search = new CancellationTokenSource();
        _search = search;
        _extras = [];
        ExtraList.ItemsSource = null;
        ExtraNote.Text = "正在搜索各磁盘，大约需要一两分钟。搜索期间也可以开始卸载，那样就不处理这里的文件。";
        try
        {
            var found = await Task.Run(() => WavesSurveyor.FindExtraShells(_env, survey, search.Token), search.Token);
            if (search.IsCancellationRequested)
                return;
            _extras = found.Select(f => new WavesExtraRow(f)).ToList();
            ExtraList.ItemsSource = _extras;
            ExtraNote.Text = found.Count == 0 ? "没有找到。" : $"找到 {found.Count} 个，默认不处理。勾选的会在清理残留时移到回收站。";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (_applyingPreset || !IsLoaded)
            return;
        var preset = FullRadio.IsChecked == true ? WavesCategories.Full : WavesCategories.Standard;
        _applyingPreset = true;
        foreach (var row in _rows)
            row.Clean = preset.Contains(row.Category);
        _applyingPreset = false;
        UpdateState();
    }

    private void Selection_Changed(object sender, RoutedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (_survey is not { } survey || _applyingPreset)
            return;
        var clean = CleanSet();
        var kept = KeptTargets(survey, clean);
        var space = kept.Count > 0 ? WavesBackup.CheckSpace(kept, _backupRoot) : null;
        BackupText.Text = $"{_backupRoot}\n这次要备份 {kept.Count} 项，{WavesSurveyor.FormatSize(kept.Sum(k => k.Size))}。备份不会自动删除。";
        BackupWarning.Text = space ?? "";
        BackupWarning.Visibility = space != null ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !survey.IsEmpty && survey.RunningBlockers.Count == 0 && space == null && LicenseCheck.IsChecked == true && _pending.Count == 0;
    }

    private HashSet<WavesCategory> CleanSet() => WavesRun.Normalize(_rows.Where(r => r.Clean).Select(r => r.Category));

    private static List<WavesTarget> KeptTargets(WavesSurvey survey, IReadOnlySet<WavesCategory> clean) =>
        survey.Targets.Where(t => !clean.Contains(t.Category) && WavesCategories.NeedsBackupWhenKept(t.Category)).ToList();

    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        ReportBox.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Collapsed;
        SurveyPanel.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Visible;
        Refresh();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_survey is not { } survey)
            return;
        var extras = _extras.Select(x => x.Target);
        Dialogs.ShowText(this, "预演清单", WavesSurveyor.Describe(survey, CleanSet(), extras).TrimEnd(), "Waves 卸载预演清单.txt");
    }

    private void Central_Click(object sender, RoutedEventArgs e)
    {
        var exe = Path.Combine(_env.Paths.CentralFolder, "Waves Central.exe");
        if (!File.Exists(exe))
        {
            Dialogs.ShowText(this, "没有找到 Waves Central", "没有在下面的位置找到 Waves Central：\n" + exe, "说明.txt");
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Win32Exception error)
        {
            Dialogs.ShowText(this, "打开失败", error.Message, "说明.txt");
        }
    }

    private void BackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择备份位置", InitialDirectory = Directory.Exists(_backupRoot) ? _backupRoot : "" };
        if (dialog.ShowDialog(this) != true)
            return;
        var chosen = dialog.FolderName;
        // 备份不能放在要被清理的地方。
        var conflict = _survey?.Targets.Select(t => t.Path).Concat(_survey.MainRoots)
            .FirstOrDefault(t => chosen.Equals(t, StringComparison.OrdinalIgnoreCase) || WavesGuard.IsUnder(chosen, t));
        if (conflict != null)
        {
            Dialogs.ShowText(this, "不能选这个位置", "这个文件夹在 Waves 的清理范围里：\n" + conflict, "说明.txt");
            return;
        }
        _backupRoot = Path.Combine(chosen, "WhatPlugin_Waves备份");
        UpdateState();
    }

    private void RestorePending_Click(object sender, RoutedEventArgs e)
    {
        if (_pending.Count == 0)
            return;
        var folder = _pending[^1];
        var errors = WavesBackup.Restore(folder, _env.Paths);
        ChangedSomething = true;
        Dialogs.ShowText(this, "恢复结果",
            errors.Count == 0 ? "备份已恢复到原位。备份文件还留在：\n" + folder : $"有 {errors.Count} 项没有恢复：\n" + string.Join("\n", errors),
            "恢复结果.txt");
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_survey is not { } survey)
            return;
        var again = WavesSurveyor.Scan(_env);
        if (again.RunningBlockers.Count > 0)
        {
            Refresh();
            return;
        }

        var clean = CleanSet();
        var extras = _extras.Where(x => x.Selected).Select(x => x.Target).ToList();
        var kept = KeptTargets(survey, clean);
        var message = $"所有 Waves 插件都会被卸载。\n\n备份保留项：{kept.Count} 项，{WavesSurveyor.FormatSize(kept.Sum(k => k.Size))}\n"
            + $"运行卸载程序：{string.Join("、", survey.Products.Where(p => !p.IsCentral || clean.Contains(WavesCategory.Central)).Select(p => p.Name))}\n\n"
            + "残留文件进回收站，注册表先导出。卸载完成前不要关闭电脑。\n\n不能保证之后一定能重装成功，这取决于 Waves 的服务器和你的授权。";
        if (!Dialogs.Confirm(this, "开始卸载 Waves？", message, "开始卸载", danger: true))
            return;
        if (clean.Contains(WavesCategory.Licenses)
            && !Dialogs.Confirm(this, "连授权文件一起清理？", "授权文件 Licenses 会移到回收站，不会备份。如果授权还没有移回云端，可能需要联系 Waves 官方支持才能恢复。", "确认清理授权", danger: true))
            return;

        _search?.Cancel();
        _running = true;
        ChangedSomething = true;
        foreach (var step in _steps)
        {
            step.State = "等待";
            step.Detail = "";
        }
        SurveyPanel.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Visible;
        ReportBox.Visibility = Visibility.Collapsed;
        HeadingText.Text = "正在卸载 Waves";
        SubheadText.Text = "请不要关闭这个窗口。";
        StartButton.Visibility = Visibility.Collapsed;
        CloseButton.IsEnabled = false;
        RecheckButton.IsEnabled = ExportButton.IsEnabled = CentralButton.IsEnabled = false;

        WavesRunResult result;
        try
        {
            WavesBackup.RememberLocation(_env.BackupRoot, _backupRoot);
            result = await new WavesRun(_env, again, clean, extras, _backupRoot, this).RunAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = new WavesRunResult(false, "运行中断：" + error.Message, "");
        }

        _running = false;
        CloseButton.IsEnabled = true;
        RecheckButton.IsEnabled = ExportButton.IsEnabled = CentralButton.IsEnabled = true;
        HeadingText.Text = result.Finished ? "卸载流程已结束" : "已停止";
        SubheadText.Text = result.Finished ? "请重启电脑后点“重新检查”。" : "请看下面的说明。";
        ReportBox.Text = result.Report;
        ReportBox.Visibility = Visibility.Visible;
    }

    public void Step(WavesStep step, WavesStepState state, string detail)
    {
        var row = _steps.First(s => s.Step == step);
        row.State = state switch
        {
            WavesStepState.Running => "进行中",
            WavesStepState.Done => "完成",
            WavesStepState.Skipped => "跳过",
            WavesStepState.Failed => "有问题",
            _ => "等待",
        };
        row.Detail = detail;
    }

    public async Task<UninstallerWait> WaitForUninstaller(WavesProduct product, string? launchError)
    {
        var message = launchError == null
            ? $"“{product.Name}”的卸载程序已打开，可能需要授权。请在它的窗口里完成卸载，窗口全部结束后再点继续。"
            : $"没能打开“{product.Name}”的卸载程序：{launchError}";
        var answer = await AskAsync(message, launchError == null ? "卸载完成，继续" : "检查结果", "停止", showHelp: false);
        return answer == 0 ? UninstallerWait.Continue : UninstallerWait.Stop;
    }

    public async Task<StillInstalledChoice> ProductStillInstalled(WavesProduct product, bool canSkip)
    {
        var message = $"“{product.Name}”还登记为已安装。可能是卸载没有完成，也可能需要重启。\n\n"
            + "可以以管理员身份重试。还是失败时，先重启电脑再运行这个工具，或试微软的修复工具。工具不会手动删除 MSI 的安装记录。";
        var answer = await AskAsync(message, "以管理员身份重试", canSkip ? "跳过这一项" : "停止", showHelp: true);
        return answer == 0 ? StillInstalledChoice.RetryAsAdmin : canSkip ? StillInstalledChoice.Skip : StillInstalledChoice.Stop;
    }

    private Task<int> AskAsync(string message, string primary, string second, bool showHelp)
    {
        _prompt = new TaskCompletionSource<int>();
        PromptText.Text = message;
        PromptPrimaryButton.Content = primary;
        PromptSecondButton.Content = second;
        PromptHelpButton.Visibility = showHelp ? Visibility.Visible : Visibility.Collapsed;
        PromptPanel.Visibility = Visibility.Visible;
        return _prompt.Task;
    }

    private void Answer(int value)
    {
        PromptPanel.Visibility = Visibility.Collapsed;
        _prompt?.TrySetResult(value);
    }

    private void PromptPrimary_Click(object sender, RoutedEventArgs e) => Answer(0);

    private void PromptSecond_Click(object sender, RoutedEventArgs e) => Answer(1);

    private void PromptHelp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(TroubleshooterUrl) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            PromptText.Text += "\n\n没能打开浏览器，网址是：" + TroubleshooterUrl;
        }
    }
}
