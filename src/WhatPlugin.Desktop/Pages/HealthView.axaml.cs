using Avalonia.Controls;
using Avalonia.Input;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>健康检查页：扫描失败、重复安装、DAW 屏蔽的插件，以及各厂家用什么工具查更新。只读，不运行插件。</summary>
public partial class HealthView : UserControl
{
    private HealthReport _report = new([], 0);
    private IReadOnlyList<VendorUpdate> _updates = [];
    private bool _refreshing;

    /// <summary>页面说明那一行跟着选中的检查项变。</summary>
    public event Action<string>? SubtitleChanged;

    public event Action<string>? StatusChanged;

    /// <summary>“查看这款插件”：参数是插件。跳转由主窗口负责。</summary>
    public event Action<PluginRow>? OpenPlugin;

    /// <summary>打开网页、定位文件。测试换成记录下来的函数，不真的打开浏览器和访达。</summary>
    public Func<string, string?> OpenUrl { get; set; } = SystemShell.OpenWebPage;

    public Func<string, string?> Reveal { get; set; } = SystemShell.Reveal;

    public HealthView()
    {
        InitializeComponent();
        RevealButton.Content = OperatingSystem.IsMacOS() ? "在访达中显示" : "在文件夹中显示";
        CategoryList.SelectionChanged += (_, _) =>
        {
            if (!_refreshing)
                ShowRows();
        };
        IssueList.SelectionChanged += (_, _) => ShowDetail();
        IssueList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: HealthRow })
                Open();
        };
        IssueList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Open();
        };
        SearchButton.Click += (_, _) => Search();
        RevealButton.Click += (_, _) => RevealFile();
        OpenButton.Click += (_, _) => Open();
    }

    public IReadOnlyList<HealthCategoryRow> Categories => CategoryList.ItemsSource as IReadOnlyList<HealthCategoryRow> ?? [];

    public HealthCategoryRow? SelectedCategory
    {
        get => CategoryList.SelectedItem as HealthCategoryRow;
        set => CategoryList.SelectedItem = value;
    }

    public IReadOnlyList<HealthRow> Rows => IssueList.ItemsSource as IReadOnlyList<HealthRow> ?? [];

    public HealthRow? SelectedRow
    {
        get => IssueList.SelectedItem as HealthRow;
        set => IssueList.SelectedItem = value;
    }

    public IReadOnlyList<HealthFileRow> Files => FileList.ItemsSource as IReadOnlyList<HealthFileRow> ?? [];

    /// <summary>安装情况变了就重算。保持选中的检查项。</summary>
    public void Show(HealthReport report, IReadOnlyList<VendorUpdate> updates)
    {
        _report = report;
        _updates = updates;
        var selected = SelectedCategory?.Title;
        var categories = HealthListing.Categories(report, updates);
        _refreshing = true;
        CategoryList.ItemsSource = categories;
        CategoryList.SelectedItem = categories.FirstOrDefault(c => c.Title == selected) ?? categories[0];
        _refreshing = false;
        ShowRows();
    }

    /// <summary>状态栏上的一句总结。</summary>
    public string Summary => HealthListing.Describe(_report);

    private void ShowRows()
    {
        if (SelectedCategory is not { } category)
            return;
        SubtitleChanged?.Invoke(category.Explanation);
        var rows = HealthListing.Rows(category, _report, _updates);
        IssueList.ItemsSource = rows;
        IssuesEmpty.IsVisible = rows.Count == 0;
        ShowDetail();
    }

    private void ShowDetail()
    {
        var row = SelectedRow;
        DetailPanel.IsVisible = row != null;
        DetailEmpty.IsVisible = row == null;
        if (row == null)
            return;

        DetailTitle.Text = row.Title;
        DetailNote.Text = HealthListing.Note(row);
        var files = HealthListing.Files(row.Plugin);
        FileList.ItemsSource = files;
        FileList.SelectedIndex = files.Count > 0 ? 0 : -1;
        FileList.IsVisible = row.Plugin != null;
        RevealButton.IsVisible = files.Count > 0;
        OpenButton.IsVisible = row.Plugin != null;
    }

    private void Search()
    {
        if (SelectedRow is { } row && OpenUrl(HealthListing.SearchUrl(row)) is { } problem)
            StatusChanged?.Invoke(problem);
    }

    private void RevealFile()
    {
        if (FileList.SelectedItem is HealthFileRow file && Reveal(file.Path) is { } problem)
            StatusChanged?.Invoke(problem);
    }

    private void Open()
    {
        if (SelectedRow is { Plugin: { } plugin })
            OpenPlugin?.Invoke(plugin);
    }
}
