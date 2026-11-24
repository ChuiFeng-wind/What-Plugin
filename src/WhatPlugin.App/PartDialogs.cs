using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>一组勾选项，上面有“全选”“全不选”“反选”。勾选规则在 Core 的 PartSelection 里。</summary>
internal sealed class PartChecklist
{
    private readonly List<(PartOption Option, CheckBox Box)> _items = [];
    private readonly PartSelection _selection;
    private bool _syncing;

    public StackPanel Panel { get; } = new();

    public event Action? Changed;

    public PartChecklist(IReadOnlyList<PartOption> options, string idPrefix)
    {
        _selection = new PartSelection(options);
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-10, 10, 0, 4) };
        quick.Children.Add(Quick("全选", idPrefix + "SelectAll", _selection.SelectAll));
        quick.Children.Add(Quick("全不选", idPrefix + "SelectNone", _selection.SelectNone));
        quick.Children.Add(Quick("反选", idPrefix + "Invert", _selection.Invert));
        Panel.Children.Add(quick);

        foreach (var option in options)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap });
            if (option.Detail.Length > 0)
            {
                var detail = new TextBlock { Text = option.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                text.Children.Add(detail);
            }
            var box = new CheckBox
            {
                Content = text,
                Margin = new Thickness(option.Parent == null ? 0 : 26, 5, 0, 5),
            };
            AutomationProperties.SetName(box, option.Label);
            AutomationProperties.SetHelpText(box, option.Detail);
            AutomationProperties.SetAutomationId(box, idPrefix + option.Id);
            // 不能勾的项整行变淡，和没勾的区分开。
            box.IsEnabledChanged += (_, _) => box.Opacity = box.IsEnabled ? 1 : 0.45;
            box.Checked += (_, _) => Toggled(option, true);
            box.Unchecked += (_, _) => Toggled(option, false);
            _items.Add((option, box));
            Panel.Children.Add(box);
        }
        _selection.Changed += Sync;
        Sync();
    }

    /// <summary>勾上的、而且能勾的项。</summary>
    public IReadOnlySet<string> CheckedIds => _selection.CheckedIds;

    public bool Any => _selection.Any;

    private Button Quick(string text, string id, Action apply)
    {
        var button = new Button { Content = text, Height = 28, Style = (Style)Application.Current.FindResource("GhostButton") };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => apply();
        return button;
    }

    private void Toggled(PartOption option, bool value)
    {
        if (!_syncing)
            _selection.Set(option.Id, value);
    }

    /// <summary>按 PartSelection 的状态刷新勾选框：父项没勾时子项变灰、取消。</summary>
    private void Sync()
    {
        _syncing = true;
        foreach (var (option, box) in _items)
        {
            box.IsEnabled = _selection.IsEnabled(option.Id);
            box.Opacity = box.IsEnabled ? 1 : 0.45;
            box.IsChecked = _selection.IsChecked(option.Id);
        }
        _syncing = false;
        Changed?.Invoke();
    }
}

/// <summary>左下角“导出备份”“导入备份”的勾选窗口。</summary>
public static class PartDialogs
{
    public const string SecretsId = TransferListing.SecretsId;
    public const string ShareReviewsId = TransferListing.ShareReviewsId;
    public const string ShareNotesId = TransferListing.ShareNotesId;

    /// <summary>勾选列表的最大高度：屏幕矮（例如笔记本 150% 缩放）时列表滚动，窗口不超出屏幕。others 是窗口里其余部分大约占的高度。</summary>
    private static double ListHeight(double others) => Math.Clamp(SystemParameters.WorkArea.Height - others, 200, 470);

