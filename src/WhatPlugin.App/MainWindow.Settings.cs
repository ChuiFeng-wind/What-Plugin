using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>一个主题选项。预览用它自己的颜色画，不跟随当前主题。</summary>
public sealed class ThemeOption(ThemePalette palette, bool isCurrent)
{
    public string Id => palette.Id;
    public string Name => palette.Name;
    public bool IsCurrent => isCurrent;
    public string Description => palette.IsDark ? "深色" : "浅色";
    public Brush Bg => Brush("Bg1");
    public Brush Side => Brush("Bg0");
    public Brush Card => Brush("Surface");
    public Brush Line => Brush("LineStrong");
    public Brush Ink => Brush("Text");
    public Brush Dim => Brush("Muted");
    public Brush Accent => Brush("Accent");

    private Brush Brush(string key) => ThemeManager.CreateBrush(palette[key]);
}

/// <summary>设置页里的一个插件文件夹。</summary>
/// <summary>DAW 用过、但还没加进扫描的 VST2 文件夹。</summary>
public sealed record SuggestedFolder(string Path, IReadOnlyList<string> Daws)
{
    public string Label => Daws.Count > 0 ? $"DAW（{string.Join("、", Daws)}）在用这个文件夹：" : "DAW 在用这个文件夹：";
}

public sealed record ScanFolderRow(string Path, string Note, bool Removable)
{
    public Visibility RemoveVisibility => Removable ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow
{
    private const string ThemeSetting = "theme";
    private static readonly PluginFormat[] NonVst2Formats = [PluginFormat.Vst3, PluginFormat.Clap, PluginFormat.Aax];
    private static readonly PluginFormat[] Vst2Only = [PluginFormat.Vst2];

    private void InitializeSettings()
    {
        ThemeManager.Apply(_database.GetSetting(ThemeSetting));
        ThemeList.ItemsSource = ThemePalettes.All.Select(p => new ThemeOption(p, p.Id == ThemeManager.Current.Id)).ToList();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id || id == ThemeManager.Current.Id)
            return;
        ThemeManager.Apply(id);
        _database.SetSetting(ThemeSetting, id);
        // 下面这些颜色是代码取的，不会自动跟着变。
        UpdateStars();
        RefreshProjectReport();
        StatusText.Text = "已切换到“" + ThemeManager.Current.Name + "”主题";
    }

    private void ShowSettings()
    {
        RefreshReviewSources();
        var scans = _database.LoadFileScans().Values.ToList();
        var custom = _database.LoadVst2Folders();
        var roots = PluginDiscovery.Roots(custom);

        Vst3FolderList.ItemsSource = roots
            .Where(r => r.Format != PluginFormat.Vst2 && Directory.Exists(r.Path))
            .Select(r => new ScanFolderRow(r.Path, Labels.Format(r.Format) + "，" + FileCount(scans, r.Path), false))
            .ToList();

        var defaults = PluginDiscovery.DefaultRoots().Where(r => r.Format == PluginFormat.Vst2).Select(r => r.Path).ToList();
        Vst2FolderList.ItemsSource = custom
            .Select(f => new ScanFolderRow(f, Directory.Exists(f) ? "我添加的，" + FileCount(scans, f) : "我添加的，现在找不到这个文件夹", true))
            .Concat(defaults
                .Where(d => Directory.Exists(d) && !custom.Contains(d, StringComparer.OrdinalIgnoreCase))
                .Select(d => new ScanFolderRow(d, "默认，" + FileCount(scans, d), false)))
            .ToList();

        var known = roots.Select(r => r.Path).ToList();
        var hosts = HostPluginCache.LoadAll();
        var suggestions = HostPluginCache.TopFolders(hosts.KnownPaths)
            .Where(Directory.Exists)
            .Where(f => !known.Any(k => k.Equals(f, StringComparison.OrdinalIgnoreCase) || HostPluginCache.IsInside(f, k)))
            .Select(f => new SuggestedFolder(f, hosts.DawsUsing(f)))
            .ToList();
        Vst2SuggestionList.ItemsSource = suggestions;
        Vst2SuggestionList.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Vst2Summary.Text = DescribeVst2(scans);
        Vst2HostNote.Text = hosts.Daws.Count > 0
            ? $"子文件夹会一起扫描。效果器和音源的区分来自 DAW 自己的记录（这台电脑上找到了 {string.Join("、", hosts.Daws)} 的记录），新装的 VST2 要先在 DAW 里扫描一次。"
            : "子文件夹会一起扫描。效果器和音源的区分来自 DAW 自己的记录（支持 Cubase、Nuendo、Studio One、REAPER、Cakewalk、FL Studio），新装的 VST2 要先在 DAW 里扫描一次。这台电脑上还没找到这些 DAW 的记录。";
        Vst3Summary.Text = DescribeOtherFormats(scans);
        ShowUsageSettings();
        if (_scanCancel == null)
            StatusText.Text = "";
    }

