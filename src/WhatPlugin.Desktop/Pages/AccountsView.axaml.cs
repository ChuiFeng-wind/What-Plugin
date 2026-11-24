using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 已购账号：买过的插件、账号密码序列号和授权情况。同厂家的插件默认共用一个厂家账号。
/// 密码和序列号加密保存在本机（Windows 用 DPAPI，Mac 的密钥在钥匙串里），不进搜索，也不写日志。
/// </summary>
public partial class AccountsView : UserControl
{
    private sealed record PurchaseSnapshot(PurchaseFields Fields, string Vendor, string Account, string Password);

    private readonly PluginDatabase _database;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly VendorFiller _vendorFiller = new();
    private readonly StackPanel _actions;
    private List<WishItem> _items = [];
    private List<WishRow> _purchases = [];
    private Dictionary<string, PlatformAccount> _platformAccounts = [];
    private List<PlatformEntry> _platforms = [];
    private IReadOnlyList<PluginRow> _effects = [];
    private HashSet<string> _installed = [];
    private WishRow? _shownPurchase;
    private PurchaseSnapshot? _purchaseSnapshot;
    // 详情里账号框当前显示的是哪个平台的账号；厂家改了之后不能把旧平台的内容存到新平台。
    private string _shownLoginTarget = "";
    private PlatformEntry? _shownPlatform;
    private (string Account, string Password, string Note)? _platformSnapshot;
    private bool _loading;
    private bool _building;
    private string _listedSearch = "";

    public event Action<string>? StatusChanged;

    public event Action<string>? SubtitleChanged;

    /// <summary>已购或心愿单的数量变了，侧边栏的数字要更新。</summary>
    public event Action? CountsChanged;

    /// <summary>对话框。测试换成按脚本回答的实现。</summary>
    public IDialogs Dialogs { get; set; }

    /// <summary>这台电脑的名字（“激活在”的“本机”和换机清单用）。</summary>
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>标题右边的“换机清单”“导出账号”“批量添加”。</summary>
    public Control Actions => _actions;

    // 设计器需要无参构造函数。
    public AccountsView() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db"))) { }

