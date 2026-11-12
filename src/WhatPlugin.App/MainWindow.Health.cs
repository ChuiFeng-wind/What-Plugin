using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>健康检查和查更新入口。只读：不运行插件，不改动任何文件。</summary>
public partial class MainWindow
{
    private const string UpdatesCategory = "按厂家查更新";

    private HealthReport _health = new([], 0);
    private IReadOnlyList<VendorUpdate> _updates = [];
    private bool _refreshingHealth;

    /// <summary>安装情况变化后都要重算，侧边栏的数字也靠它。</summary>
    private void RefreshHealth()
    {
        if (!_projectUiReady)
            return;
        var rows = _plugins.Select(p => p.Row).ToList();
        _health = PluginHealth.Build(rows, HostBlacklist.Load());
        _updates = UpdateGuide.Build(rows);
        CountHealth.Text = _health.NeedsAttention > 0 ? _health.NeedsAttention.ToString() : "";

        var selected = (HealthCategories.SelectedItem as HealthCategoryRow)?.Title;
        var categories = _health.Categories
            .Select(c => new HealthCategoryRow(c.Title, c.Explanation, c.Count, c.Kind))
            .Append(new HealthCategoryRow(UpdatesCategory,
                "没有可靠的公开版本数据库，自动比对容易误报，所以这里列出每个厂家通常用什么工具更新。这个软件自己不联网。", _updates.Count, null))
            .ToList();
        _refreshingHealth = true;
        HealthCategories.ItemsSource = categories;
        HealthCategories.SelectedItem = categories.FirstOrDefault(c => c.Title == selected) ?? categories[0];
        _refreshingHealth = false;
        ShowHealthRows();
    }

    private void HealthCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingHealth)
            ShowHealthRows();
    }

    private void ShowHealthRows()
    {
        if (HealthCategories.SelectedItem is not HealthCategoryRow category)
            return;
        HealthExplain.Text = category.Explanation;

        List<HealthRow> rows = category.Kind is { } kind
            ? _health.Issues.Where(i => i.Kind == kind)
                .OrderBy(i => i.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => new HealthRow(i.Plugin.Name, i.Plugin.Vendor.Length > 0 ? i.Plugin.Vendor : "未知", i.Detail, i.Plugin, null))
                .ToList()
            : _updates.Select(v => new HealthRow(v.Vendor, $"{v.Count} 款", "通常用 " + v.Tool, null, v)).ToList();
        HealthGrid.ItemsSource = rows;
        HealthEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_page == AppPage.Health)
            ShowProjectStatus();
        ShowHealthDetail();
    }

    private void HealthGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowHealthDetail();

    private void ShowHealthDetail()
    {
        var row = HealthGrid.SelectedItem as HealthRow;
        HealthPanel.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
        HealthPanelEmpty.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        if (row == null)
            return;

        HealthPanelTitle.Text = row.Title;
        HealthPanelNote.Text = row.Vendor != null ? "通常用 " + row.Vendor.Tool + " 检查更新" : row.Detail;
        var plugin = row.Plugin;
        HealthFiles.ItemsSource = plugin?.Files.Select(f => new HealthFileRow(
            $"{Labels.Format(f.Format)}  {Labels.Arch(f.Arch)}" + (f.Version.Length > 0 ? $"  {f.Version}" : ""), f.Path)).ToList();
        if (HealthFiles.Items.Count > 0)
            HealthFiles.SelectedIndex = 0;
        HealthFiles.Visibility = plugin != null ? Visibility.Visible : Visibility.Collapsed;
        HealthLocateButton.Visibility = plugin is { Files.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        HealthOpenButton.Visibility = plugin != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HealthSearch_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is not HealthRow row)
            return;
        OpenWebPage(row.Plugin is { } plugin
            ? UpdateGuide.SearchUrl(plugin.Vendor, plugin.Name)
            : UpdateGuide.SearchUrl(row.Title));
    }

    private void HealthLocate_Click(object sender, RoutedEventArgs e)
    {
        if (HealthFiles.SelectedItem is not HealthFileRow file)
            return;
        try
        {
            // 只在资源管理器里定位，不运行也不改动文件。
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = false });
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            StatusText.Text = "无法打开文件夹：" + error.Message;
        }
    }

    private void HealthOpen_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is HealthRow { Plugin: { } plugin })
            ShowPluginInLibrary(plugin.Key);
    }

    private void HealthGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsInside<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource))
            HealthOpen_Click(sender, e);
    }

    private string DescribeHealth()
    {
        var text = _health.NeedsAttention > 0 ? $"{_health.NeedsAttention} 款插件需要留意" : "没有影响使用的问题";
        if (_health.UnmatchedBlacklistFiles > 0)
            text += $"。宿主黑名单里另有 {_health.UnmatchedBlacklistFiles} 个文件不在扫描结果中，可能是音源或已卸载";
        return text;
    }

}

public sealed record HealthCategoryRow(string Title, string Explanation, int Count, HealthKind? Kind)
{
    public override string ToString() => $"{Title} {Count}";
}

public sealed record HealthRow(string Title, string Subtitle, string Detail, PluginRow? Plugin, VendorUpdate? Vendor)
{
    public override string ToString() => Title;
}

public sealed record HealthFileRow(string Summary, string Path)
{
    public override string ToString() => Path;
}
