using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

public enum AppPage { Plugins, Blacklist, Wishes, Accounts, Projects, Lists, Idle, Settings, Chains, Health, Extension }

public partial class MainWindow : Window
{
    private const string AllVendors = "全部厂家";
    private const string AllFormats = "全部格式";
    private const string AllRatings = "全部评分";
    private const string Rated = "已评分";
    private const string HighRated = "4 星及以上";
    private const string LowRated = "2 星及以下";
    private const string Unrated = "未评分";
    private const string HasNote = "有备注";

    private static readonly char[] TagSeparators = [',', '，', '、', ';', '；'];
    private static readonly PluginKind[] Kinds = [PluginKind.Effect, PluginKind.Unknown];

    private readonly PluginDatabase _database;
    private readonly string _scannerPath = Path.Combine(AppContext.BaseDirectory, "WhatPluginScanner.exe");
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Button[] _stars;

    private ICollectionView? _view;
    private List<PluginViewModel> _plugins = [];
    private CancellationTokenSource? _scanCancel;
    private bool _suppressFilter;
    private AppPage _page = AppPage.Plugins;

    /// <summary>正在显示推荐结果时是当前需求，否则为 null。</summary>
    private string? _need;

    private PluginViewModel? _current;
    private bool _loadingDetail;
    private int _rating;
    private List<string>? _tagsOverride;

    public MainWindow()
    {
        InitializeComponent();
        WindowEffects.Apply(this);
        StateChanged += (_, _) => UpdateWindowState();
        var databasePath = ResolveDatabasePath();
        _database = new PluginDatabase(databasePath);
        LookupExport.PublishCliPath(databasePath, PluginDatabase.DefaultPath, AppContext.BaseDirectory);
        InitializeSettings();
        RecordUsage(() => _database.RecordLaunch());
        _stars = [Star1, Star2, Star3, Star4, Star5];
        KindBox.ItemsSource = Kinds.Select(Labels.Kind).ToList();
        _saveTimer.Tick += (_, _) => SaveCurrent();

        LoadPlugins();
        InitializeItems();
        InitializeProjects();
        NavPlugins.IsChecked = true;
        ShowPage();
        LoadExtensions();
        ApplyNavOrder();
        InitializeOnboarding();

        Closing += (_, _) =>
        {
            SaveCurrent();
            FlushItems();
            FlushChain();
            SecureClipboard.ClearPending();
            _scanCancel?.Cancel();
            _projectCancel?.Cancel();
        };
        Closed += (_, _) => _database.Dispose();
    }

