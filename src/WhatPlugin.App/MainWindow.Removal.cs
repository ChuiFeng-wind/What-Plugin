using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>垃圾桶卸载和删除多余格式。只有这里会动插件文件，而且都要用户先确认。</summary>
public partial class MainWindow
{
    private static readonly PluginFormat[] EveryFormat = Enum.GetValues<PluginFormat>();

    private bool _removalBusy;
    private bool _loadingVersions;
    private Dictionary<string, List<long>>? _fileOwners;

    private Dictionary<string, List<long>> FileOwners => _fileOwners ??= RemovalPlanner.OwnersByPath(_database.LoadPlugins());

    private bool CanStartRemoval()
    {
        if (_scanCancel == null && !_removalBusy)
            return true;
        StatusText.Text = "正在扫描或卸载，请等它结束";
        return false;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.0} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    // ---------- 详情面板：已安装版本 ----------

    private void ShowInstalledVersions(PluginRow row)
    {
        var waves = RemovalPlanner.IsWaves(row);
        _loadingVersions = true;
        var versions = row.Files.Select(file =>
        {
            var exists = RemovalPlanner.PathExists(file.Path);
            var shared = FileOwners.TryGetValue(file.Path, out var owners) && owners.Any(id => id != row.Id);
            var title = $"{Labels.Format(file.Format)}，{Labels.Arch(file.Arch)}，{Labels.Status(file.Status)}";
            if (file.SdkCategory.Length > 0)
                title += $"，自带分类 {file.SdkCategory}";
            var note = !exists ? "文件已不在，重新扫描后这一行会消失"
                : shared ? "和其他插件共用这个文件，不能单独删除"
                : FormatSize(RemovalPlanner.SizeOf(file.Path));
            if (file.Error != null)
                note += $"（{file.Error}）";
            return new VersionRow(file, title, note, exists && !shared && !waves && row.Files.Count > 1);
        }).ToList();
        _loadingVersions = false;

        InstalledVersionsList.ItemsSource = versions;
        var canDelete = versions.Any(v => v.CanSelect);
        DeleteVersionsButton.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        VersionsNote.Text = waves ? "Waves 插件共用 WaveShell，这里不提供删除。"
            : canDelete ? "可以勾选不需要的版本。文件会移到回收站，可以在这里恢复；至少要留下一个可用的版本。"
            : "";
        VersionsNote.Visibility = VersionsNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDeleteVersionsButton();

        var removed = _database.LoadRemovedFiles().Where(r => r.PluginKey == row.Key).ToList();
        RemovedVersionsList.ItemsSource = removed.Select(r => new RemovedFileRow(r)).ToList();
        RemovedVersionsPanel.Visibility = removed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Version_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loadingVersions)
            UpdateDeleteVersionsButton();
    }

    private void UpdateDeleteVersionsButton()
    {
        var count = (InstalledVersionsList.ItemsSource as IEnumerable<VersionRow>)?.Count(v => v.Selected) ?? 0;
        DeleteVersionsButton.IsEnabled = count > 0;
        DeleteVersionsButton.Content = count > 0 ? $"删除选中的 {count} 个版本…" : "删除选中的版本…";
    }

    private async void DeleteVersions_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || !CanStartRemoval())
            return;
        SaveCurrent(force: true);
        var row = _current.Row;
        var chosen = ((IEnumerable<VersionRow>)InstalledVersionsList.ItemsSource).Where(v => v.Selected).ToList();
        var paths = chosen.Select(v => v.File.Path).ToList();
        var problem = RemovalPlanner.CheckFormatRemoval(row, paths, _database.LoadPlugins());
        if (problem != null)
        {
            StatusText.Text = problem;
            return;
        }

        var message = new StringBuilder("这些文件会移到回收站，可以在这里恢复：\n");
        foreach (var version in chosen)
            message.Append($"\n{Labels.Format(version.File.Format)}：{version.File.Path}");
        var projects = _report.Usage.GetValueOrDefault(row.Key)?.Projects ?? 0;
        if (projects > 0)
            message.Append($"\n\n这款插件有 {projects} 个工程用过。现有记录分不清工程用的是哪个格式，旧工程可能依赖这个版本。");
        if (UninstallMatcher.Find(row, InstalledPrograms.Load()).Exact is { IsMsi: true })
            message.Append("\n\n这款插件是 MSI 安装的，修复或更新时可能把文件装回来。");
        message.Append("\n\n官方卸载程序和它的记录不会被改动；卸载程序遇到文件已被删除时的表现，无法保证所有厂商都一致。");
        if (!Dialogs.Confirm(this, "删除这些版本？", message.ToString(), "移到回收站", danger: true))
            return;

        _removalBusy = true;
        try
        {
            var files = chosen.Select(v => (row, new RemovableFile(v.File.Path, v.File.Format, RemovalPlanner.SizeOf(v.File.Path)))).ToList();
            var results = await RecycleAsync(files);
            await RunScanAsync(full: false, EveryFormat);
            StatusText.Text = DescribeRecycle(results, "已把 {0} 个版本移到回收站，可以在详情里恢复");
        }
        finally
        {
            _removalBusy = false;
        }
    }

    private async Task<IReadOnlyList<FileOpResult>> RecycleAsync(IReadOnlyList<(PluginRow Plugin, RemovableFile File)> files)
    {
        StatusText.Text = "正在移到回收站…";
        var results = await FileOpRunner.RunAsync(new FileOpRequest(FileOpKind.Recycle, files.Select(f => f.File.Path).ToList(), _database.LoadVst2Folders()));
        foreach (var (plugin, file) in files)
        {
            if (results.First(r => string.Equals(r.Path, file.Path, StringComparison.OrdinalIgnoreCase)).Ok)
                _database.SaveRemovedFile(plugin.Key, plugin.Name, file.Path, file.Format, file.Size, DateTime.UtcNow);
        }
        return results;
    }

    private static string DescribeRecycle(IReadOnlyList<FileOpResult> results, string success)
    {
        var ok = results.Count(r => r.Ok);
        var text = string.Format(success, ok);
        var unconfirmed = results.Count(r => r.Ok && r.Unconfirmed);
        if (unconfirmed > 0)
            text += $"；其中 {unconfirmed} 个没能在回收站里确认";
        var failed = results.FirstOrDefault(r => !r.Ok);
        if (failed != null)
            text = (ok > 0 ? text + "；" : "") + $"{results.Count(r => !r.Ok)} 个失败：{failed.Error}";
        return text;
    }

    private void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RemovedFileRow row)
            _ = RestoreAsync(row.File);
    }

    private async Task RestoreAsync(RemovedFile file)
    {
        if (!CanStartRemoval())
            return;
        SaveCurrent(force: true);
        _removalBusy = true;
        try
        {
            StatusText.Text = "正在从回收站恢复…";
            var result = (await FileOpRunner.RunAsync(new FileOpRequest(FileOpKind.Restore, [file.Path], _database.LoadVst2Folders())))[0];
            // 回收站已清空时这条记录再也用不上。
            if (result.Ok || result.Error == "回收站里已经没有这个文件")
                _database.DeleteRemovedFile(file.Path);
            if (result.Ok)
                await RunScanAsync(full: false, EveryFormat);
            else if (_current != null)
                ShowInstalledVersions(_current.Row);
            UpdateRemovedFilesButton();
            StatusText.Text = result.Ok ? $"已恢复 {file.Path}" : $"恢复失败：{result.Error}";
        }
        finally
        {
            _removalBusy = false;
        }
    }

    /// <summary>删掉的文件又出现了，多半是官方更新或修复装回来的。提示一次后不再记录。</summary>
    // ---------- 垃圾桶：卸载 ----------

    private void UpdateUninstallButton()
    {
        var count = PluginGrid.SelectedItems.Count;
        UninstallButton.IsEnabled = count > 0;
        UninstallButton.Content = count > 0 ? $"卸载选中的 {count} 款…" : "卸载选中的插件…";
    }

    private void UpdateRemovedFilesButton()
    {
        var count = _database.LoadRemovedFiles().Count;
        RemovedFilesButton.Content = $"已移除的文件（{count}）";
        RemovedFilesButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUninstallHistoryButton()
    {
        var count = _database.LoadUninstallHistory().Count;
        UninstallHistoryButton.Content = count > 0 ? $"卸载历史（{count}）" : "卸载历史";
    }

    private void UninstallHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UninstallHistoryDialog(_database, _plugins.Select(p => p.Row).ToList()) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.Changed)
            return;
        UpdateUninstallHistoryButton();
        if (_current != null)
            ShowDetail();
    }

    private void RemovedFiles_Click(object sender, RoutedEventArgs e)
    {
        var chosen = RemovedFilesDialog.Show(this, _database.LoadRemovedFiles());
        if (chosen != null)
            _ = RestoreAsync(chosen);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartRemoval())
            return;
        SaveCurrent(force: true);
        var selected = PluginGrid.SelectedItems.Cast<PluginViewModel>().Select(p => p.Row).ToList();
        if (selected.Count == 0)
            return;

        var programs = InstalledPrograms.Load();
        var waves = selected.Where(RemovalPlanner.IsWaves).ToList();
        var others = selected.Except(waves).ToList();
        if (waves.Count > 0)
        {
            var goOn = WavesUninstallDialog.Show(this, waves, others.Count, programs, out var openTool);
            if (openTool)
            {
                await OpenWavesToolAsync();
                return;
            }
            if (!goOn)
                return;
        }
        if (others.Count == 0)
            return;

        var plans = RemovalPlanner.Plan(others, _database.LoadPlugins(), programs, _report.Usage);
        var dialog = new UninstallDialog(plans) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _removalBusy = true;
        try
        {
            await RunUninstallAsync(plans, dialog.Choices);
        }
        finally
        {
            _removalBusy = false;
        }
    }

    private async void WavesTool_Click(object sender, RoutedEventArgs e) => await OpenWavesToolAsync();

    /// <summary>可用 --waves-test-root 指向假目录，界面流程可以完整跑一遍而不碰真实的 Waves。</summary>
    private async Task OpenWavesToolAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--waves-test-root");
        var test = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        var tool = new WavesToolWindow(test != null ? WavesEnvironment.ForTest(test) : WavesEnvironment.Real()) { Owner = this };
        tool.ShowDialog();
        if (!tool.ChangedSomething || test != null)
            return;

        _removalBusy = true;
        try
        {
            var trashBefore = _plugins.Where(p => p.Row.User.Hidden && !p.Row.IsMissing).Select(p => p.Row.Id).ToHashSet();
            await RunScanAsync(full: false, EveryFormat, recordMissing: false);
            var goneRows = _database.LoadPlugins()
                .Where(p => RemovalPlanner.IsWaves(p) && p.Files.All(f => !RemovalPlanner.PathExists(f.Path)))
                .ToList();
            var now = DateTime.UtcNow;
            _database.AddUninstallRecords(goneRows
                .Where(p => trashBefore.Contains(p.Id))
                .Select(p => UninstallHistory.From(p, UninstallWay.WavesTool, "", now)));
            _database.ForgetUninstalled(goneRows.Select(p => p.Id).ToList());
            LoadPlugins();
        }
        finally
        {
            _removalBusy = false;
        }
    }

    private async Task RunUninstallAsync(IReadOnlyList<UninstallPlan> plans, IReadOnlyList<UninstallChoice> choices)
    {
        var chosen = choices.Where(c => c.Method != UninstallMethod.Skip).ToList();
        var recycle = chosen.Where(c => c.Method == UninstallMethod.RecycleFiles)
            .SelectMany(c => c.Plan.Files.Select(f => (c.Plan.Plugin, File: f)))
            .ToList();
        IReadOnlyList<FileOpResult> recycled = [];
        if (recycle.Count > 0)
            recycled = await RecycleAsync(recycle);

        var programs = chosen.Where(c => c.Method == UninstallMethod.Official).Select(c => c.Program!).DistinctBy(p => p.Key).ToList();
        for (var i = 0; i < programs.Count; i++)
        {
            var program = programs[i];
            var launched = StartUninstaller(program);
            var last = i == programs.Count - 1;
            var wait = Dialogs.Confirm(this, launched == null ? "官方卸载程序已打开" : "没能打开官方卸载程序",
                (launched == null ? $"请在“{program.Name}”的卸载窗口里完成操作。完成后再点下面的按钮。" : launched)
                + (last ? "" : $"\n\n接下来还有 {programs.Count - i - 1} 个卸载程序。"),
                last ? "重新扫描" : "下一个");
            if (!wait)
            {
                StatusText.Text = "已停止。卸载程序完成后，可以到“设置”里重新扫描";
                return;
            }
        }

        // 这次卸载的由下面按选的方式和原因记进历史，扫描时不再当成“发现已不在”重复记。
        await RunScanAsync(full: false, EveryFormat, recordMissing: false);
        ReportUninstall(chosen, recycled);
    }

    /// <summary>运行注册表里登记的卸载命令，不加静默参数，用户在官方窗口里操作。</summary>
    /// <returns>失败原因，成功时返回 null。</returns>
    private static string? StartUninstaller(InstalledProgram program)
    {
        var command = UninstallCommand.Parse(program.UninstallCommand);
        if (command == null)
            return "卸载命令无法识别：" + program.UninstallCommand;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command.Value.FileName, command.Value.Arguments) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception error)
        {
            return error.NativeErrorCode == 1223 ? "你取消了管理员授权。" : $"启动失败：{error.Message}";
        }
    }

    /// <summary>以磁盘上实际还在的文件为准报告，并把已经没有文件的插件移出垃圾桶。</summary>
    private void ReportUninstall(IReadOnlyList<UninstallChoice> choices, IReadOnlyList<FileOpResult> recycled)
    {
        var report = new StringBuilder();
        var gone = new List<long>();
        var history = new List<UninstallRecord>();
        foreach (var choice in choices)
        {
            var row = choice.Plan.Plugin;
            var left = row.Files.Select(f => f.Path).Where(RemovalPlanner.PathExists).ToList();
            if (left.Count == 0)
            {
                gone.Add(row.Id);
                var way = choice.Method switch
                {
                    UninstallMethod.Official => UninstallWay.Official,
                    UninstallMethod.RecycleFiles => UninstallWay.RecycleFiles,
                    _ => UninstallWay.FoundMissing,
                };
                history.Add(UninstallHistory.From(row, way, choice.Reason, DateTime.UtcNow));
                report.AppendLine($"{row.Name}：文件已全部移除");
                continue;
            }
            report.AppendLine($"{row.Name}：还剩 {left.Count} 个文件");
            foreach (var path in left)
            {
                var failed = recycled.FirstOrDefault(r => !r.Ok && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
                report.AppendLine("  " + path + (failed != null ? $"（{failed.Error}）" : ""));
            }
        }

        var unconfirmed = recycled.Count(r => r.Ok && r.Unconfirmed);
        if (unconfirmed > 0)
            report.AppendLine().AppendLine($"有 {unconfirmed} 个文件已不在原位，但没能在回收站里确认。");
        if (recycled.Any(r => r.Ok))
            report.AppendLine().AppendLine("移到回收站的文件，可以在垃圾桶页的“已移除的文件”里恢复。");

        _database.AddUninstallRecords(history);
        if (history.Count > 0)
            report.AppendLine().AppendLine($"已记进垃圾桶的“卸载历史”：{string.Join("、", history.Select(h => h.Name))}");
        _database.ForgetUninstalled(gone);
        LoadPlugins();
        UpdateUninstallHistoryButton();
        Dialogs.ShowText(this, "卸载结果", report.ToString().TrimEnd(), "卸载结果.txt");
        StatusText.Text = $"卸载结束：{gone.Count} 款已移除，{choices.Count - gone.Count} 款还有文件";
    }
}

