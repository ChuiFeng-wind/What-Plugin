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

/// <summary>设置页里的一个插件文件夹（WPF 要用 Visibility 控制移除按钮）。</summary>
public sealed record ScanFolderRow(string Path, string Note, bool Removable)
{
    public static ScanFolderRow From(ScanFolderInfo info) => new(info.Path, info.Note, info.Removable);

    public Visibility RemoveVisibility => Removable ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow
{
    private const string ThemeSetting = "theme";
    private static readonly IReadOnlyCollection<PluginFormat> NonVst2Formats = ScanFormats.Main;
    private static readonly IReadOnlyCollection<PluginFormat> Vst2Only = ScanFormats.Vst2;

    private ScanEnvironment ScanEnvironment => ScanEnvironment.ThisComputer(_scannerPath);

    private void InitializeSettings()
    {
        ThemeManager.Apply(_database.GetSetting(ThemeSetting));
        PublishLookupTheme();
        ThemeList.ItemsSource = ThemePalettes.All.Select(p => new ThemeOption(p, p.Id == ThemeManager.Current.Id)).ToList();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id || id == ThemeManager.Current.Id)
            return;
        ThemeManager.Apply(id);
        _database.SetSetting(ThemeSetting, id);
        PublishLookupTheme();
        // 下面这些颜色是代码取的，不会自动跟着变。
        UpdateStars();
        RefreshProjectReport();
        StatusText.Text = "已切换到“" + ThemeManager.Current.Name + "”主题";
    }

    /// <summary>DAW 里的查询窗口打开时直接用这份配色。</summary>
    private void PublishLookupTheme() =>
        LookupExport.PublishTheme(_databasePath, PluginDatabase.DefaultPath, ThemeManager.Current);

    private void ShowSettings()
    {
        RefreshReviewSources();
        var info = ScanSettings.Build(_database, ScanEnvironment);
        Vst3FolderList.ItemsSource = info.MainFolders.Select(ScanFolderRow.From).ToList();
        Vst2FolderList.ItemsSource = info.Vst2Folders.Select(ScanFolderRow.From).ToList();
        Vst2SuggestionList.ItemsSource = info.Suggestions;
        Vst2SuggestionList.Visibility = info.Suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Vst2Summary.Text = info.Vst2Summary;
        Vst2HostNote.Text = info.Vst2HostNote;
        Vst3Summary.Text = info.MainSummary;
        ShowUsageSettings();
        if (_scanCancel == null)
            StatusText.Text = "";
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

        SaveCurrent();
        _scanCancel = new CancellationTokenSource();
        SetScanning(true);
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressBar.Maximum = Math.Max(p.Total, 1);
            ScanProgressBar.Value = p.Done;
            StatusText.Text = $"正在扫描 {p.Done}/{p.Total}：{Path.GetFileName(p.Current)}";
        });

        ScanOutcome outcome;
        try
        {
            // 扫描期间可能还在编辑，重新加载前先保存。
            outcome = await ScanWorkflow.RunAsync(_database, ScanEnvironment, new ScanRequest(full, formats, forgetMissing, recordMissing),
                progress, _scanCancel.Token, () => SaveCurrent());
            if (outcome.Summary != null)
            {
                LoadPlugins();
                if (outcome.GoneFromTrash.Count > 0)
                    UpdateUninstallHistoryButton();
                LoadItems();
                UpdateRemovedFilesButton();
            }
        }
        finally
        {
            _scanCancel.Dispose();
            _scanCancel = null;
            SetScanning(false);
        }

        if (_page == AppPage.Settings)
            ShowSettings();
        StatusText.Text = outcome.Message;
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
