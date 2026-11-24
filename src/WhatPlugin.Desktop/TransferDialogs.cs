using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>导出、导入几个窗口共用的外观：标题、说明、按钮行，和 Windows 版的 BackupDialogs 一样。</summary>
internal static class DialogKit
{
    public static void Setup(Window window, string title, double width)
    {
        window.Title = title;
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.CanResize = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("Surface"));
        window.Bind(Window.ForegroundProperty, window.GetResourceObservable("Text"));
        window.Bind(Window.FontFamilyProperty, window.GetResourceObservable("UiFont"));
        window.FontSize = 13;
    }

    public static StackPanel Body(string title, string message)
    {
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 18) };
        body.Children.Add(new TextBlock { Text = title, Classes = { "SectionTitle" }, TextWrapping = TextWrapping.Wrap });
        var text = new TextBlock { Text = message, Classes = { "SmallNote" }, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(text, "DialogMessage");
        body.Children.Add(text);
        return body;
    }

    public static TextBlock Note(string text) =>
        new() { Text = text, Classes = { "SmallNote" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public static TextBlock Label(string text) =>
        new() { Text = text, Classes = { "FieldLabel" } };

    public static TextBlock Error()
    {
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), IsVisible = false };
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("Danger"));
        return error;
    }

    public static void Show(TextBlock error, string? message)
    {
        error.Text = message ?? "";
        error.IsVisible = !string.IsNullOrEmpty(message);
    }

    public static Button Ok(string text, bool danger = false) =>
        new() { Content = text, Classes = { danger ? "Danger" : "Primary" }, MinWidth = 88, IsDefault = true, Margin = new Thickness(6, 0, 0, 0) };

    public static Button Cancel(Window window)
    {
        var cancel = new Button { Content = "取消", Classes = { "Ghost" }, IsCancel = true };
        cancel.Click += (_, _) => window.Close(null);
        return cancel;
    }

    public static StackPanel Buttons(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }

    /// <summary>勾选列表的最大高度：屏幕矮时列表滚动，窗口不超出屏幕。others 是窗口里其余部分大约占的高度。</summary>
    public static double ListHeight(Window window, double others)
    {
        var screen = window.Screens.Primary?.WorkingArea.Height / (window.Screens.Primary?.Scaling ?? 1);
        return Math.Clamp((screen ?? 900) - others, 200, 470);
    }
}

/// <summary>一组勾选项，上面有“全选”“全不选”“反选”。勾选规则在 Core 的 PartSelection 里，这里只负责显示。</summary>
public sealed class PartChecklistView : StackPanel
{
    private readonly List<(PartOption Option, CheckBox Box)> _items = [];
    private bool _syncing;

    public PartChecklistView(PartSelection selection)
    {
        Selection = selection;
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 10, 0, 4) };
        quick.Children.Add(Quick("全选", selection.SelectAll));
        quick.Children.Add(Quick("全不选", selection.SelectNone));
        quick.Children.Add(Quick("反选", selection.Invert));
        Children.Add(quick);

        foreach (var option in selection.Options)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap });
            if (option.Detail.Length > 0)
                text.Children.Add(new TextBlock { Text = option.Detail, Classes = { "SmallNote" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var box = new CheckBox { Content = text, Margin = new Thickness(option.Parent == null ? 0 : 26, 4, 0, 4) };
            AutomationProperties.SetName(box, option.Label);
            AutomationProperties.SetHelpText(box, option.Detail);
            box.IsCheckedChanged += (_, _) =>
            {
                if (!_syncing)
                    selection.Set(option.Id, box.IsChecked == true);
            };
            _items.Add((option, box));
            Children.Add(box);
        }
        selection.Changed += Sync;
        Sync();
    }

    public PartSelection Selection { get; }

    public event Action? Changed;

    /// <summary>某一项的勾选框（测试用）。</summary>
    public CheckBox Box(string id) => _items.First(i => i.Option.Id == id).Box;

    private static Button Quick(string text, Action apply)
    {
        var button = new Button { Content = text, Classes = { "Ghost" }, Padding = new Thickness(8, 2) };
        button.Click += (_, _) => apply();
        return button;
    }

    /// <summary>父项没勾时子项变灰、取消；不能勾的项整行变淡，和没勾的区分开。</summary>
    private void Sync()
    {
        _syncing = true;
        foreach (var (option, box) in _items)
        {
            box.IsEnabled = Selection.IsEnabled(option.Id);
            box.Opacity = box.IsEnabled ? 1 : 0.45;
            box.IsChecked = Selection.IsChecked(option.Id);
        }
        _syncing = false;
        Changed?.Invoke();
    }
}

