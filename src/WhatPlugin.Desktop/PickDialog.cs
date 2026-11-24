using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 页面上要问用户的几种对话框。正式运行时弹出窗口；测试换成按脚本回答的实现，不真的弹窗。
/// </summary>
public interface IDialogs
{
    Task<bool> Confirm(string title, string message, string confirm, bool danger = false);

    /// <summary>带一个勾选项的确认（例如“同时删除笔记”）。取消时 Confirmed 为 false。</summary>
    Task<(bool Confirmed, bool Option)> ConfirmWithOption(string title, string message, string confirm, string option);

    /// <summary>输入框，取消返回 null。</summary>
    Task<string?> Prompt(string title, string message, string confirm, string text = "");

    /// <summary>从列表里选一项，取消返回 null。</summary>
    Task<int?> PickOne(string title, string message, IReadOnlyList<string> options, string confirm, int selected = 0);

    /// <summary>勾选多项，可以带一个勾选项。取消时 Picked 为 null。</summary>
    Task<(IReadOnlyList<int>? Picked, bool Option)> PickMany(string title, string message, IReadOnlyList<(string Text, bool Checked)> options, string confirm, string? option = null, bool optionChecked = false);

    /// <summary>选一个文件，取消返回 null。</summary>
    Task<string?> OpenFile(string title, string filterName, IReadOnlyList<string> extensions);

    /// <summary>选保存位置，取消返回 null。extension 例如 ".txt"。</summary>
    Task<string?> SaveFile(string title, string fileName, string filterName, string extension);

    /// <summary>显示一段文字，可以另存为文件。返回保存到的路径，没保存返回 null。</summary>
    Task<string?> ShowText(string title, string text, string fileName);

    /// <summary>批量添加已购插件。取消返回 null。</summary>
    Task<IReadOnlyList<PurchaseDraft>?> BatchAdd(IReadOnlyList<PluginRow> installed, IReadOnlyList<WishItem> purchased);

    /// <summary>导出：选用途和内容。取消返回 null。</summary>
    Task<ExportChoice?> ExportParts(IReadOnlyList<PartOption> self, IReadOnlyList<PartOption> share, bool shareMode);

    /// <summary>导入前勾选要导入的部分。取消返回 null。</summary>
    Task<IReadOnlySet<string>?> ImportParts(string title, string message, IReadOnlyList<PartOption> options, string confirm, bool danger);

    /// <summary>输入备份密码。tryPassword 返回 null 表示解开了。返回密码；跳过账号密码返回 ""；取消返回 null。</summary>
    Task<string?> BackupPassword(Func<string, string?> tryPassword);

    /// <summary>挑要分享的评价。取消返回 null。</summary>
    Task<ReviewExportResult?> ExportReviews(IReadOnlyList<PluginRow> rows, string author);

    /// <summary>挑要分享的混音笔记。取消返回 null。</summary>
    Task<NoteExportResult?> ExportNotes(IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders, string author);

    /// <summary>并排对比自己和别人的评价。</summary>
    Task CompareReviews(PluginRow row, IReadOnlyList<AttributedReview> others);

    /// <summary>每款插件选卸载方式和原因。取消返回 null。</summary>
    Task<IReadOnlyList<UninstallChoice>?> Uninstall(IReadOnlyList<UninstallPlan> plans);

    /// <summary>选中了 Waves 插件时的说明。</summary>
    Task<WavesAnswer> WavesNotice(IReadOnlyList<PluginRow> waves, int otherCount);

    /// <summary>已移除的文件，选一个恢复。关闭返回 null。</summary>
    Task<RemovedFile?> RemovedFiles(IReadOnlyList<RemovedFile> files);

    /// <summary>卸载历史。返回是否改过（删除记录或改原因）。</summary>
    Task<bool> UninstallHistory(PluginDatabase database, IReadOnlyList<PluginRow> plugins);
}

/// <summary>弹出真正的窗口。owner 是对话框所属的窗口。</summary>
public sealed class WindowDialogs(Func<Window?> owner) : IDialogs
{
    public async Task<bool> Confirm(string title, string message, string confirm, bool danger = false) =>
        owner() is { } window && await ConfirmDialog.Show(window, title, message, confirm, danger);

    public async Task<(bool Confirmed, bool Option)> ConfirmWithOption(string title, string message, string confirm, string option)
    {
        if (owner() is not { } window)
            return (false, false);
        var result = await new PickDialog(title, message, [], confirm, false, option, false, danger: true).ShowDialog<PickResult?>(window);
        return result == null ? (false, false) : (true, result.Option);
    }

