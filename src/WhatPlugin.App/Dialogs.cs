using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace WhatPlugin.App;

/// <summary>和主界面同一风格的确认框，代替系统的 MessageBox。</summary>
public static class Dialogs
{
    public static bool Confirm(Window owner, string title, string message, string okText, bool danger = false)
    {
        var option = false;
        return Confirm(owner, title, message, okText, null, ref option, danger);
    }

    /// <summary>带一个勾选项的危险操作确认，例如删除文件夹时“同时删除笔记”。</summary>
    public static bool Confirm(Window owner, string title, string message, string okText, string optionText, ref bool option) =>
        Confirm(owner, title, message, okText, optionText, ref option, danger: true);

    private static bool Confirm(Window owner, string title, string message, string okText, string? optionText, ref bool option, bool danger)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        var ok = new Button { Content = okText, MinWidth = 88, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Style = (Style)Application.Current.FindResource(danger ? "DangerButton" : "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "DialogOk");
        ok.Click += (_, _) => window.DialogResult = true;

        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "DialogCancel");
        cancel.Click += (_, _) => window.DialogResult = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        };
        AutomationProperties.SetAutomationId(body, "DialogMessage");
        content.Children.Add(body);
        CheckBox? extra = null;
        if (optionText != null)
        {
            extra = new CheckBox { IsChecked = option, Margin = new Thickness(0, 14, 0, 0), Content = new TextBlock { Text = optionText, TextWrapping = TextWrapping.Wrap } };
            AutomationProperties.SetName(extra, optionText);
            AutomationProperties.SetAutomationId(extra, "DialogOption");
            content.Children.Add(extra);
        }
        content.Children.Add(buttons);

        window.Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        if (window.ShowDialog() != true)
            return false;
        if (extra != null)
            option = extra.IsChecked == true;
        return true;
    }

    /// <summary>勾选若干项后确认。返回勾选的序号；取消返回 null。</summary>
    public static List<int>? PickMany(Window owner, string title, string message, IReadOnlyList<(string Text, bool Checked)> items, string okText)
    {
        var unused = false;
        return PickMany(owner, title, message, items, okText, null, ref unused);
    }

    /// <summary>同上，列表下面多一个选项（optionText 为空时不显示），确认后 option 是它的勾选状态。</summary>
    public static List<int>? PickMany(Window owner, string title, string message, IReadOnlyList<(string Text, bool Checked)> items, string okText, string? optionText, ref bool option)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = optionText == null ? 520 : 640,
            SizeToContent = SizeToContent.Height,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        var boxes = items.Select((item, i) =>
        {
            var box = new CheckBox
            {
                IsChecked = item.Checked,
                Margin = new Thickness(0, 4, 0, 4),
                Content = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap },
            };
            AutomationProperties.SetName(box, item.Text);
            AutomationProperties.SetAutomationId(box, $"PickItem{i}");
            return box;
        }).ToList();

        var ok = new Button { Content = okText, MinWidth = 88, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Style = (Style)Application.Current.FindResource("PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "DialogOk");
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "DialogCancel");
        cancel.Click += (_, _) => window.DialogResult = false;
        void Update() => ok.IsEnabled = boxes.Any(b => b.IsChecked == true);
        foreach (var box in boxes)
        {
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
        }
        Update();

        var list = new StackPanel();
        foreach (var box in boxes)
            list.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 10, 0, 12),
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        });
        if (boxes.Count > 1)
        {
            var bulk = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            void AddBulk(string text, string id, Func<CheckBox, bool> state)
            {
                var button = new Button
                {
                    Content = text,
                    Style = (Style)Application.Current.FindResource("GhostButton"),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                };
                AutomationProperties.SetAutomationId(button, id);
                button.Click += (_, _) =>
                {
                    foreach (var box in boxes)
                        box.IsChecked = state(box);
                };
                bulk.Children.Add(button);
            }
            AddBulk("全选", "PickAll", _ => true);
            AddBulk("取消全选", "PickNone", _ => false);
            AddBulk("反向选择", "PickInvert", box => box.IsChecked != true);
            content.Children.Add(bulk);
        }
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        CheckBox? extra = null;
        if (optionText != null)
        {
            extra = new CheckBox { IsChecked = option, Margin = new Thickness(0, 14, 0, 0), Content = new TextBlock { Text = optionText, TextWrapping = TextWrapping.Wrap } };
            AutomationProperties.SetName(extra, optionText);
            AutomationProperties.SetAutomationId(extra, "PickOption");
            content.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 0) });
            content.Children.Add(extra);
        }
        content.Children.Add(buttons);
        window.Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        if (window.ShowDialog() != true)
            return null;
        if (extra != null)
            option = extra.IsChecked == true;
        return boxes.Select((b, i) => (b, i)).Where(x => x.b.IsChecked == true).Select(x => x.i).ToList();
    }

    /// <summary>从列表里选一项后确认。返回选中的序号；取消返回 null。</summary>
    public static int? PickOne(Window owner, string title, string message, IReadOnlyList<string> items, string okText, int selected = 0)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        var list = new ListBox
        {
            ItemsSource = items.Select(text => new PickOption(text)).ToList(),
            ItemContainerStyle = (Style)Application.Current.FindResource("CategoryItem"),
            MaxHeight = 340,
            DisplayMemberPath = nameof(PickOption.Label),
        };
        AutomationProperties.SetAutomationId(list, "PickOneList");
        list.SelectedIndex = Math.Clamp(selected, -1, items.Count - 1);

        var ok = new Button { Content = okText, MinWidth = 88, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Style = (Style)Application.Current.FindResource("PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "DialogOk");
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "DialogCancel");
        cancel.Click += (_, _) => window.DialogResult = false;
        void Update() => ok.IsEnabled = list.SelectedIndex >= 0;
        list.SelectionChanged += (_, _) => Update();
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) window.DialogResult = true; };
        Update();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 10, 0, 12),
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        });
        content.Children.Add(new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Child = list,
        });
        content.Children.Add(buttons);
        window.Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        return window.ShowDialog() == true && list.SelectedIndex >= 0 ? list.SelectedIndex : null;
    }

    /// <summary>沿用分类栏的条目样式，所以包一层带 Label 的记录。</summary>
    private sealed record PickOption(string Label)
    {
        public bool IsHeader => false;
    }

    /// <summary>显示一段只读文字，可以另存成文件。</summary>
    /// <param name="saved">保存成文件成功后调用。</param>
    public static void ShowText(Window owner, string title, string text, string fileName, Action? saved = null)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 640,
            Height = 560,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetAutomationId(result, "DialogResult");

        var save = new Button { Content = "保存为文件…", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        save.Style = (Style)Application.Current.FindResource("PrimaryButton");
        AutomationProperties.SetAutomationId(save, "DialogSave");
        save.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = "文本文件 (*.txt)|*.txt", FileName = fileName };
            if (dialog.ShowDialog(window) != true)
                return;
            try
            {
                System.IO.File.WriteAllText(dialog.FileName, text);
                result.Text = "已保存";
                saved?.Invoke();
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
            {
                result.Text = "保存失败：" + error.Message;
            }
        };

        var close = new Button { Content = "关闭", MinWidth = 76, IsCancel = true };
        AutomationProperties.SetAutomationId(close, "DialogCancel");
        close.Click += (_, _) => window.Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        buttons.Children.Add(result);

        var heading = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        var body = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetAutomationId(body, "DialogText");
        AutomationProperties.SetName(body, title);

        var content = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        content.Children.Add(heading);
        content.Children.Add(buttons);
        content.Children.Add(body);

        window.Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        window.ShowDialog();
    }

    /// <summary>带一个输入框的对话框。取消时返回 null。</summary>
    public static string? Prompt(Window owner, string title, string message, string okText, string initial, bool danger = false, bool allowEmpty = false)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        var input = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(input, "DialogInput");
        AutomationProperties.SetName(input, title);

        var ok = new Button { Content = okText, MinWidth = 88, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Style = (Style)Application.Current.FindResource(danger ? "DangerButton" : "PrimaryButton");
        AutomationProperties.SetAutomationId(ok, "DialogOk");
        ok.Click += (_, _) => window.DialogResult = true;
        if (!allowEmpty)
        {
            ok.IsEnabled = initial.Trim().Length > 0;
            input.TextChanged += (_, _) => ok.IsEnabled = input.Text.Trim().Length > 0;
        }

        var cancel = new Button { Content = "取消", MinWidth = 76, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "DialogCancel");
        cancel.Click += (_, _) => window.DialogResult = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        };
        AutomationProperties.SetAutomationId(body, "DialogMessage");
        content.Children.Add(body);
        content.Children.Add(input);
        content.Children.Add(buttons);

        window.Content = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return window.ShowDialog() == true ? input.Text : null;
    }
}

