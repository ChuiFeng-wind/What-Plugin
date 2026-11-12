using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>厂家账号表的一行：一个账号平台，以及跟随它的已购插件。</summary>
public sealed class PlatformRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public PlatformRow(string platform, PlatformAccount? account, IReadOnlyList<WishItem> followers)
    {
        Platform = platform;
        Account = account ?? new PlatformAccount(platform);
        Followers = followers;
    }

    public string Platform { get; }
    public PlatformAccount Account { get; private set; }
    public IReadOnlyList<WishItem> Followers { get; }

    /// <summary>这个平台的插件用 iLok 激活（iLok 那条自己除外）。</summary>
    public bool UsesILok => !ILokFill.IsILok(Platform)
        && (VendorCatalog.UsesILok(Platform) || Followers.Any(f => VendorCatalog.UsesILok(f.Vendor)));

    public int Count => Followers.Count;
    public string CountText => Count > 0 ? $"{Count} 款" : "暂无";
    public string AccountText => Account.Account.Length > 0 ? Account.Account : "未填";
    public string PasswordMask => Account.Locked ? "无法解开" : Account.Password.Length > 0 ? "••••••••" : "未填";

    /// <summary>平台下面有哪些品牌，例如 Plugin Alliance 下的 brainworx、SPL。</summary>
    public string VendorsText
    {
        get
        {
            var vendors = Followers.Select(f => VendorCatalog.DisplayVendor(f.Vendor)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ILokFill.IsILok(Platform))
                return "iLok 账号，可以用上方“填入 iLok 账号”填到用 iLok 激活的厂家";
            var ilok = Followers.Any(f => VendorCatalog.UsesILok(f.Vendor)) || VendorCatalog.UsesILok(Platform) ? "用 iLok 激活 · " : "";
            if (vendors.Count == 0 || (vendors.Count == 1 && vendors[0] == Platform))
                return ilok + (Account.Note.Length > 0 ? Account.Note.ReplaceLineEndings(" ") : "厂家账号");
            return ilok + "含 " + string.Join("、", vendors.Take(4)) + (vendors.Count > 4 ? $" 等 {vendors.Count} 个品牌" : "");
        }
    }

    public string SearchText => string.Join(' ', Platform, Account.Account, Account.Note, VendorsText,
        string.Join(' ', Followers.Select(f => f.Name))).ToLowerInvariant();

    public void Update(PlatformAccount account)
    {
        Account = account;
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
        var followers = _purchases
            .Where(p => !p.Item.OwnLogin && VendorCatalog.PlatformOf(p.Item.Vendor).Length > 0)
            .GroupBy(p => VendorCatalog.PlatformKey(VendorCatalog.PlatformOf(p.Item.Vendor)))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Item).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var rows = new List<PlatformRow>();
        foreach (var (key, account) in _platformAccounts)
            rows.Add(new PlatformRow(account.Platform, account, followers.GetValueOrDefault(key) ?? []));
        foreach (var (key, items) in followers)
        {
            if (!_platformAccounts.ContainsKey(key))
                rows.Add(new PlatformRow(VendorCatalog.PlatformOf(items[0].Vendor), null, items));
        }
        // iLok 那条总是放在最上面，没填也显示，方便先填它再一键填到各厂家。
        if (!rows.Any(r => ILokFill.IsILok(r.Platform)))
            rows.Add(new PlatformRow(ILokFill.Platform, null, []));
        _platformRows = rows
            .OrderBy(r => !ILokFill.IsILok(r.Platform))
            .ThenBy(r => r.Account.Account.Length == 0)
            .ThenBy(r => r.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _currentPlatform = null;
        _platformView = new ListCollectionView(_platformRows) { Filter = MatchesPlatform };
        PlatformGrid.ItemsSource = _platformView;
        PlatformGrid.SelectedItem = _platformRows.FirstOrDefault(r => r.Platform == selected);
        ShowPlatformDetail();
        UpdatePlatformEmpty();
    }

    private bool MatchesPlatform(object item)
    {
        var search = AccountSearchBox.Text.Trim().ToLowerInvariant();
        return search.Length == 0 || search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(((PlatformRow)item).SearchText.Contains);
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
        PlatformSubtitle.Text = ILokFill.IsILok(row.Platform)
            ? "在这里填一次 iLok 账号和密码，再点上方“填入 iLok 账号”，就能填到用 iLok 激活的厂家。以后改了密码，再点一次即可同步。"
            : row.Count > 0
                ? $"{row.Count} 款插件跟随这个账号。修改后它们一起生效；需要分开的，在插件详情里改成“单独账号”。"
                : "目前没有插件跟随这个账号。";
        PlatformProducts.Text = row.Count > 0 ? string.Join("、", row.Followers.Select(f => f.Name)) : "无";
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
        StatusText.Text = $"已保存 {updated.Platform} 的厂家账号" + (_currentPlatform.Count > 0 ? $"，{_currentPlatform.Count} 款插件一起生效" : "");
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
        if (ilok == null || ilok.Account.Length == 0)
        {
            PlatformGrid.SelectedItem = ilokRow;
            StatusText.Text = "先在最上面的“iLok”里填好 iLok 账号和密码，再点“填入 iLok 账号”";
            return;
        }
        if (ilok.Locked)
        {
            StatusText.Text = "iLok 的密码是在另一个 Windows 账号下加密的，这里解不开，不能填入";
            return;
        }

        var targets = _platformRows.Where(r => r.UsesILok).ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = "已购插件里还没有用 iLok 激活的厂家";
            return;
        }
        var plans = targets.Select(r => (Row: r, Result: ILokFill.Apply(ilok, r.Account))).ToList();
        var items = plans.Select(p => (
            $"{p.Row.Platform}（{p.Row.Count} 款）：{ILokFill.Describe(p.Result.Action)}",
            p.Result.Action is ILokFillAction.Filled or ILokFillAction.Synced or ILokFillAction.NoteAdded)).ToList();
        var picked = Dialogs.PickMany(this, "填入 iLok 账号",
            $"把 iLok 账号 {ilok.Account} 填到下面这些用 iLok 激活的厂家。已经填了别的账号的不会覆盖，只在备注里记下 iLok 账号名；密码不会写进备注。",
            items, "填入");
        if (picked == null)
            return;

        var changed = new List<string>();
        try
        {
            foreach (var index in picked)
            {
                var (row, result) = plans[index];
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
        StatusText.Text = changed.Count > 0 ? $"已填入 iLok 账号：{string.Join("、", changed)}" : "没有需要改动的厂家";
    }

    private void PlatformDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlatform == null)
            return;
        FlushItems();
        var row = _currentPlatform;
        var message = $"{row.Platform} 的账号、密码和备注会删除，无法恢复。"
            + (row.Count > 0 ? $"\n\n跟随它的 {row.Count} 款插件会变成“未填”。设为单独账号的插件不受影响。" : "");
        if (!Dialogs.Confirm(this, "清空这个厂家账号？", message, "清空", danger: true))
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
