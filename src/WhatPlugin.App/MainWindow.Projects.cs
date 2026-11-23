using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>工程反查和闲置报告。工程文件只读，这里没有任何改动工程或插件文件的操作。</summary>
public partial class MainWindow
{
    private ProjectReportResult _report = new([], [], new Dictionary<string, PluginUsage>());
    private List<ProjectViewModel> _projects = [];
    private List<MissingViewModel> _missing = [];

    /// <summary>查“以前移进垃圾桶并卸载过”用的名称匹配，插件列表变了就重建。</summary>
    private PluginNameMatcher? _rememberedMatcher;

    private PluginNameMatcher RememberedMatcher => _rememberedMatcher ??= new PluginNameMatcher(_plugins.Select(p => p.Row));
    private ICollectionView? _projectView;
    private ICollectionView? _missingView;
    private CancellationTokenSource? _projectCancel;
    private bool _projectUiReady;

    private List<IdleViewModel> _idle = [];
    private ICollectionView? _idleView;

    private void InitializeProjects()
    {
        IdleMonths.ItemsSource = IdleListing.MonthOptions.Select(IdleListing.MonthLabel).ToList();
        IdleMonths.SelectedIndex = 1;
        ProjectSegmentAll.IsChecked = true;
        _projectUiReady = true;
        LoadProjectFolders();
        InitializeInsights();
        RefreshProjectReport();
        LoadChains();
    }

    /// <summary>安装情况、垃圾桶或工程变化后都要重算：缺失和闲置都依赖它们。</summary>
    private void RefreshProjectReport()
    {
        if (!_projectUiReady)
            return;
        var selectedProject = (ProjectGrid.SelectedItem as ProjectViewModel)?.Entry.Key;
        var selectedMissing = (MissingGrid.SelectedItem as MissingViewModel)?.Name;

        _report = ProjectListing.Load(_database);
        foreach (var plugin in _plugins)
            plugin.Usage = _report.Usage.GetValueOrDefault(plugin.Row.Key);

        _projects = _report.Projects.Select(p => new ProjectViewModel(p)).ToList();
        RebuildProjectTree();
        _projectView = CollectionViewSource.GetDefaultView(_projects);
        _projectView.Filter = MatchesProject;
        ProjectGrid.ItemsSource = _projectView;
        ProjectGrid.SelectedItem = _projects.FirstOrDefault(p => p.Entry.Key == selectedProject);

        _rememberedMatcher = null;
        _missing = ProjectListing.MissingRows(_report, RememberedMatcher).Select(m => new MissingViewModel(m)).ToList();
        _missingView = CollectionViewSource.GetDefaultView(_missing);
        _missingView.Filter = MatchesMissing;
        MissingGrid.ItemsSource = _missingView;
        MissingGrid.SelectedItem = _missing.FirstOrDefault(m => m.Name == selectedMissing);

        var withMissing = _projects.Count(p => p.MissingCount > 0);
        CountProjects.Text = _projects.Count > 0 ? _projects.Count.ToString() : "";
        ProjectSegmentMissing.Content = $"有缺失（{withMissing}）";
        ProjectSegmentSummary.Content = $"缺失汇总（{_missing.Count}）";
        ClearIgnoredButton.Visibility = _database.LoadIgnoredProjectNames().Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshIdle();
        ShowChainDetail();
        RefreshHealth();
        UpdateProjectEmpty();
        ShowProjectDetail();
    }

    private void ShowProjectStatus()
    {
        StatusText.Text = _page switch
        {
            AppPage.Projects => ProjectListing.Status(_projects.Count, _projects.Count(p => p.MissingCount > 0), _missing.Count),
            AppPage.Idle => DescribeIdle(),
            AppPage.Chains => "混音笔记只是记录，不会写入 DAW",
            AppPage.Health => DescribeHealth(),
            _ => StatusText.Text,
        };
    }

    // ---------- 工程文件夹和扫描 ----------

    private void LoadProjectFolders()
    {
        var folders = _database.LoadProjectFolders();
        ProjectFolderList.ItemsSource = folders;
        ProjectFolderList.Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectScanButton.IsEnabled = folders.Count > 0 && _projectCancel == null;
    }