    public AccountsView(PluginDatabase database)
    {
        _database = database;
        InitializeComponent();
        Dialogs = new WindowDialogs(() => TopLevel.GetTopLevel(this) as Window);
        _saveTimer.Tick += (_, _) => Flush();

        _actions = new StackPanel { Orientation = Orientation.Horizontal };
        _actions.Children.Add(ActionButton(MaterialIconKind.LaptopAccount, "换机清单", "列出每款插件的授权方式和激活在哪台电脑，换电脑前照着停用、迁移", () => _ = ChecklistAsync()));
        _actions.Children.Add(ActionButton(MaterialIconKind.Export, "导出账号", "把账号密码导出成明文文件，用于换电脑或重装系统", () => _ = ExportAsync()));
        _actions.Children.Add(ActionButton(MaterialIconKind.PlaylistPlus, "批量添加", "从已安装的插件里勾选，或粘贴表格", () => _ = BatchAddAsync(), primary: true));

        PurchaseLocked.Text = WishListing.PurchaseLockedText;
        PlatformLocked.Text = WishListing.PlatformLockedText;
        CurrencyBox.ItemsSource = Money.Currencies;
        LicenseBox.ItemsSource = WishListing.LicenseOptions;
        BatchLicense.ItemsSource = WishListing.BatchLicenseOptions;
        BatchLicense.SelectedIndex = 0;
        StateFilter.ItemsSource = WishListing.StateOptions;
        StateFilter.SelectedIndex = 0;
        ShowEmptyText();

        ModePlatforms.IsCheckedChanged += (_, _) => ModeChanged();
        SearchBox.TextChanged += (_, _) =>
        {
            if ((SearchBox.Text ?? "") == _listedSearch)
                return;
            Flush();
            ShowLists();
        };
        VendorFilter.SelectionChanged += (_, _) =>
        {
            if (_building)
                return;
            Flush();
            ShowLists();
        };
        StateFilter.SelectionChanged += (_, _) =>
        {
            Flush();
            ShowLists();
        };
        SelectAllButton.Click += (_, _) =>
        {
            PurchaseList.SelectAll();
            PurchaseList.Focus();
        };
        ILokFillButton.Click += (_, _) => _ = ILokFillAsync();

        PurchaseList.SelectionChanged += (_, _) =>
        {
            if (_building)
                return;
            // 切换前先保存，否则等待中的内容会写到下一款上。
            Flush();
            ShowPurchaseDetail();
        };
        PlatformList.SelectionChanged += (_, _) =>
        {
            if (_building)
                return;
            Flush();
            ShowPlatformDetail(PlatformList.SelectedItem as PlatformEntry);
        };

        foreach (var box in new[] { NameBox, VendorBox, AccountBox, ActivationsBox, ActivatedOnBox, InvoiceBox, PaidBox, DateBox, ReasonBox, NoteBox })
            box.TextChanged += (_, _) => PurchaseChanged();
        PasswordBox.Changed += PurchaseChanged;
        SerialBox.Changed += PurchaseChanged;
        LicenseBox.SelectionChanged += (_, _) => PurchaseChanged();
        CurrencyBox.SelectionChanged += (_, _) => PurchaseChanged();
        LoginOwn.IsCheckedChanged += (_, _) =>
        {
            if (!_loading && _shownPurchase != null && LoginOwn.IsChecked is { } own)
                _ = SetLoginOwn(own);
        };
        PlatformAccountBox.TextChanged += (_, _) => PlatformChanged();
        PlatformNote.TextChanged += (_, _) => PlatformChanged();
        PlatformPassword.Changed += PlatformChanged;

        foreach (var secret in new[] { PasswordBox, SerialBox, PlatformPassword })
        {
            var box = secret;
            box.Copied += () => StatusChanged?.Invoke(box.CopyError == null ? $"已复制{box.Label}，30 秒后自动清除" : "复制失败：" + box.CopyError);
        }
        CopyAccountButton.Click += (_, _) => _ = CopyText(AccountBox.Text ?? "");
        CopyPlatformAccountButton.Click += (_, _) => _ = CopyText(PlatformAccountBox.Text ?? "");
        PickAccountButton.Click += (_, _) => PickAccount(PickAccountButton, AccountBox);
        BatchPickAccountButton.Click += (_, _) => PickAccount(BatchPickAccountButton, BatchAccount);
        PlatformPickAccountButton.Click += (_, _) => PickAccount(PlatformPickAccountButton, PlatformAccountBox);
        ThisMachineButton.Click += (_, _) => AddThisMachine();

        BackButton.Click += (_, _) => MoveBackToWishes();
        DeleteButton.Click += (_, _) => _ = DeleteShownAsync();
        BatchApplyButton.Click += (_, _) => _ = BatchApplyAsync();
        BatchDeleteButton.Click += (_, _) => _ = BatchDeleteAsync();
        PlatformDeleteButton.Click += (_, _) => _ = DeletePlatformAsync();

        Reload();
    }

    public bool ShowingPlatforms => ModePlatforms.IsChecked == true;

    public IReadOnlyList<WishRow> Rows => _purchases;

    public IReadOnlyList<WishRow> ShownRows => PurchaseList.ItemsSource as IReadOnlyList<WishRow> ?? [];

    public IReadOnlyList<PlatformEntry> Platforms => _platforms;

    public WishRow? Shown => _shownPurchase;

    public PlatformEntry? ShownPlatform => _shownPlatform;

    public string Status => WishListing.PurchasesStatus(_purchases.Count);

    public bool HasPendingSave => _saveTimer.IsEnabled;

