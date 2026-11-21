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
        var categories = HealthListing.Categories(_health, _updates);
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

        var rows = HealthListing.Rows(category, _health, _updates);
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
        HealthPanelNote.Text = HealthListing.Note(row);
        var plugin = row.Plugin;
        HealthFiles.ItemsSource = HealthListing.Files(plugin);
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
        OpenWebPage(HealthListing.SearchUrl(row));
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

    private string DescribeHealth() => HealthListing.Describe(_health);
}
