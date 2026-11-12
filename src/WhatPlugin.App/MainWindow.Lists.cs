using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>
/// 常用清单：上面是按混音笔记标签自动整理的清单（不能手动增删，可以隐藏插件），下面是自己建的清单。
/// 选中清单里的插件，右边显示和插件库一样的详情面板，改动直接存进插件库。
/// </summary>
public partial class MainWindow
{
    private List<AutoList> _autoLists = [];
    private Grid? _detailHome;
    private int _detailHomeColumn;

    private void LoadLists(long? select = null, string? selectTag = null)
    {
        var current = ListsBox.SelectedItem as ListEntry;
        selectTag ??= select == null ? current?.Auto?.Tag : null;
        select ??= selectTag == null ? current?.Manual?.Id : null;

        _lists = _database.LoadFavoriteLists();
        _autoLists = AutoLists.Build(_database.LoadChains(), _database.LoadEffects(), LoadHiddenAuto()).ToList();

        var entries = new List<ListEntry>();
        if (_autoLists.Count > 0)
        {
            entries.Add(ListEntry.Header("按混音笔记自动整理"));
            entries.AddRange(_autoLists.Select(ListEntry.ForAuto));
        }
        if (_lists.Count > 0)
        {
            entries.Add(ListEntry.Header("我的清单"));
            entries.AddRange(_lists.Select(ListEntry.ForManual));
        }
        ListsBox.ItemsSource = entries;
        ListsBox.SelectedItem =
            entries.FirstOrDefault(e => selectTag != null && e.Auto?.Tag == selectTag)
            ?? entries.FirstOrDefault(e => select != null && e.Manual?.Id == select)
            ?? entries.FirstOrDefault(e => !e.IsHeader);
        var total = _autoLists.Count + _lists.Count;
        CountLists.Text = total > 0 ? total.ToString() : "";
        ShowListDetail();
    }

    private Dictionary<string, IReadOnlySet<string>> LoadHiddenAuto() => AutoLists.ReadHidden(_database.GetSetting(AutoLists.HiddenSettingKey));

    private string DescribeLists() =>
        _autoLists.Count + _lists.Count == 0 ? "给混音笔记加标签后，这里会自动整理出清单"
        : $"自动整理的清单 {_autoLists.Count} 个，自己建的清单 {_lists.Count} 个";

    private ListEntry? CurrentEntry => ListsBox.SelectedItem as ListEntry;

    private FavoriteList? CurrentList => CurrentEntry?.Manual;