    /// <summary>填过的账号，账号框旁边的下拉里列出。</summary>
    public IReadOnlyList<string> KnownAccounts =>
        WishListing.KnownAccounts(_items, _platformAccounts.Values, AccountBox.Text ?? "", PlatformAccountBox.Text ?? "");

    public void ShowPlatforms(bool platforms)
    {
        if (platforms)
            ModePlatforms.IsChecked = true;
        else
            ModePurchases.IsChecked = true;
    }

    /// <summary>按名字选中（测试和跳转用）。</summary>
    public void Select(params string[] names)
    {
        Flush();
        _building = true;
        PurchaseList.SelectedItems?.Clear();
        foreach (var row in ShownRows.Where(r => names.Contains(r.Name)))
            PurchaseList.SelectedItems?.Add(row);
        _building = false;
        ShowPurchaseDetail();
    }

    public void SelectPlatform(string platform) =>
        PlatformList.SelectedItem = (PlatformList.ItemsSource as IReadOnlyList<PlatformEntry>)?.FirstOrDefault(p => p.Platform == platform);

    // ---------- 读取和列表 ----------

    /// <summary>重新读已购记录和厂家账号，尽量保留选中的。</summary>
    public void Reload()
    {
        Flush();
        _effects = _database.LoadEffects();
        _installed = WishListing.InstalledNames(_database.LoadPlugins());
        _platformAccounts = PurchaseLogin.ByKey(_database.LoadPlatformAccounts());
        _items = _database.LoadWishes();
        RebuildRows();
        _building = true;
        var vendor = VendorFilter.SelectedItem as string;
        var vendors = WishListing.VendorOptions(_purchases);
        VendorFilter.ItemsSource = vendors;
        VendorFilter.SelectedItem = vendor != null && vendors.Contains(vendor) ? vendor : vendors[0];
        _building = false;
        ShowLists(redetail: true);
    }

    private void RebuildRows()
    {
        _purchases = WishListing.Rows(_items.Where(w => w.Status == WishStatus.Purchased), _installed, _platformAccounts);
        _platforms = PlatformListing.Build(_purchases.Select(p => p.Item), _platformAccounts);
        SubtitleChanged?.Invoke(WishListing.AccountSubtitle(_purchases));
    }

    /// <summary>按筛选重新列出，保留选中的。redetail 为 false 时不动右边正在编辑的内容。</summary>
    private void ShowLists(bool redetail = true)
    {
        _listedSearch = SearchBox.Text ?? "";
        var selectedIds = (PurchaseList.SelectedItems?.Cast<WishRow>() ?? []).Select(r => r.Item.Id).ToHashSet();
        var selectedPlatform = (PlatformList.SelectedItem as PlatformEntry)?.Platform;

        var vendor = VendorFilter.SelectedItem as string ?? WishListing.AllVendors;
        var state = StateFilter.SelectedItem as string ?? WishListing.AllStates;
        var shown = _purchases.Where(r => WishListing.MatchesPurchase(r, vendor, state, _listedSearch)).ToList();
        var platforms = _platforms.Where(p => WishListing.Matches(p.SearchText, _listedSearch)).ToList();

        _building = true;
        PurchaseList.ItemsSource = shown;
        foreach (var row in shown.Where(r => selectedIds.Contains(r.Item.Id)))
            PurchaseList.SelectedItems?.Add(row);
        PlatformList.ItemsSource = platforms;
        PlatformList.SelectedItem = platforms.FirstOrDefault(p => p.Platform == selectedPlatform);
        _building = false;

        PurchaseEmpty.IsVisible = shown.Count == 0;
        PurchaseEmptyText.Text = WishListing.PurchaseEmptyText(_purchases.Count);
        PlatformEmpty.IsVisible = platforms.Count == 0;
        PlatformEmptyText.Text = WishListing.PlatformEmptyText(_platforms.Count);
        if (redetail)
        {
            if (ShowingPlatforms)
                ShowPlatformDetail(PlatformList.SelectedItem as PlatformEntry);
            else
                ShowPurchaseDetail();
        }
    }

