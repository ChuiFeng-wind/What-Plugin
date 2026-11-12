using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WhatPlugin.Core;

namespace WhatPlugin.App;

public sealed class PickRow(string name, string vendor, bool exists) : INotifyPropertyChanged
{
    private bool _selected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; } = name;
    public string Vendor { get; } = vendor;
    public Visibility ExistsVisibility { get; } = exists ? Visibility.Visible : Visibility.Collapsed;

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

public partial class BatchAddDialog
{
    private const string AllVendors = "全部厂家";

    private readonly List<PickRow> _rows;
    private readonly ListCollectionView _view;
    private bool _ready;

    public BatchAddDialog(IReadOnlyList<PluginRow> installed, IReadOnlyList<WishItem> purchased)
    {
        InitializeComponent();
        WindowEffects.ApplyDialog(this);

        var existing = purchased.Select(p => Normalizer.Name(p.Name)).ToHashSet();
        _rows = installed
            .Select(p => new PickRow(p.Name, p.Vendor, existing.Contains(Normalizer.Name(p.Name))))
            .ToList();
        _view = new ListCollectionView(_rows) { Filter = MatchesPick };
        PickList.ItemsSource = _view;

        var vendors = new List<string> { AllVendors };
        vendors.AddRange(_rows.Select(r => r.Vendor).Where(v => v.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase));
        PickVendor.ItemsSource = vendors;
        PickVendor.SelectedIndex = 0;

        _ready = true;
        UpdateSummary();
    }

    public IReadOnlyList<PurchaseDraft> Drafts { get; private set; } = [];

    private bool PickMode => ModePick.IsChecked == true;

    private bool MatchesPick(object item)
    {
        var row = (PickRow)item;
        if (PickVendor.SelectedItem is string vendor && vendor != AllVendors && row.Vendor != vendor)
            return false;
        var search = PickSearch.Text.Trim();
        return search.Length == 0
            || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Vendor.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        PickPanel.Visibility = PickMode ? Visibility.Visible : Visibility.Collapsed;
        PastePanel.Visibility = PickMode ? Visibility.Collapsed : Visibility.Visible;
        UpdateSummary();
    }

    private void PickFilter_Changed(object sender, EventArgs e)
    {
        if (_ready)
            _view.Refresh();
    }

    private void PickAll_Click(object sender, RoutedEventArgs e) => SetVisibleSelection(true);

    private void PickNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.Selected = false;
        UpdateSummary();
    }

    /// <summary>只选当前筛选出来的，这样“筛选 Waves 再全选”不会把别的厂家带上。</summary>
    private void SetVisibleSelection(bool selected)
    {
        foreach (var row in _view.Cast<PickRow>())
            row.Selected = selected;
        UpdateSummary();
    }

    private void Pick_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready)
            UpdateSummary();
    }

    private void PasteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (PickMode)
        {
            var count = _rows.Count(r => r.Selected);
            SummaryText.Text = count > 0 ? $"已选 {count} 款" : "还没有选择";
            ApplyButton.IsEnabled = count > 0;
            ApplyButton.Content = count > 0 ? $"添加 {count} 款" : "添加";
            return;
        }

        var parsed = PurchaseText.Parse(PasteBox.Text);
        SummaryText.Text = parsed.BadLines.Count > 0
            ? $"可识别 {parsed.Drafts.Count} 款，第 {string.Join("、", parsed.BadLines)} 行无法识别"
            : parsed.Drafts.Count > 0 ? $"可识别 {parsed.Drafts.Count} 款" : "";
        ApplyButton.IsEnabled = parsed.Drafts.Count > 0;
        ApplyButton.Content = parsed.Drafts.Count > 0 ? $"添加 {parsed.Drafts.Count} 款" : "添加";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (PickMode)
        {
            var account = SharedAccount.Text.Trim();
            var password = SharedPassword.Secret;
            var serial = SharedSerial.Secret;
            Drafts = _rows.Where(r => r.Selected).Select(r => new PurchaseDraft(r.Name, r.Vendor, account, password, serial)).ToList();
        }
        else
        {
            var parsed = PurchaseText.Parse(PasteBox.Text);
            if (parsed.BadLines.Count > 0 && !Dialogs.Confirm(this, "有无法识别的行",
                    $"第 {string.Join("、", parsed.BadLines)} 行无法识别，会被跳过。其余 {parsed.Drafts.Count} 款继续添加吗？", "继续添加"))
                return;
            Drafts = parsed.Drafts;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