    /// <summary>返回 null 表示取消。</summary>
    public static ExportChoice? Export(Window owner, IReadOnlyList<PartOption> self, IReadOnlyList<PartOption> share, bool shareMode)
    {
        var window = BackupDialogs.NewWindow(owner, "导出");
        window.Width = 540;
        var content = BackupDialogs.Body("导出", TransferListing.ExportMessage);

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var selfMode = new RadioButton { Content = "给自己备份", GroupName = "ExportMode", Margin = new Thickness(0, 0, 6, 0), Style = (Style)Application.Current.FindResource("Segment") };
        var shareModeButton = new RadioButton { Content = "分享给别人", GroupName = "ExportMode", Style = (Style)Application.Current.FindResource("Segment") };
        AutomationProperties.SetAutomationId(selfMode, "ExportModeSelf");
        AutomationProperties.SetAutomationId(shareModeButton, "ExportModeShare");
        modes.Children.Add(selfMode);
        modes.Children.Add(shareModeButton);
        content.Children.Add(modes);

        var hint = BackupDialogs.Note("");
        AutomationProperties.SetAutomationId(hint, "ExportModeHint");
        content.Children.Add(hint);

        var selfList = new PartChecklist(self, "Self");
        var shareList = new PartChecklist(share, "Share");
        var lists = new StackPanel();
        lists.Children.Add(selfList.Panel);
        lists.Children.Add(shareList.Panel);
        content.Children.Add(new ScrollViewer { Content = lists, MaxHeight = ListHeight(480), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var password = BackupDialogs.NewPasswordBox("BackupPassword", "备份密码");
        var confirm = BackupDialogs.NewPasswordBox("BackupPasswordConfirm", "再输一次备份密码");
        var passwordPanel = new StackPanel();
        passwordPanel.Children.Add(BackupDialogs.Label(TransferListing.PasswordLabel));
        passwordPanel.Children.Add(password);
        passwordPanel.Children.Add(BackupDialogs.Label("再输一次"));
        passwordPanel.Children.Add(confirm);
        passwordPanel.Children.Add(BackupDialogs.Note(TransferListing.PasswordNote));
        content.Children.Add(passwordPanel);

        var error = BackupDialogs.ErrorText();
        content.Children.Add(error);
        var ok = BackupDialogs.OkButton("导出");
        content.Children.Add(BackupDialogs.Buttons(BackupDialogs.CancelButton(window), ok));

        bool IsShare() => shareModeButton.IsChecked == true;
        bool NeedsPassword() => TransferListing.NeedsPassword(IsShare(), selfList.CheckedIds);
        void Refresh()
        {
            var isShare = IsShare();
            selfList.Panel.Visibility = isShare ? Visibility.Collapsed : Visibility.Visible;
            shareList.Panel.Visibility = isShare ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = isShare ? TransferListing.ShareHint : TransferListing.SelfHint;
            passwordPanel.Visibility = NeedsPassword() ? Visibility.Visible : Visibility.Collapsed;
            ok.Content = isShare ? "下一步" : "导出";
            ok.IsEnabled = (isShare ? shareList : selfList).Any;
            error.Visibility = Visibility.Collapsed;
        }
        selfList.Changed += Refresh;
        shareList.Changed += Refresh;
        selfMode.Checked += (_, _) => Refresh();
        shareModeButton.Checked += (_, _) => Refresh();
        (shareMode ? shareModeButton : selfMode).IsChecked = true;
        Refresh();

        ok.Click += (_, _) =>
        {
            if (NeedsPassword() && TransferListing.CheckPassword(password.Password, confirm.Password) is { } problem)
            {
                BackupDialogs.Show(error, problem);
                (password.Password.Length < TransferListing.MinPasswordLength ? password : confirm).Focus();
                return;
            }
            window.DialogResult = true;
        };
        BackupDialogs.Finish(window, content);

        if (window.ShowDialog() != true)
            return null;
        return IsShare()
            ? new ExportChoice(true, shareList.CheckedIds, "")
            : new ExportChoice(false, selfList.CheckedIds, NeedsPassword() ? password.Password : "");
    }

    /// <summary>导入前勾选要导入哪些部分。返回勾选的 Id；取消返回 null。</summary>
    public static IReadOnlySet<string>? Import(Window owner, string title, string message, IReadOnlyList<PartOption> options, string okText, bool danger)
    {
        var window = BackupDialogs.NewWindow(owner, title);
        window.Width = 540;
        var content = BackupDialogs.Body(title, message);
        var list = new PartChecklist(options, "Import");
        content.Children.Add(new ScrollViewer { Content = list.Panel, MaxHeight = ListHeight(280), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var ok = BackupDialogs.OkButton(okText);
        if (danger)
            ok.Style = (Style)Application.Current.FindResource("DangerButton");
        ok.Click += (_, _) => window.DialogResult = true;
        list.Changed += () => ok.IsEnabled = list.Any;
        ok.IsEnabled = list.Any;
        content.Children.Add(BackupDialogs.Buttons(BackupDialogs.CancelButton(window), ok));
        BackupDialogs.Finish(window, content);
        return window.ShowDialog() == true ? list.CheckedIds : null;
    }
}