    public async Task<string?> Prompt(string title, string message, string confirm, string text = "") =>
        owner() is { } window ? await ConfirmDialog.Prompt(window, title, message, confirm, text) : null;

    public async Task<int?> PickOne(string title, string message, IReadOnlyList<string> options, string confirm, int selected = 0)
    {
        if (owner() is not { } window)
            return null;
        var initial = options.Select((o, i) => (o, i == selected)).ToList();
        var result = await new PickDialog(title, message, initial, confirm, false, null, false).ShowDialog<PickResult?>(window);
        return result?.Picked.Count > 0 ? result.Picked[0] : null;
    }

    public async Task<(IReadOnlyList<int>? Picked, bool Option)> PickMany(string title, string message, IReadOnlyList<(string Text, bool Checked)> options, string confirm, string? option = null, bool optionChecked = false)
    {
        if (owner() is not { } window)
            return (null, false);
        var result = await new PickDialog(title, message, options, confirm, true, option, optionChecked).ShowDialog<PickResult?>(window);
        return result == null ? (null, false) : (result.Picked, result.Option);
    }

    public async Task<string?> OpenFile(string title, string filterName, IReadOnlyList<string> extensions)
    {
        if (owner() is not { } window)
            return null;
        var picked = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType(filterName) { Patterns = extensions.Select(x => "*" + x).ToList() }],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFile(string title, string fileName, string filterName, string extension)
    {
        if (owner() is not { } window)
            return null;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = fileName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = ["*" + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> ShowText(string title, string text, string fileName)
    {
        if (owner() is not { } window)
            return null;
        var dialog = new TextDialog(title, text, fileName, this);
        await dialog.ShowDialog(window);
        return dialog.SavedPath;
    }

    public async Task<IReadOnlyList<PurchaseDraft>?> BatchAdd(IReadOnlyList<PluginRow> installed, IReadOnlyList<WishItem> purchased)
    {
        if (owner() is not { } window)
            return null;
        var dialog = new BatchAddDialog(installed, purchased, this);
        return await dialog.ShowDialog<IReadOnlyList<PurchaseDraft>?>(window);
    }

    public async Task<ExportChoice?> ExportParts(IReadOnlyList<PartOption> self, IReadOnlyList<PartOption> share, bool shareMode) =>
        owner() is { } window ? await new ExportPartsDialog(self, share, shareMode).ShowDialog<ExportChoice?>(window) : null;

    public async Task<IReadOnlySet<string>?> ImportParts(string title, string message, IReadOnlyList<PartOption> options, string confirm, bool danger) =>
        owner() is { } window ? await new ImportPartsDialog(title, message, options, confirm, danger).ShowDialog<IReadOnlySet<string>?>(window) : null;

    public async Task<string?> BackupPassword(Func<string, string?> tryPassword) =>
        owner() is { } window ? await new BackupPasswordDialog(tryPassword).ShowDialog<string?>(window) : null;

    public async Task<ReviewExportResult?> ExportReviews(IReadOnlyList<PluginRow> rows, string author) =>
        owner() is { } window ? await new ReviewExportDialog(rows, author).ShowDialog<ReviewExportResult?>(window) : null;

    public async Task<NoteExportResult?> ExportNotes(IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders, string author) =>
        owner() is { } window ? await new NoteExportDialog(chains, folders, author).ShowDialog<NoteExportResult?>(window) : null;

    public async Task CompareReviews(PluginRow row, IReadOnlyList<AttributedReview> others)
    {
        if (owner() is { } window)
            await new ReviewCompareDialog(row, others).ShowDialog(window);
    }

    public async Task<IReadOnlyList<UninstallChoice>?> Uninstall(IReadOnlyList<UninstallPlan> plans) =>
        owner() is { } window ? await new UninstallDialog(plans).ShowDialog<IReadOnlyList<UninstallChoice>?>(window) : null;

    public async Task<WavesAnswer> WavesNotice(IReadOnlyList<PluginRow> waves, int otherCount) =>
        owner() is { } window && await new WavesNoticeDialog(waves, otherCount).ShowDialog<object?>(window) is WavesAnswer answer ? answer : WavesAnswer.Cancel;

    public async Task<RemovedFile?> RemovedFiles(IReadOnlyList<RemovedFile> files) =>
        owner() is { } window ? await new RemovedFilesDialog(files).ShowDialog<RemovedFile?>(window) : null;

    public async Task<bool> UninstallHistory(PluginDatabase database, IReadOnlyList<PluginRow> plugins)
    {
        if (owner() is not { } window)
            return false;
        var dialog = new UninstallHistoryDialog(database, plugins);
        await dialog.ShowDialog(window);
        return dialog.Changed;
    }
}

/// <summary>只读的一段文字（例如换机清单），可以另存为 .txt。</summary>
public sealed class TextDialog : Window
{
    public string? SavedPath { get; private set; }

    public TextDialog(string title, string text, string fileName, IDialogs dialogs)
    {
        Title = title;
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Surface"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Text"));
        this.Bind(FontFamilyProperty, this.GetResourceObservable("UiFont"));
        FontSize = 13;

        var body = new DockPanel { Margin = new Thickness(24, 22, 24, 18) };
        var heading = new TextBlock { Text = title, Classes = { "SectionTitle" }, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top);
        body.Children.Add(heading);

        var status = new TextBlock { Classes = { "SmallNote" }, VerticalAlignment = VerticalAlignment.Center };
        var save = new Button { Content = "保存为文件…", Margin = new Thickness(0, 0, 6, 0) };
        var close = new Button { Content = "关闭", Classes = { "Primary" }, MinWidth = 76, IsCancel = true, IsDefault = true };
        save.Click += async (_, _) =>
        {
            if (await dialogs.SaveFile("保存" + title, fileName, "文本文件", ".txt") is not { } path)
                return;
            try
            {
                File.WriteAllText(path, text);
                SavedPath = path;
                status.Text = "已保存";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status.Text = "保存失败：" + error.Message;
            }
        };
        close.Click += (_, _) => Close();
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        body.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "Multiline" } });
        Content = body;
    }
}

