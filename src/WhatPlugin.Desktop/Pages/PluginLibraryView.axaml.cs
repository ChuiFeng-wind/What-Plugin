using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 插件库和垃圾桶（Blacklist 为 true）共用的页面，对应 Windows 版 MainWindow 里的插件页。
/// 筛选、排序和编辑规则都在 Core 里，这里只管界面。
/// </summary>
public partial class PluginLibraryView : UserControl
{
    private const string TagsExpandedSetting = "detail.tagsExpanded";
    private static readonly PluginKind[] Kinds = [PluginKind.Effect, PluginKind.Unknown];

    private readonly PluginDatabase _database;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Button[] _stars = new Button[5];

    private List<PluginItem> _plugins = [];
    private IReadOnlyList<LibraryColumn> _columns = [];
    private ProjectReportResult? _report;
    private string? _need;
    private PluginItem? _current;
    private int _rating;
    private List<string>? _tagsOverride;
    private List<string>? _timbresOverride;
    private bool? _tagsExpanded;
    private bool _loadingDetail;
    private bool _suppressFilter;
    private bool _suppressSelection;

    public bool Blacklist { get; }

    /// <summary>状态栏文字变了。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>页面副标题变了（选中分类时换成这一类的使用概况）。</summary>
    public event Action<string>? SubtitleChanged;

    /// <summary>插件数、垃圾桶数或已购账号数可能变了，左侧导航要重新数一遍。</summary>
    public event Action? CountsChanged;

    // 设计器需要无参构造函数。
    public PluginLibraryView() : this(new PluginDatabase(Path.Combine(Path.GetTempPath(), "whatplugin-designer.db")), false) { }

    public PluginLibraryView(PluginDatabase database, bool blacklist)
    {
        _database = database;
        Blacklist = blacklist;
        InitializeComponent();

        RecommendBar.IsVisible = !blacklist;
        CreateStars();
        _saveTimer.Tick += (_, _) => SaveCurrent();

        NeedBox.AddHandler(KeyDownEvent, NeedBox_KeyDown, RoutingStrategies.Tunnel);
        RecommendButton.Click += (_, _) => StartRecommendation();
        ExitRecommendButton.Click += (_, _) => ExitRecommendation();
        SearchBox.TextChanged += (_, _) => FilterChanged();
        foreach (var box in new[] { VendorFilter, FormatFilter, RatingFilter })
            box.SelectionChanged += (_, _) => FilterChanged();
        ClearFiltersButton.Click += (_, _) => ClearFilters();
        CategoryFunctionToggle.IsCheckedChanged += (_, _) => CategoryModeChanged();
        CategoryList.SelectionChanged += (_, _) => FilterChanged();
        PluginList.SelectionChanged += PluginList_SelectionChanged;
        PluginList.SizeChanged += (_, e) => UpdateColumns(e.NewSize.Width);

        foreach (var box in new[] { NoteBox, MyTagsBox, VendorBox, ReasonBox })
            box.TextChanged += Field_Changed;
        foreach (var box in new[] { MixLatency, MixCpu, MixOversampling, MixLinearPhase, MixSidechain, KindBox })
            box.SelectionChanged += (_, _) =>
            {
                if (!_loadingDetail)
                    SaveCurrent(force: true);
            };
        PurchaseKindBox.SelectionChanged += PurchaseKind_Changed;
        CompareCurrentButton.Click += (_, _) => CompareCurrent();
        TagExpandButton.Click += (_, _) => ToggleTagsExpanded();
        AddTagButton.Click += (_, _) => AddNewTag();
        NewTagBox.AddHandler(KeyDownEvent, NewTagBox_KeyDown, RoutingStrategies.Tunnel);
        ResetButton.Click += (_, _) => ResetOverrides();
        BlacklistButton.Click += (_, _) => ToggleBlacklist();

        UpdateColumns(0);
    }

    /// <summary>当前列表里显示的插件（测试检查用）。</summary>
    public IReadOnlyList<PluginItem> Shown => PluginList.ItemsSource as IReadOnlyList<PluginItem> ?? [];