/// <summary>自绘标题栏的窗口默认是直角，这里请系统按 Windows 11 的样式给圆角和深色边框。</summary>
public static class WindowEffects
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int RoundCorners = 2;

    /// <summary>离屏模式（见 <see cref="App.Offscreen"/>）下窗口不激活、不进任务栏。</summary>
    private static void Quiet(Window window)
    {
        if (!App.Offscreen)
            return;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    /// <summary>对话框没有系统标题栏，顶部一条可以拖动。</summary>
    public static void ApplyDialog(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Quiet(window);
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Apply(window);
    }

    public static void Apply(Window window)
    {
        Quiet(window);
        window.SourceInitialized += (_, _) => UpdateFrame(window);
    }

    /// <summary>窗口边框跟随主题的深浅。切换主题时也会调用。</summary>
    public static void UpdateFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var dark = ThemeManager.Current.IsDark ? 1 : 0;
        var round = RoundCorners;
        // 旧版 Windows 不认识这些属性，返回错误码而已，窗口照常显示。
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

/// <summary>
/// 打开、保存文件的系统对话框。离屏测试（见 <see cref="App.Offscreen"/>）时系统对话框会出现在屏幕上抢焦点，
/// 所以离屏模式下如果设了环境变量 WHATPLUGIN_OFFSCREEN_FILES（指向一个文本文件），就从里面按行依次取路径，不弹对话框；取完了当作取消。
/// 正常启动时不受影响。
/// </summary>
public static class FileDialogs
{
    public const string QueueVariable = "WHATPLUGIN_OFFSCREEN_FILES";

    public static bool Show(Microsoft.Win32.FileDialog dialog, Window owner)
    {
        if (App.Offscreen && Environment.GetEnvironmentVariable(QueueVariable) is { Length: > 0 } queue)
        {
            var lines = System.IO.File.Exists(queue) ? System.IO.File.ReadAllLines(queue).Where(l => l.Length > 0).ToList() : [];
            if (lines.Count == 0)
                return false;
            dialog.FileName = lines[0];
            System.IO.File.WriteAllLines(queue, lines.Skip(1));
            return true;
        }
        return dialog.ShowDialog(owner) == true;
    }
}
