using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>一个主题选项。预览用它自己的颜色画，不跟随当前主题。</summary>
public sealed class ThemeOption(ThemePalette palette, bool isCurrent)
{
    public string Id => palette.Id;
    public string Name => palette.Name;
    public bool IsCurrent => isCurrent;
    public string Description => palette.IsDark ? "深色" : "浅色";
    public IBrush Bg => Brush("Bg1");
    public IBrush Side => Brush("Bg0");
    public IBrush Card => Brush("Surface");
    public IBrush Line => Brush("LineStrong");
    public IBrush Ink => Brush("Text");
    public IBrush Dim => Brush("Muted");
    public IBrush Accent => Brush("Accent");

    private IBrush Brush(string key) => ThemeManager.CreateBrush(palette[key]);
}

/// <summary>设置页负责发起扫描，扫描本身由主窗口执行（进度在状态栏，结束后要刷新各页）。</summary>
public interface IScanHost
{
    bool IsScanning { get; }

    Task RunScanAsync(ScanRequest request);

    void CancelScan();

    void ShowStatus(string text);
}

/// <summary>设置页：主题、使用统计和插件扫描。扫描不会打开插件界面，也不会改动插件文件。</summary>
/// <summary>设置页里一位分享者的评价。用普通类：列表每次重建，内容相同的记录不该被当成同一项。</summary>
public sealed class ReviewSourceRow(ReviewSource source, string name, string detail)
{
    public ReviewSource Source { get; } = source;
    public string Name { get; } = name;
    public string Detail { get; } = detail;

    public override string ToString() => Name;
}

public partial class SettingsView : UserControl
{
    private readonly PluginDatabase _database;
    private readonly ScanEnvironment _environment;
    private readonly IScanHost _host;
    private bool _loadingUsage;

    /// <summary>用户点了某个主题（参数是主题标识）。保存由主窗口负责。</summary>
    public event Action<string>? ThemeChosen;

    /// <summary>移除了别人的评价，插件详情要重新读。</summary>
    public event Action? ReviewsChanged;

    /// <summary>弹确认框。测试换成直接返回结果的函数。</summary>
    public Func<string, string, string, Task<bool>> Confirm { get; set; }

