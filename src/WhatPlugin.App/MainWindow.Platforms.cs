using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>厂家账号表的一行。文字来自 Core 的 <see cref="PlatformEntry"/>。</summary>
public sealed class PlatformRow(PlatformEntry entry) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public PlatformEntry Entry { get; private set; } = entry;

    public string Platform => Entry.Platform;
    public PlatformAccount Account => Entry.Account;
    public IReadOnlyList<WishItem> Followers => Entry.Followers;
    public bool UsesILok => Entry.UsesILok;
    public int Count => Entry.Count;
    public string CountText => Entry.CountText;
    public string AccountText => Entry.AccountText;
    public string PasswordMask => Entry.PasswordMask;
    public string VendorsText => Entry.VendorsText;
    public string SearchText => Entry.SearchText;

    public void Update(PlatformAccount account)
    {
        Entry = Entry with { Account = account };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>已购账号页的“厂家账号”视图。</summary>
public partial class MainWindow
{
    private List<PlatformRow> _platformRows = [];
    private ICollectionView? _platformView;
    private PlatformRow? _currentPlatform;

    private bool ShowingPlatforms => AccountViewPlatforms.IsChecked == true;

    private void LoadPlatforms()
    {
        var selected = _currentPlatform?.Platform;
        _platformRows = PlatformListing.Build(_purchases.Select(p => p.Item), _platformAccounts).Select(e => new PlatformRow(e)).ToList();

        _currentPlatform = null;
        _platformView = new ListCollectionView(_platformRows) { Filter = MatchesPlatform };
        PlatformGrid.ItemsSource = _platformView;
        PlatformGrid.SelectedItem = _platformRows.FirstOrDefault(r => r.Platform == selected);
        ShowPlatformDetail();
        UpdatePlatformEmpty();
    }

    private bool MatchesPlatform(object item)
    {
        return WishListing.Matches(((PlatformRow)item).SearchText, AccountSearchBox.Text);
    }

    private void UpdatePlatformEmpty()
    {
        var shown = _platformView?.Cast<object>().Any() ?? false;
        PlatformEmpty.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        PlatformEmptyText.Text = _platformRows.Count == 0
            ? "还没有厂家账号。\n已购插件填上厂家后，会按厂家出现在这里。"
            : "没有符合搜索的厂家账号";
    }

    private void AccountView_Checked(object sender, RoutedEventArgs e)
    {
        if (PlatformView == null || PurchaseView == null)
            return;
        FlushItems();
        var platforms = ShowingPlatforms;
        PlatformView.Visibility = platforms ? Visibility.Visible : Visibility.Collapsed;
        PurchaseView.Visibility = platforms ? Visibility.Collapsed : Visibility.Visible;
        AccountVendorFilter.Visibility = platforms ? Visibility.Collapsed : Visibility.Visible;
        AccountStateFilter.Visibility = platforms ? Visibility.Collapsed : Visibility.Visible;
        SelectAllButton.Visibility = platforms ? Visibility.Collapsed : Visibility.Visible;
        ILokFillButton.Visibility = platforms ? Visibility.Visible : Visibility.Collapsed;
        AccountSearchBox.Tag = platforms ? "搜索厂家、账号或插件名" : "搜索插件名、厂家或账号";
        // 在插件视图改过的内容要反映到厂家账号里，反过来也一样。
        LoadItems();
    }

    private void PlatformGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlushItems();
        _currentPlatform = PlatformGrid.SelectedItem as PlatformRow;
        ShowPlatformDetail();
    }

    private void ShowPlatformDetail()
    {
        PlatformPanelEmpty.Visibility = _currentPlatform == null ? Visibility.Visible : Visibility.Collapsed;
        PlatformPanel.Visibility = _currentPlatform == null ? Visibility.Collapsed : Visibility.Visible;
        if (_currentPlatform == null)
            return;

        var row = _currentPlatform;
        _loadingItem = true;
        PlatformTitle.Text = row.Platform;
        PlatformAccountBox.Text = row.Account.Account;
        PlatformPassword.Secret = row.Account.Password;
        PlatformNote.Text = row.Account.Note;
        _loadingItem = false;
        PlatformPassword.IsEnabled = !row.Account.Locked;
        PlatformLocked.Visibility = row.Account.Locked ? Visibility.Visible : Visibility.Collapsed;
        PlatformSubtitle.Text = row.Entry.Subtitle;
        PlatformProducts.Text = row.Entry.ProductsText;
        PlatformDelete.IsEnabled = !row.Account.IsEmpty;
    }

    private void PlatformField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingItem)
            ScheduleItemSave();
    }

    private void PlatformSecret_Changed(object? sender, EventArgs e)
    {
        if (!_loadingItem)
            ScheduleItemSave();
    }

    private void SavePlatformPanel()
    {
        if (_currentPlatform == null || !ShowingPlatforms)
            return;
        var old = _currentPlatform.Account;
        var updated = old with
        {
            Account = PlatformAccountBox.Text.Trim(),
            Password = old.Locked ? "" : PlatformPassword.Secret,
            Note = PlatformNote.Text,
        };
        if (updated == old)
            return;
        try
        {
            _database.SavePlatformAccount(updated);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }
        _platformAccounts[VendorCatalog.PlatformKey(updated.Platform)] = updated;
        _currentPlatform.Update(updated);
        foreach (var purchase in _purchases)
            purchase.Update(purchase.Item, PurchaseLogin.Resolve(purchase.Item, _platformAccounts));
        PlatformDelete.IsEnabled = !updated.IsEmpty;
        UpdateItemSummaries();
        StatusText.Text = _currentPlatform.Entry.Saved;
    }

    private void CopyPlatformAccount_Click(object sender, RoutedEventArgs e)
    {
        if (PlatformAccountBox.Text.Length > 0)
            StatusText.Text = SecureClipboard.Copy(PlatformAccountBox.Text) ? "已复制账号，30 秒后自动清除" : "复制失败：" + SecureClipboard.LastError;
    }

    private void ILokFill_Click(object sender, RoutedEventArgs e)
    {
        FlushItems();
        var ilokRow = _platformRows.FirstOrDefault(r => ILokFill.IsILok(r.Platform));
        var ilok = ilokRow?.Account;
        if (PlatformListing.ILokProblem(ilok, _platformRows.Select(r => r.Entry).ToList()) is { } problem)
        {
            if (problem == PlatformListing.ILokFirst)
                PlatformGrid.SelectedItem = ilokRow;
            StatusText.Text = problem;
            return;
        }

        var plans = PlatformListing.ILokPlan(ilok!, _platformRows.Select(r => r.Entry));
        var picked = Dialogs.PickMany(this, "填入 iLok 账号", PlatformListing.ILokPrompt(ilok!),
            plans.Select(p => (p.Text, p.Checked)).ToList(), "填入");
        if (picked == null)
            return;

        var changed = new List<string>();
        try
        {
            foreach (var index in picked)
            {
                var (row, result, _, _) = plans[index];
                if (result.Action is ILokFillAction.Unchanged or ILokFillAction.Skipped)
                    continue;
                _database.SavePlatformAccount(result.Updated);
                changed.Add(row.Platform);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }
        _itemSaveTimer.Stop();
        LoadItems();
        StatusText.Text = PlatformListing.ILokFilled(changed);
    }

    private void PlatformDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlatform == null)
            return;
        FlushItems();
        var row = _currentPlatform;
        if (!Dialogs.Confirm(this, "清空这个厂家账号？", row.Entry.DeletePrompt, "清空", danger: true))
            return;
        try
        {
            _database.DeletePlatformAccount(row.Platform);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "删除失败：" + error.Message;
            return;
        }
        _itemSaveTimer.Stop();
        LoadItems();
        StatusText.Text = $"已清空 {row.Platform} 的厂家账号";
    }
}