    private void AddProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择存放工程的文件夹", Multiselect = true };
        if (dialog.ShowDialog(this) != true)
            return;
        foreach (var folder in dialog.FolderNames)
            _database.AddProjectFolder(folder);
        LoadProjectFolders();
        _ = RunProjectScanAsync();
    }

    private void RemoveProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string folder)
            return;
        _database.RemoveProjectFolder(folder);
        LoadProjectFolders();
        // 只从软件里忘掉这个文件夹的统计，工程文件不动。
        _ = RunProjectScanAsync();
    }

    private void ProjectScan_Click(object sender, RoutedEventArgs e) => _ = RunProjectScanAsync();

    private void ProjectCancel_Click(object sender, RoutedEventArgs e)
    {
        _projectCancel?.Cancel();
        ProjectCancelButton.IsEnabled = false;
        StatusText.Text = "正在停止…";
    }

    private async Task RunProjectScanAsync()
    {
        if (_projectCancel != null)
            return;
        _projectCancel = new CancellationTokenSource();
        SetProjectScanning(true);
        var progress = new Progress<ProjectScanProgress>(p =>
        {
            ScanProgressBar.Maximum = Math.Max(p.Total, 1);
            ScanProgressBar.Value = p.Done;
            if (_page == AppPage.Projects && p.Current != null)
                StatusText.Text = $"正在读取工程 {p.Done}/{p.Total}：{Path.GetFileName(p.Current)}";
        });

        try
        {
            var token = _projectCancel.Token;
            var summary = await Task.Run(() => ProjectScanner.Scan(_database, progress, token), CancellationToken.None);
            RefreshProjectReport();
            StatusText.Text = DescribeProjectScan(summary);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusText.Text = "读取工程出错：" + error.Message;
        }
        finally
        {
            _projectCancel.Dispose();
            _projectCancel = null;
            SetProjectScanning(false);
        }
    }

    private string DescribeProjectScan(ProjectScanSummary s) => ProjectListing.DescribeScan(s, _projects.Count);

    private void SetProjectScanning(bool scanning)
    {
        ProjectCancelButton.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ProjectCancelButton.IsEnabled = scanning;
        ProjectAddFolderButton.IsEnabled = !scanning;
        ProjectFolderList.IsEnabled = !scanning;
        ProjectScanButton.IsEnabled = !scanning && _database.LoadProjectFolders().Count > 0;
        ScanProgressBar.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        if (scanning)
            ScanProgressBar.Value = 0;
    }

    // ---------- 工程列表 ----------

    private bool ShowingMissingSummary => ProjectSegmentSummary.IsChecked == true;

    private bool MatchesProject(object item)
    {
        var project = (ProjectViewModel)item;
        return MatchesProjectFilters(project)
            && (_projectFolderFilter == null || ProjectFolderTree.Contains(_projectFolderFilter, project.Folder));
    }

    private bool MatchesProjectFilters(ProjectViewModel project)
    {
        if (ProjectSegmentMissing.IsChecked == true && project.MissingCount == 0)
            return false;
        return MatchesSearch(project.SearchText);
    }

    private bool MatchesMissing(object item) => MatchesSearch(((MissingViewModel)item).SearchText);

    private bool MatchesSearch(string text) => ProjectListing.Matches(ProjectSearchBox.Text, text);

    private void ProjectFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_projectUiReady)
            return;
        RebuildProjectTree();
        ProjectGrid.Visibility = ShowingMissingSummary ? Visibility.Collapsed : Visibility.Visible;
        MissingGrid.Visibility = ShowingMissingSummary ? Visibility.Visible : Visibility.Collapsed;
        _projectView?.Refresh();
        _missingView?.Refresh();
        UpdateProjectEmpty();
        ShowProjectDetail();
    }

    private void UpdateProjectEmpty()
    {
        var shown = ShowingMissingSummary
            ? _missingView?.Cast<object>().Count() ?? 0
            : _projectView?.Cast<object>().Count() ?? 0;
        ProjectEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectEmptyText.Text = ProjectListing.EmptyText(_database.LoadProjectFolders().Count == 0, _projects.Count, ShowingMissingSummary,
            _missing.Count, ProjectSegmentMissing.IsChecked == true, _projects.Any(p => p.MissingCount > 0), _projectFolderFilter != null);
    }

    private void ProjectSelection_Changed(object sender, SelectionChangedEventArgs e) => ShowProjectDetail();

    private void ShowProjectDetail()
    {
        var project = ShowingMissingSummary ? null : ProjectGrid.SelectedItem as ProjectViewModel;
        var missing = ShowingMissingSummary ? MissingGrid.SelectedItem as MissingViewModel : null;
        ProjectDetailEmpty.Visibility = project == null && missing == null ? Visibility.Visible : Visibility.Collapsed;
        ProjectDetailPanel.Visibility = project != null ? Visibility.Visible : Visibility.Collapsed;
        MissingDetailPanel.Visibility = missing != null ? Visibility.Visible : Visibility.Collapsed;

        if (project != null)
        {
            var detail = ProjectListing.Detail(project.Entry, RememberedMatcher);
            ProjectDetailTitle.Text = detail.Title;
            ProjectDetailMeta.Text = detail.Meta;
            ProjectDetailPath.Text = detail.Path;
            ProjectDetailError.Visibility = detail.Error != null ? Visibility.Visible : Visibility.Collapsed;
            ProjectDetailError.Text = detail.Error ?? "";
            ProjectDetailLines.ItemsSource = detail.Lines.Select(l => new ProjectLineViewModel(l)).ToList();
            ProjectDetailNotes.Text = detail.Notes;
            ProjectDetailNotes.Visibility = detail.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (missing != null)
        {
            MissingDetailTitle.Text = missing.Name;
            MissingDetailMeta.Text = ProjectListing.MissingMeta(missing.Row);
            MissingDetailProjects.ItemsSource = ProjectListing.MissingProjects(missing.Row);
        }
    }

    private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is ProjectViewModel project)
            RevealFile(project.Entry.Path);
    }

    private void IgnoreLine_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string name)
            IgnoreMissingName(name);
    }

    private void IgnoreMissing_Click(object sender, RoutedEventArgs e)
    {
        if (MissingGrid.SelectedItem is MissingViewModel missing)
            IgnoreMissingName(missing.Name);
    }

    private void IgnoreMissingName(string name)
    {
        _database.IgnoreProjectName(name);
        RefreshProjectReport();
        StatusText.Text = ProjectListing.Ignored(name);
    }

    private void ClearIgnored_Click(object sender, RoutedEventArgs e)
    {
        _database.ClearIgnoredProjectNames();
        RefreshProjectReport();
        StatusText.Text = ProjectListing.IgnoredCleared;
    }

    /// <summary>插件详情里的工程使用情况。</summary>
    private void ShowUsageDetail(PluginRow row)
    {
        var usage = _report.Usage.GetValueOrDefault(row.Key);
        DetailUsage.Text = UsageLabels.Summary(usage);
        DetailUsage.ToolTip = usage == null ? null : string.Join("\n", usage.ProjectNames.Take(30));
    }

    private void ShowPluginInLibrary(string key)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Row.Key == key);
        if (plugin == null)
            return;
        (plugin.IsBlacklisted ? NavBlacklist : NavPlugins).IsChecked = true;
        ExitRecommendation();
        ClearFilters_Click(this, new RoutedEventArgs());
        PluginGrid.SelectedItem = plugin;
        PluginGrid.ScrollIntoView(plugin);
    }

    // ---------- 闲置报告 ----------

    private int IdleMonthCount => IdleMonths.SelectedIndex >= 0 ? IdleListing.MonthOptions[IdleMonths.SelectedIndex] : IdleListing.DefaultMonths;

    private void RefreshIdle()
    {
        if (!_projectUiReady)
            return;
        var selected = IdleGrid.SelectedItems.OfType<IdleViewModel>().Select(i => i.Row.Key).ToHashSet();
        _idle = ProjectReport.FindIdle(_plugins.Select(p => p.Row), _report.Usage, IdleMonthCount, DateTime.UtcNow)
            .Select(i => new IdleViewModel(i)).ToList();
        _idleView = CollectionViewSource.GetDefaultView(_idle);
        _idleView.Filter = MatchesIdle;
        IdleGrid.ItemsSource = _idleView;
        foreach (var item in _idle.Where(i => selected.Contains(i.Row.Key)))
            IdleGrid.SelectedItems.Add(item);
        CountIdle.Text = _report.Projects.Count > 0 && _idle.Count > 0 ? _idle.Count.ToString() : "";
        UpdateIdleEmpty();
    }

    private bool MatchesIdle(object item)
    {
        var filter = IdleSegmentNever.IsChecked == true ? IdleFilter.NeverUsed : IdleSegmentOld.IsChecked == true ? IdleFilter.LongUnused : IdleFilter.All;
        return IdleListing.Matches(((IdleViewModel)item).Info, filter, IdleSearchBox.Text);
    }

    private void IdleOption_Changed(object sender, SelectionChangedEventArgs e) => RefreshIdleAndStatus();

    private void IdleFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_projectUiReady)
            return;
        _idleView?.Refresh();
        UpdateIdleEmpty();
    }

    private void RefreshIdleAndStatus()
    {
        RefreshIdle();
        if (_page == AppPage.Idle)
            ShowProjectStatus();
    }

    private void UpdateIdleEmpty()
    {
        var noProjects = _report.Projects.Count == 0;
        IdleGrid.Visibility = noProjects ? Visibility.Collapsed : Visibility.Visible;
        var shown = _idleView?.Cast<object>().Count() ?? 0;
        IdleEmpty.Visibility = noProjects || shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        IdleEmptyText.Text = IdleListing.EmptyText(_report.Projects.Count, _idle.Count);
    }

    private string DescribeIdle() =>
        IdleListing.Describe(_report.Projects.Count, _idle.Select(i => i.Idle).ToList(), IdleMonthCount);

    private void IdleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = IdleGrid.SelectedItems.Count;
        IdleBlacklistButton.IsEnabled = count > 0;
        IdleBlacklistButton.Content = IdleListing.ButtonText(count);
    }

    private void IdleGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IdleGrid.SelectedItem is IdleViewModel item)
            ShowPluginInLibrary(item.Row.Key);
    }

    private void IdleBlacklist_Click(object sender, RoutedEventArgs e)
    {
        var selected = IdleGrid.SelectedItems.OfType<IdleViewModel>().ToList();
        if (selected.Count == 0)
            return;
        var reason = Dialogs.Prompt(this, "移入垃圾桶",
            IdleListing.Prompt(selected.Count),
            "移入垃圾桶", "", danger: true, allowEmpty: true);
        if (reason == null)
            return;

        try
        {
            IdleListing.MoveToTrash(_database, selected.Select(i => i.Row), reason);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }

        LoadPlugins();
        StatusText.Text = IdleListing.Moved(selected.Count);
    }
}