    /// <summary>可用 --db 指定数据库，用于在副本上试验而不碰真实数据。</summary>
    private static string ResolveDatabasePath()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--db");
        if (index >= 0 && index + 1 < args.Length)
            return args[index + 1];
        return PluginDatabase.ResolveDefaultPath();
    }

    // ---------- 窗口和页面 ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>无边框窗口最大化时系统会多留出屏幕外的一圈，要缩回来，否则内容被裁掉。</summary>
    private void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootFrame.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!Enum.TryParse<AppPage>((string)((FrameworkElement)sender).Tag, out var page) || page == _page)
            return;
        SaveCurrent();
        FlushItems();
        FlushChain();
        _page = page;
        ShowPage();
    }

    private void ShowPage()
    {
        var pluginPage = _page is AppPage.Plugins or AppPage.Blacklist;
        PlaceDetailCard(_page == AppPage.Lists);
        // 自动清单来自混音笔记，笔记可能刚改过。
        if (_page == AppPage.Lists)
            LoadLists();
        PluginPage.Visibility = pluginPage ? Visibility.Visible : Visibility.Collapsed;
        WishPage.Visibility = _page == AppPage.Wishes ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility = _page == AppPage.Accounts ? Visibility.Visible : Visibility.Collapsed;
        ProjectPage.Visibility = _page == AppPage.Projects ? Visibility.Visible : Visibility.Collapsed;
        ListPage.Visibility = _page == AppPage.Lists ? Visibility.Visible : Visibility.Collapsed;
        IdlePage.Visibility = _page == AppPage.Idle ? Visibility.Visible : Visibility.Collapsed;
        ChainPage.Visibility = _page == AppPage.Chains ? Visibility.Visible : Visibility.Collapsed;
        HealthPage.Visibility = _page == AppPage.Health ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = _page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        ExtensionPage.Visibility = _page == AppPage.Extension ? Visibility.Visible : Visibility.Collapsed;

        if (_page == AppPage.Extension)
        {
            ShowExtensionPage();
            return;
        }

        if (_page == AppPage.Settings)
        {
            ShowSettings();
            return;
        }
        if (_page == AppPage.Health)
            RefreshHealth();
        if (_page is AppPage.Projects or AppPage.Lists or AppPage.Idle or AppPage.Chains or AppPage.Health)
        {
            ShowProjectStatus();
            return;
        }
        if (!pluginPage)
        {
            ShowItemStatus();
            return;
        }

        var blacklist = _page == AppPage.Blacklist;
        PluginPageTitle.Text = blacklist ? "垃圾桶" : "插件库";
        PluginPageSubtitle.Text = blacklist ? "垃圾桶里的插件只在本软件中隐藏，点“卸载”才会动文件" : "已安装的效果器，不含音源";
        RecommendBar.Visibility = blacklist ? Visibility.Collapsed : Visibility.Visible;
        TrashBar.Visibility = blacklist ? Visibility.Visible : Visibility.Collapsed;
        PluginGrid.SelectionMode = blacklist ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
        UpdateRemovedFilesButton();
        UpdateUninstallHistoryButton();
        if (blacklist)
            ExitRecommendation();
        if (_view == null)
            return;
        PluginGrid.SelectedItem = null;
        RebuildFilterOptions();
        UpdateColumns();
        ApplyCategorySort();
        _view.Refresh();
        if (_need != null)
            ShowRecommendation();
        else
            UpdateCount();
    }

    private void LoadPlugins()
    {
        var selectedId = _current?.Row.Id;
        _fileOwners = null;
        _rememberedMatcher = null;
        _plugins = _database.LoadEffects().Select(p => new PluginViewModel(p)).ToList();
        _view = CollectionViewSource.GetDefaultView(_plugins);
        _view.Filter = Matches;
        RebuildFilterOptions();
        if (_need != null)
        {
            // 移入垃圾桶、扫描、导入之后结果可能变化，按同一需求重新计算。
            ShowRecommendation();
        }
        else
        {
            PluginGrid.ItemsSource = _view;
            _view.Refresh();
            UpdateCount();
        }
        PluginGrid.SelectedItem = _plugins.FirstOrDefault(p => p.Row.Id == selectedId);
        RefreshProjectReport();
    }

    private void RebuildFilterOptions()
    {
        _suppressFilter = true;
        SetOptions(VendorFilter, AllVendors, _plugins.Where(InCurrentView).Select(p => p.Vendor).Distinct().Order(StringComparer.OrdinalIgnoreCase));
        RebuildCategories();
        SetOptions(FormatFilter, AllFormats, Enum.GetValues<PluginFormat>().Select(Labels.Format));
        SetOptions(RatingFilter, AllRatings, [Rated, HighRated, LowRated, Unrated, HasNote]);
        _suppressFilter = false;
    }

    /// <summary>目录里的分类在前，用户新加的分类接在后面。</summary>
    private IEnumerable<string> AllKnownTags() =>
        Classifier.TagCatalog.Concat(_plugins.SelectMany(p => p.Row.Tags).Order(StringComparer.CurrentCulture)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static void SetOptions(ComboBox box, string all, IEnumerable<string> options)
    {
        var previous = box.SelectedItem as string;
        var items = new List<string> { all };
        items.AddRange(options);
        box.ItemsSource = items;
        box.SelectedItem = previous != null && items.Contains(previous) ? previous : all;
    }

    private bool Matches(object item)
    {
        var plugin = (PluginViewModel)item;
        if (!InCurrentView(plugin))
            return false;
        var search = SearchBox.Text.Trim().ToLowerInvariant();
        if (search.Length > 0)
        {
            var text = plugin.SearchText;
            if (!search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(text.Contains))
                return false;
        }
        if (VendorFilter.SelectedItem is string vendor && vendor != AllVendors && plugin.Vendor != vendor)
            return false;
        if (SelectedCategory is { } category && !category.Matches(plugin.Row))
            return false;
        if (FormatFilter.SelectedItem is string format && format != AllFormats && !plugin.Row.Files.Any(f => Labels.Format(f.Format) == format))
            return false;
        return (RatingFilter.SelectedItem as string) switch
        {
            Rated => plugin.Rating > 0,
            HighRated => plugin.Rating >= 4,
            LowRated => plugin.Rating is > 0 and <= 2,
            Unrated => plugin.Rating == 0,
            HasNote => plugin.Row.User.Note.Length > 0,
            _ => true,
        };
    }

    private bool InBlacklistView => _page == AppPage.Blacklist;

    private bool InCurrentView(PluginViewModel plugin) => plugin.IsBlacklisted == InBlacklistView;

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (_suppressFilter || _view == null)
            return;
        _view.Refresh();
        UpdateCount();
    }

    /// <summary>推荐理由较长，推荐时收起类型、我的标签和格式，把宽度让给它。</summary>
    private void UpdateColumns()
    {
        var recommending = _need != null;
        var general = recommending ? Visibility.Collapsed : Visibility.Visible;
        UsageColumn.Visibility = general;
        LastUsedColumn.Visibility = general;
        NoteColumn.Visibility = general;
        var wide = PluginGrid.ActualWidth == 0 || PluginGrid.ActualWidth >= 860;
        LinearPhaseColumn.Visibility = !recommending && wide ? Visibility.Visible : Visibility.Collapsed;
        SidechainColumn.Visibility = LinearPhaseColumn.Visibility;
        LatencyColumn.Visibility = general;
        CpuColumn.Visibility = general;
        RecommendColumn.Visibility = recommending ? Visibility.Visible : Visibility.Collapsed;
        ReasonColumn.Visibility = !recommending && InBlacklistView ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        SearchBox.Text = "";
        VendorFilter.SelectedIndex = 0;
        SelectCategory(null);
        FormatFilter.SelectedIndex = 0;
        RatingFilter.SelectedIndex = 0;
        _suppressFilter = false;
        _view?.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var shown = _view?.Cast<object>().Count() ?? 0;
        var blacklisted = _plugins.Count(p => p.IsBlacklisted);
        CountPlugins.Text = (_plugins.Count - blacklisted).ToString();
        CountBlacklist.Text = blacklisted > 0 ? blacklisted.ToString() : "";
        UpdatePluginEmpty(shown);
        StatusText.Text = _plugins.Count == 0
            ? "还没有数据，到左下方“设置”里扫描插件。"
            : $"效果器 {_plugins.Count - blacklisted} 款，垃圾桶 {blacklisted} 款，当前显示 {shown} 款";
    }

    private void UpdatePluginEmpty(int shown)
    {
        PluginEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginEmptyText.Text = _need != null ? "没有找到匹配的插件"
            : InBlacklistView && !_plugins.Any(p => p.IsBlacklisted) ? "垃圾桶是空的"
            : _plugins.Count == 0 ? "还没有数据，到左下方“设置”里扫描插件"
            : "没有符合筛选的插件";
    }

    // ---------- 推荐 ----------

    private void NeedBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        StartRecommendation();
        e.Handled = true;
    }

    private void Recommend_Click(object sender, RoutedEventArgs e) => StartRecommendation();

    private void ExitRecommend_Click(object sender, RoutedEventArgs e) => ExitRecommendation();

    private void StartRecommendation()
    {
        var need = NeedBox.Text.Trim();
        if (need.Length == 0)
        {
            ExitRecommendation();
            StatusText.Text = "请先描述你需要的功能，例如：人声去齿音";
            return;
        }
        SaveCurrent();
        _need = need;
        ShowNewRecommendation();
    }

    private void ShowNewRecommendation()
    {
        ShowRecommendation();

        // 最相关的排在最前，新结果要从头看，不能停在上一次的滚动位置。
        if (PluginGrid.Items.Count > 0)
            PluginGrid.ScrollIntoView(PluginGrid.Items[0]);
    }

    private void ShowRecommendation()
    {
        var result = Recommender.Recommend(_need!, _plugins.Select(p => p.Row));
        var byId = _plugins.ToDictionary(p => p.Row.Id);
        var items = new List<PluginViewModel>();
        foreach (var item in result.Items)
        {
            var viewModel = byId[item.Plugin.Id];
            viewModel.RecommendReason = string.Join("；", item.Reasons);
            items.Add(viewModel);
        }

        PluginGrid.ItemsSource = items;
        FilterRow.IsEnabled = false;
        FilterRow.Opacity = 0.45;
        CategoryPanel.IsEnabled = false;
        CategoryPanel.Opacity = 0.45;
        ExitRecommendButton.Visibility = Visibility.Visible;
        UpdateColumns();
        UpdatePluginEmpty(items.Count);
        StatusText.Text = DescribeRecommendation(result);
    }

    private void ExitRecommendation()
    {
        if (_need == null)
            return;
        _need = null;
        FilterRow.IsEnabled = true;
        FilterRow.Opacity = 1;
        CategoryPanel.IsEnabled = true;
        CategoryPanel.Opacity = 1;
        ExitRecommendButton.Visibility = Visibility.Collapsed;
        UpdateColumns();
        PluginGrid.ItemsSource = _view;
        ApplyCategorySort();
        _view?.Refresh();
        UpdateCount();
    }

    private static string DescribeRecommendation(RecommendResult result)
    {
        var parts = new List<string>();
        if (result.Understood.Count > 0)
            parts.Add("需求：" + string.Join("、", result.Understood));
        if (result.Vendors.Count > 0)
            parts.Add("厂家：" + string.Join("、", result.Vendors));
        if (result.Excluded.Count > 0)
            parts.Add("排除：" + string.Join("、", result.Excluded));
        if (result.NotUnderstood.Count > 0)
            parts.Add("没有理解：" + string.Join("、", result.NotUnderstood));

        if (result.NoPluginFor is { Count: > 0 } missing)
            parts.Add("当前列表中没有对应：" + string.Join("、", missing));
        if (result.AttributeHint != null)
            parts.Add(result.AttributeHint);

        var nothingUnderstood = result.Understood.Count == 0 && result.Vendors.Count == 0;
        string outcome;
        if (result.TotalMatches == 0)
        {
            outcome = nothingUnderstood
                ? "没有找到匹配的插件，可以换个说法，例如：压缩、EQ、混响、去齿音、人声、磁带饱和"
                : "已安装的效果器中没有符合的（本软件不包含音源）";
        }
        else
        {
            outcome = $"找到 {result.TotalMatches} 款，最相关的排在最前";
        }
        parts.Add(outcome);
        return string.Join("  |  ", parts);
    }

    // ---------- 编辑面板 ----------

    private void PluginGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切换前先保存，否则还在等待自动保存的内容会写到下一个插件上。
        SaveCurrent();
        _current = PluginGrid.SelectedItem as PluginViewModel;
        ShowDetail();
        UpdateUninstallButton();
    }

    private void ShowDetail()
    {
        EmptyHint.Visibility = _current == null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = _current == null ? Visibility.Collapsed : Visibility.Visible;
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
        KindBox.SelectedIndex = Array.IndexOf(Kinds, row.Kind);
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

        BlacklistButton.Content = row.User.Hidden ? "从垃圾桶恢复" : "移入垃圾桶";
        DeleteRecordButton.Visibility = TrashMemory.IsRemembered(row) ? Visibility.Visible : Visibility.Collapsed;
        DetailTitle.Text = row.Name;
        ShowDerivedDetail(row);
        ShowUsageDetail(row);
        ShowSharedReviews(row);
        UpdateStars();
        RebuildChips();
        RebuildTimbreChips();

        var uninstalled = TrashMemory.IsRemembered(row) ? UninstallHistory.ForPlugin(row, _database.LoadUninstallHistory()) : null;
        DetailFiles.Text = TrashMemory.IsRemembered(row)
            ? (uninstalled != null ? UninstallHistory.Describe(uninstalled) + "。" : "已经卸载。") + "上面的原因、评分和感受会一直留着，以后想再加入心愿单时会提醒你。卸载历史在垃圾桶上方。"
            : row.IsMissing ? "上次扫描时没有找到此插件的文件。" : "";
        DetailFiles.Visibility = row.IsMissing ? Visibility.Visible : Visibility.Collapsed;
        ShowInstalledVersions(row);
    }

    private void ShowDerivedDetail(PluginRow row)
    {
        DetailMeta.Text = Labels.TagSource(row.TagSource);
        ResetButton.IsEnabled = row.User.HasOverrides;
    }

    private void UpdateStars()
    {
        for (var i = 0; i < _stars.Length; i++)
            _stars[i].Foreground = ThemeManager.Resource(i < _rating ? "Accent" : "LineStrong");
    }

    private void RebuildChips()
    {
        if (_current == null)
            return;
        var active = (_tagsOverride ?? (IEnumerable<string>)_current.Row.VisibleAutoTags).ToList();
        var chipStyle = (Style)FindResource("TagChip");
        var known = AllKnownTags().Concat(active).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var expanded = TagsExpanded;
        TagExpandButton.Content = expanded ? "收起" : $"编辑分类（共 {known.Count} 个）";
        NewTagPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        TagChipsEmpty.Visibility = !expanded && active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TagChips.Children.Clear();
        foreach (var tag in expanded ? known : active)
        {
            var chip = new ToggleButton
            {
                Content = tag,
                Tag = tag,
                Style = chipStyle,
                IsChecked = active.Contains(tag, StringComparer.OrdinalIgnoreCase),
            };
            AutomationProperties.SetAutomationId(chip, "Tag_" + tag);
            // 用状态事件而不是 Click：辅助功能切换开关时不会触发 Click。
            // 初始状态已在上面设好，订阅放在之后，所以重建标签不会触发保存。
            chip.Checked += Chip_Changed;
            chip.Unchecked += Chip_Changed;
            TagChips.Children.Add(chip);
        }
    }

    private List<string>? _timbresOverride;

    /// <summary>音色分类只有 8 个，全部列出来直接点选。</summary>
    private void RebuildTimbreChips()
    {
        if (_current == null)
            return;
        var active = _timbresOverride ?? (IEnumerable<string>)_current.Row.Timbres;
        var chipStyle = (Style)FindResource("TagChip");
        TimbreChips.Children.Clear();
        foreach (var timbre in Classifier.TimbreCatalog)
        {
            var chip = new ToggleButton
            {
                Content = timbre,
                Tag = timbre,
                Style = chipStyle,
                IsChecked = active.Contains(timbre, StringComparer.OrdinalIgnoreCase),
            };
            AutomationProperties.SetAutomationId(chip, "Timbre_" + timbre);
            chip.Checked += TimbreChip_Changed;
            chip.Unchecked += TimbreChip_Changed;
            TimbreChips.Children.Add(chip);
        }
    }

    private void TimbreChip_Changed(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        var chip = (ToggleButton)sender;
        var timbre = (string)chip.Tag;
        var timbres = (_timbresOverride ?? (IEnumerable<string>)_current.Row.Timbres).ToList();
        timbres.RemoveAll(t => t.Equals(timbre, StringComparison.OrdinalIgnoreCase));
        if (chip.IsChecked == true)
            timbres.Add(timbre);
        timbres = Classifier.CleanTimbres(timbres);
        // 和自动结果一致时不算手动修改。
        var auto = Classifier.TimbresIn(_tagsOverride ?? (IEnumerable<string>)_current.Row.AutoTags);
        _timbresOverride = timbres.SequenceEqual(auto, StringComparer.OrdinalIgnoreCase) ? null : timbres;
        SaveCurrent(force: true);
    }

    private const string TagsExpandedSetting = "detail.tagsExpanded";
    private bool? _tagsExpanded;

    /// <summary>默认收起：全部分类有几十个，占掉半个详情面板，平时只需要看已选的。</summary>
    private bool TagsExpanded => _tagsExpanded ??= _database.GetSetting(TagsExpandedSetting) == "1";

    private void TagExpand_Click(object sender, RoutedEventArgs e)
    {
        _tagsExpanded = !TagsExpanded;
        _database.SetSetting(TagsExpandedSetting, _tagsExpanded == true ? "1" : "0");
        RebuildChips();
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        var value = int.Parse((string)((Button)sender).Tag);
        _rating = value == _rating ? 0 : value;
        UpdateStars();
        SaveCurrent(force: true);
    }

    private void Chip_Changed(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        var chip = (ToggleButton)sender;
        var tag = (string)chip.Tag;
        var tags = (_tagsOverride ?? (IEnumerable<string>)_current.Row.VisibleAutoTags).ToList();
        tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (chip.IsChecked == true)
            tags.Add(tag);
        SetTags(tags);
    }

    /// <summary>选择结果和自动分类一致时不算手动修改，以后资料库更新还能生效。</summary>
    private void SetTags(List<string> tags)
    {
        var auto = _current!.Row.VisibleAutoTags;
        var sameAsAuto = tags.Count == auto.Count && tags.All(t => auto.Contains(t, StringComparer.OrdinalIgnoreCase));
        _tagsOverride = sameAsAuto ? null : tags;
        SaveCurrent(force: true);
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) => AddNewTag();

    private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        AddNewTag();
        e.Handled = true;
    }

    private void AddNewTag()
    {
        if (_current == null)
            return;
        var added = SplitTags(NewTagBox.Text);
        if (added.Count == 0)
            return;
        NewTagBox.Text = "";
        // 人声、鼓这些属于音色分类，写在这里就替用户勾上对应的音色。
        var timbres = Classifier.TimbresIn(added);
        if (timbres.Count > 0)
        {
            _timbresOverride = Classifier.CleanTimbres((_timbresOverride ?? (IEnumerable<string>)_current.Row.Timbres).Concat(timbres));
            RebuildTimbreChips();
        }
        added = Classifier.FunctionTagsIn(added).ToList();
        var tags = (_tagsOverride ?? (IEnumerable<string>)_current.Row.VisibleAutoTags).ToList();
        tags.AddRange(added.Where(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        SetTags(tags);
        RebuildChips();
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingDetail)
            SaveCurrent(force: true);
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingDetail)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PurchaseKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingDetail)
            SaveCurrent(force: true);
    }

    private void MixField_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingDetail)
            SaveCurrent(force: true);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        var row = _current.Row;
        _tagsOverride = null;
        _timbresOverride = null;
        _loadingDetail = true;
        VendorBox.Text = row.AutoVendor;
        KindBox.SelectedIndex = Array.IndexOf(Kinds, row.AutoKind);
        _loadingDetail = false;
        SaveCurrent(force: true);
        RebuildChips();
        RebuildTimbreChips();
    }

    private static List<string> SplitTags(string text) =>
        text.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private PluginUserData ReadPanel(PluginRow row)
    {
        var kind = KindBox.SelectedIndex >= 0 ? Kinds[KindBox.SelectedIndex] : row.AutoKind;
        var vendor = VendorBox.Text.Trim();
        return new PluginUserData(
            _rating,
            NoteBox.Text,
            SplitTags(MyTagsBox.Text),
            _tagsOverride,
            kind == row.AutoKind ? null : kind,
            vendor.Length == 0 || vendor == row.AutoVendor ? null : vendor,
            row.User.Hidden,
            ReasonBox.Text,
            new MixAttributes(
                (LatencyLevel)Math.Max(MixLatency.SelectedIndex, 0),
                (CpuLevel)Math.Max(MixCpu.SelectedIndex, 0),
                (Tri)Math.Max(MixLinearPhase.SelectedIndex, 0),
                (Tri)Math.Max(MixSidechain.SelectedIndex, 0),
                (Tri)Math.Max(MixOversampling.SelectedIndex, 0)),
            _timbresOverride,
            (PurchaseKind)Math.Max(PurchaseKindBox.SelectedIndex, 0)).Normalized();
    }

    /// <summary>切换是否在垃圾桶。原因是用户写的内容，恢复时也保留，再次移入不用重写。</summary>
    private void DeleteUninstalledRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } plugin || !TrashMemory.IsRemembered(plugin.Row))
            return;
        SaveCurrent();
        if (!Dialogs.Confirm(this, "删除记录", $"删除 {plugin.Name} 的记录？它已经卸载，删掉后移入原因、评分和感受都不再保留，以后加入心愿单也不会再提醒。", "删除", danger: true))
            return;
        _current = null;
        _database.DeleteUninstalledRecord(plugin.Row.Id);
        LoadPlugins();
        StatusText.Text = $"已删除 {plugin.Name} 的记录";
    }

    private void Blacklist_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        SaveCurrent();
        var row = _current.Row;
        var data = ReadPanel(row) with { Hidden = !row.User.Hidden };
        try
        {
            _database.SaveUserData(row.Id, data);
            RecordUsage(() => _database.RecordAction());
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }

        // 这款插件会离开当前视图，选择随之清空，面板回到空白状态。
        LoadPlugins();
        StatusText.Text = data.Hidden
            ? $"已把 {row.Name} 移入垃圾桶，可在左侧“垃圾桶”查看或恢复"
            : $"已把 {row.Name} 从垃圾桶恢复";
    }

    /// <summary>
    /// 把面板内容写入数据库并刷新这一行。不重新应用筛选：
    /// 否则在“未评分”筛选下打分，正在编辑的插件会立刻从列表消失。
    /// </summary>
    private void SaveCurrent(bool force = false)
    {
        var timerPending = _saveTimer.IsEnabled;
        _saveTimer.Stop();
        if (_current == null || (!force && !timerPending))
            return;

        var row = _current.Row;
        var data = ReadPanel(row);
        try
        {
            _database.SaveUserData(row.Id, data);
            RecordUsage(() =>
            {
                _database.RecordAction();
                if (data.Purchase != row.User.Purchase)
                    _database.RecordUsage(UsageKinds.PurchaseChange, (int)data.Purchase, (int)row.User.Purchase, row.Key);
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
            return;
        }

        var updated = row with { User = data };
        _current.Update(updated);
        if (ReferenceEquals(_current, PluginGrid.SelectedItem) || _page == AppPage.Lists)
        {
            ShowDerivedDetail(updated);
            UpdateAdoptionBadge(updated);
            if (SelectedShared is { } shared)
                ShowSharedReview(updated, shared);
        }

        var vendorsOrTagsChanged = row.Vendor != updated.Vendor || !row.Tags.SequenceEqual(updated.Tags) || !row.Timbres.SequenceEqual(updated.Timbres);
        if (vendorsOrTagsChanged)
            RebuildFilterOptions();
        StatusText.Text = $"已保存 {row.Name}";
    }

    // ---------- 备份 ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        FlushItems();
        FlushChain();
        var secretCount = _database.LoadWishes().Count(w => w.Account.Length > 0 || w.Password.Length > 0 || w.Serial.Length > 0 || w.SecretsLocked)
            + _database.LoadPlatformAccounts().Count;
        var options = BackupDialogs.ExportOptions(this, secretCount);
        if (options == null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出备份",
            Filter = "What Plugin 备份 (*.json)|*.json",
            FileName = $"WhatPlugin-备份-{DateTime.Now:yyyyMMdd}.json",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var content = BackupRestore.Collect(_database, options.Value.IncludeSecrets, out var locked);
            File.WriteAllText(dialog.FileName, UserDataBackup.Serialize(content, options.Value.IncludeSecrets ? options.Value.Password : null));
            RecordUsage(() => _database.RecordUsage(UsageKinds.BackupExport));
            var parts = new List<string>
            {
                $"{content.Plugins.Count} 款插件的记录",
                $"{content.Wishes.Count} 条心愿和已购",
                $"{content.Favorites.Count} 个常用清单",
                $"{content.Chains.Count} 条插件链",
                "设置",
                $"{content.Reviews?.Sources?.Count ?? 0} 位分享者的评价",
            };
            var secrets = content.Secrets == null
                ? "，没有包含账号密码"
                : $"，账号密码 {content.Secrets.WishCount} 条、厂家账号 {content.Secrets.PlatformCount} 个已用备份密码加密"
                  + (locked > 0 ? $"（{locked} 条密码是另一个 Windows 账号加密的，解不开，没有导出）" : "");
            StatusText.Text = "已导出 " + string.Join("、", parts) + secrets;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var dialog = new OpenFileDialog { Title = "导入备份", Filter = "What Plugin 备份 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var backup = UserDataBackup.Read(File.ReadAllText(dialog.FileName));
            BackupSecrets? secrets = null;
            if (backup.HasSecrets)
            {
                var password = BackupDialogs.AskPassword(this, attempt =>
                {
                    try
                    {
                        secrets = UserDataBackup.DecryptSecrets(backup, attempt);
                        return null;
                    }
                    catch (BackupPasswordException error)
                    {
                        return error.Message;
                    }
                    catch (FormatException error)
                    {
                        return error.Message;
                    }
                });
                if (password == null)
                    return;
                if (password.Length == 0)
                    secrets = null;
            }

            var content = backup.Content;
            var settings = content.Settings;
            var lines = new List<string>
            {
                $"插件记录 {content.Plugins.Count} 款，心愿和已购 {content.Wishes.Count} 条",
                $"常用清单 {content.Favorites.Count} 个，插件链 {content.Chains.Count} 条",
            };
            if (settings != null)
                lines.Add($"设置 {settings.Values?.Count ?? 0} 项，工程文件夹 {settings.ProjectFolders?.Count ?? 0} 个，VST2 文件夹 {settings.Vst2Folders?.Count ?? 0} 个，忽略的插件 {settings.IgnoredProjectPlugins?.Count ?? 0} 个");
            if (content.Reviews != null)
                lines.Add($"别人的评价 {content.Reviews.Sources?.Count ?? 0} 位，采用记录 {content.Reviews.Adoptions?.Count ?? 0} 条");
            lines.Add(secrets != null
                ? $"账号密码 {secrets.WishCount} 条，厂家账号 {secrets.PlatformCount} 个"
                : backup.HasSecrets ? "账号密码：已跳过，本机的保持不变" : "账号密码：这份备份里没有，本机的保持不变");
            if (!Dialogs.Confirm(this, "导入备份",
                    "备份中有：\n· " + string.Join("\n· ", lines)
                    + "\n\n相同条目现有的内容会被覆盖，同名清单会合并，同名插件链会被备份内容替换；文件夹和忽略列表只增不减，这台电脑上不存在的文件夹不会添加。",
                    "导入", danger: true))
                return;

            FlushItems();
            FlushChain();
            Mouse.OverrideCursor = Cursors.Wait;
            RestoreSummary result;
            try
            {
                result = BackupRestore.Apply(_database, content, secrets);
                RecordUsage(() => _database.RecordUsage(UsageKinds.BackupImport, result.Plugins.Updated + result.Plugins.Added));
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            ReloadAfterImport();

            var done = new List<string>
            {
                $"插件更新 {result.Plugins.Updated} 款（另有 {result.Plugins.Added} 款当前未安装，已保留记录）",
                $"心愿和已购新增 {result.Wishes.Added} 条、更新 {result.Wishes.Updated} 条",
                $"常用清单 {result.Lists} 个",
                $"插件链 {result.Chains} 条",
            };
            if (settings != null)
                done.Add($"设置 {result.Settings} 项、文件夹 {result.Folders} 个" + (result.FoldersMissing > 0 ? $"（{result.FoldersMissing} 个在这台电脑上不存在，未添加）" : ""));
            if (result.ReviewSources > 0 || result.Adoptions > 0)
                done.Add($"别人的评价 {result.ReviewSources} 位、采用记录 {result.Adoptions} 条");
            if (secrets != null)
                done.Add($"账号密码 {result.WishSecrets} 条、厂家账号 {result.Platforms} 个");
            if (result.UsageEvents > 0 || result.Achievements > 0)
                done.Add($"使用记录 {result.UsageEvents} 条、成就 {result.Achievements} 个");
            if (result.Uninstalls > 0)
                done.Add($"卸载历史 {result.Uninstalls} 条");
            StatusText.Text = "已导入：" + string.Join("；", done);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusText.Text = "导入失败：" + error.Message;
        }
    }

    /// <summary>导入会改到几乎所有页面用的数据，包括主题和界面状态，全部重新读一遍。</summary>
    private void ReloadAfterImport()
    {
        _current = null;
        _tagsExpanded = null;
        _projectTreeVisible = null;
        _collapsedFolders = null;
        _adoptions = null;
        InitializeSettings();
        LoadPlugins();
        ShowDetail();
        LoadItems();
        LoadLists();
        LoadChains();
        LoadProjectFolders();
        ReloadReviews();
        RefreshProjectReport();
        if (_page == AppPage.Settings)
            ShowSettings();
    }
}
