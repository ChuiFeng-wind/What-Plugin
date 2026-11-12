using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WhatPlugin.Core;

namespace WhatPlugin.App;

public sealed class ReviewPickRow(PluginRow row) : INotifyPropertyChanged
{
    private bool _selected = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PluginRow Row { get; } = row;
    public string Name => Row.Name;
    public string Vendor => Row.Vendor;
    public string Stars => Row.User.Rating > 0 ? new string('★', Row.User.Rating) : "";

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
}

/// <summary>选择要分享哪些插件的评价。默认全选，只列出写过评价的插件。</summary>
public partial class ReviewExportDialog
{
    private readonly List<ReviewPickRow> _rows;
    private readonly ListCollectionView _view;
    private readonly bool _ready;

    public ReviewExportDialog(IEnumerable<PluginRow> rows, string author)
    {
        InitializeComponent();
        WindowEffects.ApplyDialog(this);

        _rows = rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => new ReviewPickRow(r)).ToList();
        _view = new ListCollectionView(_rows) { Filter = Matches };
        PickList.ItemsSource = _view;
        AuthorBox.Text = author;
        _ready = true;
        UpdateSummary();
    }

    public string Author => AuthorBox.Text.Trim();

    public IReadOnlyList<PluginRow> SelectedRows => _rows.Where(r => r.Selected).Select(r => r.Row).ToList();

    public ReviewExportOptions Options => new(IncludeUsage.IsChecked == true, IncludeCategories.IsChecked == true, IncludeMix.IsChecked == true);

    private bool Matches(object item)
    {
        var row = (ReviewPickRow)item;
        var search = SearchBox.Text.Trim();
        return search.Length == 0
            || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Vendor.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            _view.Refresh();
    }

    private void Author_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            UpdateSummary();
    }

    private void PickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _view.Cast<ReviewPickRow>())
            row.Selected = true;
        UpdateSummary();
    }

    private void PickNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.Selected = false;
        UpdateSummary();
    }

    private void Pick_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready)
            UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _rows.Count(r => r.Selected);
        SummaryText.Text = Author.Length == 0 ? "请填写昵称" : count > 0 ? $"已选 {count} 款" : "还没有选择";
        ExportButton.IsEnabled = count > 0 && Author.Length > 0;
        ExportButton.Content = count > 0 ? $"导出 {count} 款" : "导出";
    }

    private void Export_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