    private static string FileCount(List<FileScanResult> scans, string folder)
    {
        var count = scans.Count(s => HostPluginCache.IsInside(s.Path, folder));
        return count > 0 ? $"{count} 个文件" : "还没有扫描到文件";
    }

    private static string DescribeVst2(List<FileScanResult> scans)
    {
        var files = scans.Where(s => s.Format == PluginFormat.Vst2).ToList();
        if (files.Count == 0)
            return "还没有扫描过 VST2。";
        var classes = files.SelectMany(f => f.Classes).ToList();
        var instruments = classes.Count(c => c.IsInstrument == true);
        var effects = classes.Count(c => c.IsInstrument == false);
        var unknown = classes.Count - instruments - effects;
        var text = $"已扫描 {files.Count} 个文件：效果器 {effects} 个，音源 {instruments} 个（不显示）";
        return unknown > 0 ? text + $"，DAW 没有记录的 {unknown} 个（只能按名称判断）" : text;
    }

    private static string DescribeOtherFormats(List<FileScanResult> scans)
    {
        var files = scans.Count(s => s.Format != PluginFormat.Vst2);
        return files > 0 ? $"已扫描 {files} 个文件。" : "还没有扫描过。";
    }

    private void AddVst2Folder_Click(object sender, RoutedEventArgs e)
    {
        if (ScanBusy())
            return;
        var dialog = new OpenFolderDialog { Title = "选择存放 VST2 插件的文件夹", Multiselect = true };
        if (dialog.ShowDialog(this) != true)
            return;
        foreach (var folder in dialog.FolderNames)
            _database.AddVst2Folder(folder);
        _ = RunScanAsync(full: false, Vst2Only);
    }

    private void AddSuggestedVst2Folder_Click(object sender, RoutedEventArgs e)
    {
        if (ScanBusy() || ((FrameworkElement)sender).Tag is not string folder)
            return;
        _database.AddVst2Folder(folder);
        _ = RunScanAsync(full: false, Vst2Only);
    }

    private void RemoveVst2Folder_Click(object sender, RoutedEventArgs e)
    {
        if (ScanBusy() || ((FrameworkElement)sender).Tag is not string folder)
            return;
        _database.RemoveVst2Folder(folder);
        // 只是不再读取这个文件夹，插件文件不动。写过备注、评分的插件保留并标记为缺失，其余的从列表移除。
        _ = RunScanAsync(full: false, Vst2Only, forgetMissing: true);
    }

    private bool ScanBusy()
    {
        if (_scanCancel == null)
            return false;
        StatusText.Text = "正在扫描，等结束后再改文件夹。";
        return true;
    }

    private void Vst2Scan_Click(object sender, RoutedEventArgs e) => _ = RunScanAsync(full: false, Vst2Only);

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = RunScanAsync(full: false, NonVst2Formats);

