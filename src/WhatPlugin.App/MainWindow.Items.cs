using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>心愿单和已购账号两页。</summary>
public partial class MainWindow
{
    private const string SortByPriority = WishListing.SortByPriority;
    private const string SortByPrice = WishListing.SortByPrice;
    private const string SortByRecent = WishListing.SortByRecent;
    private const string AllAccountVendors = WishListing.AllVendors;
    private const string AllAccountStates = WishListing.AllStates;
    private const string WithLogin = WishListing.WithLogin;
    private const string WithoutLogin = WishListing.WithoutLogin;

    private readonly DispatcherTimer _itemSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private List<WishViewModel> _wishes = [];
    private List<WishViewModel> _purchases = [];
    private ICollectionView? _wishView;
    private ICollectionView? _purchaseView;
    private WishViewModel? _currentWish;
    private WishViewModel? _currentPurchase;
    private bool _loadingItem;
    private Dictionary<string, PlatformAccount> _platformAccounts = [];
    // 详情里账号框当前显示的是哪个平台的账号；厂家改了之后不能把旧平台的内容存到新平台。
    private string _shownLoginTarget = "";
    // 用户自己清空了厂家的记录，这次运行里不再自动填。
    private readonly VendorFiller _vendorFiller = new();

    private void InitializeItems()
    {
        var currencies = Money.Currencies.ToList();
        WishCurrency.ItemsSource = currencies;
        PurchaseCurrency.ItemsSource = currencies;
        PurchaseLicense.ItemsSource = WishListing.LicenseOptions;
        BatchLicense.ItemsSource = WishListing.BatchLicenseOptions;
        BatchLicense.SelectedIndex = 0;
        PurchaseLocked.Text = WishListing.PurchaseLockedText;
        PlatformLocked.Text = WishListing.PlatformLockedText;
        _loadingItem = true;
        WishSortBox.ItemsSource = WishListing.SortOptions;
        WishSortBox.SelectedIndex = 0;
        AccountStateFilter.ItemsSource = WishListing.StateOptions;
        AccountStateFilter.SelectedIndex = 0;
        _loadingItem = false;
        _itemSaveTimer.Tick += (_, _) => FlushItems();
        LoadItems();
    }

    private void LoadItems()
    {
        var wishId = _currentWish?.Item.Id;
        var purchaseId = _currentPurchase?.Item.Id;

        var installed = WishListing.InstalledNames(_database.LoadPlugins());
        _platformAccounts = PurchaseLogin.ByKey(_database.LoadPlatformAccounts());
        var all = _database.LoadWishes().Select(w => new WishViewModel(w, WishListing.IsInstalled(w, installed), PurchaseLogin.Resolve(w, _platformAccounts))).ToList();
        _wishes = all.Where(w => w.Item.Status == WishStatus.Wished).ToList();
        RecordUsage(() => _database.RecordWishCount(_wishes.Count));
        _purchases = all.Where(w => w.Item.Status == WishStatus.Purchased).ToList();

        _currentWish = null;
        _currentPurchase = null;

        _wishView = new ListCollectionView(_wishes) { Filter = MatchesWish };
        ApplyWishSort();
        WishList.ItemsSource = _wishView;
        WishList.SelectedItem = _wishes.FirstOrDefault(w => w.Item.Id == wishId);

        _purchaseView = new ListCollectionView(_purchases) { Filter = MatchesPurchase };
        var vendors = _purchases.Select(p => p.Item.Vendor).Where(v => v.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase);
        var filtering = _loadingItem;
        _loadingItem = true;
        SetOptions(AccountVendorFilter, AllAccountVendors, vendors);
        _loadingItem = filtering;
        PurchaseGrid.ItemsSource = _purchaseView;
        PurchaseGrid.SelectedItem = _purchases.FirstOrDefault(w => w.Item.Id == purchaseId);

        ShowWishDetail();
        ShowPurchaseDetail();
        LoadPlatforms();
        UpdateItemSummaries();
    }