    private void ModeChanged()
    {
        Flush();
        var platforms = ShowingPlatforms;
        PlatformView.IsVisible = platforms;
        PurchaseView.IsVisible = !platforms;
        VendorFilter.IsVisible = !platforms;
        StateFilter.IsVisible = !platforms;
        SelectAllButton.IsVisible = !platforms;
        ILokFillButton.IsVisible = platforms;
        SearchBox.PlaceholderText = platforms ? "搜索厂家、账号或插件名" : "搜索插件名、厂家或账号";
        ShowEmptyText();
        // 在插件视图改过的内容要反映到厂家账号里，反过来也一样。
        Reload();
    }

    private void ShowEmptyText() => DetailEmptyText.Text = ShowingPlatforms
        ? "选择一个厂家账号\n\n同一个厂家的插件默认共用这里的账号密码。个别需要分开的，在插件详情里改成“单独账号”。"
        : $"选择一款查看账号\n\n按住 {AppPlatform.CommandKeyName} 或 Shift 多选，或者先按厂家筛选再点“全选”，可以一次填同一个账号";

    // ---------- 一款插件 ----------

    private void ShowPurchaseDetail()
    {
        if (ShowingPlatforms)
            return;
        var selected = PurchaseList.SelectedItems?.Count ?? 0;
        _shownPurchase = selected == 1 ? PurchaseList.SelectedItem as WishRow : null;
        _shownPlatform = null;
        DetailEmpty.IsVisible = selected == 0;
        PurchasePanel.IsVisible = _shownPurchase != null;
        BatchPanel.IsVisible = selected > 1;
        PlatformPanel.IsVisible = false;
        BatchTitle.Text = $"已选 {selected} 款";
        if (_shownPurchase == null)
        {
            _purchaseSnapshot = null;
            return;
        }

        var item = _shownPurchase.Item;
        var login = PurchaseLogin.Resolve(item, _platformAccounts);
        _shownLoginTarget = login.FromPlatform ? login.Platform : "";
        _loading = true;
        NameBox.Text = item.Name;
        VendorBox.Text = item.Vendor;
        LoginFollow.IsChecked = login.FromPlatform;
        LoginOwn.IsChecked = !login.FromPlatform;
        LoginFollow.IsEnabled = login.Platform.Length > 0;
        AccountBox.Text = login.Account;
        PasswordBox.Secret = login.Password;
        SerialBox.Secret = item.Serial;
        CurrencyBox.SelectedItem = Money.Currencies.Contains(item.Currency) ? item.Currency : Money.Currencies[0];
        PaidBox.Text = WishListing.FormatAmount(item.PaidPrice);
        DateBox.Text = item.PurchasedOn?.ToString("yyyy-MM-dd") ?? "";
        LicenseBox.SelectedIndex = (int)item.LicenseKind;
        ActivatedOnBox.Text = item.ActivatedOn;
        ActivationsBox.Text = item.ActivationsLeft?.ToString() ?? "";
        InvoiceBox.Text = item.Invoice;
        ReasonBox.Text = item.Reason;
        NoteBox.Text = item.Note;
        _purchaseSnapshot = CurrentPurchase();
        _loading = false;

        PurchaseLocked.IsVisible = item.SecretsLocked || login.Locked;
        PasswordBox.IsEnabled = !login.Locked;
        SerialBox.IsEnabled = !item.SecretsLocked;
        LoginHint.Text = WishListing.DescribeLogin(item, login, _purchases.Select(p => p.Item));
    }