public sealed class VersionRow(MergedFile file, string title, string note, bool canSelect) : INotifyPropertyChanged
{
    private bool _selected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MergedFile File { get; } = file;
    public string Title { get; } = title;
    public string Path => File.Path;
    public string Note { get; } = note;
    public bool CanSelect { get; } = canSelect;
    public Visibility CheckVisibility => CanSelect ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => Title + " " + Path;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public override string ToString() => AutomationName;
}

public sealed class RemovedFileRow(RemovedFile file)
{
    public RemovedFile File { get; } = file;
    public string Title { get; } = $"{Labels.Format(file.Format)}，{MainWindow.FormatSize(file.Size)}，{file.RemovedUtc.ToLocalTime():yyyy-MM-dd} 移除";
    public string Path => File.Path;
    public string RestoreName => "恢复 " + File.Path;

    public override string ToString() => Path;
}

/// <summary>选中 Waves 时的说明。普通卸载碰不得 WaveShell，所以这里不动任何文件。</summary>
internal static class WavesUninstallDialog
{
    /// <returns>是否继续处理其余插件。</returns>
    public static bool Show(Window owner, IReadOnlyList<PluginRow> waves, int otherCount, IReadOnlyList<InstalledProgram> programs, out bool openTool)
    {
        var wantsTool = false;
        var window = new Window
        {
            Owner = owner,
            Title = "选中了 Waves 插件",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        var names = string.Join("、", waves.Select(w => w.Name).Take(6)) + (waves.Count > 6 ? $" 等 {waves.Count} 款" : "");
        var message = $"{names} 是 Waves 插件。它们共用 WaveShell，不能像普通插件一样删文件，否则其他 Waves 插件也会受影响。\n\n"
            + "只卸载这几款：打开 Waves Central，在已安装的产品里选中它们后卸载。如果 Waves Central 看不到它们，就只能用下面的完全卸载工具。\n\n"
            + "完全卸载全部 Waves：所有 Waves 插件都会消失，残留可能影响之后重装，所以要用专门的工具。打开工具后先只读检查，你确认后才会动手。";
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 10, 0, 0) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        AutomationProperties.SetAutomationId(body, "DialogMessage");

        var result = false;
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        AutomationProperties.SetAutomationId(note, "DialogResult");

        var central = new Button { Content = "打开 Waves Central", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(central, "WavesCentral");
        central.Click += (_, _) =>
        {
            var error = OpenWavesCentral(programs);
            if (error == null)
                window.Close();
            else
                note.Text = error;
        };

        var full = new Button { Content = "完全卸载全部 Waves", Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(full, "WavesFullUninstall");
        full.Click += (_, _) =>
        {
            wantsTool = true;
            window.Close();
        };

        var waveButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        central.Margin = new Thickness(0);
        waveButtons.Children.Add(full);
        waveButtons.Children.Add(central);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        if (otherCount > 0)
        {
            var skip = new Button { Content = $"跳过 Waves，继续其余 {otherCount} 款", Margin = new Thickness(0, 0, 8, 0) };
            skip.Style = (Style)Application.Current.FindResource("PrimaryButton");
            AutomationProperties.SetAutomationId(skip, "DialogOk");
            skip.Click += (_, _) =>
            {
                result = true;
                window.Close();
            };
            buttons.Children.Add(skip);
        }
        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "DialogCancel");
        cancel.Click += (_, _) => window.Close();
        buttons.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        content.Children.Add(new TextBlock { Text = "选中了 Waves 插件", FontSize = 17, FontWeight = FontWeights.SemiBold });
        content.Children.Add(body);
        content.Children.Add(note);
        content.Children.Add(waveButtons);
        content.Children.Add(buttons);

        var border = new Border { BorderThickness = new Thickness(1), Child = content };
        border.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        window.Content = border;
        window.ShowDialog();
        openTool = wantsTool;
        return result;
    }

    private static string? OpenWavesCentral(IReadOnlyList<InstalledProgram> programs)
    {
        var folders = programs.Where(p => p.Name.StartsWith("Waves Central", StringComparison.OrdinalIgnoreCase)).Select(p => p.InstallLocation)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Waves Central"));
        var exe = folders.Where(f => f.Length > 0).Select(f => Path.Combine(f, "Waves Central.exe")).FirstOrDefault(File.Exists);
        if (exe == null)
            return "没有找到 Waves Central，请先从 Waves 官网安装。";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception error)
        {
            return "打开失败：" + error.Message;
        }
    }
}

