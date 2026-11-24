using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Material.Icons;
using Material.Icons.Avalonia;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

public enum WelcomeChoice { Skip, Tour, ScanAndTour }

/// <summary>第一次打开时的欢迎窗口，也可以从设置里再打开。和 Windows 版的欢迎窗口内容一样。</summary>
public sealed class WelcomeDialog : Window
{
    /// <summary>Mac 上扫描 VST3 和 AU，Windows 上先扫 VST3（VST2 要另加文件夹）。</summary>
    public static string ScanNote => AppPlatform.IsMac
        ? "先扫描 VST3 和 AU 插件，大概要几分钟，扫描时可以继续看导览。扫描只读取插件清单，不打开插件界面，不弹授权窗口。"
        : "先扫描 VST3 插件，大概要几分钟，扫描时可以继续看导览。扫描只读取插件清单，不打开插件界面，不弹授权窗口。VST2 插件要在“设置”里添加所在的文件夹。";

    public WelcomeDialog(bool canScan)
    {
        Title = "欢迎使用 WhatPlugin";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Surface"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Text"));
        this.Bind(FontFamilyProperty, this.GetResourceObservable("UiFont"));
        FontSize = 13;

        var body = new StackPanel { Margin = new Thickness(28, 26, 28, 22) };

        var logo = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(11), ClipToBounds = true };
        logo.Child = new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://WhatPlugin.Desktop/Assets/logo.png"))), Stretch = Stretch.UniformToFill };
        AutomationProperties.SetName(logo, "WhatPlugin 标志");
        var heading = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = "欢迎使用 WhatPlugin", FontSize = 21, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(Dim(new TextBlock { Text = "给音乐人和混音师的插件管理工具", Margin = new Thickness(0, 3, 0, 0) }));
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(logo);
        top.Children.Add(heading);
        body.Children.Add(top);

        var features = new UniformGrid { Columns = 3, Margin = new Thickness(0, 22, 0, 0) };
        features.Children.Add(Feature(MaterialIconKind.Bookshelf, "整理插件", "扫描本机插件，自动认出厂家和分类，写笔记、打分。", new Thickness(0, 0, 6, 0)));
        features.Children.Add(Feature(MaterialIconKind.FolderSearchOutline, "分析工程", "查出工程缺了哪些插件，哪些插件一直闲着。", new Thickness(3, 0, 3, 0)));
        features.Children.Add(Feature(MaterialIconKind.NoteEditOutline, "记下经验", "把效果链记成混音笔记，买过、卸过的都有记录。", new Thickness(6, 0, 0, 0)));
        body.Children.Add(features);

        var first = new StackPanel();
        first.Children.Add(new TextBlock { Text = "第一步：扫描插件", FontWeight = FontWeight.SemiBold });
        first.Children.Add(Dim(new TextBlock { Text = ScanNote, Classes = { "SmallNote" }, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap }));
        var firstBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14), Margin = new Thickness(0, 16, 0, 0), Child = first };
        firstBox.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentSoft"));
        body.Children.Add(firstBox);

        body.Children.Add(new TextBlock
        {
            Text = "所有数据只保存在这台电脑上。工程文件只读不改，不读取也不修改 iLok / PACE 授权。",
            Classes = { "SmallNote" }, Margin = new Thickness(2, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        var skip = new Button { Content = "跳过", Classes = { "Ghost" }, IsCancel = true };
        ToolTip.SetTip(skip, "以后可以在“设置”里重新查看新手指引");
        var tour = new Button { Content = "只看导览", Classes = { canScan ? "Ghost" : "Primary" }, Margin = new Thickness(0, 0, 8, 0), IsDefault = !canScan };
        var scanContent = new StackPanel { Orientation = Orientation.Horizontal };
        scanContent.Children.Add(new MaterialIcon { Kind = MaterialIconKind.Refresh, Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0) });
        scanContent.Children.Add(new TextBlock { Text = "开始扫描并参观", VerticalAlignment = VerticalAlignment.Center });
        // 已经在扫描时只提供导览。
        var scan = new Button { Content = scanContent, Classes = { "Primary" }, IsDefault = canScan, IsVisible = canScan, Padding = new Thickness(16, 0) };
        AutomationProperties.SetName(scan, "开始扫描并参观");
        skip.Click += (_, _) => Close(WelcomeChoice.Skip);
        tour.Click += (_, _) => Close(WelcomeChoice.Tour);
        scan.Click += (_, _) => Close(WelcomeChoice.ScanAndTour);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(tour);
        right.Children.Add(scan);
        var buttons = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        DockPanel.SetDock(skip, Dock.Left);
        buttons.Children.Add(skip);
        buttons.Children.Add(right);
        body.Children.Add(buttons);

        Content = body;
        Opened += (_, _) => (canScan ? scan : tour).Focus();
    }

    private TextBlock Dim(TextBlock text)
    {
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextDim"));
        return text;
    }

    private Border Feature(MaterialIconKind icon, string title, string text, Thickness margin)
    {
        var panel = new StackPanel();
        var glyph = new MaterialIcon { Kind = icon, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        glyph.Bind(MaterialIcon.ForegroundProperty, this.GetResourceObservable("Accent"));
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = text, Classes = { "SmallNote" }, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        var card = new Border { Classes = { "Card" }, Padding = new Thickness(14, 14, 14, 12), Margin = margin, Child = panel };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("Bg1"));
        return card;
    }

    /// <summary>关掉窗口（按 Esc 或标题栏关闭）算跳过。</summary>
    public static async Task<WelcomeChoice> Show(Window owner, bool canScan) =>
        await new WelcomeDialog(canScan).ShowDialog<object?>(owner) is WelcomeChoice choice ? choice : WelcomeChoice.Skip;
}