    public PluginItem? Selected => _current;

    // ---------- 加载 ----------

    /// <summary>从数据库重新读一遍。页面显示时调用：另一页移入、恢复或导入后这里要跟着变。</summary>
    public void Load()
    {
        var selectedId = _current?.Row.Id;
        SaveCurrent();
        _report = ProjectReport.Build(_database.LoadProjectFiles(), _database.LoadPlugins(), _database.LoadIgnoredProjectNames());
        _plugins = _database.LoadEffects().Select(p => new PluginItem(p) { Usage = _report.Usage.GetValueOrDefault(p.Key) }).ToList();
        RebuildFilterOptions();
        if (_need != null)
            ShowRecommendation();
        else
            ApplyFilter();
        Select(_plugins.FirstOrDefault(p => p.Row.Id == selectedId));
    }

    private bool InView(PluginItem item) => item.Row.User.Hidden == Blacklist;

    private void RebuildFilterOptions()
    {
        _suppressFilter = true;
        SetOptions(VendorFilter, LibraryFilter.AllVendors,
            _plugins.Where(InView).Select(p => p.Vendor).Distinct().Order(StringComparer.OrdinalIgnoreCase));
        SetOptions(FormatFilter, LibraryFilter.AllFormats, LibraryFilter.FormatsOnThisSystem.Select(PluginLabels.Format));
        SetOptions(RatingFilter, LibraryFilter.RatingLabels[0], LibraryFilter.RatingLabels.Skip(1));
        RebuildCategories();
        _suppressFilter = false;
    }

    private static void SetOptions(ComboBox box, string all, IEnumerable<string> options)
    {
        var previous = box.SelectedItem as string;
        var items = new List<string> { all };
        items.AddRange(options);
        box.ItemsSource = items;
        box.SelectedItem = previous != null && items.Contains(previous) ? previous : all;
    }

    // ---------- 分类栏 ----------

    private bool ShowingTimbres => CategoryTimbreToggle.IsChecked == true;

    private static bool IsTimbre(LibraryCategory category) => category.Kind is LibraryCategoryKind.Timbre or LibraryCategoryKind.NoTimbre;

    private LibraryCategory? SelectedCategory => CategoryList.SelectedItem is LibraryCategory { IsComparison: true } c ? c : null;

    /// <summary>按当前视图重算各分类的数量，尽量保持原来的选择。上面的切换决定列功能分类还是音色分类。</summary>
    private void RebuildCategories()
    {
        var previous = CategoryList.SelectedItem as LibraryCategory;
        var timbres = ShowingTimbres;
        var categories = LibraryCategories.Build(_plugins.Where(InView).Select(p => p.Row).ToList())
            .Where(c => c.Kind == LibraryCategoryKind.All || (!c.IsHeader && IsTimbre(c) == timbres))
            .ToList();
        var suppress = _suppressFilter;
        _suppressFilter = true;
        CategoryList.ItemsSource = categories;
        CategoryList.SelectedItem = categories.FirstOrDefault(c => previous != null && c.Kind == previous.Kind && c.Label == previous.Label)
            ?? categories.FirstOrDefault();
        _suppressFilter = suppress;
    }

    private void CategoryModeChanged()
    {
        if (_suppressFilter)
            return;
        _suppressFilter = true;
        RebuildCategories();
        _suppressFilter = false;
        FilterChanged();
    }

    /// <summary>选中一个分类；它属于另一组（功能 / 音色）时先切过去。</summary>
    private void SelectCategory(LibraryCategory? category)
    {
        _suppressFilter = true;
        if (category != null && IsTimbre(category) != ShowingTimbres)
        {
            (IsTimbre(category) ? CategoryTimbreToggle : CategoryFunctionToggle).IsChecked = true;
            RebuildCategories();
        }
        var list = (CategoryList.ItemsSource as IEnumerable<LibraryCategory>)?.ToList() ?? [];
        CategoryList.SelectedItem = category == null
            ? list.FirstOrDefault()
            : list.FirstOrDefault(c => c.Kind == category.Kind && c.Label == category.Label) ?? list.FirstOrDefault();
        _suppressFilter = false;
    }