    private void ListsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentEntry is { IsHeader: true })
            return;
        ShowListDetail();
    }

    private void ListSearch_Changed(object sender, TextChangedEventArgs e) => ShowListCandidates();

    private void NewListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        AddList();
        e.Handled = true;
    }

    private void AddList_Click(object sender, RoutedEventArgs e) => AddList();

    private void AddList()
    {
        try
        {
            var list = _database.AddFavoriteList(NewListBox.Text);
            NewListBox.Text = "";
            LoadLists(list.Id);
            StatusText.Text = $"已新建清单“{list.Name}”";
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private void RenameList_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentList is not { } list)
            return;
        var name = Dialogs.Prompt(this, "重命名清单", "新的名称：", "保存", list.Name);
        if (name == null)
            return;
        try
        {
            _database.RenameFavoriteList(list.Id, name);
            LoadLists(list.Id);
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private void DeleteList_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentList is not { } list)
            return;
        if (!Dialogs.Confirm(this, "删除清单", $"删除清单“{list.Name}”？只删除清单本身，插件不受影响。", "删除", danger: true))
            return;
        _database.DeleteFavoriteList(list.Id);
        LoadLists();
        StatusText.Text = $"已删除清单“{list.Name}”";
    }

    private void ShowListDetail()
    {
        if (!_projectUiReady)
            return;
        var entry = CurrentEntry is { IsHeader: false } e ? e : null;
        ListDetail.Visibility = entry != null ? Visibility.Visible : Visibility.Collapsed;
        ListEmpty.Visibility = entry == null ? Visibility.Visible : Visibility.Collapsed;
        var selectedKey = (ListItems.SelectedItem as FavoritePluginViewModel)?.Key;
        if (entry == null)
        {
            ListItems.ItemsSource = null;
            ShowListSide();
            return;
        }

        ListTitle.Text = entry.Label;
        ManualListButtons.Visibility = entry.Manual != null ? Visibility.Visible : Visibility.Collapsed;
        List<FavoritePluginViewModel> items;
        if (entry.Auto is { } auto)
        {
            var showHidden = ShowHiddenBox.IsChecked == true;
            ShowHiddenBox.Visibility = auto.Hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowHiddenBox.Content = $"显示已隐藏的 {auto.Hidden.Count} 款";
            items = auto.Items.Select(i => FavoritePluginViewModel.ForAuto(i, hidden: false))
                .Concat(showHidden ? auto.Hidden.Select(i => FavoritePluginViewModel.ForAuto(i, hidden: true)) : [])
                .ToList();
            ListMeta.Text = $"根据 {auto.NoteCount} 条带“{auto.Tag}”标签的混音笔记整理，按出现次数排序（首选算 1 次，备选算半次）。改笔记后会自动更新";
            ListItemsEmpty.Text = auto.Hidden.Count > 0 ? "插件都隐藏了" : "这些笔记里还没有已安装的插件";
        }
        else
        {
            var list = entry.Manual!;
            ShowHiddenBox.Visibility = Visibility.Collapsed;
            // 音源不进入这个软件的任何列表，备份里带来的音源键也不显示。
            var rows = _database.LoadEffects().ToDictionary(p => p.Key);
            items = list.PluginKeys
                .Where(rows.ContainsKey)
                .Select(key => new FavoritePluginViewModel(rows[key], _report.Usage.GetValueOrDefault(key)))
                .ToList();
            ListMeta.Text = items.Count == 0 ? "还没有插件" : $"{items.Count} 款插件，点一下在右边查看和修改详情";
            ListItemsEmpty.Text = "从右边添加插件";
        }
        ListItems.ItemsSource = items;
        ListItems.SelectedItem = items.FirstOrDefault(i => i.Key == selectedKey);
        ListItemsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowListSide();
    }

    private void ShowHidden_Changed(object sender, RoutedEventArgs e) => ShowListDetail();

    /// <summary>右边：选中了插件就显示详情；自己建的清单没选插件时显示添加插件。</summary>
    private void ShowListSide()
    {
        var selected = ListItems.SelectedItem as FavoritePluginViewModel;
        var showCandidates = selected == null && CurrentList != null;
        ListCandidatesCard.Visibility = showCandidates ? Visibility.Visible : Visibility.Collapsed;
        DetailCard.Visibility = showCandidates ? Visibility.Collapsed : Visibility.Visible;
        if (showCandidates)
            ShowListCandidates();
    }

    private void ShowListCandidates_Click(object sender, RoutedEventArgs e)
    {
        ListItems.SelectedItem = null;
        ListSearchBox.Focus();
    }

    private void ListItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_page != AppPage.Lists)
            return;
        SaveCurrent();
        var key = (ListItems.SelectedItem as FavoritePluginViewModel)?.Key;
        _current = key == null ? null : _plugins.FirstOrDefault(p => p.Row.Key == key);
        ShowDetail();
        ShowListSide();
    }

    /// <summary>详情面板只有一个：在常用清单页时借过来放在右边，离开时放回插件库。</summary>
    private void PlaceDetailCard(bool onListPage)
    {
        if (_detailHome == null)
        {
            _detailHome = (Grid)DetailCard.Parent;
            _detailHomeColumn = Grid.GetColumn(DetailCard);
        }
        var target = onListPage ? ListArea : _detailHome;
        if (ReferenceEquals(DetailCard.Parent, target))
            return;

        // 换位置前保存正在编辑的内容，并清空面板，避免把一边的插件带到另一边。
        SaveCurrent();
        _current = null;
        ((Panel)DetailCard.Parent).Children.Remove(DetailCard);
        target.Children.Add(DetailCard);
        Grid.SetColumn(DetailCard, onListPage ? 2 : _detailHomeColumn);
        DetailCard.Visibility = Visibility.Visible;
        if (onListPage)
        {
            ListItems.SelectedItem = null;
            ShowListSide();
        }
        ShowDetail();
    }

    private void HideInAutoList_Click(object sender, RoutedEventArgs e) => SetAutoHidden(sender, hide: true);

    private void RestoreInAutoList_Click(object sender, RoutedEventArgs e) => SetAutoHidden(sender, hide: false);

    private void SetAutoHidden(object sender, bool hide)
    {
        if (((FrameworkElement)sender).Tag is not string key || CurrentEntry?.Auto is not { } auto)
            return;
        var hidden = AutoLists.SetHidden(LoadHiddenAuto(), auto.Tag, key, hide);
        _database.SetSetting(AutoLists.HiddenSettingKey, AutoLists.WriteHidden(hidden));
        var name = auto.Items.Concat(auto.Hidden).FirstOrDefault(i => i.Plugin.Key == key)?.Plugin.Name ?? "插件";
        LoadLists(selectTag: auto.Tag);
        StatusText.Text = hide ? $"已在“{auto.Name}”里隐藏 {name}，勾选“显示已隐藏”可以恢复" : $"已在“{auto.Name}”里恢复 {name}";
    }

    /// <summary>
    /// 自动清单里的“删除”：从带这个标签的笔记里删掉这款插件，清单跟着更新。
    /// 只是不想在清单里看到，用隐藏就够了，所以要先确认。
    /// </summary>
    private void DeleteFromAutoList_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string key || CurrentEntry?.Auto is not { } auto)
            return;
        var item = auto.Items.Concat(auto.Hidden).FirstOrDefault(i => i.Plugin.Key == key);
        if (item == null)
            return;

        FlushChain();
        var rows = _database.LoadPlugins();
        var changes = _database.LoadChains()
            .Where(c => c.TagList.Contains(auto.Tag, StringComparer.OrdinalIgnoreCase))
            .Select(c => (Chain: c, Steps: AutoLists.WithoutPlugin(c, key, rows)))
            .Where(x => x.Steps != null)
            .ToList();
        if (changes.Count == 0)
            return;
        var names = string.Join("、", changes.Take(8).Select(x => $"“{x.Chain.Name}”")) + (changes.Count > 8 ? $" 等 {changes.Count} 条" : "");
        if (!Dialogs.Confirm(this, "从笔记里删除插件",
                $"从带“{auto.Tag}”标签的 {changes.Count} 条混音笔记里删除 {item.Plugin.Name}：{names}。\n\n"
                + "当首选的那一步由第一个备选顶上，没有备选就删掉这一步，这一步写的思路也会一起删掉。插件本身不受影响。\n"
                +"只是不想在这个清单里看到它的话，用旁边的隐藏就可以。",
                "删除", danger: true))
            return;

        foreach (var (chain, steps) in changes)
            _database.SaveChain(chain.Id, chain.Note, steps!);
        RecordUsage(() => _database.RecordAction());
        LoadChains();
        LoadLists(selectTag: auto.Tag);
        StatusText.Text = $"已从 {changes.Count} 条笔记里删除 {item.Plugin.Name}";
    }

    /// <summary>右侧：没有搜索时按工程使用次数给建议，有搜索时列出匹配的已安装效果器。</summary>
    private void ShowListCandidates()
    {
        if (!_projectUiReady || CurrentList is not { } list)
            return;
        var inList = list.PluginKeys.ToHashSet();
        var search = ListSearchBox.Text.Trim().ToLowerInvariant();
        if (search.Length == 0)
        {
            ListCandidateLabel.Text = "按工程使用次数建议";
            var suggested = ProjectReport.SuggestForList(_plugins.Select(p => p.Row), _report.Usage, inList);
            ListCandidates.ItemsSource = suggested.Select(s => new FavoritePluginViewModel(s.Plugin, s.Usage)).ToList();
            ListCandidatesEmpty.Text = _report.Usage.Count == 0 ? "扫描工程后，这里会列出你用得最多的插件" : "常用的都加进来了";
            ListCandidatesEmpty.Visibility = suggested.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        ListCandidateLabel.Text = "搜索结果";
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _plugins
            .Where(p => !p.IsBlacklisted && !p.Row.IsMissing && !inList.Contains(p.Row.Key) && words.All(p.SearchText.Contains))
            .OrderByDescending(p => p.Usage?.Projects ?? 0)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(p => new FavoritePluginViewModel(p.Row, p.Usage))
            .ToList();
        ListCandidates.ItemsSource = matches;
        ListCandidatesEmpty.Text = "没有匹配的插件";
        ListCandidatesEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCurrentList(Func<List<string>, List<string>> change)
    {
        if (CurrentList is not { } list)
            return;
        _database.SetFavoriteItems(list.Id, change(list.PluginKeys.ToList()));
        RecordUsage(() => _database.RecordAction());
        LoadLists(list.Id);
    }

    private void AddToList_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string key)
            return;
        UpdateCurrentList(keys => [.. keys, key]);
    }

    private void RemoveFromList_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string key)
            return;
        if (ListItems.SelectedItem is FavoritePluginViewModel { } selected && selected.Key == key)
            ListItems.SelectedItem = null;
        UpdateCurrentList(keys => keys.Where(k => k != key).ToList());
        StatusText.Text = "已从清单移除";
    }

    private void MoveInList_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not string key)
            return;
        var delta = (string)button.CommandParameter == "up" ? -1 : 1;
        UpdateCurrentList(keys =>
        {
            // 列表里可能有不显示的键（音源），按显示的顺序移动才符合看到的结果。
            var visible = ((IEnumerable<FavoritePluginViewModel>)ListItems.ItemsSource).Select(i => i.Key).ToList();
            var at = visible.IndexOf(key);
            var target = at + delta;
            if (at < 0 || target < 0 || target >= visible.Count)
                return keys;
            (visible[at], visible[target]) = (visible[target], visible[at]);
            return [.. visible, .. keys.Where(k => !visible.Contains(k))];
        });
    }
}

/// <summary>清单列表的一项：分组标题、自动清单或自己建的清单。沿用分类栏的样式。</summary>
public sealed record ListEntry(string Label, string CountText, bool IsHeader, AutoList? Auto, FavoriteList? Manual)
{
    public static ListEntry Header(string text) => new(text, "", true, null, null);

    public static ListEntry ForAuto(AutoList list) => new(list.Name, list.Items.Count.ToString(), false, list, null);

    public static ListEntry ForManual(FavoriteList list) => new(list.Name, list.PluginKeys.Count.ToString(), false, null, list);

    public override string ToString() => Label;
}