/// <summary>左下角“导出备份”：先选用途（给自己备份、分享给别人），再勾要导出的内容。关闭时返回 ExportChoice，取消返回 null。</summary>
public sealed class ExportPartsDialog : Window
{
    private readonly RadioButton _selfMode;
    private readonly RadioButton _shareMode;
    private readonly TextBlock _hint;
    private readonly StackPanel _passwordPanel;
    private readonly SecretBox _password;
    private readonly SecretBox _confirm;
    private readonly TextBlock _error;
    private readonly Button _ok;

    public ExportPartsDialog(IReadOnlyList<PartOption> self, IReadOnlyList<PartOption> share, bool shareMode)
    {
        DialogKit.Setup(this, "导出", 560);
        var body = DialogKit.Body("导出", TransferListing.ExportMessage);

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        _selfMode = new RadioButton { Content = "给自己备份", Classes = { "Segment" }, GroupName = "ExportMode", MinWidth = 110, Margin = new Thickness(0, 0, 6, 0) };
        _shareMode = new RadioButton { Content = "分享给别人", Classes = { "Segment" }, GroupName = "ExportMode", MinWidth = 110 };
        modes.Children.Add(_selfMode);
        modes.Children.Add(_shareMode);
        body.Children.Add(modes);

        _hint = DialogKit.Note("");
        body.Children.Add(_hint);

        SelfList = new PartChecklistView(new PartSelection(self));
        ShareList = new PartChecklistView(new PartSelection(share));
        var lists = new StackPanel();
        lists.Children.Add(SelfList);
        lists.Children.Add(ShareList);
        var scroll = new ScrollViewer { Content = lists, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Opened += (_, _) => scroll.MaxHeight = DialogKit.ListHeight(this, 480);
        scroll.MaxHeight = 470;
        body.Children.Add(scroll);

        _password = new SecretBox { Label = "备份密码", ShowCopy = false };
        _confirm = new SecretBox { Label = "再输一次备份密码", ShowCopy = false };
        _passwordPanel = new StackPanel();
        _passwordPanel.Children.Add(DialogKit.Label(TransferListing.PasswordLabel));
        _passwordPanel.Children.Add(_password);
        _passwordPanel.Children.Add(DialogKit.Label("再输一次"));
        _passwordPanel.Children.Add(_confirm);
        _passwordPanel.Children.Add(DialogKit.Note(TransferListing.PasswordNote));
        body.Children.Add(_passwordPanel);

        _error = DialogKit.Error();
        body.Children.Add(_error);
        _ok = DialogKit.Ok("导出");
        _ok.Click += (_, _) => Finish();
        body.Children.Add(DialogKit.Buttons(DialogKit.Cancel(this), _ok));
        Content = body;

        SelfList.Changed += Refresh;
        ShareList.Changed += Refresh;
        _selfMode.IsCheckedChanged += (_, _) => Refresh();
        _shareMode.IsCheckedChanged += (_, _) => Refresh();
        (shareMode ? _shareMode : _selfMode).IsChecked = true;
        Refresh();
    }

    public PartChecklistView SelfList { get; }

    public PartChecklistView ShareList { get; }

    public bool IsShare
    {
        get => _shareMode.IsChecked == true;
        set => (value ? _shareMode : _selfMode).IsChecked = true;
    }

    public bool NeedsPassword => TransferListing.NeedsPassword(IsShare, SelfList.Selection.CheckedIds);

    public bool PasswordVisible => _passwordPanel.IsVisible;

    public string Error => _error.IsVisible ? _error.Text ?? "" : "";

    public string OkText => _ok.Content as string ?? "";

    public bool CanFinish => _ok.IsEnabled;

    public void SetPassword(string password, string confirm)
    {
        _password.Secret = password;
        _confirm.Secret = confirm;
    }

    private void Refresh()
    {
        var share = IsShare;
        SelfList.IsVisible = !share;
        ShareList.IsVisible = share;
        _hint.Text = share ? TransferListing.ShareHint : TransferListing.SelfHint;
        _passwordPanel.IsVisible = NeedsPassword;
        _ok.Content = share ? "下一步" : "导出";
        _ok.IsEnabled = (share ? ShareList : SelfList).Selection.Any;
        DialogKit.Show(_error, null);
    }

    /// <summary>检查密码后关闭。不合格时显示原因并返回 null。</summary>
    public ExportChoice? Finish()
    {
        if (!_ok.IsEnabled)
            return null;
        if (NeedsPassword && TransferListing.CheckPassword(_password.Secret, _confirm.Secret) is { } problem)
        {
            DialogKit.Show(_error, problem);
            (_password.Secret.Length < TransferListing.MinPasswordLength ? _password : _confirm).Box.Focus();
            return null;
        }
        var choice = IsShare
            ? new ExportChoice(true, ShareList.Selection.CheckedIds, "")
            : new ExportChoice(false, SelfList.Selection.CheckedIds, NeedsPassword ? _password.Secret : "");
        Close(choice);
        return choice;
    }
}

/// <summary>导入前勾选要导入哪些部分。关闭时返回勾选的 Id，取消返回 null。</summary>
public sealed class ImportPartsDialog : Window
{
    private readonly Button _ok;

