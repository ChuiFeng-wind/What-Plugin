using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>插件库左边的分类栏。</summary>
public partial class MainWindow
{
    // ---------- 插件库左边的分类栏（以前的同类对比） ----------

    private bool _refreshingCategories;

    private LibraryCategory? SelectedCategory => CategoryList.SelectedItem is LibraryCategory { IsHeader: false, Kind: not LibraryCategoryKind.All } c ? c : null;

    private bool ShowingTimbres => CategoryTimbreToggle.IsChecked == true;

    private static bool IsTimbre(LibraryCategory category) => category.Kind is LibraryCategoryKind.Timbre or LibraryCategoryKind.NoTimbre;

    /// <summary>按当前视图（插件库或垃圾桶）重算各分类的数量，尽量保持原来的选择。上面的切换决定列功能分类还是音色分类。</summary>
    private void RebuildCategories()
    {
        var previous = CategoryList.SelectedItem as LibraryCategory;
        var rows = _plugins.Where(InCurrentView).Select(p => p.Row).ToList();
        var timbres = ShowingTimbres;
        var categories = LibraryCategories.Build(rows)
            .Where(c => c.Kind == LibraryCategoryKind.All || (c.Kind != LibraryCategoryKind.Header && IsTimbre(c) == timbres))
            .ToList();
        _refreshingCategories = true;
        CategoryList.ItemsSource = categories;
        CategoryList.SelectedItem = categories.FirstOrDefault(c => previous != null && c.Kind == previous.Kind && c.Label == previous.Label)
            ?? categories.FirstOrDefault();
        _refreshingCategories = false;
    }

    private void SelectCategory(LibraryCategory? category)
    {
        if (category != null && IsTimbre(category) != ShowingTimbres)
        {
            _refreshingCategories = true;
            (IsTimbre(category) ? CategoryTimbreToggle : CategoryFunctionToggle).IsChecked = true;
            _refreshingCategories = false;
            RebuildCategories();
        }
        if (CategoryList.ItemsSource is not IEnumerable<LibraryCategory> items)
            return;
        var list = items.ToList();
        _refreshingCategories = true;
        CategoryList.SelectedItem = category == null
            ? list.FirstOrDefault()
            : list.FirstOrDefault(c => c.Kind == category.Kind && c.Label == category.Label) ?? list.FirstOrDefault();
        _refreshingCategories = false;
        ApplyCategorySort();
    }

    /// <summary>功能分类和音色分类互相切换：换一组分类，回到“全部”。</summary>
    private void CategoryMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingCategories || _view == null)
            return;
        RebuildCategories();
        ApplyCategorySort();
        _view.Refresh();
        UpdateCount();
    }

    private void LibraryCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingCategories || _view == null)
            return;
        ApplyCategorySort();
        _view.Refresh();
        UpdateCount();
    }

    /// <summary>选中一个分类就是在做同类对比：用得多的排前面，其次看评分。选“全部”时恢复按厂家排。</summary>
    private void ApplyCategorySort()
    {
        if (_view == null || _need != null)
            return;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            // 垃圾桶里已经卸载的排在最后。
            if (InBlacklistView)
                _view.SortDescriptions.Add(new SortDescription(nameof(PluginViewModel.IsUninstalled), ListSortDirection.Ascending));
            if (SelectedCategory != null)
            {
                _view.SortDescriptions.Add(new SortDescription(nameof(PluginViewModel.UsageCount), ListSortDirection.Descending));
                _view.SortDescriptions.Add(new SortDescription(nameof(PluginViewModel.Rating), ListSortDirection.Descending));
                _view.SortDescriptions.Add(new SortDescription(nameof(PluginViewModel.Name), ListSortDirection.Ascending));
            }
            else if (InBlacklistView)
            {
                _view.SortDescriptions.Add(new SortDescription(nameof(PluginViewModel.Vendor), ListSortDirection.Ascending));
                _view.SortDescriptions.Add(new SortDescription(nameof(PluginViewModel.Name), ListSortDirection.Ascending));
            }
        }
        foreach (var column in PluginGrid.Columns)
            column.SortDirection = null;
        UpdateCategorySummary();
    }

    /// <summary>选中分类时，页面副标题换成这一类的使用概况。</summary>
    private void UpdateCategorySummary()
    {
        if (_page != AppPage.Plugins || _need != null)
            return;
        if (SelectedCategory is not { } category)
        {
            PluginPageSubtitle.Text = "已安装的效果器，不含音源。左边选一个分类，就能把同类插件放在一起比较";
            return;
        }
        var rows = _plugins.Where(p => InCurrentView(p) && category.Matches(p.Row))
            .Select(p => new ComparisonRow(p.Row, p.Usage)).ToList();
        var summary = PluginComparison.Summarize(rows, DateTime.UtcNow);
        PluginPageSubtitle.Text = _report.Projects.Count == 0
            ? $"“{category.Label}”共 {summary.Total} 款，{summary.Unrated} 款未评分。到“工程”页扫描后可以看到使用情况"
            : $"“{category.Label}”共 {summary.Total} 款，过去一年用过 {summary.UsedRecently} 款，工程里从没用过 {summary.NeverUsed} 款，{summary.Unrated} 款未评分";
    }

    /// <summary>窗口窄的时候先收起线性相位和侧链两列，插件名和备注要留够宽度。</summary>
    private void PluginGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 860;
        var visibility = narrow || _need != null ? Visibility.Collapsed : Visibility.Visible;
        LinearPhaseColumn.Visibility = visibility;
        SidechainColumn.Visibility = visibility;
        var veryNarrow = e.NewSize.Width < 700;
        LatencyColumn.Visibility = veryNarrow || _need != null ? Visibility.Collapsed : Visibility.Visible;
        CpuColumn.Visibility = veryNarrow || _need != null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>插件详情里的“看同类”：在左边选中这款插件所属范围最窄的分类。</summary>
    private void CompareCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        SaveCurrent();
        var row = _current.Row;
        var owned = PluginComparison.Categories(_plugins.Select(p => p.Row));
        var category = row.Tags.Count > 0
            ? new LibraryCategory(LibraryCategoryKind.Function, PurchaseAdvisor.NarrowestTag(row.Tags, owned), 0)
            : new LibraryCategory(LibraryCategoryKind.NoFunction, LibraryCategories.NoFunctionLabel, 0);
        var plugin = _current;
        SearchBox.Text = "";
        SelectCategory(category);
        _view?.Refresh();
        UpdateCount();
        PluginGrid.SelectedItem = plugin;
        PluginGrid.ScrollIntoView(plugin);
    }

    private static bool IsInside<T>(object source) where T : DependencyObject
    {
        for (var node = source as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is T)
                return true;
        }
        return false;
    }
}