    private void UpdateItemSummaries()
    {
        CountWishes.Text = _wishes.Count > 0 ? _wishes.Count.ToString() : "";
        CountAccounts.Text = _purchases.Count > 0 ? _purchases.Count.ToString() : "";

        WishSubtitle.Text = WishListing.WishSubtitle(_wishes.Select(w => w.Row).ToList());
        AccountSubtitle.Text = WishListing.AccountSubtitle(_purchases.Select(p => p.Row).ToList());

        var wishShown = _wishView?.Cast<object>().Any() ?? false;
        WishEmpty.Visibility = wishShown ? Visibility.Collapsed : Visibility.Visible;
        WishEmptyText.Text = WishListing.WishEmptyText(_wishes.Count);

        var purchaseShown = _purchaseView?.Cast<object>().Any() ?? false;
        PurchaseEmpty.Visibility = purchaseShown ? Visibility.Collapsed : Visibility.Visible;
        PurchaseEmptyText.Text = WishListing.PurchaseEmptyText(_purchases.Count);
    }

    private void ShowItemStatus()
    {
        if (_page == AppPage.Wishes)
        {
            StatusText.Text = WishListing.WishesStatus(_wishes.Count);
            // 工程或插件可能在别的页面变过。
            ShowWishAdvice();
        }
        else if (_page == AppPage.Accounts)
            StatusText.Text = WishListing.PurchasesStatus(_purchases.Count);
    }

    private void FlushItems()
    {
        var pending = _itemSaveTimer.IsEnabled;
        _itemSaveTimer.Stop();
        if (!pending)
            return;
        SaveWishPanel();
        SavePurchasePanel();
        SavePlatformPanel();
    }

    private void ScheduleItemSave()
    {
        _itemSaveTimer.Stop();
        _itemSaveTimer.Start();
    }

    private bool TrySave(WishItem item)
    {
        try
        {
            _database.SaveWish(item);
            RecordUsage(() => _database.RecordAction());
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return false;
        }
    }

    // ---------- 心愿单 ----------

    private bool MatchesWish(object item) => WishListing.Matches(((WishViewModel)item).SearchText, WishSearchBox.Text);

    private void ApplyWishSort()
    {
        if (_wishView == null)
            return;
        _wishView.SortDescriptions.Clear();
        switch (WishSortBox.SelectedItem as string)
        {
            case SortByPrice:
                _wishView.SortDescriptions.Add(new SortDescription(nameof(WishViewModel.PriceSort), ListSortDirection.Ascending));
                break;
            case SortByRecent:
                _wishView.SortDescriptions.Add(new SortDescription(nameof(WishViewModel.Created), ListSortDirection.Descending));
                break;
            default:
                _wishView.SortDescriptions.Add(new SortDescription(nameof(WishViewModel.Priority), ListSortDirection.Descending));
                _wishView.SortDescriptions.Add(new SortDescription(nameof(WishViewModel.Created), ListSortDirection.Descending));
                break;
        }
    }

    private void WishFilter_Changed(object sender, EventArgs e)
    {
        if (_loadingItem || _wishView == null)
            return;
        FlushItems();
        ApplyWishSort();
        _wishView.Refresh();
        UpdateItemSummaries();
    }

