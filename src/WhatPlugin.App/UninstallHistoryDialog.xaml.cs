using System.Windows;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>卸载历史里的一条。改原因时直接写数据库。</summary>
public sealed class UninstallHistoryRow(UninstallRecord record, bool back, Action<UninstallRecord, string> saveReason)
{
    private string _reason = record.Reason;

    public UninstallRecord Record { get; } = record;
    public string Name => Record.Name;
    public string Vendor => Record.Vendor;
    public string When => $"{Record.DateText} · {Record.WayText}";
    public Visibility BackVisibility => back ? Visibility.Visible : Visibility.Collapsed;
    public string DeleteName => $"删除 {Record.Name} 的卸载记录";
    public string ReasonName => $"{Record.Name} 的卸载原因";
    public string ReasonHint => Record.TrashReason.Length > 0 ? "没填时用移入原因：" + Record.TrashReason : "为什么卸载";

    public string Reason
    {
        get => _reason;
        set
        {
            var clean = (value ?? "").Trim();
            if (clean == _reason)
                return;
            _reason = clean;
            saveReason(Record, clean);
        }
    }

    /// <summary>卸载时的移入原因、评分和感受。</summary>
    public string Snapshot
    {
        get
        {
            var parts = new List<string>();
            if (Record.TrashReason.Length > 0)
                parts.Add("移入垃圾桶的原因：" + Record.TrashReason);
            if (Record.Rating > 0)
                parts.Add($"当时评分 {Record.Rating} 星");
            if (Record.Note.Length > 0)
                parts.Add("当时的感受：" + Record.Note);
            return string.Join("\n", parts);
        }
    }

    public Visibility SnapshotVisibility => Snapshot.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Search => string.Join("\n", Record.Name, Record.Vendor, _reason, Record.TrashReason, Record.Note);

    public override string ToString() => Name;
}

/// <summary>垃圾桶的卸载历史：查看、改原因、删除单条。</summary>
public partial class UninstallHistoryDialog
{
    private readonly PluginDatabase _database;
    private readonly List<UninstallHistoryRow> _rows;

    public UninstallHistoryDialog(PluginDatabase database, IReadOnlyList<PluginRow> plugins)
    {
        InitializeComponent();
        WindowEffects.ApplyDialog(this);
        _database = database;
        _rows = database.LoadUninstallHistory()
            .Select(r => new UninstallHistoryRow(r, UninstallHistory.IsBack(r, plugins), Save))
            .ToList();
        Refresh();
        // 按 Esc 关闭时，正在编辑的原因也要保存。
        Closing += (_, _) => CloseButton.Focus();
    }

    /// <summary>删除或改过原因。</summary>
    public bool Changed { get; private set; }

    private void Save(UninstallRecord record, string reason)
    {
        _database.SetUninstallReason(record.Id, reason);
        Changed = true;
    }

    private void Refresh()
    {
        var search = SearchBox.Text.Trim();
        var visible = _rows.Where(r => search.Length == 0 || r.Search.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        HistoryList.ItemsSource = visible;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _rows.Count == 0 ? ""
            : search.Length > 0 ? $"找到 {visible.Count} 条，共 {_rows.Count} 条"
            : $"共 {_rows.Count} 条";
    }

    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_rows != null)
            Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not UninstallHistoryRow row)
            return;
        if (!Dialogs.Confirm(this, "删除这条卸载记录？",
                $"会删除“{row.Name}”{row.Record.DateText}的卸载记录和当时写的原因，删除后不能恢复。插件库和垃圾桶里的记录不受影响。", "删除", danger: true))
            return;
        _database.DeleteUninstallRecord(row.Record.Id);
        _rows.Remove(row);
        Changed = true;
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
