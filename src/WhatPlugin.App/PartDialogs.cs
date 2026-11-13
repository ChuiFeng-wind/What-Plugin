using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WhatPlugin.App;

/// <summary>导出、导入时的一个勾选项。Parent 不为空时缩进显示，父项没勾时它不能勾。</summary>
public sealed record PartOption(string Id, string Label, string Detail = "", bool Enabled = true, bool Checked = true, string? Parent = null);

/// <summary>一组勾选项，上面有“全选”“全不选”“反选”。只对能勾的项起作用。</summary>
internal sealed class PartChecklist
{
    private readonly List<(PartOption Option, CheckBox Box)> _items = [];
    private readonly Dictionary<string, bool> _wanted = [];
    private bool _syncing;

    public StackPanel Panel { get; } = new();

    public event Action? Changed;

    public PartChecklist(IReadOnlyList<PartOption> options, string idPrefix)
    {
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-10, 10, 0, 4) };
        quick.Children.Add(Quick("全选", idPrefix + "SelectAll", _ => true));
        quick.Children.Add(Quick("全不选", idPrefix + "SelectNone", _ => false));
        quick.Children.Add(Quick("反选", idPrefix + "Invert", current => !current));
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
                IsChecked = option.Enabled && option.Checked,
                IsEnabled = option.Enabled,
                Content = text,
                Margin = new Thickness(option.Parent == null ? 0 : 26, 5, 0, 5),
            };
            AutomationProperties.SetName(box, option.Label);
            AutomationProperties.SetHelpText(box, option.Detail);
            AutomationProperties.SetAutomationId(box, idPrefix + option.Id);
            _wanted[option.Id] = option.Enabled && option.Checked;
            // 不能勾的项整行变淡，和没勾的区分开。
            box.Opacity = box.IsEnabled ? 1 : 0.45;
            box.IsEnabledChanged += (_, _) => box.Opacity = box.IsEnabled ? 1 : 0.45;
            box.Checked += (_, _) => Toggled(option, true);
            box.Unchecked += (_, _) => Toggled(option, false);
            _items.Add((option, box));
            Panel.Children.Add(box);
        }
        Sync();
    }

    /// <summary>勾上的、而且能勾的项。</summary>
    public IReadOnlySet<string> CheckedIds => _items.Where(i => i.Box.IsEnabled && i.Box.IsChecked == true).Select(i => i.Option.Id).ToHashSet();

    public bool Any => CheckedIds.Count > 0;

    private Button Quick(string text, string id, Func<bool, bool> value)
    {
        var button = new Button { Content = text, Height = 28, Style = (Style)Application.Current.FindResource("GhostButton") };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) =>
        {
            _syncing = true;
            foreach (var (option, box) in _items)
            {
                if (!option.Enabled)
                    continue;
                // 子项按它自己想要的状态算：父项没勾时它被暂时取消，反选时不该把这个当成它的状态。
                var next = value(_wanted[option.Id]);
                _wanted[option.Id] = next;
                box.IsChecked = next;
            }
            _syncing = false;
            Sync();
        };
        return button;
    }

    private void Toggled(PartOption option, bool value)
    {
        if (_syncing)
            return;
        _wanted[option.Id] = value;
        Sync();
    }

    /// <summary>父项没勾时子项变灰、取消；父项再勾上时子项恢复原来的状态。</summary>
    private void Sync()
    {
        _syncing = true;
        foreach (var (option, box) in _items)
        {
            if (option.Parent == null || !option.Enabled)
                continue;
            var parent = _items.FirstOrDefault(i => i.Option.Id == option.Parent).Box;
            var enabled = parent == null || parent.IsEnabled && parent.IsChecked == true;
            box.IsEnabled = enabled;
            box.IsChecked = enabled && _wanted[option.Id];
        }
        _syncing = false;
        Changed?.Invoke();
    }
}

/// <summary>导出时选的用途和内容。Share 为 true 时 Parts 是 "reviews"、"notes"，否则是 BackupParts 的名称。</summary>
public sealed record ExportChoice(bool Share, IReadOnlySet<string> Parts, string Password);

/// <summary>左下角“导出备份”“导入备份”的勾选窗口。</summary>
public static class PartDialogs
{
    public const string SecretsId = "Secrets";
    public const string ShareReviewsId = "reviews";
    public const string ShareNotesId = "notes";

    /// <summary>勾选列表的最大高度：屏幕矮（例如笔记本 150% 缩放）时列表滚动，窗口不超出屏幕。others 是窗口里其余部分大约占的高度。</summary>
    private static double ListHeight(double others) => Math.Clamp(SystemParameters.WorkArea.Height - others, 200, 470);

    /// <summary>返回 null 表示取消。</summary>
    public static ExportChoice? Export(Window owner, IReadOnlyList<PartOption> self, IReadOnlyList<PartOption> share, bool shareMode)
    {
        var window = BackupDialogs.NewWindow(owner, "导出");
        window.Width = 540;
        var content = BackupDialogs.Body("导出", "先选用途，再勾要导出的内容。");

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
        passwordPanel.Children.Add(BackupDialogs.Label("备份密码（用来加密账号、密码和序列号）"));
        passwordPanel.Children.Add(password);
        passwordPanel.Children.Add(BackupDialogs.Label("再输一次"));
        passwordPanel.Children.Add(confirm);
        passwordPanel.Children.Add(BackupDialogs.Note("导入时要输入它。它不会保存在任何地方，忘了就解不开账号密码（其他内容不受影响）。"));
        content.Children.Add(passwordPanel);

        var error = BackupDialogs.ErrorText();
        content.Children.Add(error);
        var ok = BackupDialogs.OkButton("导出");
        content.Children.Add(BackupDialogs.Buttons(BackupDialogs.CancelButton(window), ok));

        bool IsShare() => shareModeButton.IsChecked == true;
        bool NeedsPassword() => !IsShare() && selfList.CheckedIds.Contains(SecretsId);
        void Refresh()
        {
            var isShare = IsShare();
            selfList.Panel.Visibility = isShare ? Visibility.Collapsed : Visibility.Visible;
            shareList.Panel.Visibility = isShare ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = isShare
                ? "发给别人看。不含工程名、路径和账号密码；对方导入后单独存放，不会覆盖对方自己的内容。下一步挑要分享的插件和笔记，两样都勾时存成一个文件。"
                : "换电脑、重装或留底用。导入时会覆盖本机相同的条目；没勾的部分，导入时本机内容不动。";
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
            if (NeedsPassword())
            {
                if (password.Password.Length < BackupDialogs.MinPasswordLength)
                {
                    BackupDialogs.Show(error, $"备份密码至少 {BackupDialogs.MinPasswordLength} 位，或者不勾“账号、密码和序列号”");
                    password.Focus();
                    return;
                }
                if (password.Password != confirm.Password)
                {
                    BackupDialogs.Show(error, "两次输入的备份密码不一样");
                    confirm.Focus();
                    return;
                }
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
