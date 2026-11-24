using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>批量添加时可以勾选的一款已安装插件。</summary>
public sealed class BatchPickRow(string name, string vendor, bool exists) : Observable
{
    private bool _selected;

    public string Name { get; } = name;
    public string Vendor { get; } = vendor;
    public bool Exists { get; } = exists;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Notify(nameof(Selected));
        }
    }
}

/// <summary>
/// 和 Windows 版一样的两种方式：从已安装的插件里勾选（可以统一填账号密码序列号），或者粘贴文本。
/// 关闭时返回要写入的记录，取消返回 null。
/// </summary>
public sealed class BatchAddDialog : Window
{
    private readonly IDialogs _dialogs;
    private readonly List<BatchPickRow> _rows;
    private readonly RadioButton _modePick;
    private readonly Control _pickPanel;
    private readonly Control _pastePanel;
    private readonly ComboBox _vendor;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBox _account;
    private readonly SecretBox _password;
    private readonly SecretBox _serial;
    private readonly TextBox _paste;
    private readonly TextBlock _summary;
    private readonly Button _apply;

    public BatchAddDialog(IReadOnlyList<PluginRow> installed, IReadOnlyList<WishItem> purchased, IDialogs dialogs)
    {
        _dialogs = dialogs;
        Title = "批量添加已购插件";
        Width = 660;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Surface"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Text"));
        this.Bind(FontFamilyProperty, this.GetResourceObservable("UiFont"));
        FontSize = 13;

        var existing = purchased.Select(p => Normalizer.Name(p.Name)).ToHashSet();
        _rows = installed.Select(p => new BatchPickRow(p.Name, p.Vendor, existing.Contains(Normalizer.Name(p.Name)))).ToList();

        var body = new DockPanel { Margin = new Thickness(24, 22, 24, 18) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "批量添加已购插件", Classes = { "SectionTitle" } });
        top.Children.Add(new TextBlock { Text = "已经在列表里的插件不会重复添加，只更新填写的内容。", Classes = { "SmallNote" }, Margin = new Thickness(0, 6, 0, 12) });
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _modePick = new RadioButton { Content = "从已安装的插件选", Classes = { "Segment" }, GroupName = "BatchMode", IsChecked = true, MinWidth = 130, Margin = new Thickness(0, 0, 6, 0) };
        var modePaste = new RadioButton { Content = "粘贴文本", Classes = { "Segment" }, GroupName = "BatchMode", MinWidth = 96 };
        modes.Children.Add(_modePick);
        modes.Children.Add(modePaste);
        top.Children.Add(modes);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);

        // 底部：汇总和按钮
        _summary = new TextBlock { Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _apply = new Button { Content = "添加", Classes = { "Primary" }, MinWidth = 90, IsDefault = true, Margin = new Thickness(6, 0, 0, 0) };
        var cancel = new Button { Content = "取消", Classes = { "Ghost" }, IsCancel = true };
        _apply.Click += async (_, _) => await ApplyAsync();
        cancel.Click += (_, _) => Close(null);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_apply);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_summary);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        // 从已安装的插件选
        var pick = new DockPanel();
        var shared = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*"), Margin = new Thickness(0, 10, 0, 0) };
        _account = new TextBox();
        AutomationProperties.SetName(_account, "统一账号");
        _password = new SecretBox { Label = "统一密码", ShowCopy = false };
        _serial = new SecretBox { Label = "统一序列号", ShowCopy = false };
        shared.Children.Add(Field("统一账号（选填）", _account));
        shared.Children.Add(Field("统一密码（选填）", _password, 2));
        shared.Children.Add(Field("统一序列号（选填）", _serial, 4));
        DockPanel.SetDock(shared, Dock.Bottom);
        pick.Children.Add(shared);

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("160,8,*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        _vendor = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_vendor, "按厂家筛选");
        _vendor.ItemsSource = new[] { WishListing.AllVendors }
            .Concat(_rows.Select(r => r.Vendor).Where(v => v.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase)).ToList();
        _vendor.SelectedIndex = 0;
        _search = new TextBox { PlaceholderText = "搜索插件名" };
        AutomationProperties.SetName(_search, "搜索插件名");
        Grid.SetColumn(_search, 2);
        var all = new Button { Content = "全选", Classes = { "Ghost" }, Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(all, "只选当前筛选出来的插件");
        var none = new Button { Content = "清空", Classes = { "Ghost" } };
        Grid.SetColumn(all, 3);
        Grid.SetColumn(none, 4);
        filters.Children.Add(_vendor);
        filters.Children.Add(_search);
        filters.Children.Add(all);
        filters.Children.Add(none);
        DockPanel.SetDock(filters, Dock.Top);
        pick.Children.Add(filters);

        _list = new ListBox { Classes = { "Plugins" } };
        AutomationProperties.SetName(_list, "已安装的插件");
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<BatchPickRow>((row, _) => PickLine(row), supportsRecycling: false);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _list };
        frame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Line"));
        pick.Children.Add(frame);
        _pickPanel = pick;

        // 粘贴文本
        var paste = new DockPanel { IsVisible = false };
        var pasteHint = new TextBlock { Text = "每行一款，依次是：插件名，厂家，账号，密码，序列号。后面的可以不写。可以直接从表格粘贴，或者粘贴“导出账号”文件的内容。", Classes = { "SmallNote" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(pasteHint, Dock.Top);
        paste.Children.Add(pasteHint);
        _paste = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, Classes = { "Multiline" }, PlaceholderText = "Pro-Q 4, FabFilter, me@example.com, 我的密码", VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(_paste, "粘贴已购插件");
        paste.Children.Add(_paste);
        _pastePanel = paste;

        var panels = new Panel();
        panels.Children.Add(_pickPanel);
        panels.Children.Add(_pastePanel);
        body.Children.Add(panels);
        Content = body;

        _modePick.IsCheckedChanged += (_, _) =>
        {
            _pickPanel.IsVisible = PickMode;
            _pastePanel.IsVisible = !PickMode;
            UpdateSummary();
        };
        _vendor.SelectionChanged += (_, _) => Filter();
        _search.TextChanged += (_, _) => Filter();
        all.Click += (_, _) => SelectVisible();
        none.Click += (_, _) =>
        {
            foreach (var row in _rows)
                row.Selected = false;
            Filter();
        };
        _paste.TextChanged += (_, _) => UpdateSummary();
        Filter();
    }

    public bool PickMode => _modePick.IsChecked == true;

    public IReadOnlyList<BatchPickRow> Rows => _rows;

    public string Summary => _summary.Text ?? "";

    public void UsePaste(string text)
    {
        _modePick.IsChecked = false;
        _pickPanel.IsVisible = false;
        _pastePanel.IsVisible = true;
        _paste.Text = text;
        UpdateSummary();
    }

    /// <summary>只选当前筛选出来的，这样“筛选 Waves 再全选”不会把别的厂家带上。</summary>
    public void SelectVisible()
    {
        foreach (var row in Visible())
            row.Selected = true;
        Filter();
    }

    public void SetShared(string account, string password, string serial)
    {
        _account.Text = account;
        _password.Secret = password;
        _serial.Secret = serial;
    }

    /// <summary>按当前方式要写入的记录。</summary>
    public IReadOnlyList<PurchaseDraft> Drafts()
    {
        if (!PickMode)
            return PurchaseText.Parse(_paste.Text ?? "").Drafts;
        var account = (_account.Text ?? "").Trim();
        return _rows.Where(r => r.Selected).Select(r => new PurchaseDraft(r.Name, r.Vendor, account, _password.Secret, _serial.Secret)).ToList();
    }

    private IEnumerable<BatchPickRow> Visible()
    {
        var vendor = _vendor.SelectedItem as string ?? WishListing.AllVendors;
        var search = (_search.Text ?? "").Trim();
        return _rows.Where(r =>
            (vendor == WishListing.AllVendors || r.Vendor == vendor)
            && (search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Vendor.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private void Filter()
    {
        _list.ItemsSource = Visible().ToList();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int count;
        if (PickMode)
        {
            count = _rows.Count(r => r.Selected);
            _summary.Text = count > 0 ? $"已选 {count} 款" : "还没有选择";
        }
        else
        {
            var parsed = PurchaseText.Parse(_paste.Text ?? "");
            count = parsed.Drafts.Count;
            _summary.Text = parsed.BadLines.Count > 0
                ? $"可识别 {count} 款，第 {string.Join("、", parsed.BadLines)} 行无法识别"
                : count > 0 ? $"可识别 {count} 款" : "";
        }
        _apply.IsEnabled = count > 0;
        _apply.Content = count > 0 ? $"添加 {count} 款" : "添加";
    }

    private async Task ApplyAsync()
    {
        if (!PickMode)
        {
            var parsed = PurchaseText.Parse(_paste.Text ?? "");
            if (parsed.BadLines.Count > 0 && !await _dialogs.Confirm("有无法识别的行",
                    $"第 {string.Join("、", parsed.BadLines)} 行无法识别，会被跳过。其余 {parsed.Drafts.Count} 款继续添加吗？", "继续添加"))
                return;
        }
        var drafts = Drafts();
        if (drafts.Count > 0)
            Close(drafts);
    }

    private Control PickLine(BatchPickRow? row)
    {
        // 列表换内容时模板可能先拿到空的数据。
        if (row == null)
            return new Panel();
        var check = new CheckBox { IsChecked = row.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        AutomationProperties.SetName(check, row.Name);
        check.IsCheckedChanged += (_, _) =>
        {
            row.Selected = check.IsChecked == true;
            UpdateSummary();
        };
        var line = new DockPanel { Height = 36 };
        DockPanel.SetDock(check, Dock.Left);
        line.Children.Add(check);
        if (row.Exists)
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "已在列表", FontSize = 11 } };
            badge.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentSoft"));
            DockPanel.SetDock(badge, Dock.Right);
            line.Children.Add(badge);
        }
        var vendor = new TextBlock { Text = row.Vendor, Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        DockPanel.SetDock(vendor, Dock.Right);
        line.Children.Add(vendor);
        line.Children.Add(new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return line;
    }

    private static Control Field(string label, Control input, int column = 0)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Classes = { "FieldLabel" }, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(input);
        Grid.SetColumn(panel, column);
        return panel;
    }
}
