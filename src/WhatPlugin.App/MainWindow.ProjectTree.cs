using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>工程页左侧的文件夹树。只影响显示，不改动任何文件。</summary>
public partial class MainWindow
{
    private const string CollapsedFoldersSetting = "projects.collapsedFolders";
    private const string ProjectTreeVisibleSetting = "projects.treeVisible";

    private string? _projectFolderFilter;
    private HashSet<string>? _collapsedFolders;
    private bool _rebuildingProjectTree;
    private bool? _projectTreeVisible;
    private bool _projectGridNarrow;

    private const double ProjectGridNarrowWidth = 560;

    /// <summary>窄窗口下保留的“最近修改”“缺失”两列加滚动条的宽度。</summary>
    private const double ProjectGridNarrowReserved = 186;

    private bool ProjectTreeVisible =>
        _projectTreeVisible ??= _database.GetSetting(ProjectTreeVisibleSetting) != "0";

    private HashSet<string> CollapsedFolders => _collapsedFolders ??= LoadCollapsedFolders();

    private HashSet<string> LoadCollapsedFolders()
    {
        var saved = _database.GetSetting(CollapsedFoldersSetting);
        try
        {
            var paths = saved == null ? null : JsonSerializer.Deserialize<string[]>(saved);
            return new HashSet<string>(paths ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>树上的数量跟随搜索和“有缺失”筛选，但不受当前选中的文件夹影响。</summary>
    private void RebuildProjectTree()
    {
        var matched = _projects.Where(MatchesProjectFilters).ToList();
        var nodes = ProjectFolderTree.Build(_database.LoadProjectFolders(), matched.Select(p => p.Entry));
        var searching = ProjectSearchBox.Text.Trim().Length > 0;

        _rebuildingProjectTree = true;
        var all = new ProjectFolderViewModel(null, "全部工程", matched.Count, matched.Count(p => p.MissingCount > 0), [], true, OnProjectFolderToggled);
        var items = new List<ProjectFolderViewModel> { all };
        items.AddRange(nodes.Select(n => ToViewModel(n, searching)));

        // 搜索后选中的文件夹里可能已经没有结果，这时回到全部工程，免得列表看起来是空的。
        var selected = _projectFolderFilter == null ? null : Select(items, _projectFolderFilter);
        if (selected == null)
        {
            _projectFolderFilter = null;
            all.IsSelected = true;
        }
        ProjectFolderTreeView.ItemsSource = items;
        _rebuildingProjectTree = false;

        UpdateProjectTreeVisibility();
    }

    private ProjectFolderViewModel ToViewModel(ProjectFolderNode node, bool forceExpanded) =>
        new(node.Path, node.Name, node.ProjectCount, node.MissingProjects,
            node.Children.Select(c => ToViewModel(c, forceExpanded)).ToList(),
            forceExpanded || !CollapsedFolders.Contains(node.Path),
            OnProjectFolderToggled);

    /// <summary>选中并展开上级，否则选中的文件夹藏在折叠的层级里，看不出列表为什么变少了。</summary>
    private static ProjectFolderViewModel? Select(IEnumerable<ProjectFolderViewModel> items, string path)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
                return item;
            }
            if (Select(item.Children, path) is { } child)
            {
                item.IsExpanded = true;
                return child;
            }
        }
        return null;
    }

    private void OnProjectFolderToggled(ProjectFolderViewModel folder)
    {
        // 搜索时的展开是临时的，不能覆盖用户自己的折叠状态。
        if (_rebuildingProjectTree || folder.Path == null || ProjectSearchBox.Text.Trim().Length > 0)
            return;
        if (folder.IsExpanded)
            CollapsedFolders.Remove(folder.Path);
        else
            CollapsedFolders.Add(folder.Path);
        _database.SetSetting(CollapsedFoldersSetting, JsonSerializer.Serialize(CollapsedFolders.Order(StringComparer.OrdinalIgnoreCase)));
    }

    private void ProjectFolder_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_rebuildingProjectTree || e.NewValue is not ProjectFolderViewModel folder || folder.Path == _projectFolderFilter)
            return;
        _projectFolderFilter = folder.Path;
        _projectView?.Refresh();
        UpdateProjectEmpty();
        ShowProjectDetail();
    }

    private void ToggleProjectTree_Click(object sender, RoutedEventArgs e)
    {
        _projectTreeVisible = !ProjectTreeVisible;
        _database.SetSetting(ProjectTreeVisibleSetting, _projectTreeVisible == true ? "1" : "0");
        // 隐藏后列表必须显示全部工程，否则用户看不到是什么在过滤。
        if (_projectTreeVisible == false)
            _projectFolderFilter = null;
        RebuildProjectTree();
        _projectView?.Refresh();
        UpdateProjectEmpty();
    }

    /// <summary>窗口较窄时先收起次要的列，保证工程名看得见。</summary>
    private void ProjectGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < ProjectGridNarrowWidth;
        if (narrow != _projectGridNarrow)
        {
            _projectGridNarrow = narrow;
            var visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            ProjectHostColumn.Visibility = visibility;
            ProjectVersionsColumn.Visibility = visibility;
            ProjectEffectsColumn.Visibility = visibility;
            if (!narrow)
                ProjectNameColumn.Width = new DataGridLength(2.4, DataGridLengthUnitType.Star);
        }
        // 窄的时候星号列不会把剩余列留在可见区域内，所以按实际宽度计算。
        if (narrow)
            ProjectNameColumn.Width = new DataGridLength(Math.Max(ProjectNameColumn.MinWidth, e.NewSize.Width - ProjectGridNarrowReserved));
    }

    private void UpdateProjectTreeVisibility()
    {
        var hasProjects = _projects.Count > 0;
        ProjectTreeCard.Visibility = hasProjects && ProjectTreeVisible && !ShowingMissingSummary ? Visibility.Visible : Visibility.Collapsed;
        ProjectTreeToggle.Visibility = hasProjects && !ShowingMissingSummary ? Visibility.Visible : Visibility.Collapsed;
        ProjectTreeToggle.Content = ProjectTreeVisible ? "隐藏文件夹" : "显示文件夹";
    }
}

/// <summary>文件夹树的一项。Path 为空表示“全部工程”。</summary>
public sealed class ProjectFolderViewModel(
    string? path,
    string name,
    int count,
    int missing,
    IReadOnlyList<ProjectFolderViewModel> children,
    bool expanded,
    Action<ProjectFolderViewModel> toggled) : INotifyPropertyChanged
{
    private bool _isExpanded = expanded;
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Path { get; } = path;
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string MissingText { get; } = missing > 0 ? $"缺 {missing}" : "";
    public Visibility MissingVisibility { get; } = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ToolTip { get; } = path ?? "所有已添加文件夹里的工程";
    public IReadOnlyList<ProjectFolderViewModel> Children { get; } = children;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            toggled(this);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
