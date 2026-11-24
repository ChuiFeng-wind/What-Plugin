using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 心愿单：想买的插件、为什么想买、价格和目标价。买前提醒同类已有多少款、多少款闲置。
/// 标记为已买后转到已购账号，原因和价格都留着。
/// </summary>
public partial class WishesView : UserControl
{
    private readonly PluginDatabase _database;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly VendorFiller _vendorFiller = new();
    private List<WishRow> _rows = [];
    private IReadOnlyList<PluginRow> _effects = [];
    private IReadOnlyDictionary<string, PluginUsage> _usage = new Dictionary<string, PluginUsage>();
    private WishRow? _shown;
    private WishFields? _shownFields;
    private string _shownVendor = "";
    private bool _loading;
    private bool _building;
    private string _listedSearch = "";

    public event Action<string>? StatusChanged;

    public event Action<string>? SubtitleChanged;

    /// <summary>心愿单或已购的数量变了，侧边栏的数字要更新。</summary>
    public event Action? CountsChanged;

    /// <summary>对话框。测试换成按脚本回答的实现。</summary>
    public IDialogs Dialogs { get; set; }

    /// <summary>打开网页。测试换成记录下来的函数；出错时返回说明。</summary>
    public Func<string, string?> OpenWebPage { get; set; } = SystemShell.OpenWebPage;

    // 设计器需要无参构造函数。
    public WishesView() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db"))) { }

