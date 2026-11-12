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
    private const string SortByPriority = "按想要程度";
    private const string SortByPrice = "按价格";
    private const string SortByRecent = "最近添加";
    private const string AllAccountVendors = "全部厂家";
    private const string AllAccountStates = "全部";
    private const string WithLogin = "已填账号";
    private const string WithoutLogin = "未填账号";

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
    private readonly HashSet<long> _vendorCleared = [];

    private void InitializeItems()
    {
        var currencies = Money.Currencies.ToList();
        WishCurrency.ItemsSource = currencies;
        PurchaseCurrency.ItemsSource = currencies;
        var licenses = Enum.GetValues<LicenseKind>().Select(LicenseMigration.KindText).ToList();
        PurchaseLicense.ItemsSource = licenses;
        BatchLicense.ItemsSource = new[] { "不修改" }.Concat(licenses.Skip(1)).ToList();
        BatchLicense.SelectedIndex = 0;
        _loadingItem = true;
        WishSortBox.ItemsSource = new[] { SortByPriority, SortByPrice, SortByRecent };
        WishSortBox.SelectedIndex = 0;
        AccountStateFilter.ItemsSource = new[] { AllAccountStates, WithLogin, WithoutLogin };
        AccountStateFilter.SelectedIndex = 0;
        _loadingItem = false;
        _itemSaveTimer.Tick += (_, _) => FlushItems();
        LoadItems();
    }

    private void LoadItems()
    {
        var wishId = _currentWish?.Item.Id;
        var purchaseId = _currentPurchase?.Item.Id;

        var installed = _database.LoadPlugins().Where(p => !p.IsMissing).Select(p => Normalizer.Name(p.Name)).ToHashSet();
        _platformAccounts = PurchaseLogin.ByKey(_database.LoadPlatformAccounts());
        var all = _database.LoadWishes().Select(w => new WishViewModel(w, IsInstalled(w, installed), PurchaseLogin.Resolve(w, _platformAccounts))).ToList();
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

    /// <summary>插件名可能带厂家（FabFilter Pro-Q 3），也可能不带，两种都算已安装。</summary>
    private static bool IsInstalled(WishItem item, HashSet<string> installed)
    {
        var name = Normalizer.Name(item.Name);
        return installed.Contains(name) || (item.Vendor.Length > 0 && installed.Contains(Normalizer.Vendor(item.Vendor) + name));
    }

    private void UpdateItemSummaries()
    {
        CountWishes.Text = _wishes.Count > 0 ? _wishes.Count.ToString() : "";
        CountAccounts.Text = _purchases.Count > 0 ? _purchases.Count.ToString() : "";

        var total = Money.Total(_wishes.Select(w => (w.Item.Price, w.Item.Currency)));
        WishSubtitle.Text = _wishes.Count == 0
            ? "记下想买的插件、原因和价格，买了之后转到已购账号"
            : $"{_wishes.Count} 款" + (total.Length > 0 ? $"，全买下来约 {total}" : "");

        var withLogin = _purchases.Count(p => p.HasLogin);
        var paid = Money.Total(_purchases.Select(p => (p.Item.PaidPrice, p.Item.Currency)));
        AccountSubtitle.Text = (_purchases.Count == 0 ? "" : $"{_purchases.Count} 款，{withLogin} 款已填账号，")
            + (paid.Length > 0 ? $"已花 {paid}，" : "")
            + "密码和序列号加密保存在本机";

        var wishShown = _wishView?.Cast<object>().Any() ?? false;
        WishEmpty.Visibility = wishShown ? Visibility.Collapsed : Visibility.Visible;
        WishEmptyText.Text = _wishes.Count == 0 ? "还没有想买的插件，在上面输入名字加入" : "没有符合搜索的心愿";

        var purchaseShown = _purchaseView?.Cast<object>().Any() ?? false;
        PurchaseEmpty.Visibility = purchaseShown ? Visibility.Collapsed : Visibility.Visible;
        PurchaseEmptyText.Text = _purchases.Count == 0 ? "还没有已购记录，点右上角“批量添加”" : "没有符合筛选的记录";
    }

    private void ShowItemStatus()
    {
        if (_page == AppPage.Wishes)
        {
            StatusText.Text = _wishes.Count == 0 ? "" : $"心愿单 {_wishes.Count} 款";
            // 工程或插件可能在别的页面变过。
            ShowWishAdvice();
        }
        else if (_page == AppPage.Accounts)
            StatusText.Text = _purchases.Count == 0 ? "" : $"已购 {_purchases.Count} 款";
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

    private bool MatchesWish(object item)
    {
        var search = WishSearchBox.Text.Trim().ToLowerInvariant();
        return search.Length == 0 || search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(((WishViewModel)item).SearchText.Contains);
    }

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
            StatusText.Text = "请先输入插件名";
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
        StatusText.Text = $"已加入心愿单：{name}" + (added.Vendor.Length > 0 ? $"，厂家自动识别为 {added.Vendor}（不对可以直接改）" : "") + "，可以在右边补上原因和价格";
    }

    /// <summary>以前卸载过的插件：垃圾桶里的已卸载记录，或卸载历史（插件记录删了也能找到）。没卸载过返回 null。</summary>
    private (string Name, string Text)? FindRemembered(string name, IReadOnlyList<PluginRow> rows, IReadOnlyList<UninstallRecord> history)
    {
        var row = TrashMemory.Find(name, rows);
        var record = row != null ? UninstallHistory.ForPlugin(row, history) : UninstallHistory.Find(name, history, rows);
        if (row == null && record == null)
            return null;
        return (row?.Name ?? record!.Name, UninstallHistory.Reminder(row, record));
    }

    /// <summary>
    /// 要加入心愿单的插件里，有以前移进垃圾桶又卸载了的：先把当时写的原因和评价摆出来，确认还要加。
    /// 没有这种插件时直接返回 true。
    /// </summary>
    private bool ConfirmRememberedWishes(IReadOnlyList<string> names)
    {
        var rows = _plugins.Select(p => p.Row).ToList();
        var history = _database.LoadUninstallHistory();
        var found = names.Select(n => FindRemembered(n, rows, history)).OfType<(string Name, string Text)>().DistinctBy(f => f.Name).ToList();
        if (found.Count == 0)
            return true;
        var head = found.Count == 1
            ? $"你以前把 {found[0].Name} 移进垃圾桶，后来卸载了。"
            : $"下面 {found.Count} 款插件你以前都移进垃圾桶，后来卸载了。";
        var lines = string.Join("\n", found.Select(f => $"· {f.Name}：{f.Text}"));
        return Dialogs.Confirm(this, "以前不要过的插件", $"{head}\n\n{lines}\n\n还要加入心愿单吗？", "仍然加入");
    }

    /// <summary>心愿单详情顶上的提醒：这款以前移进垃圾桶并卸载过。</summary>
    private void ShowWishTrashNote(WishItem item)
    {
        var remembered = item.Status == WishStatus.Wished
            ? FindRemembered(item.Name, _plugins.Select(p => p.Row).ToList(), _database.LoadUninstallHistory())
            : null;
        WishTrashNote.Visibility = remembered != null ? Visibility.Visible : Visibility.Collapsed;
        WishTrashText.Text = remembered is { } r ? $"你以前把 {r.Name} 移进垃圾桶，后来卸载了。{r.Text}" : "";
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
        var categories = new[] { PurchaseAdvisor.AutoCategory }
            .Concat(PluginComparison.Categories(_plugins.Select(p => p.Row)).Select(c => c.Tag).Where(t => t != PluginComparison.Uncategorized))
            .ToList();
        if (item.Category.Length > 0 && !categories.Contains(item.Category))
            categories.Add(item.Category);
        WishCategory.ItemsSource = categories;
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
        var problems = new List<string>();
        var name = WishName.Text.Trim();
        if (name.Length == 0)
        {
            name = old.Name;
            problems.Add("插件名不能为空");
        }
        if (!Money.TryParse(WishPrice.Text, out var price))
        {
            price = old.Price;
            problems.Add("价格只能填数字");
        }
        if (!Money.TryParse(WishTarget.Text, out var target))
        {
            target = old.TargetPrice;
            problems.Add("目标价只能填数字");
        }
        if (!Money.TryParse(WishLowest.Text, out var lowest))
        {
            lowest = old.LowestPrice;
            problems.Add("历史最低价只能填数字");
        }
        var category = WishCategory.SelectedItem as string ?? PurchaseAdvisor.AutoCategory;

        var vendor = FillVendor(old, name, WishVendor.Text.Trim(), out var guessed);
        if (guessed)
        {
            _loadingItem = true;
            WishVendor.Text = vendor;
            _loadingItem = false;
        }
        var updated = old with
        {
            Name = name,
            Vendor = vendor,
            Currency = WishCurrency.SelectedItem as string ?? old.Currency,
            Price = price,
            TargetPrice = target,
            LowestPrice = lowest,
            Category = category == PurchaseAdvisor.AutoCategory ? "" : category,
            Priority = WishPriorityMustHave.IsChecked == true ? WishPriority.MustHave : WishPriority.CanWait,
            Reason = WishReason.Text,
            Url = WishUrl.Text.Trim(),
            Note = WishNote.Text,
        };
        if (!TrySave(updated))
            return;
        _currentWish.Update(updated);
        UpdateItemSummaries();
        ShowWishAdvice();
        StatusText.Text = problems.Count > 0 ? string.Join("，", problems) + "，这一项没有保存"
            : guessed ? $"已保存 {updated.Name}，厂家自动识别为 {vendor}（不对可以直接改）"
            : $"已保存 {updated.Name}";
    }

    /// <summary>
    /// 厂家留空时按插件名猜：先找已安装的同名插件，再查厂家目录。同名属于多个厂家时不填。
    /// 用户亲手清空的厂家不再自动填回去。
    /// </summary>
    private string FillVendor(WishItem old, string name, string vendor, out bool guessed)
    {
        guessed = false;
        if (vendor.Length > 0)
        {
            _vendorCleared.Remove(old.Id);
            return vendor;
        }
        if (old.Vendor.Length > 0 && name == old.Name)
        {
            _vendorCleared.Add(old.Id);
            return "";
        }
        if (_vendorCleared.Contains(old.Id))
            return "";
        var guess = GuessVendor(name);
        guessed = guess.Length > 0;
        return guess;
    }

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

    private string GuessVendor(string name)
    {
        var key = Normalizer.Name(name);
        var installed = _plugins.Select(p => p.Row).Where(p => p.Vendor.Length > 0 && Normalizer.Name(p.Name) == key)
            .Select(p => p.Vendor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (installed.Count == 1)
            return installed[0];
        if (installed.Count > 1)
            return "";
        return VendorCatalog.Guess(name) is { IsAmbiguous: false } guess ? guess.Vendor : "";
    }

    private void WishOpenUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = WishUrl.Text.Trim();
        if (!url.Contains("://", StringComparison.Ordinal) && url.Length > 0)
            url = "https://" + url;
        OpenWebPage(url);
    }

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
        var bought = item with
        {
            Status = WishStatus.Purchased,
            PurchasedOn = DateOnly.FromDateTime(DateTime.Today),
            PaidPrice = item.Price,
        };
        if (!TrySave(bought))
            return;

        WishList.SelectedItem = null;
        LoadItems();
        StatusText.Text = $"已把 {item.Name} 移到已购账号，可以去那里填账号密码";
    }

    private void WishDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWish == null)
            return;
        FlushItems();
        var item = _currentWish.Item;
        if (!Dialogs.Confirm(this, "删除这款心愿？", $"“{item.Name}”的原因、价格和备注都会删除，无法恢复。", "删除", danger: true))
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
        var purchase = (WishViewModel)item;
        if (AccountVendorFilter.SelectedItem is string vendor && vendor != AllAccountVendors && purchase.Item.Vendor != vendor)
            return false;
        switch (AccountStateFilter.SelectedItem as string)
        {
            case WithLogin when !purchase.HasLogin:
            case WithoutLogin when purchase.HasLogin:
                return false;
        }
        var search = AccountSearchBox.Text.Trim().ToLowerInvariant();
        return search.Length == 0 || search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(purchase.SearchText.Contains);
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

    private string DescribeLogin(WishItem item, EffectiveLogin login)
    {
        if (login.Platform.Length == 0)
            return "厂家没填，账号只存在这一款上。填上厂家后就能和同厂家的插件共用账号。";
        var sharing = _purchases.Count(p => !p.Item.OwnLogin && VendorCatalog.PlatformKey(VendorCatalog.PlatformOf(p.Item.Vendor)) == VendorCatalog.PlatformKey(login.Platform));
        if (login.FromPlatform)
        {
            var same = login.Platform == VendorCatalog.DisplayVendor(item.Vendor) ? "" : $"{VendorCatalog.DisplayVendor(item.Vendor)} 的插件用 {login.Platform} 账号登录。";
            return same + (login.HasAny
                ? $"这是 {login.Platform} 的厂家账号，{sharing} 款插件共用，在这里修改会一起生效。"
                : $"{login.Platform} 还没有厂家账号，在这里填写后，{sharing} 款同厂家的插件都会使用它。");
        }
        return $"只用于这一款，不影响 {login.Platform} 的厂家账号。";
    }

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
            var platformEmpty = shared == null || (shared.Account.Length == 0 && shared.Password.Length == 0 && !shared.Locked);
            var hasOwn = item.Account.Length > 0 || item.Password.Length > 0;
            if (platformEmpty && hasOwn && !item.SecretsLocked
                && Dialogs.Confirm(this, "设为厂家账号？", $"把这一款的账号密码设为 {platform} 的厂家账号吗？同厂家其余跟随厂家账号的插件也会使用它。", "设为厂家账号"))
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
        StatusText.Text = own ? $"{item.Name} 改为单独账号" : $"{item.Name} 改为使用 {platform} 的厂家账号";
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

        var problems = new List<string>();
        var name = PurchaseName.Text.Trim();
        if (name.Length == 0)
        {
            name = old.Name;
            problems.Add("插件名不能为空");
        }
        if (!Money.TryParse(PurchasePaid.Text, out var paid))
        {
            paid = old.PaidPrice;
            problems.Add("实付只能填数字");
        }
        var date = old.PurchasedOn;
        if (string.IsNullOrWhiteSpace(PurchaseDate.Text))
            date = null;
        else if (DateOnly.TryParse(PurchaseDate.Text.Trim(), out var parsed))
            date = parsed;
        else
            problems.Add("日期格式应为 2026-11-06");
        var activations = old.ActivationsLeft;
        if (string.IsNullOrWhiteSpace(PurchaseActivations.Text))
            activations = null;
        else if (int.TryParse(PurchaseActivations.Text.Trim(), out var left) && left >= 0)
            activations = left;
        else
            problems.Add("剩余激活次数只能填整数");

        var vendor = FillVendor(old, name, PurchaseVendor.Text.Trim(), out var guessed);
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
            var locked = shared?.Locked ?? false;
            if ((shared?.Account ?? "") != account || (!locked && (shared?.Password ?? "") != password))
            {
                try
                {
                    var saved = new PlatformAccount(shownPlatform, account, locked ? "" : password, shared?.Note ?? "", locked);
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

        var updated = old with
        {
            Name = name,
            Vendor = vendor,
            Account = ownAccount,
            Password = ownPassword,
            Serial = old.SecretsLocked ? old.Serial : PurchaseSerial.Secret,
            Currency = PurchaseCurrency.SelectedItem as string ?? old.Currency,
            PaidPrice = paid,
            PurchasedOn = date,
            LicenseKind = (LicenseKind)Math.Max(PurchaseLicense.SelectedIndex, 0),
            ActivatedOn = PurchaseActivatedOn.Text.Trim(),
            Invoice = PurchaseInvoice.Text.Trim(),
            ActivationsLeft = activations,
            Reason = PurchaseReason.Text,
            Note = PurchaseNote.Text,
        };
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
        StatusText.Text = problems.Count > 0 ? string.Join("，", problems) + "，这一项没有保存"
            : guessed ? $"已保存 {updated.Name}，厂家自动识别为 {vendor}（不对可以直接改）"
            : shownPlatform.Length > 0 ? $"已保存 {updated.Name}，账号密码存在 {shownPlatform} 的厂家账号里"
            : $"已保存 {updated.Name}";
    }

    /// <summary>把这台电脑加到“激活在”，已有其他机器时保留。</summary>
    private void PurchaseThisMachine_Click(object sender, RoutedEventArgs e)
    {
        var machine = Environment.MachineName;
        var current = PurchaseActivatedOn.Text.Trim();
        if (_currentPurchase != null && LicenseMigration.IsActivatedOn(_currentPurchase.Item with { ActivatedOn = current }, machine))
            return;
        PurchaseActivatedOn.Text = current.Length > 0 ? $"{current}，{machine}" : machine;
    }

    private void MigrationChecklist_Click(object sender, RoutedEventArgs e)
    {
        FlushItems();
        var text = LicenseMigration.BuildChecklist(_database.LoadWishes(), Environment.MachineName, DateTime.Now);
        Dialogs.ShowText(this, "换机清单", text, $"WhatPlugin-换机清单-{DateTime.Now:yyyyMMdd}.txt",
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
        StatusText.Text = $"已把 {item.Name} 移回心愿单" + (item.HasLogin ? "，账号密码仍然保留" : "");
    }

    private void PurchaseDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPurchase == null)
            return;
        FlushItems();
        var item = _currentPurchase.Item;
        var message = item.HasLogin
            ? $"“{item.Name}”保存的账号、密码和序列号都会删除，无法恢复。"
            : $"“{item.Name}”的记录会删除，无法恢复。";
        if (!Dialogs.Confirm(this, "删除这条记录？", message, "删除", danger: true))
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

        // 厂家账号不会被覆盖（不一样时存成单独账号），这里只数单独账号和序列号。
        var overwritten = selected.Count(i =>
            (i.OwnLogin && account.Length > 0 && i.Account.Length > 0 && i.Account != account)
            || (i.OwnLogin && password.Length > 0 && (i.Password.Length > 0 || i.SecretsLocked) && i.Password != password)
            || (serial.Length > 0 && (i.Serial.Length > 0 || i.SecretsLocked) && i.Serial != serial));
        if (overwritten > 0 && !Dialogs.Confirm(this, "覆盖已有内容？",
                $"选中的 {selected.Count} 款里，有 {overwritten} 款已经填了不一样的内容，保存后旧内容无法找回。", "覆盖", danger: true))
            return;

        LoginAssignment? assigned = null;
        try
        {
            if (hasLogin)
                assigned = _database.SetLogins(selected.Select(i => i.Id), account, password, serial);
            if (license != null || activatedOn.Length > 0)
            {
                // SetLogins 刚改过账号，这里要读最新的，否则会写回旧内容。
                var ids = selected.Select(i => i.Id).ToHashSet();
                foreach (var item in _database.LoadWishes().Where(w => ids.Contains(w.Id)))
                {
                    _database.SaveWish(item with
                    {
                        LicenseKind = license ?? item.LicenseKind,
                        ActivatedOn = activatedOn.Length > 0 ? activatedOn : item.ActivatedOn,
                    });
                }
            }
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
        StatusText.Text = $"已保存到 {selected.Count} 款插件" + DescribeAssignment(assigned?.NewPlatforms, assigned?.Own ?? 0);
    }

    private void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPurchases();
        var withLogin = selected.Count(i => i.HasLogin);
        var message = $"会删除这 {selected.Count} 条记录" + (withLogin > 0 ? $"，其中 {withLogin} 条保存的账号密码也一起删除" : "") + "，无法恢复。";
        if (!Dialogs.Confirm(this, "删除选中的记录？", message, "删除", danger: true))
            return;
        DeleteItems(selected, PurchaseGrid.UnselectAll);
        StatusText.Text = $"已删除 {selected.Count} 条记录";
    }

    private void BatchAdd_Click(object sender, RoutedEventArgs e)
    {
        FlushItems();
        var dialog = new BatchAddDialog(_database.LoadEffects().Where(p => !p.IsMissing).ToList(), _purchases.Select(p => p.Item).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            // 粘贴的文本常常没写厂家，按插件名补上，才能归到厂家账号下。
            var guessedCount = 0;
            var drafts = dialog.Drafts.Select(d =>
            {
                if (d.Vendor.Length > 0)
                    return d;
                var vendor = GuessVendor(d.Name);
                if (vendor.Length > 0)
                    guessedCount++;
                return d with { Vendor = vendor };
            }).ToList();
            var result = _database.UpsertPurchased(drafts);
            LoadItems();
            StatusText.Text = $"新增 {result.Added} 款，更新 {result.Updated} 款"
                + (guessedCount > 0 ? $"，{guessedCount} 款按插件名自动填了厂家" : "")
                + DescribeAssignment(result.NewPlatforms, 0);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
        }
    }

    private static string DescribeAssignment(IReadOnlyList<string>? newPlatforms, int own)
    {
        var text = newPlatforms is { Count: > 0 } ? $"；新建厂家账号：{string.Join("、", newPlatforms)}" : "";
        if (own > 0)
            text += $"；{own} 款和已有的厂家账号不一样，存成了单独账号";
        return text;
    }

    private void ExportLogins_Click(object sender, RoutedEventArgs e)
    {
        FlushItems();
        if (!Dialogs.Confirm(this, "导出明文账号？",
                "导出的文件里账号、密码、序列号都是明文，任何拿到文件的人都能看到。\n\n这个功能用于换电脑或重装系统：加密内容绑定当前 Windows 账号，重装后解不开。迁移完请删除这个文件。",
                "继续导出"))
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出账号",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"WhatPlugin-账号-{DateTime.Now:yyyyMMdd}.txt",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            // 跟随厂家的插件导出厂家账号，这样导入后能重新归到厂家账号下。
            var platforms = PurchaseLogin.ByKey(_database.LoadPlatformAccounts());
            var items = _database.LoadWishes()
                .Select(w => (Item: w, Login: PurchaseLogin.Resolve(w, platforms)))
                .Where(x => x.Login.HasAny || x.Item.Serial.Length > 0 || x.Item.SecretsLocked)
                .Select(x => x.Item with
                {
                    Account = x.Login.Account,
                    Password = x.Login.Password,
                    SecretsLocked = x.Item.SecretsLocked || x.Login.Locked,
                })
                .ToList();
            File.WriteAllText(dialog.FileName, PurchaseText.Format(items, out var skipped));
            StatusText.Text = $"已导出 {items.Count - skipped.Count} 款的账号" + (skipped.Count > 0 ? $"，{skipped.Count} 款无法导出：{string.Join("、", skipped)}" : "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
    }
}