    // ---------- 筛选和列表 ----------

    public LibraryFilter CurrentFilter => new(
        Blacklist,
        SearchBox.Text ?? "",
        VendorFilter.SelectedIndex > 0 ? VendorFilter.SelectedItem as string : null,
        FormatFilter.SelectedIndex > 0 ? LibraryFilter.FormatsOnThisSystem[FormatFilter.SelectedIndex - 1] : null,
        (RatingFilter)Math.Max(RatingFilter.SelectedIndex, 0),
        SelectedCategory);

    private void FilterChanged()
    {
        if (_suppressFilter || _need != null)
            return;
        ApplyFilter();
    }

    /// <summary>按当前筛选重排列表，保持选中的插件（还在列表里的话）。</summary>
    private void ApplyFilter()
    {
        var filter = CurrentFilter;
        var shown = filter.Order(_plugins.Where(p => filter.Matches(p.Row)), p => p.Row, p => p.Usage).ToList();
        SetItems(shown);
        UpdateSubtitle(filter);
        UpdateEmpty(shown.Count);
        Status(LibraryText.Status(_plugins.Count, _plugins.Count(p => p.Row.User.Hidden), shown.Count));
    }

    private void SetItems(List<PluginItem> items)
    {
        var keep = _current;
        _suppressSelection = true;
        PluginList.ItemsSource = items;
        PluginList.SelectedItem = keep != null && items.Contains(keep) ? keep : null;
        _suppressSelection = false;
        if (PluginList.SelectedItem == null && keep != null)
            SelectionChangedTo(null);
    }

    private void Select(PluginItem? item)
    {
        if (item != null && Shown.Contains(item))
        {
            PluginList.SelectedItem = item;
            PluginList.ScrollIntoView(item);
        }
    }

    /// <summary>按名称选中一款插件（测试用）。</summary>
    public void Select(string name) => Select(Shown.FirstOrDefault(p => p.Name == name));

    /// <summary>从别的页面跳过来看某一款：退出推荐、清除筛选，再选中它。</summary>
    public void ShowPlugin(string key)
    {
        ExitRecommendation();
        ClearFilters();
        Select(Shown.FirstOrDefault(p => p.Row.Key == key));
    }

    private void ClearFilters()
    {
        _suppressFilter = true;
        SearchBox.Text = "";
        VendorFilter.SelectedIndex = 0;
        FormatFilter.SelectedIndex = 0;
        RatingFilter.SelectedIndex = 0;
        SelectCategory(null);
        _suppressFilter = false;
        ApplyFilter();
    }

    private void UpdateSubtitle(LibraryFilter filter)
    {
        if (Blacklist)
        {
            SubtitleChanged?.Invoke(LibraryText.TrashSubtitle);
            return;
        }
        if (filter.Category is not { } category)
        {
            SubtitleChanged?.Invoke(LibraryText.DefaultSubtitle);
            return;
        }
        var rows = _plugins.Where(p => InView(p) && category.Matches(p.Row)).Select(p => new ComparisonRow(p.Row, p.Usage)).ToList();
        SubtitleChanged?.Invoke(LibraryText.CategorySummary(category, rows, _report?.Projects.Count > 0, DateTime.UtcNow));
    }

    private void UpdateEmpty(int shown)
    {
        PluginEmpty.IsVisible = shown == 0;
        PluginEmptyText.Text = LibraryText.Empty(_need != null, Blacklist, _plugins.Count, _plugins.Count(p => p.Row.User.Hidden));
    }

    private void Status(string text) => StatusChanged?.Invoke(text);

    // ---------- 列 ----------