    private void QuickAddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        QuickAdd();
        e.Handled = true;
    }

    private void QuickAdd_Click(object sender, RoutedEventArgs e) => QuickAdd();

    private void QuickAdd()
    {
        var name = QuickAddBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = WishListing.NameRequired;
            QuickAddBox.Focus();
            return;
        }

        if (!ConfirmRememberedWishes([name]))
            return;
        FlushItems();
        WishItem added;
        try
        {
            added = _database.AddWish(new WishItem(0, name, GuessVendor(name)));
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }

        QuickAddBox.Text = "";
        WishSearchBox.Text = "";
        LoadItems();
        WishList.SelectedItem = _wishes.FirstOrDefault(w => w.Item.Id == added.Id);
        if (WishList.SelectedItem != null)
            WishList.ScrollIntoView(WishList.SelectedItem);
        StatusText.Text = WishListing.QuickAdded(name, added.Vendor);
    }

    /// <summary>
    /// 要加入心愿单的插件里，有以前移进垃圾桶又卸载了的：先把当时写的原因和评价摆出来，确认还要加。
    /// 没有这种插件时直接返回 true。
    /// </summary>
    private bool ConfirmRememberedWishes(IReadOnlyList<string> names) =>
        NoteWishes.RememberedPrompt(names, _plugins.Select(p => p.Row).ToList(), _database.LoadUninstallHistory()) is not { } prompt
        || Dialogs.Confirm(this, "以前不要过的插件", prompt, "仍然加入");

    /// <summary>心愿单详情顶上的提醒：这款以前移进垃圾桶并卸载过。</summary>
    private void ShowWishTrashNote(WishItem item)
    {
        var remembered = item.Status == WishStatus.Wished
            ? NoteWishes.FindRemembered(item.Name, _plugins.Select(p => p.Row).ToList(), _database.LoadUninstallHistory())
            : null;
        WishTrashNote.Visibility = remembered != null ? Visibility.Visible : Visibility.Collapsed;
        WishTrashText.Text = remembered is { } r ? WishListing.TrashNote(r) : "";
    }

    private void WishList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切换前先保存，否则等待中的内容会写到下一款上。
        FlushItems();
        _currentWish = WishList.SelectedItem as WishViewModel;
        ShowWishDetail();
    }

    private void ShowWishDetail()
    {
        WishPanelEmpty.Visibility = _currentWish == null ? Visibility.Visible : Visibility.Collapsed;
        WishPanel.Visibility = _currentWish == null ? Visibility.Collapsed : Visibility.Visible;
        if (_currentWish == null)
            return;

        var item = _currentWish.Item;
        ShowWishTrashNote(item);
        _loadingItem = true;
        WishName.Text = item.Name;
        WishVendor.Text = item.Vendor;
        WishCurrency.SelectedItem = item.Currency;
        WishPrice.Text = item.Price?.ToString("0.##") ?? "";
        WishTarget.Text = item.TargetPrice?.ToString("0.##") ?? "";
        WishLowest.Text = item.LowestPrice?.ToString("0.##") ?? "";
        WishCategory.ItemsSource = WishListing.Categories(item, _plugins.Select(p => p.Row));
        WishCategory.SelectedItem = item.Category.Length > 0 ? item.Category : PurchaseAdvisor.AutoCategory;
        WishPriorityMustHave.IsChecked = WishPriority.Clean(item.Priority) == WishPriority.MustHave;
        WishPriorityCanWait.IsChecked = WishPriority.Clean(item.Priority) == WishPriority.CanWait;
        WishReason.Text = item.Reason;
        WishUrl.Text = item.Url;
        WishNote.Text = item.Note;
        _loadingItem = false;
        ShowWishAdvice();
    }

    /// <summary>买前提醒：同类已有多少款、多少款闲置，以及现价和目标价的差距。</summary>
    private void ShowWishAdvice()
    {
        if (_currentWish == null)
            return;
        var item = _currentWish.Item;
        var advice = PurchaseAdvisor.Advise(item.Name, item.Vendor, item.Category, _plugins.Select(p => p.Row).ToList(), _report.Usage, DateTime.UtcNow);
        WishAdvice.Text = PurchaseAdvisor.Describe(advice);
        WishPriceHint.Text = PurchaseAdvisor.DescribePrice(item);
        WishPriceHint.Visibility = WishPriceHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WishField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingItem)
            ScheduleItemSave();
    }

    private void SaveWishPanel()
    {
        if (_currentWish == null)
            return;
        var old = _currentWish.Item;

        // 填错的项保留原值并提示，其余内容照常保存。
        var (parsed, problems) = WishListing.ParseWish(old, new WishFields(
            WishName.Text, WishPrice.Text, WishTarget.Text, WishLowest.Text,
            WishCurrency.SelectedItem as string, WishCategory.SelectedItem as string,
            WishPriorityMustHave.IsChecked == true, WishReason.Text, WishUrl.Text, WishNote.Text));

        var vendor = FillVendor(old, parsed.Name, WishVendor.Text, out var guessed);
        if (guessed)
        {
            _loadingItem = true;
            WishVendor.Text = vendor;
            _loadingItem = false;
        }
        var updated = parsed with { Vendor = vendor };
        if (!TrySave(updated))
            return;
        _currentWish.Update(updated);
        UpdateItemSummaries();
        ShowWishAdvice();
        StatusText.Text = WishListing.Saved(updated.Name, problems, guessed ? vendor : "");
    }

    private string FillVendor(WishItem old, string name, string vendor, out bool guessed) =>
        _vendorFiller.Fill(old, name, vendor, _plugins.Select(p => p.Row), out guessed);

    // ---------- 厂家候选 ----------

    /// <summary>候选项沿用分类栏的条目样式，所以包一层带 Label 的记录。</summary>
    private sealed record VendorOption(string Label)
    {
        public bool IsHeader => false;
    }

    private void WishVendor_TextChanged(object sender, TextChangedEventArgs e)
    {
        WishField_Changed(sender, e);
        if (_loadingItem || !WishVendor.IsKeyboardFocusWithin)
            return;
        var extra = _plugins.Select(p => p.Row.Vendor).Concat(_wishes.Select(w => w.Item.Vendor));
        var options = VendorCatalog.Suggest(WishVendor.Text, extra)
            .Where(v => !v.Equals(WishVendor.Text.Trim(), StringComparison.Ordinal))
            .Select(v => new VendorOption(v)).ToList();
        WishVendorSuggestions.ItemsSource = options;
        WishVendorSuggestions.SelectedIndex = -1;
        WishVendorPopup.IsOpen = options.Count > 0;
    }

    private void WishVendor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!WishVendorPopup.IsOpen)
            return;
        var count = WishVendorSuggestions.Items.Count;
        switch (e.Key)
        {
            case Key.Down:
                WishVendorSuggestions.SelectedIndex = Math.Min(WishVendorSuggestions.SelectedIndex + 1, count - 1);
                WishVendorSuggestions.ScrollIntoView(WishVendorSuggestions.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                WishVendorSuggestions.SelectedIndex = Math.Max(WishVendorSuggestions.SelectedIndex - 1, 0);
                WishVendorSuggestions.ScrollIntoView(WishVendorSuggestions.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when WishVendorSuggestions.SelectedItem is VendorOption option:
                PickVendor(option.Label);
                e.Handled = e.Key == Key.Enter;
                break;
            case Key.Escape:
                WishVendorPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void WishVendorSuggestion_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: VendorOption option })
            PickVendor(option.Label);
    }

    private void WishVendor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 点候选项时焦点不会离开输入框（列表不可获得焦点），这里只处理真的离开的情况。
        if (!WishVendorPopup.IsMouseOver)
            WishVendorPopup.IsOpen = false;
    }

    private void PickVendor(string vendor)
    {
        WishVendorPopup.IsOpen = false;
        WishVendor.Text = vendor;
        WishVendor.CaretIndex = vendor.Length;
        WishVendorPopup.IsOpen = false;
        WishVendor.Focus();
    }

    private string GuessVendor(string name) => NoteWishes.GuessVendor(name, _plugins.Select(p => p.Row));

    private void WishOpenUrl_Click(object sender, RoutedEventArgs e) => OpenWebPage(WishListing.WishUrl(WishUrl.Text));

    /// <summary>查价只是打开一次网页搜索，软件自己不联网抓价格。</summary>
    private void WishPriceSearch_Click(object sender, RoutedEventArgs e) =>
        OpenWebPage(PurchaseAdvisor.PriceSearchUrl(WishName.Text.Trim(), WishVendor.Text.Trim()));

    private void OpenWebPage(string url)
    {
        // 只打开网页链接，这个地址最终会交给系统执行。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText.Text = "这不是有效的网页链接";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "打不开链接：" + error.Message;
        }
    }

    private void WishBought_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWish == null)
            return;
        ScheduleItemSave();
        FlushItems();
        var item = _currentWish.Item;
        if (!TrySave(WishListing.Bought(item, DateOnly.FromDateTime(DateTime.Today))))
            return;

        WishList.SelectedItem = null;
        LoadItems();
        StatusText.Text = WishListing.BoughtText(item);
    }

    private void WishDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWish == null)
            return;
        FlushItems();
        var item = _currentWish.Item;
        if (!Dialogs.Confirm(this, "删除这款心愿？", WishListing.WishDeletePrompt(item), "删除", danger: true))
            return;
        DeleteItems([item], () => WishList.SelectedItem = null);
        StatusText.Text = $"已删除 {item.Name}";
    }

    private void DeleteItems(IReadOnlyCollection<WishItem> items, Action clearSelection)
    {
        try
        {
            _database.DeleteWishes(items.Select(i => i.Id));
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "删除失败：" + error.Message;
            return;
        }
        // 先取消等待中的保存，否则会往已删除的记录写。
        _itemSaveTimer.Stop();
        _currentWish = null;
        _currentPurchase = null;
        clearSelection();
        LoadItems();
    }

    // ---------- 已购账号 ----------

    private bool MatchesPurchase(object item)
    {
        return WishListing.MatchesPurchase(((WishViewModel)item).Row, AccountVendorFilter.SelectedItem as string ?? "",
            AccountStateFilter.SelectedItem as string ?? "", AccountSearchBox.Text);
    }

    private void AccountFilter_Changed(object sender, EventArgs e)
    {
        if (_loadingItem || _purchaseView == null)
            return;
        FlushItems();
        _purchaseView.Refresh();
        _platformView?.Refresh();
        UpdatePlatformEmpty();
        UpdateItemSummaries();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        PurchaseGrid.SelectAll();
        PurchaseGrid.Focus();
    }

    private void PurchaseGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlushItems();
        _currentPurchase = PurchaseGrid.SelectedItems.Count == 1 ? PurchaseGrid.SelectedItem as WishViewModel : null;
        ShowPurchaseDetail();
    }

    private void ShowPurchaseDetail()
    {
        var selected = PurchaseGrid.SelectedItems.Count;
        PurchasePanelEmpty.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        PurchasePanel.Visibility = _currentPurchase != null ? Visibility.Visible : Visibility.Collapsed;
        BatchPanel.Visibility = selected > 1 ? Visibility.Visible : Visibility.Collapsed;
        BatchTitle.Text = $"已选 {selected} 款";
        if (_currentPurchase == null)
            return;

        var item = _currentPurchase.Item;
        _loadingItem = true;
        PurchaseName.Text = item.Name;
        PurchaseVendor.Text = item.Vendor;
        var login = PurchaseLogin.Resolve(item, _platformAccounts);
        _shownLoginTarget = login.FromPlatform ? login.Platform : "";
        LoginFollow.IsChecked = login.FromPlatform;
        LoginOwn.IsChecked = !login.FromPlatform;
        LoginFollow.IsEnabled = login.Platform.Length > 0;
        PurchaseAccount.Text = login.Account;
        PurchasePassword.Secret = login.Password;
        PurchaseSerial.Secret = item.Serial;
        PurchaseCurrency.SelectedItem = item.Currency;
        PurchasePaid.Text = item.PaidPrice?.ToString("0.##") ?? "";
        PurchaseDate.Text = item.PurchasedOn?.ToString("yyyy-MM-dd") ?? "";
        PurchaseLicense.SelectedIndex = (int)item.LicenseKind;
        PurchaseActivatedOn.Text = item.ActivatedOn;
        PurchaseActivations.Text = item.ActivationsLeft?.ToString() ?? "";
        PurchaseInvoice.Text = item.Invoice;
        PurchaseReason.Text = item.Reason;
        PurchaseNote.Text = item.Note;
        _loadingItem = false;

        PurchaseLocked.Visibility = item.SecretsLocked || login.Locked ? Visibility.Visible : Visibility.Collapsed;
        PurchasePassword.IsEnabled = !login.Locked;
        PurchaseSerial.IsEnabled = !item.SecretsLocked;
        LoginHint.Text = DescribeLogin(item, login);
    }

    private string DescribeLogin(WishItem item, EffectiveLogin login) =>
        WishListing.DescribeLogin(item, login, _purchases.Select(p => p.Item));

    private void LoginMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingItem || _currentPurchase == null)
            return;
        // 先按原来的方式存下正在编辑的内容。
        ScheduleItemSave();
        FlushItems();
        var item = _currentPurchase.Item;
        var own = LoginOwn.IsChecked == true;
        if (own == item.OwnLogin)
            return;

        var platform = VendorCatalog.PlatformOf(item.Vendor);
        if (!own && platform.Length > 0)
        {
            _platformAccounts.TryGetValue(VendorCatalog.PlatformKey(platform), out var shared);
            if (WishListing.OfferAsPlatform(item, shared)
                && Dialogs.Confirm(this, "设为厂家账号？", WishListing.OfferAsPlatformPrompt(platform), "设为厂家账号"))
            {
                try
                {
                    _database.SavePlatformAccount(new PlatformAccount(platform, item.Account, item.Password, shared?.Note ?? ""));
                }
                catch (Microsoft.Data.Sqlite.SqliteException error)
                {
                    StatusText.Text = "保存失败：" + error.Message;
                    return;
                }
            }
        }

        // 切换时不删这一款自己存的账号，切回来还在。
        if (!TrySave(item with { OwnLogin = own }))
            return;
        LoadItems();
        StatusText.Text = WishListing.LoginModeChanged(item, own);
    }

    private void PurchaseField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingItem)
            ScheduleItemSave();
    }

    private void PurchaseSecret_Changed(object? sender, EventArgs e)
    {
        if (!_loadingItem)
            ScheduleItemSave();
    }

    private void SavePurchasePanel()
    {
        if (_currentPurchase == null)
            return;
        var old = _currentPurchase.Item;

        var (parsed, problems) = WishListing.ParsePurchase(old, new PurchaseFields(
            PurchaseName.Text, PurchaseSerial.Secret, PurchaseCurrency.SelectedItem as string, PurchasePaid.Text, PurchaseDate.Text,
            PurchaseLicense.SelectedIndex, PurchaseActivatedOn.Text, PurchaseActivations.Text, PurchaseInvoice.Text,
            PurchaseReason.Text, PurchaseNote.Text));

        var vendor = FillVendor(old, parsed.Name, PurchaseVendor.Text, out var guessed);
        if (guessed)
        {
            _loadingItem = true;
            PurchaseVendor.Text = vendor;
            _loadingItem = false;
        }

        // 账号框显示的是厂家账号时，改动写到厂家账号；显示的是这一款自己的，就写到这一款。
        var shownPlatform = _shownLoginTarget;
        var account = PurchaseAccount.Text.Trim();
        var password = PurchasePassword.Secret;
        var ownAccount = old.Account;
        var ownPassword = old.Password;
        if (shownPlatform.Length > 0)
        {
            _platformAccounts.TryGetValue(VendorCatalog.PlatformKey(shownPlatform), out var shared);
            if (WishListing.ChangedPlatform(shownPlatform, shared, account, password) is { } saved)
            {
                try
                {
                    _database.SavePlatformAccount(saved);
                    _platformAccounts[VendorCatalog.PlatformKey(shownPlatform)] = saved;
                }
                catch (Microsoft.Data.Sqlite.SqliteException error)
                {
                    StatusText.Text = "保存失败：" + error.Message;
                    return;
                }
            }
        }
        else
        {
            ownAccount = account;
            ownPassword = old.SecretsLocked ? old.Password : password;
        }

        var updated = parsed with { Vendor = vendor, Account = ownAccount, Password = ownPassword };
        if (!TrySave(updated))
            return;
        var login = PurchaseLogin.Resolve(updated, _platformAccounts);
        _currentPurchase.Update(updated, login);
        // 同平台的其他行显示的账号也要跟着变。
        foreach (var other in _purchases.Where(p => p != _currentPurchase))
            other.Update(other.Item, PurchaseLogin.Resolve(other.Item, _platformAccounts));
        // 厂家改了，账号框要换成新平台的内容。
        if ((login.FromPlatform ? login.Platform : "") != shownPlatform)
            ShowPurchaseDetail();
        else
            LoginHint.Text = DescribeLogin(updated, login);
        UpdateItemSummaries();
        StatusText.Text = WishListing.Saved(updated.Name, problems, guessed ? vendor : "", shownPlatform);
    }

    /// <summary>把这台电脑加到“激活在”，已有其他机器时保留。</summary>
    private void PurchaseThisMachine_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase == null)
            return;
        var text = WishListing.AddMachine(_currentPurchase.Item, PurchaseActivatedOn.Text, Environment.MachineName);
        if (text != PurchaseActivatedOn.Text.Trim())
            PurchaseActivatedOn.Text = text;
    }

    private void MigrationChecklist_Click(object sender, RoutedEventArgs e)
    {
        FlushItems();
        var text = LicenseMigration.BuildChecklist(_database.LoadWishes(), Environment.MachineName, DateTime.Now);
        Dialogs.ShowText(this, "换机清单", text, WishListing.ChecklistFileName(DateTime.Now),
            saved: () => RecordUsage(() => _database.RecordUsage(UsageKinds.ChecklistExport)));
    }

    private void CopyAccount_Click(object sender, RoutedEventArgs e)
    {
        if (PurchaseAccount.Text.Length > 0)
            StatusText.Text = SecureClipboard.Copy(PurchaseAccount.Text) ? "已复制账号，30 秒后自动清除" : "复制失败：" + SecureClipboard.LastError;
    }

    private void Secret_Copied(object? sender, EventArgs e)
    {
        var box = (SecretBox)sender!;
        StatusText.Text = box.CopyError == null ? $"已复制{box.Label}，30 秒后自动清除" : "复制失败：" + box.CopyError;
    }

    private void PurchaseBack_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase == null)
            return;
        ScheduleItemSave();
        FlushItems();
        var item = _currentPurchase.Item;
        if (!TrySave(item with { Status = WishStatus.Wished }))
            return;
        PurchaseGrid.SelectedItem = null;
        LoadItems();
        StatusText.Text = WishListing.BackText(item);
    }

    private void PurchaseDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase == null)
            return;
        FlushItems();
        var item = _currentPurchase.Item;
        if (!Dialogs.Confirm(this, "删除这条记录？", WishListing.PurchaseDeletePrompt(item), "删除", danger: true))
            return;
        DeleteItems([item], PurchaseGrid.UnselectAll);
        StatusText.Text = $"已删除 {item.Name}";
    }

    private List<WishItem> SelectedPurchases() =>
        PurchaseGrid.SelectedItems.Cast<WishViewModel>().Select(p => p.Item).ToList();

    private void BatchApply_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPurchases();
        var account = BatchAccount.Text.Trim();
        var password = BatchPassword.Secret;
        var serial = BatchSerial.Secret;
        var license = BatchLicense.SelectedIndex > 0 ? (LicenseKind?)BatchLicense.SelectedIndex : null;
        var activatedOn = BatchActivatedOn.Text.Trim();
        var hasLogin = account.Length > 0 || password.Length > 0 || serial.Length > 0;
        if (!hasLogin && license == null && activatedOn.Length == 0)
        {
            StatusText.Text = "至少填一项";
            return;
        }

        var overwritten = WishListing.Overwritten(selected, account, password, serial);
        if (overwritten > 0 && !Dialogs.Confirm(this, "覆盖已有内容？", WishListing.OverwritePrompt(selected.Count, overwritten), "覆盖", danger: true))
            return;

        LoginAssignment? assigned;
        try
        {
            assigned = WishListing.ApplyBatch(_database, selected.Select(i => i.Id).ToList(), account, password, serial, license, activatedOn);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }

        BatchAccount.Text = "";
        BatchPassword.Secret = "";
        BatchSerial.Secret = "";
        BatchLicense.SelectedIndex = 0;
        BatchActivatedOn.Text = "";
        PurchaseGrid.UnselectAll();
        LoadItems();
        StatusText.Text = WishListing.BatchSaved(selected.Count, assigned);
    }

    private void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPurchases();
        if (!Dialogs.Confirm(this, "删除选中的记录？", WishListing.BatchDeletePrompt(selected), "删除", danger: true))
            return;
        DeleteItems(selected, PurchaseGrid.UnselectAll);
        StatusText.Text = $"已删除 {selected.Count} 条记录";
    }

    // ---------- 用过的账号 ----------

    /// <summary>填过的账号（已购记录和厂家账号里的），用得多的排前面。只有账号，没有密码。</summary>
    private IReadOnlyList<string> KnownAccounts() => WishListing.KnownAccounts(
        _purchases.Concat(_wishes).Select(w => w.Item), _platformAccounts.Values, PurchaseAccount.Text, PlatformAccountBox.Text);

    /// <summary>账号框旁边的下拉：列出填过的账号，点一下填进这个框。</summary>
    private void PickAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox box } button)
            return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var accounts = KnownAccounts();
        if (accounts.Count == 0)
            menu.Items.Add(new MenuItem { Header = WishListing.NoAccounts, IsEnabled = false });
        foreach (var account in accounts)
        {
            // 用 TextBlock 当标题：账号里的下划线不会被当成快捷键吃掉。
            var item = new MenuItem { Header = new TextBlock { Text = account }, IsCheckable = false };
            System.Windows.Automation.AutomationProperties.SetName(item, account);
            if (string.Equals(account, box.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                item.FontWeight = FontWeights.SemiBold;
            item.Click += (_, _) =>
            {
                box.Text = account;
                box.Focus();
                box.CaretIndex = box.Text.Length;
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void BatchAdd_Click(object sender, RoutedEventArgs e)
    {
        FlushItems();
        var dialog = new BatchAddDialog(_database.LoadEffects().Where(p => !p.IsMissing).ToList(), _purchases.Select(p => p.Item).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            StatusText.Text = WishListing.AddPurchases(_database, dialog.Drafts, _plugins.Select(p => p.Row).ToList());
            LoadItems();
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
        }
    }

    private void ExportLogins_Click(object sender, RoutedEventArgs e)
    {
        FlushItems();
        if (!Dialogs.Confirm(this, "导出明文账号？", WishListing.ExportWarning, "继续导出"))
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出账号",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = WishListing.ExportFileName(DateTime.Now),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var items = WishListing.ExportItems(_database.LoadWishes(), _database.LoadPlatformAccounts());
            File.WriteAllText(dialog.FileName, PurchaseText.Format(items, out var skipped));
            StatusText.Text = WishListing.Exported(items.Count, skipped);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
    }
}