    public ImportPartsDialog(string title, string message, IReadOnlyList<PartOption> options, string okText, bool danger)
    {
        DialogKit.Setup(this, title, 560);
        var body = DialogKit.Body(title, message);
        List = new PartChecklistView(new PartSelection(options));
        var scroll = new ScrollViewer { Content = List, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, MaxHeight = 470 };
        Opened += (_, _) => scroll.MaxHeight = DialogKit.ListHeight(this, 280);
        body.Children.Add(scroll);
        _ok = DialogKit.Ok(okText, danger);
        _ok.Click += (_, _) => Finish();
        List.Changed += () => _ok.IsEnabled = List.Selection.Any;
        _ok.IsEnabled = List.Selection.Any;
        body.Children.Add(DialogKit.Buttons(DialogKit.Cancel(this), _ok));
        Content = body;
    }

    public PartChecklistView List { get; }

    public void Finish()
    {
        if (_ok.IsEnabled)
            Close(List.Selection.CheckedIds);
    }
}

/// <summary>
/// 导入备份时输入备份密码。tryPassword 返回 null 表示解开了，否则返回错误提示并留在窗口里。
/// 关闭时返回：解开时是密码；选择跳过账号密码时是 ""；取消整个导入时是 null。
/// </summary>
public sealed class BackupPasswordDialog : Window
{
    private readonly Func<string, string?> _tryPassword;
    private readonly TextBlock _error;

    public BackupPasswordDialog(Func<string, string?> tryPassword)
    {
        _tryPassword = tryPassword;
        DialogKit.Setup(this, "输入备份密码", 480);
        var body = DialogKit.Body("输入备份密码", TransferListing.AskPasswordMessage);
        body.Children.Add(DialogKit.Label("备份密码"));
        Password = new SecretBox { Label = "备份密码", ShowCopy = false };
        body.Children.Add(Password);
        _error = DialogKit.Error();
        body.Children.Add(_error);

        var ok = DialogKit.Ok("解开");
        ok.Click += (_, _) => Unlock();
        var skip = new Button { Content = "跳过账号密码", Margin = new Thickness(6, 0, 0, 0) };
        skip.Click += (_, _) => Close("");
        body.Children.Add(DialogKit.Buttons(DialogKit.Cancel(this), skip, ok));
        Content = body;
        Opened += (_, _) => Password.Box.Focus();
    }