    // 设计器需要无参构造函数。
    public SettingsView() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db")),
        ScanEnvironment.ThisComputer(""), new NoScanHost()) { }

    public SettingsView(PluginDatabase database, ScanEnvironment environment, IScanHost host)
    {
        _database = database;
        _environment = environment;
        _host = host;
        InitializeComponent();
        Confirm = (title, message, confirm) => TopLevel.GetTopLevel(this) is Window owner
            ? ConfirmDialog.Show(owner, title, message, confirm, danger: true)
            : Task.FromResult(false);

        ThemeList.ItemsSource = ThemePalettes.All.Select(p => new ThemeOption(p, p.Id == ThemeManager.Current.Id)).ToList();
        MainFoldersNote.Text = ScanSettings.MainFoldersNote;
        ReviewSourcesNote.Text = ReviewListing.SourcesNote;
        ReviewSourcesEmpty.Text = ReviewListing.NoSources;

        ScanButton.Click += (_, _) => Scan(new ScanRequest(Full: false, ScanFormats.Main));
        FullScanButton.Click += (_, _) => Scan(new ScanRequest(Full: true, ScanFormats.Main));
        Vst2ScanButton.Click += (_, _) => Scan(new ScanRequest(Full: false, ScanFormats.Vst2));
        CancelScanButton.Click += (_, _) => CancelScan();
        AddVst2FolderButton.Click += async (_, _) => await PickVst2Folders();
        UsageEnabledBox.IsCheckedChanged += (_, _) => UsageEnabled_Changed();
        ClearUsageButton.Click += async (_, _) => await ClearUsage();
        InstallLookupButton.Click += (_, _) => InstallLookup();
        RevealLookupButton.Click += (_, _) => { if (SystemShell.Reveal(LookupPlugin.Target) is { } problem) _host.ShowStatus(problem); };
    }

    /// <summary>应用里带的查询插件和它的安装位置。测试换成临时目录。</summary>
    public LookupPluginInstaller LookupPlugin { get; set; } = LookupPluginInstaller.ThisComputer();

    public string LookupStateText => LookupState.Text ?? "";

    public bool LookupCardVisible => LookupCard.IsVisible;

    private void ShowLookup()
    {
        var state = LookupPlugin.State;
        LookupCard.IsVisible = state != LookupPluginState.NotBundled;
        LookupState.Text = LookupPlugin.StateText;
        InstallLookupButton.Content = state switch
        {
            LookupPluginState.Outdated => "更新",
            LookupPluginState.Installed => "重新安装",
            _ => "安装到 VST3 文件夹",
        };
        RevealLookupButton.IsVisible = state != LookupPluginState.NotInstalled;
    }

    public void InstallLookup()
    {
        var error = LookupPlugin.Install();
        ShowLookup();
        _host.ShowStatus(error ?? "已安装查询插件。请在 DAW 里重新扫描插件，再在效果器列表里找 WhatPluginLookup");
    }

    /// <summary>重新读取文件夹和扫描结果。每次打开这一页、扫描结束后调用。</summary>
    public void Refresh()
    {
        var info = ScanSettings.Build(_database, _environment);
        MainFolderList.ItemsSource = info.MainFolders;
        Vst2FolderList.ItemsSource = info.Vst2Folders;
        SuggestionList.ItemsSource = info.Suggestions;
        SuggestionList.IsVisible = info.Suggestions.Count > 0;
        MainSummary.Text = info.MainSummary;
        Vst2Summary.Text = info.Vst2Summary;
        Vst2HostNote.Text = info.Vst2HostNote;
        ShowUsage();
        RefreshReviewSources();
        SetScanning(_host.IsScanning);
        ShowLookup();
    }

    // ---------- 别人分享的评价 ----------

    public IReadOnlyList<ReviewSourceRow> ReviewSources => ReviewSourceList.ItemsSource as IReadOnlyList<ReviewSourceRow> ?? [];

    public void RefreshReviewSources()
    {
        var rows = _database.LoadEffects();
        var sources = _database.LoadReviewSources();
        var library = new ReviewLibrary(_database.LoadSharedReviews());
        ReviewSourceList.ItemsSource = sources.Select(s => new ReviewSourceRow(
            s, ReviewListing.AuthorLabel(s, sources), ReviewListing.SourceDetail(s, library.CountMatched(s, rows)))).ToList();
        ReviewSourcesEmpty.IsVisible = sources.Count == 0;
    }

    private async void RemoveReviewSource_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ReviewSourceRow row)
            await RemoveReviewSource(row);
    }

    public async Task RemoveReviewSource(ReviewSourceRow row)
    {
        if (!await Confirm("移除这份评价？", ReviewListing.RemovePrompt(row.Source), "移除"))
            return;
        _database.RemoveReviewSource(row.Source.Id);
        RefreshReviewSources();
        ReviewsChanged?.Invoke();
        _host.ShowStatus(ReviewListing.Removed(row.Source));
    }

    public void SetScanning(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        FullScanButton.IsEnabled = !scanning;
        Vst2ScanButton.IsEnabled = !scanning;
        AddVst2FolderButton.IsEnabled = !scanning;
        CancelScanButton.IsVisible = scanning;
        CancelScanButton.IsEnabled = scanning;
    }

    public IReadOnlyList<ScanFolderInfo> MainFolders => MainFolderList.ItemsSource as IReadOnlyList<ScanFolderInfo> ?? [];

    public IReadOnlyList<ScanFolderInfo> Vst2Folders => Vst2FolderList.ItemsSource as IReadOnlyList<ScanFolderInfo> ?? [];

    public IReadOnlyList<SuggestedFolder> Suggestions => SuggestionList.ItemsSource as IReadOnlyList<SuggestedFolder> ?? [];

    // ---------- 扫描 ----------

    private void Scan(ScanRequest request) => _ = _host.RunScanAsync(request);

    private void CancelScan()
    {
        _host.CancelScan();
        CancelScanButton.IsEnabled = false;
        _host.ShowStatus("正在停止，等待当前插件结束…");
    }

    private bool ScanBusy()
    {
        if (!_host.IsScanning)
            return false;
        _host.ShowStatus("正在扫描，等结束后再改文件夹。");
        return true;
    }

    private async Task PickVst2Folders()
    {
        if (ScanBusy() || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择存放 VST2 插件的文件夹", AllowMultiple = true });
        var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await AddVst2Folders(paths);
    }

    /// <summary>加进扫描并马上扫一次 VST2。</summary>
    public Task AddVst2Folders(IEnumerable<string> folders)
    {
        if (ScanBusy())
            return Task.CompletedTask;
        foreach (var folder in folders)
            _database.AddVst2Folder(folder);
        return _host.RunScanAsync(new ScanRequest(Full: false, ScanFormats.Vst2));
    }

    /// <summary>
    /// 只是不再读取这个文件夹，插件文件不动。写过备注、评分的插件保留并标记为缺失，其余的从列表移除。
    /// </summary>
    public Task RemoveVst2Folder(string folder)
    {
        if (ScanBusy())
            return Task.CompletedTask;
        _database.RemoveVst2Folder(folder);
        return _host.RunScanAsync(new ScanRequest(Full: false, ScanFormats.Vst2, ForgetMissing: true));
    }

    private void AddSuggestedFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder })
            _ = AddVst2Folders([folder]);
    }

    private void RemoveVst2Folder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder })
            _ = RemoveVst2Folder(folder);
    }

    // ---------- 使用统计 ----------

    private void ShowUsage()
    {
        _loadingUsage = true;
        UsageEnabledBox.IsChecked = _database.UsageEnabled;
        _loadingUsage = false;
        var count = _database.CountUsageEvents();
        UsageSummary.Text = (_database.UsageEnabled ? "正在记录。" : "已关闭，不再记录。") + (count > 0 ? $"已记下 {count} 条。" : "还没有记录。");
        ClearUsageButton.IsEnabled = count > 0;
    }

    private void UsageEnabled_Changed()
    {
        if (_loadingUsage)
            return;
        var enabled = UsageEnabledBox.IsChecked == true;
        Quietly(() => _database.UsageEnabled = enabled);
        ShowUsage();
        _host.ShowStatus(enabled ? "已开始记录使用统计" : "已停止记录使用统计，已经记下的还在，可以点“清空记录”删除");
    }

    private async Task ClearUsage()
    {
        if (!await Confirm("清空使用统计？", "已经记下的打开日期、导出次数等会被删除，以后依赖它们的成就要重新累计。已经解锁的成就不受影响。", "清空"))
            return;
        Quietly(_database.ClearUsage);
        ShowUsage();
        _host.ShowStatus("已清空使用统计");
    }

    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (SqliteException)
        {
        }
    }

    // ---------- 主题 ----------

    private void Theme_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string id })
            ThemeChosen?.Invoke(id);
    }

    private sealed class NoScanHost : IScanHost
    {
        public bool IsScanning => false;
        public Task RunScanAsync(ScanRequest request) => Task.CompletedTask;
        public void CancelScan() { }
        public void ShowStatus(string text) { }
    }
}
