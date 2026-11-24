using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>导出评价窗口的结果：昵称、挑中的插件和一起导出的可选项。</summary>
public sealed record ReviewExportResult(string Author, IReadOnlyList<PluginRow> Rows, ReviewExportOptions Options);

/// <summary>导出混音笔记窗口的结果。</summary>
public sealed record NoteExportResult(string Author, IReadOnlyList<PluginChain> Chains, bool IncludeRouting);

/// <summary>导出评价时可以勾选的一款插件，默认选中。</summary>
public sealed class ReviewPickRow(PluginRow row) : Observable
{
    private bool _selected = true;

    public PluginRow Row { get; } = row;
    public string Name => Row.Name;
    public string Vendor => Row.Vendor;
    public string Stars => Row.User.Rating > 0 ? new string('★', Row.User.Rating) : "";

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

/// <summary>和 Windows 版的 ReviewExportDialog 一样：选择要分享哪些插件的评价。只列出写过评价的插件。</summary>
public sealed class ReviewExportDialog : Window
{
    private readonly List<ReviewPickRow> _rows;
    private readonly TextBox _author;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly CheckBox _usage;
    private readonly CheckBox _categories;
    private readonly CheckBox _mix;
    private readonly TextBlock _summary;
    private readonly Button _ok;

    public ReviewExportDialog(IEnumerable<PluginRow> rows, string author)
    {
        DialogKit.Setup(this, "导出评价", 600);
        SizeToContent = SizeToContent.Manual;
        Height = 640;
        _rows = rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => new ReviewPickRow(r)).ToList();

        var layout = new DockPanel { Margin = new Thickness(24, 22, 24, 18) };
        var top = DialogKit.Body("导出评价", ReviewListing.ExportExplanation);
        top.Children.Add(DialogKit.Label("你的昵称"));
        _author = new TextBox { Text = author, PlaceholderText = "别人看到的名字", MaxLength = 40 };
        AutomationProperties.SetName(_author, "你的昵称");
        top.Children.Add(_author);
        var filter = new DockPanel { Margin = new Thickness(0, 12, 0, 8) };
        var none = new Button { Content = "清空", Classes = { "Ghost" } };
        var all = new Button { Content = "全选", Classes = { "Ghost" }, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(none, Dock.Right);
        DockPanel.SetDock(all, Dock.Right);
        filter.Children.Add(none);
        filter.Children.Add(all);
        _search = new TextBox { PlaceholderText = "搜索插件名或厂家" };
        AutomationProperties.SetName(_search, "搜索插件名或厂家");
        filter.Children.Add(_search);
        top.Children.Add(filter);
        top.Margin = new Thickness(0);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        var bottom = new StackPanel();
        bottom.Children.Add(DialogKit.Label("一起导出（可选）"));
        var options = new WrapPanel();
        _usage = new CheckBox { Content = "使用次数", Margin = new Thickness(0, 0, 16, 0) };
        ToolTip.SetTip(_usage, "只有次数，不含工程名");
        _categories = new CheckBox { Content = "功能分类", Margin = new Thickness(0, 0, 16, 0) };
        _mix = new CheckBox { Content = "混音属性" };
        options.Children.Add(_usage);
        options.Children.Add(_categories);
        options.Children.Add(_mix);
        bottom.Children.Add(options);
        _summary = new TextBlock { Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center };
        _ok = DialogKit.Ok("导出");
        _ok.Click += (_, _) => Finish();
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var buttons = DialogKit.Buttons(DialogKit.Cancel(this), _ok);
        buttons.Margin = new Thickness(0);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_summary);
        bottom.Children.Add(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);

        _list = new ListBox { Classes = { "Plugins" } };
        AutomationProperties.SetName(_list, "写过评价的插件");
        _list.ItemTemplate = new FuncDataTemplate<ReviewPickRow>((row, _) => Line(row), supportsRecycling: false);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _list };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Line"));
        layout.Children.Add(frame);
        Content = layout;

