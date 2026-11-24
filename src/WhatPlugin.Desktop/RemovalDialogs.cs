using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>每款插件选一种卸载方式和原因。这里不执行任何操作，点“开始卸载”后才返回选择。</summary>
public sealed class UninstallDialog : Window
{
    private readonly List<Row> _rows = [];
    private readonly TextBlock _summary;
    private readonly Button _start;

    public UninstallDialog(IReadOnlyList<UninstallPlan> plans)
    {
        DialogKit.Setup(this, "卸载插件", 640);
        var body = DialogKit.Body($"卸载 {plans.Count} 款插件", "每款选一种方式。现在还不会动任何文件，点“开始卸载”后才执行。");

        var list = new StackPanel { Spacing = 10 };
        foreach (var plan in plans)
        {
            var row = new Row(plan, UpdateSummary);
            _rows.Add(row);
            list.Children.Add(row.View);
        }
        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = 470,
            Margin = new Thickness(0, 14, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        body.Children.Add(scroller);

        _summary = DialogKit.Note("");
        AutomationProperties.SetAutomationId(_summary, "UninstallSummary");
        body.Children.Add(_summary);
        _start = DialogKit.Ok("开始卸载", danger: true);
        _start.Click += (_, _) => Close(Choices);
        body.Children.Add(DialogKit.Buttons(DialogKit.Cancel(this), _start));
        Content = body;
        Opened += (_, _) => scroller.MaxHeight = DialogKit.ListHeight(this, 300);
        UpdateSummary();
    }

    public IReadOnlyList<UninstallChoice> Choices => _rows.Select(r => r.Choice).ToList();

    /// <summary>某款插件的方式下拉框（测试用）。</summary>
    public ComboBox MethodBox(string name) => _rows.First(r => r.Plan.Plugin.Name == name).Method;

    public TextBox ReasonBox(string name) => _rows.First(r => r.Plan.Plugin.Name == name).Reason;

    public string Summary => _summary.Text ?? "";

    public bool CanStart => _start.IsEnabled;

    private void UpdateSummary()
    {
        var (text, canStart) = UninstallOptions.Summary(Choices);
        _summary.Text = text;
        _start.IsEnabled = canStart;
    }

    private sealed class Row
    {
        private readonly IReadOnlyList<UninstallOption> _options;
        private readonly TextBlock _detail;

        public Row(UninstallPlan plan, Action changed)
        {
            Plan = plan;
            _options = UninstallOptions.For(plan);
            Method = new ComboBox { ItemsSource = _options.Select(o => o.Label).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(Method, "怎么卸载 " + plan.Plugin.Name);
            _detail = new TextBlock { Classes = { "SmallNote" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            Reason = new TextBox { Text = plan.Plugin.User.BlacklistReason, PlaceholderText = "为什么卸载（选填）", Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(Reason, "卸载 " + plan.Plugin.Name + " 的原因");
            Method.SelectionChanged += (_, _) =>
            {
                ShowDetail();
                changed();
            };

            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = plan.Plugin.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(Method);
            content.Children.Add(_detail);
            content.Children.Add(Reason);
            View = new Border
            {
                Child = content,
                Padding = new Thickness(14, 10, 14, 12),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
            };
            View.Bind(Border.BorderBrushProperty, View.GetResourceObservable("Line"));
            ShowDetail();
        }

        public UninstallPlan Plan { get; }
        public ComboBox Method { get; }
        public TextBox Reason { get; }
        public Border View { get; }

        private UninstallOption Option => _options[Math.Max(Method.SelectedIndex, 0)];

        public UninstallChoice Choice => new(Plan, Option.Method, Option.Program, (Reason.Text ?? "").Trim());

        private void ShowDetail()
        {
            // 跨平台版不读取“已安装的应用”。
            _detail.Text = UninstallOptions.Detail(Plan, Option, checkedPrograms: false);
            _detail.IsVisible = _detail.Text.Length > 0;
        }
    }
}

public enum WavesAnswer { Cancel, SkipWaves }

/// <summary>选中 Waves 时的说明。普通卸载碰不得 WaveShell，所以这里不动任何文件。</summary>
public sealed class WavesNoticeDialog : Window
{
    public WavesNoticeDialog(IReadOnlyList<PluginRow> waves, int otherCount)
    {
        DialogKit.Setup(this, "选中了 Waves 插件", 500);
        var body = DialogKit.Body("选中了 Waves 插件", RemovalText.WavesMessage(waves, fullTool: false));
        var error = DialogKit.Error();
        body.Children.Add(error);

        var central = new Button { Content = "打开 Waves Central", Classes = { "Ghost" } };
        AutomationProperties.SetAutomationId(central, "WavesCentral");
        central.Click += (_, _) =>
        {
            var problem = SystemShell.OpenWavesCentral();
            if (problem == null)
                Close(WavesAnswer.Cancel);
            else
                DialogKit.Show(error, problem);
        };
        var buttons = new List<Control> { central, DialogKit.Cancel(this) };
        if (otherCount > 0)
        {
            var skip = DialogKit.Ok($"跳过 Waves，继续其余 {otherCount} 款");
            skip.Click += (_, _) => Close(WavesAnswer.SkipWaves);
            buttons.Add(skip);
        }
        body.Children.Add(DialogKit.Buttons([.. buttons]));
        Content = body;
    }
}

/// <summary>移到回收站（废纸篓）的文件，一次恢复一个。返回要恢复的文件，关闭返回 null。</summary>
public sealed class RemovedFilesDialog : Window
{
    public RemovedFilesDialog(IReadOnlyList<RemovedFile> files)
    {
        DialogKit.Setup(this, "已移除的文件", 640);
        var body = DialogKit.Body("已移除的文件", RemovalText.RemovedFilesHint);
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var group in files.GroupBy(f => f.PluginName).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            list.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            foreach (var file in group)
                list.Children.Add(RemovedFileLine(file, () => Close(file)));
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var close = DialogKit.Ok("关闭");
        close.IsCancel = true;
        close.Click += (_, _) => Close(null);
        body.Children.Add(DialogKit.Buttons(close));
        Content = body;
    }

    /// <summary>一行已移除的文件：路径、格式大小日期，右边“恢复”。详情面板也用它。</summary>
    public static Control RemovedFileLine(RemovedFile file, Action restore)
    {
        var button = new Button { Content = "恢复", Classes = { "Ghost" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, -8, 0) };
        AutomationProperties.SetName(button, "恢复 " + file.Path);
        button.Click += (_, _) => restore();
        DockPanel.SetDock(button, Dock.Right);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = file.Path, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        text.Children.Add(new TextBlock { Text = RemovalText.RemovedTitle(file), Classes = { "SmallNote" } });
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        line.Children.Add(button);
        line.Children.Add(text);
        return line;
    }
}

/// <summary>垃圾桶的卸载历史：查看、搜索、改原因、删除单条。改动直接写数据库。</summary>
public sealed class UninstallHistoryDialog : Window
{
    private readonly PluginDatabase _database;
    private readonly List<(UninstallRecord Record, bool Back, TextBox Reason)> _rows = [];
    private readonly TextBox _search;
    private readonly TextBlock _summary;
    private readonly TextBlock _empty;
    private readonly StackPanel _list = new() { Spacing = 10 };

    public UninstallHistoryDialog(PluginDatabase database, IReadOnlyList<PluginRow> plugins)
    {
        _database = database;
        DialogKit.Setup(this, "卸载历史", 620);
        var body = DialogKit.Body("卸载历史", "卸载过的插件和当时的原因。插件记录删了，这里也还留着。");
        var top = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _summary = new TextBlock { Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(_summary, Dock.Right);
        _search = new TextBox { PlaceholderText = "搜索名称、厂家或原因" };
        AutomationProperties.SetName(_search, "搜索卸载历史");
        _search.TextChanged += (_, _) => Refresh();
        top.Children.Add(_summary);
        top.Children.Add(_search);
        body.Children.Add(top);

        _empty = DialogKit.Note("还没有卸载记录。在垃圾桶里卸载插件后，会记在这里。");
        body.Children.Add(_empty);
        body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 440, Margin = new Thickness(0, 12, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var close = DialogKit.Ok("关闭");
        close.IsCancel = true;
        close.Click += (_, _) => Close(Changed);
        body.Children.Add(DialogKit.Buttons(close));
        Content = body;

        foreach (var record in database.LoadUninstallHistory())
            _rows.Add((record, UninstallHistory.IsBack(record, plugins), ReasonBox(record)));
        // 关窗前把还在编辑的原因写进去。
        Closing += (_, _) => SaveAll();
        Refresh();
    }

    /// <summary>删除过记录或改过原因。</summary>
    public bool Changed { get; private set; }

    /// <summary>当前显示的记录名称（测试用）。</summary>
    public IReadOnlyList<string> ShownNames => _list.Children.OfType<Control>().Select(c => c.Tag as string ?? "").ToList();

    public void Search(string text) => _search.Text = text;

    public TextBox Reason(string name) => _rows.First(r => r.Record.Name == name).Reason;

    public async Task DeleteAsync(string name, Func<Task<bool>>? confirm = null)
    {
        var row = _rows.First(r => r.Record.Name == name);
        confirm ??= () => ConfirmDialog.Show(this, "删除这条卸载记录？",
            $"会删除“{row.Record.Name}”{row.Record.DateText}的卸载记录和当时写的原因，删除后不能恢复。插件库和垃圾桶里的记录不受影响。", "删除", danger: true);
        if (!await confirm())
            return;
        _database.DeleteUninstallRecord(row.Record.Id);
        _rows.Remove(row);
        Changed = true;
        Refresh();
    }

    public void SaveAll()
    {
        // Save 会替换列表里的记录，所以先拷一份再遍历。
        foreach (var (record, _, box) in _rows.ToList())
            Save(record, box);
    }

    private TextBox ReasonBox(UninstallRecord record)
    {
        var box = new TextBox
        {
            Text = record.Reason,
            PlaceholderText = record.TrashReason.Length > 0 ? "没填时用移入原因：" + record.TrashReason : "为什么卸载",
            Margin = new Thickness(0, 8, 0, 0),
        };
        AutomationProperties.SetName(box, record.Name + " 的卸载原因");
        box.LostFocus += (_, _) => Save(record, box);
        return box;
    }

    private void Save(UninstallRecord record, TextBox box)
    {
        var index = _rows.FindIndex(r => r.Record.Id == record.Id);
        if (index < 0)
            return;
        var text = (box.Text ?? "").Trim();
        if (text == _rows[index].Record.Reason)
            return;
        _database.SetUninstallReason(record.Id, text);
        _rows[index] = (_rows[index].Record with { Reason = text }, _rows[index].Back, box);
        Changed = true;
    }

    private void Refresh()
    {
        var search = (_search.Text ?? "").Trim();
        var visible = _rows.Where(r => search.Length == 0 || Searchable(r.Record, r.Reason).Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.Children.Clear();
        foreach (var row in visible)
            _list.Children.Add(Card(row.Record, row.Back, row.Reason));
        _empty.IsVisible = _rows.Count == 0;
        _summary.Text = _rows.Count == 0 ? ""
            : search.Length > 0 ? $"找到 {visible.Count} 条，共 {_rows.Count} 条"
            : $"共 {_rows.Count} 条";
    }

    private static string Searchable(UninstallRecord record, TextBox reason) =>
        string.Join("\n", record.Name, record.Vendor, reason.Text ?? "", record.TrashReason, record.Note);

    private Control Card(UninstallRecord record, bool back, TextBox reason)
    {
        var delete = new Button { Content = "删除", Classes = { "Ghost" }, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, -4, -8, 0) };
        AutomationProperties.SetName(delete, $"删除 {record.Name} 的卸载记录");
        delete.Click += async (_, _) => await DeleteAsync(record.Name);
        DockPanel.SetDock(delete, Dock.Right);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = record.Name, FontWeight = FontWeight.SemiBold });
        if (record.Vendor.Length > 0)
            title.Children.Add(new TextBlock { Text = record.Vendor, Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center });
        if (back)
        {
            var badge = new Border { Padding = new Thickness(8, 1), CornerRadius = new CornerRadius(9), Child = new TextBlock { Text = "现在又装着", FontSize = 12 } };
            badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("AccentSoft"));
            title.Children.Add(badge);
        }
        var head = new DockPanel();
        head.Children.Add(delete);
        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(new TextBlock { Text = $"{record.DateText} · {record.WayText}", Classes = { "SmallNote" }, Margin = new Thickness(0, 2, 0, 0) });
        head.Children.Add(titles);

        var content = new StackPanel();
        content.Children.Add(head);
        var snapshot = Snapshot(record);
        if (snapshot.Length > 0)
            content.Children.Add(new TextBlock { Text = snapshot, Classes = { "SmallNote" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        (reason.Parent as Panel)?.Children.Remove(reason);
        content.Children.Add(reason);
        var card = new Border { Child = content, Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Tag = record.Name };
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("Line"));
        return card;
    }

    private static string Snapshot(UninstallRecord record)
    {
        var parts = new List<string>();
        if (record.TrashReason.Length > 0)
            parts.Add("移入垃圾桶的原因：" + record.TrashReason);
        if (record.Rating > 0)
            parts.Add($"当时评分 {record.Rating} 星");
        if (record.Note.Length > 0)
            parts.Add("当时的感受：" + record.Note);
        return string.Join("\n", parts);
    }
}