/// <summary>列出移到回收站的文件，一次恢复一个。</summary>
internal static class RemovedFilesDialog
{
    public static RemovedFile? Show(Window owner, IReadOnlyList<RemovedFile> files)
    {
        var window = new Window
        {
            Owner = owner,
            Title = "已移除的文件",
            Width = 640,
            Height = 520,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        RemovedFile? chosen = null;
        var list = new StackPanel();
        foreach (var group in files.GroupBy(f => f.PluginName).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            list.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
            foreach (var file in group)
            {
                var row = new RemovedFileRow(file);
                var restore = new Button { Content = "恢复", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                restore.Style = (Style)Application.Current.FindResource("GhostButton");
                AutomationProperties.SetName(restore, row.RestoreName);
                restore.Click += (_, _) =>
                {
                    chosen = file;
                    window.Close();
                };
                DockPanel.SetDock(restore, Dock.Right);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = file.Path, TextWrapping = TextWrapping.Wrap });
                var detail = new TextBlock { Text = row.Title, FontSize = 12 };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                text.Children.Add(detail);
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                line.Children.Add(restore);
                line.Children.Add(text);
                list.Children.Add(line);
            }
        }

        var close = new Button { Content = "关闭", MinWidth = 76, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        AutomationProperties.SetAutomationId(close, "DialogCancel");
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "已移除的文件", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock { Text = "这些文件在回收站里。清空回收站后就不能再从这里恢复了。", FontSize = 12, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        heading.Children.Add(hint);
        DockPanel.SetDock(heading, Dock.Top);

        var layout = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        layout.Children.Add(heading);
        layout.Children.Add(close);
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        var border = new Border { BorderThickness = new Thickness(1), Child = layout };
        border.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        window.Content = border;
        window.ShowDialog();
        return chosen;
    }
}