    private PurchaseSnapshot CurrentPurchase() => new(
        new PurchaseFields(NameBox.Text ?? "", SerialBox.Secret, CurrencyBox.SelectedItem as string, PaidBox.Text ?? "", DateBox.Text ?? "",
            LicenseBox.SelectedIndex, ActivatedOnBox.Text ?? "", ActivationsBox.Text ?? "", InvoiceBox.Text ?? "", ReasonBox.Text ?? "", NoteBox.Text ?? ""),
        VendorBox.Text ?? "", AccountBox.Text ?? "", PasswordBox.Secret);

    // TextChanged 是稍后才触发的：和显示时的内容一样就不算修改。
    private void PurchaseChanged()
    {
        if (_loading || _shownPurchase == null || _purchaseSnapshot == null || ShowingPlatforms)
            return;
        if (CurrentPurchase() != _purchaseSnapshot)
            ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>把等待中的修改写进去（切换、离开页面或关闭前调用）。</summary>
    public void Flush()
    {
        var pending = _saveTimer.IsEnabled;
        _saveTimer.Stop();
        if (!pending)
            return;
        SavePurchase();
        SavePlatform();
    }

    private void SavePurchase()
    {
        if (_shownPurchase == null || ShowingPlatforms)
            return;
        var old = _shownPurchase.Item;
        var current = CurrentPurchase();
        var (parsed, problems) = WishListing.ParsePurchase(old, current.Fields);
        var vendor = _vendorFiller.Fill(old, parsed.Name, current.Vendor, _effects, out var guessed);

        // 账号框显示的是厂家账号时，改动写到厂家账号；显示的是这一款自己的，就写到这一款。
        var shownPlatform = _shownLoginTarget;
        var account = current.Account.Trim();
        var ownAccount = old.Account;
        var ownPassword = old.Password;
        try
        {
            if (shownPlatform.Length > 0)
            {
                _platformAccounts.TryGetValue(VendorCatalog.PlatformKey(shownPlatform), out var shared);
                if (WishListing.ChangedPlatform(shownPlatform, shared, account, current.Password) is { } saved)
                {
                    _database.SavePlatformAccount(saved);
                    _platformAccounts[VendorCatalog.PlatformKey(shownPlatform)] = saved;
                }
            }
            else
            {
                ownAccount = account;
                ownPassword = old.SecretsLocked ? old.Password : current.Password;
            }
            var updated = parsed with { Vendor = vendor, Account = ownAccount, Password = ownPassword };
            _database.SaveWish(updated);
            _database.RecordAction();
            _items[_items.FindIndex(i => i.Id == updated.Id)] = updated;
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }

        if (guessed)
        {
            _loading = true;
            VendorBox.Text = vendor;
            _loading = false;
        }
        _purchaseSnapshot = CurrentPurchase() with { Vendor = guessed ? vendor : current.Vendor };
        RebuildRows();
        ShowLists(redetail: false);
        var row = _purchases.First(r => r.Item.Id == old.Id);
        _shownPurchase = row;
        // 厂家改了，账号框要换成新平台的内容。
        if ((row.Login.FromPlatform ? row.Login.Platform : "") != shownPlatform)
            ShowPurchaseDetail();
        else
            LoginHint.Text = WishListing.DescribeLogin(row.Item, row.Login, _purchases.Select(p => p.Item));
        StatusChanged?.Invoke(WishListing.Saved(row.Item.Name, problems, guessed ? vendor : "", shownPlatform));
    }

    /// <summary>改成单独账号，或改回跟随厂家账号。厂家账号还空着时问要不要把这一款的设为厂家账号。</summary>
    public async Task SetLoginOwn(bool own)
    {
        if (_shownPurchase == null)
            return;
        // 先按原来的方式存下正在编辑的内容。
        ScheduleSave();
        Flush();
        if (_shownPurchase == null)
            return;
        var item = _shownPurchase.Item;
        if (own == item.OwnLogin)
            return;

        var platform = VendorCatalog.PlatformOf(item.Vendor);
        try
        {
            if (!own && platform.Length > 0)
            {
                _platformAccounts.TryGetValue(VendorCatalog.PlatformKey(platform), out var shared);
                if (WishListing.OfferAsPlatform(item, shared)
                    && await Dialogs.Confirm("设为厂家账号？", WishListing.OfferAsPlatformPrompt(platform), "设为厂家账号"))
                    _database.SavePlatformAccount(new PlatformAccount(platform, item.Account, item.Password, shared?.Note ?? ""));
            }
            // 切换时不删这一款自己存的账号，切回来还在。
            _database.SaveWish(item with { OwnLogin = own });
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        Reload();
        StatusChanged?.Invoke(WishListing.LoginModeChanged(item, own));
    }

    /// <summary>把这台电脑加到“激活在”，已有其他机器时保留。</summary>
    public void AddThisMachine()
    {
        if (_shownPurchase == null)
            return;
        var text = WishListing.AddMachine(_shownPurchase.Item, ActivatedOnBox.Text ?? "", MachineName);
        if (text != (ActivatedOnBox.Text ?? "").Trim())
            ActivatedOnBox.Text = text;
    }

    public void MoveBackToWishes()
    {
        if (_shownPurchase == null)
            return;
        ScheduleSave();
        Flush();
        var item = _shownPurchase.Item;
        try
        {
            _database.SaveWish(item with { Status = WishStatus.Wished });
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        ClearSelection();
        Reload();
        StatusChanged?.Invoke(WishListing.BackText(item));
        CountsChanged?.Invoke();
    }

    public async Task DeleteShownAsync()
    {
        if (_shownPurchase == null)
            return;
        Flush();
        var item = _shownPurchase.Item;
        if (!await Dialogs.Confirm("删除这条记录？", WishListing.PurchaseDeletePrompt(item), "删除", danger: true))
            return;
        if (DeleteItems([item]))
            StatusChanged?.Invoke($"已删除 {item.Name}");
    }

    private bool DeleteItems(IReadOnlyCollection<WishItem> items)
    {
        try
        {
            _database.DeleteWishes(items.Select(i => i.Id));
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("删除失败：" + error.Message);
            return false;
        }
        // 先取消等待中的保存，否则会往已删除的记录写。
        _saveTimer.Stop();
        ClearSelection();
        Reload();
        CountsChanged?.Invoke();
        return true;
    }

    private void ClearSelection()
    {
        _building = true;
        PurchaseList.SelectedItems?.Clear();
        _building = false;
        _shownPurchase = null;
        _purchaseSnapshot = null;
    }

    // ---------- 多选 ----------

    private List<WishItem> SelectedItems() => (PurchaseList.SelectedItems?.Cast<WishRow>() ?? []).Select(r => r.Item).ToList();

    /// <summary>批量填写（测试用）：和在多选面板里填好再点保存一样。</summary>
    public void SetBatch(string account, string password, string serial, int license = 0, string activatedOn = "")
    {
        BatchAccount.Text = account;
        BatchPassword.Secret = password;
        BatchSerial.Secret = serial;
        BatchLicense.SelectedIndex = license;
        BatchActivatedOn.Text = activatedOn;
    }

    public async Task BatchApplyAsync()
    {
        var selected = SelectedItems();
        var account = (BatchAccount.Text ?? "").Trim();
        var password = BatchPassword.Secret;
        var serial = BatchSerial.Secret;
        LicenseKind? license = BatchLicense.SelectedIndex > 0 ? (LicenseKind)BatchLicense.SelectedIndex : null;
        var activatedOn = (BatchActivatedOn.Text ?? "").Trim();
        if (account.Length == 0 && password.Length == 0 && serial.Length == 0 && license == null && activatedOn.Length == 0)
        {
            StatusChanged?.Invoke("至少填一项");
            return;
        }
        var overwritten = WishListing.Overwritten(selected, account, password, serial);
        if (overwritten > 0 && !await Dialogs.Confirm("覆盖已有内容？", WishListing.OverwritePrompt(selected.Count, overwritten), "覆盖", danger: true))
            return;

        LoginAssignment? assigned;
        try
        {
            assigned = WishListing.ApplyBatch(_database, selected.Select(i => i.Id).ToList(), account, password, serial, license, activatedOn);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        SetBatch("", "", "");
        ClearSelection();
        Reload();
        StatusChanged?.Invoke(WishListing.BatchSaved(selected.Count, assigned));
    }

    public async Task BatchDeleteAsync()
    {
        var selected = SelectedItems();
        if (selected.Count == 0 || !await Dialogs.Confirm("删除选中的记录？", WishListing.BatchDeletePrompt(selected), "删除", danger: true))
            return;
        if (DeleteItems(selected))
            StatusChanged?.Invoke($"已删除 {selected.Count} 条记录");
    }

    public async Task BatchAddAsync()
    {
        Flush();
        var drafts = await Dialogs.BatchAdd(_effects.Where(p => !p.IsMissing).ToList(), _purchases.Select(p => p.Item).ToList());
        if (drafts == null || drafts.Count == 0)
            return;
        try
        {
            var status = WishListing.AddPurchases(_database, drafts, _effects);
            Reload();
            StatusChanged?.Invoke(status);
            CountsChanged?.Invoke();
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
        }
    }

    // ---------- 账号 ----------

    private async Task CopyText(string text)
    {
        if (text.Length == 0)
            return;
        var error = await SecureClipboard.Copy(TopLevel.GetTopLevel(this), text);
        StatusChanged?.Invoke(error == null ? "已复制账号，30 秒后自动清除" : "复制失败：" + error);
    }

    /// <summary>账号框旁边的下拉：列出填过的账号，点一下填进这个框。</summary>
    private void PickAccount(Control anchor, TextBox box)
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        var accounts = KnownAccounts;
        if (accounts.Count == 0)
            menu.Items.Add(new MenuItem { Header = WishListing.NoAccounts, IsEnabled = false });
        foreach (var account in accounts)
        {
            // 用 TextBlock 当标题：账号里的下划线不会被当成快捷键吃掉。
            var item = new MenuItem { Header = new TextBlock { Text = account } };
            AutomationProperties.SetName(item, account);
            if (string.Equals(account, (box.Text ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                item.FontWeight = FontWeight.SemiBold;
            item.Click += (_, _) =>
            {
                box.Text = account;
                box.Focus();
                box.CaretIndex = account.Length;
            };
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    // ---------- 换机清单和导出 ----------

    public async Task ChecklistAsync()
    {
        Flush();
        var text = LicenseMigration.BuildChecklist(_database.LoadWishes(), MachineName, DateTime.Now);
        if (await Dialogs.ShowText("换机清单", text, WishListing.ChecklistFileName(DateTime.Now)) != null)
        {
            try
            {
                _database.RecordUsage(UsageKinds.ChecklistExport);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // 统计写不进去不影响使用。
            }
        }
    }

    public async Task ExportAsync()
    {
        Flush();
        if (!await Dialogs.Confirm("导出明文账号？", WishListing.ExportWarning, "继续导出"))
            return;
        if (await Dialogs.SaveFile("导出账号", WishListing.ExportFileName(DateTime.Now), "文本文件", ".txt") is not { } path)
            return;
        try
        {
            var items = WishListing.ExportItems(_database.LoadWishes(), _database.LoadPlatformAccounts());
            File.WriteAllText(path, PurchaseText.Format(items, out var skipped));
            StatusChanged?.Invoke(WishListing.Exported(items.Count, skipped));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusChanged?.Invoke("导出失败：" + error.Message);
        }
    }

    // ---------- 厂家账号 ----------

    private void ShowPlatformDetail(PlatformEntry? row)
    {
        if (!ShowingPlatforms)
            return;
        _shownPlatform = row;
        _shownPurchase = null;
        DetailEmpty.IsVisible = row == null;
        PlatformPanel.IsVisible = row != null;
        PurchasePanel.IsVisible = false;
        BatchPanel.IsVisible = false;
        if (row == null)
        {
            _platformSnapshot = null;
            return;
        }
        _loading = true;
        PlatformTitle.Text = row.Platform;
        PlatformAccountBox.Text = row.Account.Account;
        PlatformPassword.Secret = row.Account.Password;
        PlatformNote.Text = row.Account.Note;
        _platformSnapshot = CurrentPlatform();
        _loading = false;
        PlatformPassword.IsEnabled = !row.Account.Locked;
        PlatformLocked.IsVisible = row.Account.Locked;
        PlatformSubtitle.Text = row.Subtitle;
        PlatformProducts.Text = row.ProductsText;
        PlatformDeleteButton.IsEnabled = !row.Account.IsEmpty;
    }

    private (string, string, string) CurrentPlatform() => (PlatformAccountBox.Text ?? "", PlatformPassword.Secret, PlatformNote.Text ?? "");

    private void PlatformChanged()
    {
        if (_loading || _shownPlatform == null || _platformSnapshot == null || !ShowingPlatforms)
            return;
        if (CurrentPlatform() != _platformSnapshot)
            ScheduleSave();
    }

    private void SavePlatform()
    {
        if (_shownPlatform == null || !ShowingPlatforms)
            return;
        var old = _shownPlatform.Account;
        var updated = old with
        {
            Account = (PlatformAccountBox.Text ?? "").Trim(),
            Password = old.Locked ? "" : PlatformPassword.Secret,
            Note = PlatformNote.Text ?? "",
        };
        _platformSnapshot = CurrentPlatform();
        if (updated == old)
            return;
        try
        {
            _database.SavePlatformAccount(updated);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        _platformAccounts[VendorCatalog.PlatformKey(updated.Platform)] = updated;
        RebuildRows();
        ShowLists(redetail: false);
        _shownPlatform = _platforms.FirstOrDefault(p => p.Platform == updated.Platform) ?? _shownPlatform with { Account = updated };
        PlatformDeleteButton.IsEnabled = !updated.IsEmpty;
        StatusChanged?.Invoke(_shownPlatform.Saved);
    }

    public async Task ILokFillAsync()
    {
        Flush();
        var ilokRow = _platforms.FirstOrDefault(r => r.IsILok);
        var ilok = ilokRow?.Account;
        if (PlatformListing.ILokProblem(ilok, _platforms) is { } problem)
        {
            if (problem == PlatformListing.ILokFirst && ilokRow != null)
                SelectPlatform(ilokRow.Platform);
            StatusChanged?.Invoke(problem);
            return;
        }
        var plans = PlatformListing.ILokPlan(ilok!, _platforms);
        var (picked, _) = await Dialogs.PickMany("填入 iLok 账号", PlatformListing.ILokPrompt(ilok!), plans.Select(p => (p.Text, p.Checked)).ToList(), "填入");
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
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        _saveTimer.Stop();
        Reload();
        StatusChanged?.Invoke(PlatformListing.ILokFilled(changed));
    }

    public async Task DeletePlatformAsync()
    {
        if (_shownPlatform == null)
            return;
        Flush();
        var row = _shownPlatform;
        if (!await Dialogs.Confirm("清空这个厂家账号？", row.DeletePrompt, "清空", danger: true))
            return;
        try
        {
            _database.DeletePlatformAccount(row.Platform);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("删除失败：" + error.Message);
            return;
        }
        _saveTimer.Stop();
        Reload();
        StatusChanged?.Invoke($"已清空 {row.Platform} 的厂家账号");
    }

    private static Button ActionButton(MaterialIconKind icon, string text, string tip, Action click, bool primary = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new MaterialIcon { Kind = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Classes = { primary ? "Primary" : "Ghost" }, Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }
}