    private void UpdateColumns(double width)
    {
        var columns = LibraryColumn.Visible(_need != null, Blacklist, width);
        if (columns.SequenceEqual(_columns))
            return;
        _columns = columns;

        var header = LibraryColumn.CreateGrid(columns);
        for (var i = 0; i < columns.Count; i++)
        {
            var text = new TextBlock { Text = columns[i].Header, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Padding = LibraryColumn.CellPadding };
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Muted"));
            Grid.SetColumn(text, i);
            header.Children.Add(text);
        }
        GridHeader.Child = header;

        // 换列以后行控件要重新生成；保持选中项。
        PluginList.ItemTemplate = new FuncDataTemplate<PluginItem>((_, _) => new PluginRowView(columns), supportsRecycling: true);
        if (PluginList.ItemsSource is List<PluginItem> items)
            SetItems(items);
    }

    /// <summary>当前显示的列名（测试检查用）。</summary>
    public IReadOnlyList<string> ColumnHeaders => _columns.Select(c => c.Header).ToList();

    // ---------- 推荐 ----------

    private void NeedBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        StartRecommendation();
        e.Handled = true;
    }

    public void StartRecommendation()
    {
        var need = (NeedBox.Text ?? "").Trim();
        if (need.Length == 0)
        {
            ExitRecommendation();
            Status("请先描述你需要的功能，例如：人声去齿音");
            return;
        }
        SaveCurrent();
        _need = need;
        ShowRecommendation();
        if (Shown.Count > 0)
            PluginList.ScrollIntoView(Shown[0]);
    }

    private void ShowRecommendation()
    {
        var result = Recommender.Recommend(_need!, _plugins.Select(p => p.Row));
        var byId = _plugins.ToDictionary(p => p.Row.Id);
        var items = new List<PluginItem>();
        foreach (var match in result.Items)
        {
            var item = byId[match.Plugin.Id];
            item.RecommendReason = string.Join("；", match.Reasons);
            item.Changed();
            items.Add(item);
        }
        SetRecommending(true);
        SetItems(items);
        UpdateEmpty(items.Count);
        Status(LibraryText.DescribeRecommendation(result));
    }

    public void ExitRecommendation()
    {
        if (_need == null)
            return;
        _need = null;
        SetRecommending(false);
        ApplyFilter();
    }

    /// <summary>推荐时筛选和分类不起作用，变灰表示。</summary>
    private void SetRecommending(bool on)
    {
        foreach (var control in new Control[] { FilterRow, CategoryPanel })
        {
            control.IsEnabled = !on;
            control.Opacity = on ? 0.45 : 1;
        }
        ExitRecommendButton.IsVisible = on;
        UpdateColumns(PluginList.Bounds.Width);
    }

    // ---------- 详情 ----------

    private void PluginList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;
        SelectionChangedTo(PluginList.SelectedItem as PluginItem);
    }

    private void SelectionChangedTo(PluginItem? item)
    {
        // 切换前先保存，否则还在等待自动保存的内容会写到下一个插件上。
        SaveCurrent();
        _current = item;
        ShowDetail();
    }

    private void ShowDetail()
    {
        EmptyHint.IsVisible = _current == null;
        DetailPanel.IsVisible = _current != null;
        if (_current == null)
            return;

        var row = _current.Row;
        _loadingDetail = true;
        _rating = row.User.Rating;
        _tagsOverride = row.User.TagsOverride?.ToList();
        _timbresOverride = row.User.TimbresOverride?.ToList();
        NoteBox.Text = row.User.Note;
        MyTagsBox.Text = string.Join("，", row.User.MyTags);
        VendorBox.Text = row.Vendor;
        KindBox.SelectedIndex = Math.Max(Array.IndexOf(Kinds, row.Kind), 0);
        NewTagBox.Text = "";
        ReasonBox.Text = row.User.BlacklistReason;
        PurchaseKindBox.SelectedIndex = (int)row.User.Purchase;
        var mix = row.User.Mix ?? new MixAttributes();
        MixLatency.SelectedIndex = (int)mix.Latency;
        MixCpu.SelectedIndex = (int)mix.Cpu;
        MixLinearPhase.SelectedIndex = (int)mix.LinearPhase;
        MixSidechain.SelectedIndex = (int)mix.Sidechain;
        MixOversampling.SelectedIndex = (int)mix.Oversampling;
        _loadingDetail = false;

        DetailTitle.Text = row.Name;
        BlacklistButton.Content = row.User.Hidden ? "从垃圾桶恢复" : "移入垃圾桶";
        var usage = _current.Usage;
        DetailUsage.Text = PluginLabels.UsageSummary(usage);
        ToolTip.SetTip(DetailUsage, usage == null ? null : string.Join("\n", usage.ProjectNames.Take(30)));
        DetailFiles.Text = TrashMemory.IsRemembered(row)
            ? "已经卸载。上面的原因、评分和感受会一直留着，以后想再加入心愿单时会提醒你。"
            : row.IsMissing ? "上次扫描时没有找到此插件的文件。" : "";
        DetailFiles.IsVisible = row.IsMissing;
        ShowDerivedDetail(row);
        UpdateStars();
        RebuildChips();
        RebuildTimbreChips();
    }

    private void ShowDerivedDetail(PluginRow row)
    {
        DetailMeta.Text = PluginLabels.TagSource(row.TagSource);
        ResetButton.IsEnabled = row.User.HasOverrides;
    }

    private void CreateStars()
    {
        for (var i = 0; i < _stars.Length; i++)
        {
            var value = i + 1;
            var star = new Button
            {
                Content = new MaterialIcon { Kind = MaterialIconKind.Star, Width = 20, Height = 20 },
                Classes = { "Star" },
            };
            AutomationProperties.SetName(star, $"{value} 星");
            ToolTip.SetTip(star, "再点一次当前星级可清除");
            star.Click += (_, _) =>
            {
                _rating = value == _rating ? 0 : value;
                UpdateStars();
                SaveCurrent(force: true);
            };
            _stars[i] = star;
            Stars.Children.Add(star);
        }
    }

    private void UpdateStars()
    {
        for (var i = 0; i < _stars.Length; i++)
            _stars[i].Classes.Set("On", i < _rating);
    }

    /// <summary>当前星级（测试检查用）。</summary>
    public int DetailRating => _rating;

    /// <summary>点第几颗星（测试用）。</summary>
    public void ClickStar(int value) => _stars[value - 1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>默认收起：全部分类有几十个，占掉半个详情面板，平时只需要看已选的。</summary>
    private bool TagsExpanded => _tagsExpanded ??= _database.GetSetting(TagsExpandedSetting) == "1";

    private void ToggleTagsExpanded()
    {
        _tagsExpanded = !TagsExpanded;
        _database.SetSetting(TagsExpandedSetting, _tagsExpanded == true ? "1" : "0");
        RebuildChips();
    }

    private void RebuildChips()
    {
        if (_current == null)
            return;
        var active = (_tagsOverride ?? _current.Row.VisibleAutoTags).ToList();
        var known = PluginEdits.KnownTags(_plugins.Select(p => p.Row), active);
        var expanded = TagsExpanded;
        TagExpandButton.Content = expanded ? "收起" : $"编辑分类（共 {known.Count} 个）";
        NewTagPanel.IsVisible = expanded;
        TagChipsEmpty.IsVisible = !expanded && active.Count == 0;
        TagChips.Children.Clear();
        foreach (var tag in expanded ? known : active)
        {
            var chip = Chip(tag, active.Contains(tag, StringComparer.OrdinalIgnoreCase), "Tag_");
            chip.IsCheckedChanged += (_, _) =>
            {
                if (_current == null)
                    return;
                _tagsOverride = PluginEdits.ToggleTag(_current.Row, _tagsOverride, tag, chip.IsChecked == true);
                SaveCurrent(force: true);
            };
            TagChips.Children.Add(chip);
        }
    }

    /// <summary>音色分类只有 8 个，全部列出来直接点选。</summary>
    private void RebuildTimbreChips()
    {
        if (_current == null)
            return;
        var active = _timbresOverride ?? _current.Row.Timbres;
        TimbreChips.Children.Clear();
        foreach (var timbre in Classifier.TimbreCatalog)
        {
            var chip = Chip(timbre, active.Contains(timbre, StringComparer.OrdinalIgnoreCase), "Timbre_");
            chip.IsCheckedChanged += (_, _) =>
            {
                if (_current == null)
                    return;
                _timbresOverride = PluginEdits.ToggleTimbre(_current.Row, _tagsOverride, _timbresOverride, timbre, chip.IsChecked == true);
                SaveCurrent(force: true);
            };
            TimbreChips.Children.Add(chip);
        }
    }

    /// <summary>初始状态先设好再订阅，重建标签时不会触发保存。</summary>
    private static ToggleButton Chip(string text, bool on, string idPrefix)
    {
        var chip = new ToggleButton { Content = text, IsChecked = on, Classes = { "Chip" } };
        AutomationProperties.SetAutomationId(chip, idPrefix + text);
        AutomationProperties.SetName(chip, text);
        return chip;
    }

    private void NewTagBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        AddNewTag();
        e.Handled = true;
    }

    private void AddNewTag()
    {
        if (_current == null || PluginEdits.SplitTags(NewTagBox.Text ?? "").Count == 0)
            return;
        var (tags, timbres) = PluginEdits.AddTags(_current.Row, _tagsOverride, _timbresOverride, NewTagBox.Text ?? "");
        NewTagBox.Text = "";
        _tagsOverride = tags;
        _timbresOverride = timbres;
        SaveCurrent(force: true);
        RebuildChips();
        RebuildTimbreChips();
    }

    private void ResetOverrides()
    {
        if (_current == null)
            return;
        var row = _current.Row;
        _tagsOverride = null;
        _timbresOverride = null;
        _loadingDetail = true;
        VendorBox.Text = row.AutoVendor;
        KindBox.SelectedIndex = Math.Max(Array.IndexOf(Kinds, row.AutoKind), 0);
        _loadingDetail = false;
        SaveCurrent(force: true);
        RebuildChips();
        RebuildTimbreChips();
    }

    /// <summary>
    /// Avalonia 的 TextChanged 是排队触发的：程序填详情时的事件会在填完以后才到，
    /// 所以不能靠“正在填详情”的标记过滤，而是看文字和已保存的是否真的不同。
    /// </summary>
    private void Field_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loadingDetail || !TextDiffers())
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>感受、标签、厂家和移入原因和已保存的内容不同（按保存时的规整方式比较）。</summary>
    private bool TextDiffers()
    {
        if (_current == null)
            return false;
        var saved = _current.Row.User;
        var panel = ReadPanel(_current.Row);
        return panel.Note != saved.Note
            || !panel.MyTags.SequenceEqual(saved.MyTags)
            || panel.VendorOverride != saved.VendorOverride
            || panel.BlacklistReason != saved.BlacklistReason;
    }

    private void PurchaseKind_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingDetail || _current == null)
            return;
        var before = _current.Row.User.Purchase;
        SaveCurrent(force: true);
        if (before != PurchaseKind.Paid && _current is { Row.User.Purchase: PurchaseKind.Paid } plugin)
            AddToPurchases(plugin.Row);
    }

    /// <summary>
    /// 获取方式标成“已购”时，在“已购账号”里建一条（同名的已经有了就不重复建；在心愿单里的转成已购）。
    /// 改回别的不会删：那边可能已经填了账号和序列号。
    /// </summary>
    private void AddToPurchases(PluginRow row)
    {
        try
        {
            var name = Normalizer.Name(row.Name);
            if (_database.LoadWishes().Any(w => w.Status == WishStatus.Purchased && Normalizer.Name(w.Name) == name))
                return;
            var result = _database.UpsertPurchased([new PurchaseDraft(row.Name, row.Vendor)]);
            CountsChanged?.Invoke();
            Status(result.Updated > 0
                ? $"{row.Name} 原来在心愿单，已转到“已购账号”"
                : $"已把 {row.Name} 加到“已购账号”，可以去那里填账号和序列号");
        }
        catch (SqliteException error)
        {
            Status("加到已购账号失败：" + error.Message);
        }
    }

    /// <summary>“看同类”：在左边选中这款插件所属范围最窄的分类。</summary>
    private void CompareCurrent()
    {
        if (_current == null)
            return;
        SaveCurrent();
        var plugin = _current;
        _suppressFilter = true;
        SearchBox.Text = "";
        _suppressFilter = false;
        SelectCategory(LibraryText.CompareCategory(plugin.Row, _plugins.Select(p => p.Row)));
        ApplyFilter();
        Select(plugin);
    }

    /// <summary>移入或移出垃圾桶。原因是用户写的内容，恢复时也保留，再次移入不用重写。</summary>
    private void ToggleBlacklist()
    {
        if (_current == null)
            return;
        SaveCurrent();
        var row = _current.Row;
        var data = ReadPanel(row) with { Hidden = !row.User.Hidden };
        try
        {
            _database.SaveUserData(row.Id, data);
            Quietly(() => _database.RecordAction());
        }
        catch (SqliteException error)
        {
            Status("保存失败：" + error.Message);
            return;
        }

        // 这款插件会离开当前视图，选择随之清空，面板回到空白状态。
        _current = null;
        Load();
        ShowDetail();
        CountsChanged?.Invoke();
        Status(data.Hidden
            ? $"已把 {row.Name} 移入垃圾桶，可在左侧“垃圾桶”查看或恢复"
            : $"已把 {row.Name} 从垃圾桶恢复");
    }

    private PluginUserData ReadPanel(PluginRow row)
    {
        var kind = KindBox.SelectedIndex >= 0 ? Kinds[KindBox.SelectedIndex] : row.AutoKind;
        var vendor = (VendorBox.Text ?? "").Trim();
        return new PluginUserData(
            _rating,
            NoteBox.Text ?? "",
            PluginEdits.SplitTags(MyTagsBox.Text ?? ""),
            _tagsOverride,
            kind == row.AutoKind ? null : kind,
            vendor.Length == 0 || vendor == row.AutoVendor ? null : vendor,
            row.User.Hidden,
            ReasonBox.Text ?? "",
            new MixAttributes(
                (LatencyLevel)Math.Max(MixLatency.SelectedIndex, 0),
                (CpuLevel)Math.Max(MixCpu.SelectedIndex, 0),
                (Tri)Math.Max(MixLinearPhase.SelectedIndex, 0),
                (Tri)Math.Max(MixSidechain.SelectedIndex, 0),
                (Tri)Math.Max(MixOversampling.SelectedIndex, 0)),
            _timbresOverride,
            (PurchaseKind)Math.Max(PurchaseKindBox.SelectedIndex, 0)).Normalized();
    }

    /// <summary>
    /// 把面板内容写入数据库并刷新这一行。不重新筛选：
    /// 否则在“未评分”筛选下打分，正在编辑的插件会立刻从列表消失。
    /// </summary>
    public void SaveCurrent(bool force = false)
    {
        var timerPending = _saveTimer.IsEnabled;
        _saveTimer.Stop();
        // 刚打完字、文字变化的事件还没排到就切换时，计时器还没启动，所以也要比较一次内容。
        if (_current == null || (!force && !timerPending && !TextDiffers()))
            return;

        var row = _current.Row;
        var data = ReadPanel(row);
        try
        {
            _database.SaveUserData(row.Id, data);
            Quietly(() =>
            {
                _database.RecordAction();
                if (data.Purchase != row.User.Purchase)
                    _database.RecordUsage(UsageKinds.PurchaseChange, (int)data.Purchase, (int)row.User.Purchase, row.Key);
            });
        }
        catch (SqliteException error)
        {
            Status("保存失败：" + error.Message);
            return;
        }

        var updated = row with { User = data };
        _current.Update(updated);
        ShowDerivedDetail(updated);

        var vendorsOrTagsChanged = row.Vendor != updated.Vendor || !row.Tags.SequenceEqual(updated.Tags) || !row.Timbres.SequenceEqual(updated.Timbres);
        if (vendorsOrTagsChanged)
            RebuildFilterOptions();
        Status($"已保存 {row.Name}");
    }

    /// <summary>使用统计写不进去不影响保存本身。</summary>
    private static void Quietly(Action record)
    {
        try
        {
            record();
        }
        catch (SqliteException)
        {
        }
    }
}