    private void FullScan_Click(object sender, RoutedEventArgs e) => _ = RunScanAsync(full: true, NonVst2Formats);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _scanCancel?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在停止，等待当前插件结束…";
    }

    /// <param name="recordMissing">垃圾桶里装着的插件这次扫不到了，记进卸载历史（在本软件外卸载的）。</param>
    private async Task RunScanAsync(bool full, IReadOnlyCollection<PluginFormat> formats, bool forgetMissing = false, bool recordMissing = true)
    {
        if (_scanCancel != null)
            return;
        if (!File.Exists(_scannerPath))
        {
            StatusText.Text = "找不到扫描器 WhatPluginScanner.exe，请重新生成程序。";
            return;
        }

        SaveCurrent();
        var rememberedBefore = _plugins.Where(p => p.IsUninstalled).Select(p => p.Row.Id).ToHashSet();
        var trashInstalledBefore = _plugins.Where(p => p.Row.User.Hidden && !p.Row.IsMissing).Select(p => p.Row.Id).ToHashSet();
        _scanCancel = new CancellationTokenSource();
        SetScanning(true);
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressBar.Maximum = Math.Max(p.Total, 1);
            ScanProgressBar.Value = p.Done;
            StatusText.Text = $"正在扫描 {p.Done}/{p.Total}：{Path.GetFileName(p.Current)}";
        });

        string? outcome = null;
        try
        {
            var service = new ScanService(_database, new ScannerRunner(_scannerPath, TimeSpan.FromSeconds(60)));
            var token = _scanCancel.Token;
            var roots = PluginDiscovery.Roots(_database.LoadVst2Folders());
            var summary = await Task.Run(
                () => service.ScanAsync(roots, new ScanOptions(Full: full, Formats: formats, HostInfo: HostPluginCache.LoadAll()), progress, token),
                CancellationToken.None);

            // 扫描期间可能还在编辑，重新加载前先保存。
            SaveCurrent();
            if (forgetMissing && !summary.Cancelled)
                _database.DeleteMissingWithoutUserData();
            LoadPlugins();
            IReadOnlyList<PluginRow> newlyGone = recordMissing && !summary.Cancelled
                ? UninstallHistory.NewlyMissing(trashInstalledBefore, _plugins.Select(p => p.Row))
                : [];
            if (newlyGone.Count > 0)
            {
                var now = DateTime.UtcNow;
                _database.AddUninstallRecords(newlyGone.Select(p => UninstallHistory.From(p, UninstallWay.FoundMissing, "", now)));
                UpdateUninstallHistoryButton();
            }
            if (!summary.Cancelled)
                RecordUsage(() => _database.RecordUsage(UsageKinds.Scan, _plugins.Count));
            LoadItems();
            outcome = Describe(summary) + CheckReturnedFiles();
            var returned = TrashMemory.Returned(rememberedBefore, _plugins.Select(p => p.Row));
            if (returned.Count > 0)
                outcome += returned.Count == 1
                    ? $"。{returned[0].Name} 以前卸载过，又装回来了，仍在垃圾桶里（{TrashMemory.Describe(returned[0])}）"
                    : $"。有 {returned.Count} 款以前卸载过的插件又装回来了，仍在垃圾桶里：{string.Join("、", returned.Select(p => p.Name))}";
            if (newlyGone.Count > 0)
                outcome += $"。垃圾桶里的 {string.Join("、", newlyGone.Take(5).Select(p => p.Name))}{(newlyGone.Count > 5 ? $" 等 {newlyGone.Count} 款" : "")} 已经不在了，已记进卸载历史";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            outcome = "扫描出错：" + error.Message;
        }
        finally
        {
            _scanCancel.Dispose();
            _scanCancel = null;
            SetScanning(false);
        }

        if (_page == AppPage.Settings)
            ShowSettings();
        StatusText.Text = outcome;
    }

    private string Describe(ScanSummary s)
    {
        var scanned = s.Loaded + s.ModuleInfo + s.FileInfoOnly + s.LoadFailed + s.TimedOut;
        var parts = new List<string>
        {
            s.Cancelled ? "扫描已停止" : "扫描完成",
            $"共 {_plugins.Count} 款效果器",
            $"本次处理 {scanned} 个文件，跳过未变化的 {s.Skipped} 个",
        };
        if (s.LoadFailed > 0)
            parts.Add($"{s.LoadFailed} 个读取失败");
        if (s.TimedOut > 0)
            parts.Add($"{s.TimedOut} 个超时");
        return string.Join("，", parts);
    }

    private void SetScanning(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        FullScanButton.IsEnabled = !scanning;
        Vst2ScanButton.IsEnabled = !scanning;
        AddVst2FolderButton.IsEnabled = !scanning;
        CancelButton.IsEnabled = scanning;
        CancelButton.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = !scanning;
        ScanProgressBar.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        if (scanning)
            ScanProgressBar.Value = 0;
    }
}
