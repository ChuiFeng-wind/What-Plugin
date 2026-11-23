using Avalonia.Controls;
using Avalonia.Input;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>闲置报告：已安装但长期没进工程的效果器。要不要移入垃圾桶由用户决定，文件不会被移动或卸载。</summary>
public partial class IdleView : UserControl
{
    private readonly PluginDatabase _database;
    private ProjectReportResult _report = new([], [], new Dictionary<string, PluginUsage>());
    private IReadOnlyList<IdleListRow> _idle = [];

    /// <summary>页面说明。多选的按键 Mac 上是 Command。</summary>
    public static string Subtitle =>
        "已安装但长期没进工程的效果器，只统计已扫描的工程。要不要移入垃圾桶由你决定，文件不会被移动或卸载。"
        + (OperatingSystem.IsMacOS() ? "按住 Command 或 Shift 可多选" : "按住 Ctrl 或 Shift 可多选") + "，双击查看详情";

    public event Action<string>? StatusChanged;

    public event Action<PluginRow>? OpenPlugin;

    /// <summary>重新算过闲置的插件（侧边栏的数字跟着变）。</summary>
    public event Action? RowsChanged;

    /// <summary>移入了垃圾桶：插件库、垃圾桶和数量都要刷新。</summary>
    public event Action? Trashed;

    /// <summary>询问移入原因，取消返回 null。测试换成直接给答案。</summary>
    public Func<int, Task<string?>>? AskReason { get; set; }

    // 设计器需要无参构造函数。
    public IdleView() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db"))) { }

    public IdleView(PluginDatabase database)
    {
        _database = database;
        InitializeComponent();
        AskReason = AskWithDialog;
        MonthsBox.ItemsSource = IdleListing.MonthOptions.Select(IdleListing.MonthLabel).ToList();
        MonthsBox.SelectedIndex = IdleListing.MonthOptions.ToList().IndexOf(IdleListing.DefaultMonths);
        MonthsBox.SelectionChanged += (_, _) =>
        {
            Show(_report);
            StatusChanged?.Invoke(Summary);
        };
        SearchBox.TextChanged += (_, _) => Refilter();
        foreach (var segment in new[] { SegmentAll, SegmentNever, SegmentOld })
            segment.IsCheckedChanged += (_, _) =>
            {
                if (segment.IsChecked == true)
                    Refilter();
            };
        IdleList.SelectionChanged += (_, _) => UpdateButton();
        IdleList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: IdleListRow row })
                OpenPlugin?.Invoke(row.Row);
        };
        IdleList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && IdleList.SelectedItem is IdleListRow row)
                OpenPlugin?.Invoke(row.Row);
        };
        TrashButton.Click += async (_, _) => await MoveSelectedToTrash();
        UpdateButton();
    }

    public int Months => MonthsBox.SelectedIndex >= 0 ? IdleListing.MonthOptions[MonthsBox.SelectedIndex] : IdleListing.DefaultMonths;

    public int MonthsIndex
    {
        get => MonthsBox.SelectedIndex;
        set => MonthsBox.SelectedIndex = value;
    }

    /// <summary>全部闲置的插件（不看筛选），侧边栏的数字用它。</summary>
    public IReadOnlyList<IdleListRow> AllRows => _idle;

    public IReadOnlyList<IdleListRow> Rows => IdleList.ItemsSource as IReadOnlyList<IdleListRow> ?? [];

    public IList<object>? Selected => IdleList.SelectedItems?.Cast<object>().ToList();

    public void Select(IEnumerable<IdleListRow> rows)
    {
        IdleList.SelectedItems?.Clear();
        foreach (var row in rows)
            IdleList.SelectedItems?.Add(row);
    }

    public string Summary => IdleListing.Describe(_report.Projects.Count, _idle.Select(i => i.Idle).ToList(), Months);

    /// <summary>工程或安装情况变化后重算，保持选中。</summary>
    public void Show(ProjectReportResult report)
    {
        _report = report;
        _idle = ProjectReport.FindIdle(_database.LoadEffects(), report.Usage, Months, DateTime.UtcNow)
            .Select(i => new IdleListRow(i)).ToList();
        Refilter();
        RowsChanged?.Invoke();
    }

    private IdleFilter Filter => SegmentNever.IsChecked == true ? IdleFilter.NeverUsed : SegmentOld.IsChecked == true ? IdleFilter.LongUnused : IdleFilter.All;

    private void Refilter()
    {
        var selected = IdleList.SelectedItems?.OfType<IdleListRow>().Select(r => r.Row.Key).ToHashSet() ?? [];
        var rows = _idle.Where(r => IdleListing.Matches(r, Filter, SearchBox.Text ?? "")).ToList();
        IdleList.ItemsSource = rows;
        Select(rows.Where(r => selected.Contains(r.Row.Key)));
        var noProjects = _report.Projects.Count == 0;
        IdleList.IsVisible = Header.IsVisible = !noProjects;
        EmptyPanel.IsVisible = noProjects || rows.Count == 0;
        EmptyText.Text = IdleListing.EmptyText(_report.Projects.Count, _idle.Count);
        UpdateButton();
    }

    private void UpdateButton()
    {
        var count = IdleList.SelectedItems?.Count ?? 0;
        TrashButton.IsEnabled = count > 0;
        TrashButton.Content = IdleListing.ButtonText(count);
    }

    private async Task<string?> AskWithDialog(int count) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? await ConfirmDialog.Prompt(owner, "移入垃圾桶", IdleListing.Prompt(count), "移入垃圾桶", danger: true)
            : null;

    public async Task MoveSelectedToTrash()
    {
        var selected = IdleList.SelectedItems?.OfType<IdleListRow>().ToList() ?? [];
        if (selected.Count == 0 || AskReason == null || await AskReason(selected.Count) is not { } reason)
            return;
        try
        {
            IdleListing.MoveToTrash(_database, selected.Select(r => r.Row), reason);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        Trashed?.Invoke();
        StatusChanged?.Invoke(IdleListing.Moved(selected.Count));
    }
}
