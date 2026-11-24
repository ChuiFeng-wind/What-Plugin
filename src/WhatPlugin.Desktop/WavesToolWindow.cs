using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// Waves 完全卸载工具（只在 Windows 上有）。和 Windows 版的同名窗口一样：检查阶段只读，
/// 所有改动都在 Core 的 <see cref="WavesRun"/> 里按固定顺序进行，这里只负责显示和确认。
/// </summary>
public sealed class WavesToolWindow : Window, IWavesRunUi
{
    private const string TroubleshooterUrl = "https://support.microsoft.com/help/17588/windows-fix-problems-that-block-programs-being-installed-or-removed";

    private static readonly (WavesStep Step, string Title)[] StepTitles =
    [
        (WavesStep.Backup, "备份保留项"),
        (WavesStep.UninstallMain, "运行 Waves 卸载程序"),
        (WavesStep.UninstallCentral, "运行 Waves Central 卸载程序"),
        (WavesStep.Restore, "恢复保留项"),
        (WavesStep.CleanFiles, "清理残留文件（进回收站）"),
        (WavesStep.CleanRegistry, "清理注册表（先导出备份）"),
        (WavesStep.Verify, "复查"),
    ];

    private readonly WavesEnvironment _env;
    private readonly TextBlock _heading = new() { Text = "Waves 完全卸载工具", FontSize = 19, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _subhead = Note("这一页只读取，不会改动任何内容。点“开始卸载”后才会动手。工具只负责卸载和清理，不会生成、修改或修复任何授权。");
    private readonly ScrollViewer _surveyPanel = new() { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private readonly DockPanel _runPanel = new() { IsVisible = false };
    private readonly Border _pendingPanel;
    private readonly TextBlock _pendingText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 20 };
    private readonly TextBlock _blocker = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
    private readonly RadioButton _standard = new() { Content = "标准清理", Classes = { "Segment" }, GroupName = "WavesPreset" };
    private readonly RadioButton _full = new() { Content = "完全清理", Classes = { "Segment" }, GroupName = "WavesPreset", Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel _categoryList = new();
    private readonly TextBlock _extraNote = Note("正在搜索各磁盘…");
    private readonly StackPanel _extraList = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _backupText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _backupWarning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
    private readonly CheckBox _license = new() { Content = "我已经处理好授权，或确认不需要处理" };
    private readonly StackPanel _stepList = new();
    private readonly Dictionary<WavesStep, (TextBlock State, TextBlock Detail)> _steps = [];
    private readonly Border _promptPanel;
    private readonly TextBlock _promptText = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    private readonly Button _promptHelp = new() { Content = "打开微软修复工具页面", Classes = { "Ghost" }, Margin = new Thickness(0, 0, 8, 0), IsVisible = false };
    private readonly Button _promptSecond = new() { MinWidth = 76 };
    private readonly Button _promptPrimary = new() { Classes = { "Primary" }, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox _report = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, IsVisible = false, Margin = new Thickness(0, 6, 0, 0), VerticalContentAlignment = VerticalAlignment.Top };
    private readonly Button _close = new() { Content = "关闭", MinWidth = 76 };
    private readonly Button _start = new() { Content = "开始卸载", Classes = { "Danger" }, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _recheck = new() { Content = "重新检查", Classes = { "Ghost" } };
    private readonly Button _export = new() { Content = "查看预演清单", Classes = { "Ghost" }, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _central = new() { Content = "打开 Waves Central", Classes = { "Ghost" }, Margin = new Thickness(6, 0, 0, 0) };

    private WavesSurvey? _survey;
    private readonly Dictionary<WavesCategory, CheckBox> _categories = [];
    private readonly Dictionary<WavesCategory, TextBlock> _actions = [];
    private List<(WavesTarget Target, CheckBox Box)> _extras = [];
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
        Dialogs = new WindowDialogs(() => this);
        PickFolder = PickFolderAsync;

        Title = "Waves 完全卸载工具";
        Width = 880;
        Height = 760;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Surface"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Text"));
        this.Bind(FontFamilyProperty, this.GetResourceObservable("UiFont"));
        FontSize = 13;

        foreach (var text in new[] { _pendingText, _blocker, _backupWarning })
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Danger"));
        foreach (var text in new[] { _summary, _backupText })
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextDim"));
        AutomationProperties.SetName(_standard, "标准清理");
        AutomationProperties.SetName(_full, "完全清理");
        AutomationProperties.SetName(_categoryList, "清理项");
        AutomationProperties.SetName(_extraList, "其他 WaveShell");
        AutomationProperties.SetName(_stepList, "卸载步骤");
        AutomationProperties.SetName(_report, "卸载结果");

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_heading);
        _subhead.Margin = new Thickness(0, 4, 0, 0);
        header.Children.Add(_subhead);
        if (env.IsTest)
        {
            var test = new TextBlock { Text = "测试模式：只操作测试文件夹，不会碰真实的 Waves。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            test.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Danger"));
            header.Children.Add(test);
        }

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_recheck);
        left.Children.Add(_export);
        left.Children.Add(_central);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_close);
        right.Children.Add(_start);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(left);

        // ---------- 检查结果 ----------
        var restore = new Button { Content = "恢复这份备份", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var pending = new DockPanel();
        DockPanel.SetDock(restore, Dock.Right);
        pending.Children.Add(restore);
        pending.Children.Add(_pendingText);
        _pendingPanel = Card(pending, new Thickness(0, 0, 0, 10));
        _pendingPanel.IsVisible = false;

        var survey = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        survey.Children.Add(_pendingPanel);
        survey.Children.Add(Card(Stack(Bold("检查结果"), _summary, _blocker), new Thickness(0, 0, 0, 10)));

        var presets = new StackPanel { Orientation = Orientation.Horizontal };
        presets.Children.Add(_standard);
        presets.Children.Add(_full);
        var presetRow = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        DockPanel.SetDock(presets, Dock.Right);
        presetRow.Children.Add(presets);
        var cleanTitle = Bold("清理项");
        cleanTitle.VerticalAlignment = VerticalAlignment.Center;
        presetRow.Children.Add(cleanTitle);
        survey.Children.Add(presetRow);
        var presetNote = Note("勾选表示清理，不勾选表示保留。保留项会先备份，因为 MSI 卸载时也可能把它们删掉，卸载后再放回原位。");
        presetNote.Margin = new Thickness(0, 0, 0, 8);
        survey.Children.Add(presetNote);
        survey.Children.Add(_categoryList);

        _extraNote.Margin = new Thickness(0, 4, 0, 0);
        survey.Children.Add(Card(Stack(Bold("标准目录以外的 WaveShell"), _extraNote, _extraList), new Thickness(0, 2, 0, 10)));

        var backupButton = new Button { Content = "更改", Classes = { "Ghost" }, VerticalAlignment = VerticalAlignment.Top };
        var backupHead = new DockPanel();
        DockPanel.SetDock(backupButton, Dock.Right);
        backupHead.Children.Add(backupButton);
        backupHead.Children.Add(Bold("备份位置"));
        survey.Children.Add(Card(Stack(backupHead, _backupText, _backupWarning), new Thickness(0, 0, 0, 10)));

        var licenseNote = Note("如果你有官方授权，请先用“打开 Waves Central”把授权移回云端。没有需要处理的授权时也可以继续，但卸载后恢复的授权文件不保证还能用。");
        licenseNote.Margin = new Thickness(0, 4, 0, 8);
        survey.Children.Add(Card(Stack(Bold("授权"), licenseNote, _license), new Thickness(0, 0, 0, 4)));
        _surveyPanel.Content = survey;

        // ---------- 卸载过程 ----------
        foreach (var (step, title) in StepTitles)
        {
            var state = new TextBlock { Text = "等待", Classes = { "SmallNote" }, Margin = new Thickness(12, 0, 0, 0) };
            var detail = new TextBlock { Classes = { "SmallNote" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40 };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(state, Dock.Right);
            row.Children.Add(state);
            row.Children.Add(Stack(Bold(title), detail));
            _stepList.Children.Add(row);
            _steps[step] = (state, detail);
        }
        var promptButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        promptButtons.Children.Add(_promptHelp);
        promptButtons.Children.Add(_promptSecond);
        promptButtons.Children.Add(_promptPrimary);
        _promptPanel = Card(Stack(_promptText, promptButtons), new Thickness(0, 6, 0, 10));
        _promptPanel.IsVisible = false;
        DockPanel.SetDock(_stepList, Dock.Top);
        DockPanel.SetDock(_promptPanel, Dock.Top);
        _runPanel.Children.Add(_stepList);
        _runPanel.Children.Add(_promptPanel);
        _runPanel.Children.Add(_report);

        var middle = new Panel();
        middle.Children.Add(_surveyPanel);
        middle.Children.Add(_runPanel);
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        _standard.IsCheckedChanged += (_, _) => ApplyPreset();
        _full.IsCheckedChanged += (_, _) => ApplyPreset();
        _license.IsCheckedChanged += (_, _) => UpdateState();
        _recheck.Click += (_, _) => Recheck();
        _export.Click += async (_, _) => await ShowPreviewAsync();
        _central.Click += async (_, _) => await OpenCentralAsync();
        backupButton.Click += async (_, _) => await ChangeBackupFolderAsync();
        restore.Click += async (_, _) => await RestorePendingAsync();
        _close.Click += (_, _) => Close();
        _start.Click += async (_, _) => await StartAsync();
        _promptPrimary.Click += (_, _) => Answer(0);
        _promptSecond.Click += (_, _) => Answer(1);
        _promptHelp.Click += (_, _) =>
        {
            if (SystemShell.OpenWebPage(TroubleshooterUrl) != null)
                _promptText.Text += "\n\n没能打开浏览器，网址是：" + TroubleshooterUrl;
        };
        Closing += OnClosing;
        Refresh();
    }

    /// <summary>确认和文字窗口。测试换成脚本。</summary>
    public IDialogs Dialogs { get; set; }

    /// <summary>选备份文件夹，取消返回 null。测试换成脚本。</summary>
    public Func<Task<string?>> PickFolder { get; set; }

    /// <summary>卸载或恢复动过文件后为 true，主窗口要重新扫描。</summary>
    public bool ChangedSomething { get; private set; }

    // 测试检查用。
    public string Summary => _summary.Text ?? "";
    public string BackupText => _backupText.Text ?? "";
    public bool CanStart => _start.IsEnabled && _start.IsVisible;
    public bool Running => _running;
    public string Report => _report.IsVisible ? _report.Text ?? "" : "";
    public string? Prompt => _promptPanel.IsVisible ? _promptText.Text : null;
    public string Heading => _heading.Text ?? "";
    public IReadOnlyDictionary<WavesCategory, CheckBox> Categories => _categories;
    public IReadOnlyList<(WavesTarget Target, CheckBox Box)> Extras => _extras;
    public CheckBox License => _license;
    public RadioButton FullPreset => _full;
    public string StepState(WavesStep step) => _steps[step].State.Text ?? "";
    public void AnswerPrompt(bool primary) => Answer(primary ? 0 : 1);

    /// <summary>磁盘上额外 WaveShell 的搜索（后台进行）。测试等它结束。</summary>
    public Task ExtraSearch { get; private set; } = Task.CompletedTask;

    private static TextBlock Note(string text) =>
        new() { Text = text, Classes = { "SmallNote" }, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Bold(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    private static Border Card(Control child, Thickness margin) =>
        new() { Classes = { "Card" }, Padding = new Thickness(14, 12), Margin = margin, Child = child };

    private void OnClosing(object? sender, WindowClosingEventArgs e)
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

        var chosen = _full.IsChecked == true ? WavesCategories.Full : WavesCategories.Standard;
        var previous = _categories.ToDictionary(c => c.Key, c => c.Value.IsChecked == true);
        _categories.Clear();
        _actions.Clear();
        _categoryList.Children.Clear();
        _applyingPreset = true;
        foreach (var category in Enum.GetValues<WavesCategory>()
                     .Where(c => survey.Targets.Any(t => t.Category == c) || survey.RegistryTargets.Any(t => t.Category == c)))
            _categoryList.Children.Add(CategoryCard(category, DescribeCategory(survey, category),
                previous.TryGetValue(category, out var clean) ? clean : chosen.Contains(category)));
        _standard.IsChecked = _full.IsChecked != true;
        _applyingPreset = false;

        var lines = new List<string>
        {
            survey.Products.Count == 0
                ? "卸载程序：没有找到"
                : "卸载程序：" + string.Join("、", survey.Products.Select(p => $"{p.Name} {p.Version}" + (p.MsiCode != null ? "（MSI）" : ""))),
            "主目录：" + (survey.MainRoots.Count == 0 ? "没有找到" : string.Join("、", survey.MainRoots)),
            $"文件：共 {WavesSurveyor.FormatSize(survey.Targets.Sum(t => t.Size))}；注册表：{survey.RegistryTargets.Count} 项，不含 MSI 自己管理的记录",
        };
        if (survey.RunningBackground.Count > 0)
            lines.Add("正在运行的 Waves 后台程序：" + string.Join("、", survey.RunningBackground) + "。卸载程序可能会要求关闭它们。");
        if (survey.IsEmpty)
            lines.Add("已知位置没有查到 Waves 的内容。");
        _summary.Text = string.Join("\n", lines);

        _blocker.Text = survey.RunningBlockers.Count > 0 ? "请先关闭：" + string.Join("、", survey.RunningBlockers) + "，再点“重新检查”。" : "";
        _blocker.IsVisible = survey.RunningBlockers.Count > 0;

        _pending = WavesBackup.FindUnrestored(_env.BackupRoot);
        _pendingPanel.IsVisible = _pending.Count > 0;
        if (_pending.Count > 0)
            _pendingText.Text = $"上次卸载有一份备份还没恢复：{_pending[^1]}";

        UpdateState();
        ExtraSearch = SearchExtrasAsync(survey);
    }

    private Border CategoryCard(WavesCategory category, string detail, bool clean)
    {
        var title = WavesCategories.Label(category);
        var box = new CheckBox { IsChecked = clean, IsEnabled = !WavesCategories.IsLocked(category), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        AutomationProperties.SetName(box, "清理 " + title);
        var action = new TextBlock { Classes = { "SmallNote" }, Margin = new Thickness(12, 0, 0, 0) };
        _categories[category] = box;
        _actions[category] = action;
        ShowAction(category);
        box.IsCheckedChanged += (_, _) =>
        {
            ShowAction(category);
            UpdateState();
        };

        var head = new DockPanel();
        DockPanel.SetDock(action, Dock.Right);
        head.Children.Add(action);
        head.Children.Add(Bold(title));
        var text = Note(detail);
        text.Margin = new Thickness(0, 4, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(Stack(head, text));
        var card = Card(row, new Thickness(0, 0, 0, 8));
        card.Padding = new Thickness(14, 10);
        return card;
    }

    private void ShowAction(WavesCategory category) =>
        _actions[category].Text = _categories[category].IsChecked == true ? "卸载后清理，进回收站" : WavesCategories.KeepAction(category);

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

    private async Task SearchExtrasAsync(WavesSurvey survey)
    {
        _search?.Cancel();
        var search = new CancellationTokenSource();
        _search = search;
        _extras = [];
        _extraList.Children.Clear();
        _extraNote.Text = "正在搜索各磁盘，大约需要一两分钟。搜索期间也可以开始卸载，那样就不处理这里的文件。";
        try
        {
            var found = await Task.Run(() => WavesSurveyor.FindExtraShells(_env, survey, search.Token), search.Token);
            if (search.IsCancellationRequested)
                return;
            foreach (var target in found)
            {
                var box = new CheckBox { Margin = new Thickness(0, 3), Content = new TextBlock { Text = $"{target.Path}（{WavesSurveyor.FormatSize(target.Size)}）", TextWrapping = TextWrapping.Wrap } };
                AutomationProperties.SetName(box, target.Path);
                _extras.Add((target, box));
                _extraList.Children.Add(box);
            }
            _extraNote.Text = found.Count == 0 ? "没有找到。" : $"找到 {found.Count} 个，默认不处理。勾选的会在清理残留时移到回收站。";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyPreset()
    {
        if (_applyingPreset || _survey == null)
            return;
        var preset = _full.IsChecked == true ? WavesCategories.Full : WavesCategories.Standard;
        _applyingPreset = true;
        foreach (var (category, box) in _categories)
            box.IsChecked = preset.Contains(category);
        _applyingPreset = false;
        UpdateState();
    }

    private void UpdateState()
    {
        if (_survey is not { } survey || _applyingPreset)
            return;
        var kept = KeptTargets(survey, CleanSet());
        var space = kept.Count > 0 ? WavesBackup.CheckSpace(kept, _backupRoot) : null;
        _backupText.Text = $"{_backupRoot}\n这次要备份 {kept.Count} 项，{WavesSurveyor.FormatSize(kept.Sum(k => k.Size))}。备份不会自动删除。";
        _backupWarning.Text = space ?? "";
        _backupWarning.IsVisible = space != null;
        _start.IsEnabled = !survey.IsEmpty && survey.RunningBlockers.Count == 0 && space == null && _license.IsChecked == true && _pending.Count == 0;
    }

    private HashSet<WavesCategory> CleanSet() =>
        WavesRun.Normalize(_categories.Where(c => c.Value.IsChecked == true).Select(c => c.Key));

    private static List<WavesTarget> KeptTargets(WavesSurvey survey, IReadOnlySet<WavesCategory> clean) =>
        survey.Targets.Where(t => !clean.Contains(t.Category) && WavesCategories.NeedsBackupWhenKept(t.Category)).ToList();

    private void Recheck()
    {
        _report.IsVisible = false;
        _runPanel.IsVisible = false;
        _surveyPanel.IsVisible = true;
        _start.IsVisible = true;
        _heading.Text = "Waves 完全卸载工具";
        Refresh();
    }

    private async Task ShowPreviewAsync()
    {
        if (_survey is not { } survey)
            return;
        await Dialogs.ShowText("预演清单", WavesSurveyor.Describe(survey, CleanSet(), _extras.Select(x => x.Target)).TrimEnd(), "Waves 卸载预演清单.txt");
    }

    private async Task OpenCentralAsync()
    {
        var exe = Path.Combine(_env.Paths.CentralFolder, "Waves Central.exe");
        if (!File.Exists(exe))
        {
            await Dialogs.ShowText("没有找到 Waves Central", "没有在下面的位置找到 Waves Central：\n" + exe, "说明.txt");
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Win32Exception error)
        {
            await Dialogs.ShowText("打开失败", error.Message, "说明.txt");
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var start = Directory.Exists(_backupRoot) ? await StorageProvider.TryGetFolderFromPathAsync(_backupRoot) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择备份位置", SuggestedStartLocation = start });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task ChangeBackupFolderAsync()
    {
        if (await PickFolder() is not { } chosen)
            return;
        // 备份不能放在要被清理的地方。
        var conflict = _survey?.Targets.Select(t => t.Path).Concat(_survey.MainRoots)
            .FirstOrDefault(t => chosen.Equals(t, StringComparison.OrdinalIgnoreCase) || WavesGuard.IsUnder(chosen, t));
        if (conflict != null)
        {
            await Dialogs.ShowText("不能选这个位置", "这个文件夹在 Waves 的清理范围里：\n" + conflict, "说明.txt");
            return;
        }
        _backupRoot = Path.Combine(chosen, "WhatPlugin_Waves备份");
        UpdateState();
    }

    private async Task RestorePendingAsync()
    {
        if (_pending.Count == 0)
            return;
        var folder = _pending[^1];
        var errors = WavesBackup.Restore(folder, _env.Paths);
        ChangedSomething = true;
        await Dialogs.ShowText("恢复结果",
            errors.Count == 0 ? "备份已恢复到原位。备份文件还留在：\n" + folder : $"有 {errors.Count} 项没有恢复：\n" + string.Join("\n", errors),
            "恢复结果.txt");
        Refresh();
    }

    private async Task StartAsync()
    {
        if (_survey is not { } survey || _running)
            return;
        var again = WavesSurveyor.Scan(_env);
        if (again.RunningBlockers.Count > 0)
        {
            Refresh();
            return;
        }

        var clean = CleanSet();
        var extras = _extras.Where(x => x.Box.IsChecked == true).Select(x => x.Target).ToList();
        var kept = KeptTargets(survey, clean);
        var message = $"所有 Waves 插件都会被卸载。\n\n备份保留项：{kept.Count} 项，{WavesSurveyor.FormatSize(kept.Sum(k => k.Size))}\n"
            + $"运行卸载程序：{string.Join("、", survey.Products.Where(p => !p.IsCentral || clean.Contains(WavesCategory.Central)).Select(p => p.Name))}\n\n"
            + "残留文件进回收站，注册表先导出。卸载完成前不要关闭电脑。\n\n不能保证之后一定能重装成功，这取决于 Waves 的服务器和你的授权。";
        if (!await Dialogs.Confirm("开始卸载 Waves？", message, "开始卸载", danger: true))
            return;
        if (clean.Contains(WavesCategory.Licenses)
            && !await Dialogs.Confirm("连授权文件一起清理？", "授权文件 Licenses 会移到回收站，不会备份。如果授权还没有移回云端，可能需要联系 Waves 官方支持才能恢复。", "确认清理授权", danger: true))
            return;

        _search?.Cancel();
        _running = true;
        ChangedSomething = true;
        foreach (var (state, detail) in _steps.Values)
        {
            state.Text = "等待";
            detail.Text = "";
        }
        _surveyPanel.IsVisible = false;
        _runPanel.IsVisible = true;
        _report.IsVisible = false;
        _heading.Text = "正在卸载 Waves";
        _subhead.Text = "请不要关闭这个窗口。";
        _start.IsVisible = false;
        _close.IsEnabled = _recheck.IsEnabled = _export.IsEnabled = _central.IsEnabled = false;

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
        _close.IsEnabled = _recheck.IsEnabled = _export.IsEnabled = _central.IsEnabled = true;
        _heading.Text = result.Finished ? "卸载流程已结束" : "已停止";
        _subhead.Text = result.Finished ? "请重启电脑后点“重新检查”。" : "请看下面的说明。";
        _report.Text = result.Report;
        _report.IsVisible = true;
    }

    public void Step(WavesStep step, WavesStepState state, string detail)
    {
        var (stateText, detailText) = _steps[step];
        stateText.Text = state switch
        {
            WavesStepState.Running => "进行中",
            WavesStepState.Done => "完成",
            WavesStepState.Skipped => "跳过",
            WavesStepState.Failed => "有问题",
            _ => "等待",
        };
        detailText.Text = detail;
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
        _promptText.Text = message;
        _promptPrimary.Content = primary;
        _promptSecond.Content = second;
        _promptHelp.IsVisible = showHelp;
        _promptPanel.IsVisible = true;
        _promptPrimary.Focus();
        return _prompt.Task;
    }

    private void Answer(int value)
    {
        _promptPanel.IsVisible = false;
        _prompt?.TrySetResult(value);
    }
}