public sealed record PickResult(IReadOnlyList<int> Picked, bool Option);

/// <summary>
/// 和 Windows 版的 Dialogs.PickOne、PickMany 一样：标题、说明、一个列表（单选或勾选），下面可以带一个勾选项。
/// 列表为空时就是带勾选项的确认框。
/// </summary>
public sealed class PickDialog : Window
{
    private readonly ListBox? _list;
    private readonly List<CheckBox> _checks = [];
    private readonly CheckBox? _option;

    public PickDialog(string title, string message, IReadOnlyList<(string Text, bool Checked)> options, string confirm, bool multiple, string? option, bool optionChecked, bool danger = false)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Surface"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Text"));
        this.Bind(FontFamilyProperty, this.GetResourceObservable("UiFont"));
        FontSize = 13;

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 18) };
        body.Children.Add(new TextBlock { Text = title, Classes = { "SectionTitle" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message, Classes = { "SmallNote" }, Margin = new Thickness(0, 8, 0, 0) });

        if (options.Count > 0 && multiple)
        {
            var panel = new StackPanel();
            foreach (var (text, isChecked) in options)
            {
                var check = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = isChecked, Margin = new Thickness(0, 2) };
                _checks.Add(check);
                panel.Children.Add(check);
            }
            var all = new Button { Content = "全选", Classes = { "Ghost" }, Padding = new Thickness(8, 2), Margin = new Thickness(-8, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            all.Click += (_, _) =>
            {
                var on = _checks.Any(c => c.IsChecked != true);
                foreach (var check in _checks)
                    check.IsChecked = on;
            };
            body.Children.Add(all);
            body.Children.Add(Framed(new ScrollViewer { Content = panel, MaxHeight = 360, Padding = new Thickness(10, 6) }));
        }
        else if (options.Count > 0)
        {
            _list = new ListBox
            {
                ItemsSource = options.Select(o => o.Text).ToList(),
                SelectedIndex = Math.Max(0, options.ToList().FindIndex(o => o.Checked)),
                MaxHeight = 360,
            };
            _list.DoubleTapped += (_, _) => Finish();
            body.Children.Add(Framed(_list));
        }

        if (option != null)
        {
            _option = new CheckBox { Content = new TextBlock { Text = option, TextWrapping = TextWrapping.Wrap }, IsChecked = optionChecked, Margin = new Thickness(0, 12, 0, 0) };
            body.Children.Add(_option);
        }

        var ok = new Button { Content = confirm, Classes = { danger ? "Danger" : "Primary" }, MinWidth = 76, Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Classes = { "Ghost" }, IsCancel = true };
        ok.Click += (_, _) => Finish();
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);
        Content = body;
    }

    private Border Framed(Control content)
    {
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 10, 0, 0), Child = content, ClipToBounds = true };
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Line"));
        return border;
    }

    private void Finish()
    {
        IReadOnlyList<int> picked = _list != null
            ? (_list.SelectedIndex >= 0 ? [_list.SelectedIndex] : [])
            : _checks.Select((c, i) => (c, i)).Where(x => x.c.IsChecked == true).Select(x => x.i).ToList();
        if (_list != null && picked.Count == 0)
            return;
        Close(new PickResult(picked, _option?.IsChecked == true));
    }
}