    public WishesView(PluginDatabase database)
    {
        _database = database;
        InitializeComponent();
        Dialogs = new WindowDialogs(() => TopLevel.GetTopLevel(this) as Window);
        _saveTimer.Tick += (_, _) => Flush();
        DetailEmptyText.Text = WishListing.WishPanelEmpty;

        SortBox.ItemsSource = WishListing.SortOptions;
        SortBox.SelectedIndex = 0;
        CurrencyBox.ItemsSource = Money.Currencies;
        SortBox.SelectionChanged += (_, _) => ShowList(_shown?.Item.Id);
        SearchBox.TextChanged += (_, _) =>
        {
            // TextChanged 是稍后才触发的：程序自己清空的搜索已经处理过。
            if ((SearchBox.Text ?? "") == _listedSearch)
                return;
            Flush();
            ShowList(_shown?.Item.Id);
        };
        QuickAddBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = QuickAdd(QuickAddBox.Text ?? "");
                e.Handled = true;
            }
        };
        QuickAddButton.Click += (_, _) => _ = QuickAdd(QuickAddBox.Text ?? "");
        WishList.SelectionChanged += (_, _) =>
        {
            if (_building)
                return;
            // 切换前先保存，否则等待中的内容会写到下一款上。
            Flush();
            ShowDetail(WishList.SelectedItem as WishRow);
        };

        VendorBox.AsyncPopulator = (text, _) => Task.FromResult<IEnumerable<object>>(VendorSuggestions(text ?? ""));
        foreach (var box in new[] { NameBox, PriceBox, TargetBox, LowestBox, ReasonBox, UrlBox, NoteBox })
            box.TextChanged += (_, _) => FieldChanged();
        VendorBox.TextChanged += (_, _) => FieldChanged();
        CurrencyBox.SelectionChanged += (_, _) => FieldChanged();
        CategoryBox.SelectionChanged += (_, _) => FieldChanged();
        MustHave.IsCheckedChanged += (_, _) => FieldChanged();

        BoughtButton.Click += (_, _) => MarkBought();
        DeleteButton.Click += (_, _) => _ = DeleteShownAsync();
        OpenUrlButton.Click += (_, _) => Open(WishListing.WishUrl(UrlBox.Text ?? ""));
        PriceSearchButton.Click += (_, _) => Open(PurchaseAdvisor.PriceSearchUrl((NameBox.Text ?? "").Trim(), (VendorBox.Text ?? "").Trim()));

        Reload();
    }

    public IReadOnlyList<WishRow> Rows => _rows;

    /// <summary>列表里显示的（经过搜索和排序）。</summary>
    public IReadOnlyList<WishRow> Shown => WishList.ItemsSource as IReadOnlyList<WishRow> ?? [];

    public WishRow? Selected => _shown;

    public string Status => WishListing.WishesStatus(_rows.Count);

    public string AdviceText => AdviceBlock.Text ?? "";

    public bool HasPendingSave => _saveTimer.IsEnabled;

    public void SetUsage(IReadOnlyDictionary<string, PluginUsage> usage)
    {
        _usage = usage;
        ShowAdvice();
    }

    /// <summary>重新读心愿单，尽量保留选中的那一款。</summary>
    public void Reload(long? select = null)
    {
        Flush();
        _effects = _database.LoadEffects();
        var installed = WishListing.InstalledNames(_database.LoadPlugins());
        var platforms = PurchaseLogin.ByKey(_database.LoadPlatformAccounts());
        _rows = WishListing.Rows(_database.LoadWishes().Where(w => w.Status == WishStatus.Wished), installed, platforms);
        try
        {
            _database.RecordWishCount(_rows.Count);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // 统计写不进去不影响使用。
        }
        ShowList(select ?? _shown?.Item.Id);
        SubtitleChanged?.Invoke(WishListing.WishSubtitle(_rows));
    }

    public void Select(string name) => WishList.SelectedItem = Shown.FirstOrDefault(r => r.Name == name);

    private void ShowList(long? select)
    {
        _listedSearch = SearchBox.Text ?? "";
        var shown = WishListing.Wishes(_rows, _listedSearch, SortBox.SelectedItem as string ?? WishListing.SortByPriority).ToList();
        _building = true;
        WishList.ItemsSource = shown;
        var selected = shown.FirstOrDefault(r => r.Item.Id == select);
        WishList.SelectedItem = selected;
        _building = false;
        EmptyPanel.IsVisible = shown.Count == 0;
        EmptyText.Text = WishListing.WishEmptyText(_rows.Count);
        if (selected?.Item.Id != _shown?.Item.Id || selected == null)
            ShowDetail(selected);
    }

    private void ShowDetail(WishRow? row)
    {
        _shown = row;
        DetailEmpty.IsVisible = row == null;
        DetailPanel.IsVisible = row != null;
        if (row == null)
        {
            _shownFields = null;
            return;
        }
        var item = row.Item;
        var remembered = NoteWishes.FindRemembered(item.Name, _effects, _database.LoadUninstallHistory());
        TrashNote.IsVisible = remembered != null;
        TrashText.Text = remembered is { } r ? WishListing.TrashNote(r) : "";

        _loading = true;
        NameBox.Text = item.Name;
        VendorBox.Text = item.Vendor;
        CurrencyBox.SelectedItem = Money.Currencies.Contains(item.Currency) ? item.Currency : Money.Currencies[0];
        PriceBox.Text = WishListing.FormatAmount(item.Price);
        TargetBox.Text = WishListing.FormatAmount(item.TargetPrice);
        LowestBox.Text = WishListing.FormatAmount(item.LowestPrice);
        var categories = WishListing.Categories(item, _effects);
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedItem = item.Category.Length > 0 ? item.Category : PurchaseAdvisor.AutoCategory;
        MustHave.IsChecked = WishPriority.Clean(item.Priority) == WishPriority.MustHave;
        CanWait.IsChecked = !MustHave.IsChecked;
        ReasonBox.Text = item.Reason;
        UrlBox.Text = item.Url;
        NoteBox.Text = item.Note;
        _shownFields = CurrentFields();
        _shownVendor = item.Vendor;
        _loading = false;
        ShowAdvice();
    }

    /// <summary>买前提醒：同类已有多少款、多少款闲置，以及现价和目标价的差距。</summary>
    private void ShowAdvice()
    {
        if (_shown == null)
            return;
        var item = _shown.Item;
        AdviceBlock.Text = PurchaseAdvisor.Describe(PurchaseAdvisor.Advise(item.Name, item.Vendor, item.Category, _effects, _usage, DateTime.UtcNow));
        PriceHint.Text = PurchaseAdvisor.DescribePrice(item);
        PriceHint.IsVisible = PriceHint.Text.Length > 0;
    }

    private WishFields CurrentFields() => new(
        NameBox.Text ?? "", PriceBox.Text ?? "", TargetBox.Text ?? "", LowestBox.Text ?? "",
        CurrencyBox.SelectedItem as string, CategoryBox.SelectedItem as string, MustHave.IsChecked == true,
        ReasonBox.Text ?? "", UrlBox.Text ?? "", NoteBox.Text ?? "");

    // TextChanged 是稍后才触发的：和显示时的内容一样就不算修改。
    private void FieldChanged()
    {
        if (_loading || _shown == null || _shownFields == null)
            return;
        if (CurrentFields() != _shownFields || (VendorBox.Text ?? "") != _shownVendor)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    /// <summary>把等待中的修改写进去（切换、离开页面或关闭前调用）。</summary>
    public void Flush()
    {
        var pending = _saveTimer.IsEnabled;
        _saveTimer.Stop();
        if (pending)
            Save();
    }

    private void Save()
    {
        if (_shown == null)
            return;
        var old = _shown.Item;
        // 填错的项保留原值并提示，其余内容照常保存。
        var fields = CurrentFields();
        var (parsed, problems) = WishListing.ParseWish(old, fields);
        var vendor = _vendorFiller.Fill(old, parsed.Name, VendorBox.Text ?? "", _effects, out var guessed);
        var updated = parsed with { Vendor = vendor };
        try
        {
            _database.SaveWish(updated);
            _database.RecordAction();
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
        _shownFields = fields;
        _shownVendor = guessed ? vendor : VendorBox.Text ?? "";
        var row = _shown with { Item = updated };
        _rows[_rows.FindIndex(r => r.Item.Id == updated.Id)] = row;
        _shown = row;
        ShowList(updated.Id);
        ShowAdvice();
        SubtitleChanged?.Invoke(WishListing.WishSubtitle(_rows));
        StatusChanged?.Invoke(WishListing.Saved(updated.Name, problems, guessed ? vendor : ""));
    }

    private IEnumerable<object> VendorSuggestions(string text) =>
        VendorCatalog.Suggest(text, _effects.Select(p => p.Vendor).Concat(_rows.Select(w => w.Item.Vendor)))
            .Where(v => !v.Equals(text.Trim(), StringComparison.Ordinal));

    public async Task QuickAdd(string text)
    {
        var name = text.Trim();
        if (name.Length == 0)
        {
            StatusChanged?.Invoke(WishListing.NameRequired);
            QuickAddBox.Focus();
            return;
        }
        if (NoteWishes.RememberedPrompt([name], _effects, _database.LoadUninstallHistory()) is { } prompt
            && !await Dialogs.Confirm("以前不要过的插件", prompt, "仍然加入"))
            return;
        Flush();
        WishItem added;
        try
        {
            added = _database.AddWish(new WishItem(0, name, NoteWishes.GuessVendor(name, _effects)));
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        QuickAddBox.Text = "";
        SearchBox.Text = "";
        Reload(added.Id);
        if (WishList.SelectedItem != null)
            WishList.ScrollIntoView(WishList.SelectedItem);
        StatusChanged?.Invoke(WishListing.QuickAdded(name, added.Vendor));
        CountsChanged?.Invoke();
    }

    public void MarkBought()
    {
        if (_shown == null)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
        Flush();
        var item = _shown.Item;
        try
        {
            _database.SaveWish(WishListing.Bought(item, DateOnly.FromDateTime(DateTime.Today)));
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("保存失败：" + error.Message);
            return;
        }
        _shown = null;
        Reload();
        StatusChanged?.Invoke(WishListing.BoughtText(item));
        CountsChanged?.Invoke();
    }

    public async Task DeleteShownAsync()
    {
        if (_shown == null)
            return;
        Flush();
        var item = _shown.Item;
        if (!await Dialogs.Confirm("删除这款心愿？", WishListing.WishDeletePrompt(item), "删除", danger: true))
            return;
        try
        {
            _database.DeleteWishes([item.Id]);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusChanged?.Invoke("删除失败：" + error.Message);
            return;
        }
        // 先取消等待中的保存，否则会往已删除的记录写。
        _saveTimer.Stop();
        _shown = null;
        Reload();
        StatusChanged?.Invoke($"已删除 {item.Name}");
        CountsChanged?.Invoke();
    }

    private void Open(string url)
    {
        if (OpenWebPage(url) is { } error)
            StatusChanged?.Invoke(error);
    }
}