    public SecretBox Password { get; }

    public string Error => _error.IsVisible ? _error.Text ?? "" : "";

    public void Unlock()
    {
        var attempt = Password.Secret;
        if (_tryPassword(attempt) is { } problem)
        {
            DialogKit.Show(_error, problem);
            Password.Box.SelectAll();
            Password.Box.Focus();
            return;
        }
        Close(attempt);
    }
}

/// <summary>同一款插件，自己和每位分享者的评价并排显示，同一项对齐。</summary>
public sealed class ReviewCompareDialog : Window
{
    private const double LabelWidth = 62;
    private const double ColumnWidth = 260;

    public ReviewCompareDialog(PluginRow row, IReadOnlyList<AttributedReview> others)
    {
        DialogKit.Setup(this, "对比评价", Math.Min(LabelWidth + ColumnWidth * (others.Count + 1) + 80, 1120));
        SizeToContent = SizeToContent.Manual;
        Height = 620;
        CanResize = true;

        Grid = new Grid();
        Grid.ColumnDefinitions.Add(new ColumnDefinition(LabelWidth, GridUnitType.Pixel));
        for (var i = 0; i < 4; i++)
            Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var (label, index) in new[] { ("评分", 1), ("感受", 2), ("标签", 3) })
            Add(Cell(label, muted: true), index, 0);

        var own = row.User.Normalized();
        AddColumn("我", own.Rating, own.Note, own.MyTags, "");
        foreach (var other in others)
            AddColumn(ReviewListing.ShortLabel(other.Source, others.Select(o => o.Source)), other.Review.Rating, other.Review.Note, other.Review.Tags, ReviewListing.Extras(other.Review));

        var close = new Button { Content = "关闭", Classes = { "Primary" }, MinWidth = 76, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "对比评价：" + row.Name, Classes = { "SectionTitle" }, TextWrapping = TextWrapping.Wrap });
        top.Children.Add(new TextBlock { Text = ReviewListing.IdentityNote, Classes = { "SmallNote" }, Margin = new Thickness(0, 4, 0, 14) });
        DockPanel.SetDock(top, Dock.Top);
        var bottom = DialogKit.Buttons(close);
        DockPanel.SetDock(bottom, Dock.Bottom);
        var layout = new DockPanel { Margin = new Thickness(24, 20, 24, 18) };
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(new ScrollViewer
        {
            Content = Grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });
        Content = layout;
    }

    /// <summary>对比表格（测试检查用）。</summary>
    public Grid Grid { get; }

    private void AddColumn(string name, int rating, string note, IReadOnlyList<string> tags, string extras)
    {
        var column = Grid.ColumnDefinitions.Count;
        Grid.ColumnDefinitions.Add(new ColumnDefinition(ColumnWidth, GridUnitType.Pixel));
        var header = Cell(name, muted: false);
        header.FontWeight = FontWeight.SemiBold;
        Add(header, 0, column);
        var stars = Cell(ReviewListing.Stars(rating), muted: rating == 0);
        if (rating > 0)
            stars.Bind(TextBlock.ForegroundProperty, stars.GetResourceObservable("Accent"));
        Add(stars, 1, column);
        Add(Cell(note.Length > 0 ? note : "没有写", muted: note.Length == 0), 2, column);
        var tagPanel = new StackPanel();
        tagPanel.Children.Add(Cell(tags.Count > 0 ? string.Join("、", tags) : "没有标签", muted: tags.Count == 0));
        if (extras.Length > 0)
        {
            var extra = Cell(extras, muted: true);
            extra.FontSize = 12;
            extra.Margin = new Thickness(0, 6, 16, 0);
            tagPanel.Children.Add(extra);
        }
        Add(tagPanel, 3, column);
    }

    private static TextBlock Cell(string text, bool muted)
    {
        var cell = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 0, 16, 0) };
        if (muted)
            cell.Bind(TextBlock.ForegroundProperty, cell.GetResourceObservable("Muted"));
        return cell;
    }

    private void Add(Control element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, element.Margin.Right, 14);
        Grid.Children.Add(element);
    }
}