        _search.TextChanged += (_, _) => Filter();
        _author.TextChanged += (_, _) => UpdateSummary();
        all.Click += (_, _) => SelectVisible(true);
        none.Click += (_, _) =>
        {
            foreach (var row in _rows)
                row.Selected = false;
            Filter();
        };
        Filter();
    }

    public IReadOnlyList<ReviewPickRow> Rows => _rows;

    public IReadOnlyList<ReviewPickRow> Shown => _list.ItemsSource as IReadOnlyList<ReviewPickRow> ?? [];

    public string Author
    {
        get => (_author.Text ?? "").Trim();
        set => _author.Text = value;
    }

    public string Search
    {
        set
        {
            _search.Text = value;
            Filter();
        }
    }

    public string Summary => _summary.Text ?? "";

    public bool CanFinish => _ok.IsEnabled;

    public void SetOptions(bool usage, bool categories, bool mix)
    {
        _usage.IsChecked = usage;
        _categories.IsChecked = categories;
        _mix.IsChecked = mix;
    }

    /// <summary>“全选”只选当前搜出来的插件。</summary>
    public void SelectVisible(bool selected)
    {
        foreach (var row in Shown)
            row.Selected = selected;
        Filter();
    }

    private void Filter()
    {
        var search = (_search.Text ?? "").Trim();
        _list.ItemsSource = _rows.Where(r => search.Length == 0
            || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || r.Vendor.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _rows.Count(r => r.Selected);
        _summary.Text = ReviewListing.ExportSummary(Author, count);
        _ok.IsEnabled = count > 0 && Author.Length > 0;
        _ok.Content = ReviewListing.ExportButton(count);
    }

    private Control Line(ReviewPickRow? row)
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
        var line = new DockPanel { Height = 34 };
        DockPanel.SetDock(check, Dock.Left);
        line.Children.Add(check);
        var stars = new TextBlock { Text = row.Stars, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
        stars.Bind(TextBlock.ForegroundProperty, stars.GetResourceObservable("Accent"));
        DockPanel.SetDock(stars, Dock.Right);
        line.Children.Add(stars);
        var vendor = new TextBlock { Text = row.Vendor, Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(vendor, Dock.Right);
        line.Children.Add(vendor);
        line.Children.Add(new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return line;
    }

    public void Finish()
    {
        if (!_ok.IsEnabled)
            return;
        Close(new ReviewExportResult(Author, _rows.Where(r => r.Selected).Select(r => r.Row).ToList(),
            new ReviewExportOptions(_usage.IsChecked == true, _categories.IsChecked == true, _mix.IsChecked == true)));
    }
}

/// <summary>导出笔记窗口里的一行：文件夹或笔记。文件夹的勾选状态由里面的笔记决定，勾它就勾上里面所有笔记。</summary>
public sealed class NotePickRow : Observable
{
    private bool _selected = true;
    private bool? _folderState = true;
    private string _detail;

    public NotePickRow(PluginChain chain, int depth, string path)
    {
        Chain = chain;
        Depth = depth;
        Name = chain.Name;
        Path = path;
        _detail = chain.Steps.Count > 0 ? $"{chain.Steps.Count} 步" : "空笔记";
        Search = string.Join("\n", new[] { chain.Name, path }
            .Concat(chain.TagList)
            .Concat(chain.Steps.SelectMany(s => s.AllPicks).Select(p => p.PluginName)));
    }

    public NotePickRow(NoteFolder folder, int depth, IReadOnlySet<long> subtree)
    {
        Folder = folder;
        Subtree = subtree;
        Depth = depth;
        Name = folder.Name;
        Path = "";
        _detail = "";
        Search = "";
    }

    public PluginChain? Chain { get; }
    public NoteFolder? Folder { get; }
    public IReadOnlySet<long> Subtree { get; } = new HashSet<long>();
    public bool IsFolder => Folder != null;
    public int Depth { get; }
    public string Name { get; }
    public string Path { get; }
    public string Search { get; }
    public string Detail => _detail;

    /// <summary>文件夹：全选为 true，全不选为 false，部分为 null。笔记：是否选中。</summary>
    public bool? Selected => IsFolder ? _folderState : _selected;

    public bool IsNoteSelected => !IsFolder && _selected;

    public void SetNote(bool selected)
    {
        if (IsFolder || _selected == selected)
            return;
        _selected = selected;
        Notify(nameof(Selected));
    }

    public void SetFolderState(bool? state, int selected, int total)
    {
        _detail = total > 0 ? $"{selected}/{total}" : "空";
        _folderState = state;
        NotifyAll();
    }
}

/// <summary>和 Windows 版的 NoteExportDialog 一样：按文件夹层级列出笔记，默认全选；搜索时只列笔记。</summary>
public sealed class NoteExportDialog : Window
{
    private readonly List<NotePickRow> _rows = [];
    private readonly TextBox _author;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly CheckBox _routes;
    private readonly TextBlock _summary;
    private readonly Button _ok;

    public NoteExportDialog(IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders, string author)
    {
        DialogKit.Setup(this, "导出混音笔记", 600);
        SizeToContent = SizeToContent.Manual;
        Height = 660;

        void Add(long? parent, int depth)
        {
            foreach (var folder in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _rows.Add(new NotePickRow(folder, depth, MixNotes.Subtree(folder.Id, folders)));
                Add(folder.Id, depth + 1);
            }
            foreach (var chain in chains.Where(c => c.FolderId == parent).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                _rows.Add(new NotePickRow(chain, depth, string.Join(" › ", MixNotes.PathOf(parent, folders))));
        }
        Add(null, 0);
        // 文件夹找不到的笔记（数据异常）也列出来，放在最后。
        var listed = _rows.Where(r => r.Chain != null).Select(r => r.Chain!.Id).ToHashSet();
        foreach (var chain in chains.Where(c => !listed.Contains(c.Id)))
            _rows.Add(new NotePickRow(chain, 0, ""));

        var layout = new DockPanel { Margin = new Thickness(24, 22, 24, 18) };
        var top = DialogKit.Body("导出混音笔记", ReviewListing.NoteExportExplanation);
        top.Children.Add(DialogKit.Label("你的昵称（可不填）"));
        _author = new TextBox { Text = author, PlaceholderText = "别人导入后，文件夹叫“来自 昵称 的笔记”", MaxLength = 40 };
        AutomationProperties.SetName(_author, "你的昵称");
        top.Children.Add(_author);
        var filter = new DockPanel { Margin = new Thickness(0, 12, 0, 8) };
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        foreach (var (text, value) in new (string, Func<NotePickRow, bool>)[] { ("全选", _ => true), ("取消全选", _ => false), ("反向选择", r => !r.IsNoteSelected) })
        {
            var button = new Button { Content = text, Classes = { "Ghost" } };
            button.Click += (_, _) => SetVisible(value);
            quick.Children.Add(button);
        }
        DockPanel.SetDock(quick, Dock.Right);
        filter.Children.Add(quick);
        _search = new TextBox { PlaceholderText = "搜索笔记、标签或插件" };
        AutomationProperties.SetName(_search, "搜索笔记、标签或插件");
        filter.Children.Add(_search);
        top.Children.Add(filter);
        top.Margin = new Thickness(0);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        var bottom = new StackPanel();
        _routes = new CheckBox { Content = "一起导出路由（输出、发送、侧链）", Margin = new Thickness(0, 12, 0, 0) };
        ToolTip.SetTip(_routes, "只保留选中的笔记之间的连线");
        bottom.Children.Add(_routes);
        _summary = new TextBlock { Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center };
        _ok = DialogKit.Ok("导出");
        _ok.Click += (_, _) => Finish();
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var buttons = DialogKit.Buttons(DialogKit.Cancel(this), _ok);
        buttons.Margin = new Thickness(0);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_summary);
        bottom.Children.Add(footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);

        _list = new ListBox { Classes = { "Plugins" } };
        AutomationProperties.SetName(_list, "混音笔记");
        _list.ItemTemplate = new FuncDataTemplate<NotePickRow>((row, _) => Line(row), supportsRecycling: false);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _list };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Line"));
        layout.Children.Add(frame);
        Content = layout;

        _search.TextChanged += (_, _) => Filter();
        Filter();
        Refresh();
    }

    public IReadOnlyList<NotePickRow> Rows => _rows;

    public IReadOnlyList<NotePickRow> Shown => _list.ItemsSource as IReadOnlyList<NotePickRow> ?? [];

    public string Author => (_author.Text ?? "").Trim();

    public string Summary => _summary.Text ?? "";

    public bool IncludeRouting
    {
        get => _routes.IsChecked == true;
        set => _routes.IsChecked = value;
    }

    public string Search
    {
        set
        {
            _search.Text = value;
            Filter();
        }
    }

    public IReadOnlyList<PluginChain> SelectedChains => _rows.Where(r => r.IsNoteSelected).Select(r => r.Chain!).ToList();

    private bool Searching => (_search.Text ?? "").Trim().Length > 0;

    private void Filter()
    {
        var search = (_search.Text ?? "").Trim();
        _list.ItemsSource = search.Length == 0
            ? _rows.ToList()
            : _rows.Where(r => !r.IsFolder && r.Search.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>点文件夹：勾上（或取消）里面所有笔记。</summary>
    public void ToggleFolder(NotePickRow folder, bool select)
    {
        foreach (var row in _rows.Where(r => r.Chain is { FolderId: { } f } && folder.Subtree.Contains(f)))
            row.SetNote(select);
        Refresh();
    }

    public void ToggleNote(NotePickRow note, bool select)
    {
        note.SetNote(select);
        Refresh();
    }

    private void SetVisible(Func<NotePickRow, bool> value)
    {
        foreach (var row in Shown.Where(r => !r.IsFolder).ToList())
            row.SetNote(value(row));
        Refresh();
    }

    private void Refresh()
    {
        foreach (var folder in _rows.Where(r => r.IsFolder))
        {
            var notes = _rows.Where(r => r.Chain is { FolderId: { } f } && folder.Subtree.Contains(f)).ToList();
            var selected = notes.Count(r => r.IsNoteSelected);
            folder.SetFolderState(notes.Count == 0 ? false : selected == notes.Count ? true : selected == 0 ? false : null, selected, notes.Count);
        }
        var count = _rows.Count(r => r.IsNoteSelected);
        _summary.Text = ReviewListing.NoteSummary(count);
        _ok.IsEnabled = count > 0;
        _ok.Content = ReviewListing.NoteExportButton(count);
    }

    private Control Line(NotePickRow? row)
    {
        if (row == null)
            return new Panel();
        var check = new CheckBox { IsChecked = row.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        AutomationProperties.SetName(check, row.Name);
        var syncing = false;
        check.IsCheckedChanged += (_, _) =>
        {
            if (syncing)
                return;
            // 部分选中的文件夹被点时 Avalonia 给出 false，和 Windows 版一样当作“取消全部”。
            if (row.IsFolder)
                ToggleFolder(row, check.IsChecked == true);
            else
                ToggleNote(row, check.IsChecked == true);
        };
        row.PropertyChanged += (_, _) =>
        {
            syncing = true;
            check.IsChecked = row.Selected;
            syncing = false;
        };
        var line = new DockPanel { Height = 34, Margin = new Thickness(Searching ? 0 : row.Depth * 18, 0, 0, 0) };
        DockPanel.SetDock(check, Dock.Left);
        line.Children.Add(check);
        var detail = new TextBlock { Text = row.Detail, Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
        row.PropertyChanged += (_, _) => detail.Text = row.Detail;
        DockPanel.SetDock(detail, Dock.Right);
        line.Children.Add(detail);
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (row.IsFolder)
            name.Children.Add(new Material.Icons.Avalonia.MaterialIcon { Kind = Material.Icons.MaterialIconKind.FolderOutline, Width = 15, Height = 15, Margin = new Thickness(0, 0, 6, 0) });
        name.Children.Add(new TextBlock { Text = row.Name, FontWeight = row.IsFolder ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        if (Searching && row.Path.Length > 0)
            name.Children.Add(new TextBlock { Text = "  " + row.Path, Classes = { "SmallNote" } });
        line.Children.Add(name);
        return line;
    }

    public void Finish()
    {
        if (_ok.IsEnabled)
            Close(new NoteExportResult(Author, SelectedChains, IncludeRouting));
    }
}
