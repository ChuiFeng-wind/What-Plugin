using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>插件库和垃圾桶页面。用临时数据库里的一批示例插件，不碰默认位置的那份。</summary>
public sealed class LibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-lib-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;

    public LibraryTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        _database = new PluginDatabase(_dbPath);
        Seed();
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static MergedPlugin Plugin(string name, string vendor, params string[] tags)
    {
        var key = Normalizer.Name(name);
        return new MergedPlugin(key, name, vendor, InfoSource.PluginLoad,
            new Classification(PluginKind.Effect, tags, ClassificationSource.Seed),
            [new MergedFile(Path.Combine("Plugins", key + ".vst3"), PluginFormat.Vst3, Arch.X64, "1.0", "Fx", ScanStatus.Loaded, null)]);
    }

    /// <summary>一批常见的效果器，有评分、感受、混音属性，还有一款在垃圾桶里。</summary>
    private void Seed()
    {
        _database.SaveMerged(
        [
            Plugin("Pro-Q 4", "FabFilter", "EQ"),
            Plugin("Pro-C 2", "FabFilter", "压缩", "动态"),
            Plugin("Pro-L 2", "FabFilter", "限制器", "母带"),
            Plugin("Pro-DS", "FabFilter", "去齿音", "人声"),
            Plugin("ValhallaRoom", "Valhalla DSP", "混响"),
            Plugin("ValhallaDelay", "Valhalla DSP", "延迟"),
            Plugin("Decapitator", "Soundtoys", "失真/饱和"),
            Plugin("EchoBoy", "Soundtoys", "延迟", "磁带"),
            Plugin("Kickstart 2", "Nicky Romero", "动态", "工具"),
            Plugin("Old Free Comp", "Freeware", "压缩"),
        ]);
        Edit("Pro-Q 4", d => d with { Rating = 5, Note = "动态 EQ 切人声浑浊段很好用", Mix = new MixAttributes(LatencyLevel.Zero, CpuLevel.Light, Tri.Yes, Tri.Yes) });
        Edit("Pro-C 2", d => d with { Rating = 4, Note = "鼓组总线", Mix = new MixAttributes(LatencyLevel.Low, CpuLevel.Light) });
        Edit("ValhallaRoom", d => d with { Rating = 5, Note = "长尾巴", Mix = new MixAttributes(Cpu: CpuLevel.Medium) });
        Edit("Decapitator", d => d with { Rating = 3 });
        Edit("Old Free Comp", d => d with { Hidden = true, BlacklistReason = "音色发闷，有更好的" });
    }

    private void Edit(string name, Func<PluginUserData, PluginUserData> change)
    {
        var row = _database.LoadEffects().Single(p => p.Name == name);
        _database.SaveUserData(row.Id, change(row.User).Normalized());
    }

    private PluginRow Row(string name) => _database.LoadEffects().Single(p => p.Name == name);

    private MainWindow Open(double width = 1440, double height = 900)
    {
        var window = new MainWindow(_database, _dbPath) { Width = width, Height = height };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static PluginLibraryView Library(MainWindow window, string key = "Plugins") => window.Library(key)!;

    private static T Find<T>(PluginLibraryView view, string name) where T : Control => view.FindControl<T>(name)!;

    private static List<string> Names(PluginLibraryView view) => view.Shown.Select(p => p.Name).ToList();

    [AvaloniaFact]
    public void ListsEffectsButNotTheTrash()
    {
        var window = Open();
        var view = Library(window);

        Assert.Equal(9, view.Shown.Count);
        Assert.DoesNotContain("Old Free Comp", Names(view));
        Assert.Equal("效果器 9 款，垃圾桶 1 款，当前显示 9 款", window.Status);
        // 行真的画出来了：名称和厂家都在可视树里。
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Pro-Q 4", texts);
        Assert.Contains("Valhalla DSP", texts);
    }

    [AvaloniaFact]
    public void SearchVendorRatingAndCategoryFilterTheList()
    {
        var window = Open();
        var view = Library(window);

        Find<TextBox>(view, "SearchBox").Text = "人声";
        Settle(window);
        Assert.Equal(["Pro-DS", "Pro-Q 4"], Names(view).Order());

        Find<TextBox>(view, "SearchBox").Text = "";
        Settle(window);
        Find<ComboBox>(view, "VendorFilter").SelectedItem = "Soundtoys";
        Assert.Equal(["Decapitator", "EchoBoy"], Names(view));

        Find<ComboBox>(view, "VendorFilter").SelectedIndex = 0;
        Find<ComboBox>(view, "RatingFilter").SelectedIndex = (int)RatingFilter.HighRated;
        Assert.Equal(["Pro-C 2", "Pro-Q 4", "ValhallaRoom"], Names(view).Order());

        Find<Button>(view, "ClearFiltersButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(9, view.Shown.Count);

        // 选一个分类：同类放在一起，评分高的在前；副标题换成这一类的概况。
        var categories = Find<ListBox>(view, "CategoryList");
        categories.SelectedItem = categories.Items.Cast<LibraryCategory>().Single(c => c.Label == "延迟");
        Assert.Equal(["EchoBoy", "ValhallaDelay"], Names(view));
        Assert.Contains("“延迟”共 2 款", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).First(t => t?.Contains("“延迟”") == true));
    }

    [AvaloniaFact]
    public void TimbreCategoriesAreASeparateList()
    {
        var window = Open();
        var view = Library(window);

        Find<RadioButton>(view, "CategoryTimbreToggle").IsChecked = true;
        var labels = Find<ListBox>(view, "CategoryList").Items.Cast<LibraryCategory>().Select(c => c.Label).ToList();

        Assert.Contains("人声", labels);
        Assert.DoesNotContain("EQ", labels);
    }

    [AvaloniaFact]
    public void StarsAndMixAttributesSaveRightAway()
    {
        var window = Open();
        var view = Library(window);
        view.Select("Decapitator");
        Settle(window);
        Assert.Equal(3, view.DetailRating);

        view.ClickStar(5);
        Assert.Equal(5, Row("Decapitator").User.Rating);
        // 再点一次当前星级清除。
        view.ClickStar(5);
        Assert.Equal(0, Row("Decapitator").User.Rating);

        Find<ComboBox>(view, "MixCpu").SelectedIndex = (int)CpuLevel.Heavy;
        Assert.Equal(CpuLevel.Heavy, Row("Decapitator").User.Mix?.Cpu);
        Assert.Equal("已保存 Decapitator", window.Status);
        // 列表里这一行跟着变。
        Assert.Equal(new MixAttributes(Cpu: CpuLevel.Heavy).CpuText, view.Shown.Single(p => p.Name == "Decapitator").CpuText);
    }

    [AvaloniaFact]
    public void TypedTextIsSavedWhenSwitchingPluginsOrPages()
    {
        var window = Open();
        var view = Library(window);
        view.Select("EchoBoy");
        Settle(window);

        Find<TextBox>(view, "NoteBox").Text = "人声 slap 很有味道";
        Find<TextBox>(view, "MyTagsBox").Text = "人声，复古";
        view.Select("Pro-L 2");
        Assert.Equal("人声 slap 很有味道", Row("EchoBoy").User.Note);
        Assert.Equal(["人声", "复古"], Row("EchoBoy").User.MyTags);

        Find<TextBox>(view, "NoteBox").Text = "母带最后一级";
        window.Navigate("Settings");
        Assert.Equal("母带最后一级", Row("Pro-L 2").User.Note);
    }

    [AvaloniaFact]
    public void JustLookingAtPluginsDoesNotSaveAnything()
    {
        var window = Open();
        var view = Library(window);
        view.Select("Pro-Q 4");
        Settle(window);
        view.Select("Pro-C 2");
        Settle(window);

        // 填详情时排队到达的文字变化事件不算修改，切走时也不保存。
        view.SaveCurrent();
        window.Navigate("Settings");

        Assert.DoesNotContain("已保存", window.Status);
    }

    [AvaloniaFact]
    public void ChoosingTagsAndTimbresAndResetting()
    {
        var window = Open();
        var view = Library(window);
        view.Select("Pro-C 2");
        Settle(window);

        var drums = view.GetVisualDescendants().OfType<ToggleButton>().Single(c => c.Content as string == "鼓" && c.Classes.Contains("Chip"));
        drums.IsChecked = true;
        Assert.Equal(["鼓"], Row("Pro-C 2").Timbres);

        Find<Button>(view, "TagExpandButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.Equal("1", _database.GetSetting("detail.tagsExpanded"));
        Find<TextBox>(view, "NewTagBox").Text = "总线胶水";
        Find<Button>(view, "AddTagButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("总线胶水", Row("Pro-C 2").Tags);
        Assert.Equal(ClassificationSource.Manual, Row("Pro-C 2").TagSource);

        Find<Button>(view, "ResetButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(Row("Pro-C 2").User.HasOverrides);
        Assert.Equal(["压缩", "动态"], Row("Pro-C 2").Tags);
    }

    [AvaloniaFact]
    public void MovingToTheTrashAndBack()
    {
        var window = Open();
        var view = Library(window);
        view.Select("Kickstart 2");
        Settle(window);
        Find<TextBox>(view, "ReasonBox").Text = "用 Pro-C 2 侧链代替";

        Find<Button>(view, "BlacklistButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(Row("Kickstart 2").User.Hidden);
        Assert.Equal("用 Pro-C 2 侧链代替", Row("Kickstart 2").User.BlacklistReason);
        Assert.DoesNotContain("Kickstart 2", Names(view));
        Assert.Null(view.Selected);
        Assert.Contains("已把 Kickstart 2 移入垃圾桶", window.Status);
        Assert.Contains("2", window.GetVisualDescendants().OfType<RadioButton>().Single(r => Equals(r.Tag, "Blacklist"))
            .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        window.Navigate("Blacklist");
        Settle(window);
        var trash = Library(window, "Blacklist");
        Assert.Equal(["Kickstart 2", "Old Free Comp"], Names(trash).Order());
        Assert.Contains("移入原因", trash.ColumnHeaders);
        Assert.Contains(trash.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "音色发闷，有更好的" && t.IsEffectivelyVisible && t.Bounds.Width > 40);
        trash.Select("Kickstart 2");
        Settle(window);
        Assert.Equal("从垃圾桶恢复", Find<Button>(trash, "BlacklistButton").Content);
        Find<Button>(trash, "BlacklistButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.False(Row("Kickstart 2").User.Hidden);
        // 原因是用户写的，恢复后也留着，再移入不用重写。
        Assert.Equal("用 Pro-C 2 侧链代替", Row("Kickstart 2").User.BlacklistReason);
        window.Navigate("Plugins");
        Assert.Contains("Kickstart 2", Names(view));
    }

    [AvaloniaFact]
    public void MarkingAsPaidAddsItToPurchases()
    {
        var window = Open();
        var view = Library(window);
        view.Select("ValhallaRoom");
        Settle(window);

        Find<ComboBox>(view, "PurchaseKindBox").SelectedIndex = (int)PurchaseKind.Paid;

        Assert.Equal(PurchaseKind.Paid, Row("ValhallaRoom").User.Purchase);
        Assert.Contains(_database.LoadWishes(), w => w.Name == "ValhallaRoom" && w.Status == WishStatus.Purchased);
        Assert.Contains("已把 ValhallaRoom 加到“已购账号”", window.Status);

        // 改回去再改成已购，不会重复建。
        Find<ComboBox>(view, "PurchaseKindBox").SelectedIndex = (int)PurchaseKind.Free;
        Find<ComboBox>(view, "PurchaseKindBox").SelectedIndex = (int)PurchaseKind.Paid;
        Assert.Single(_database.LoadWishes(), w => w.Name == "ValhallaRoom");
    }

    [AvaloniaFact]
    public void RecommendationReplacesTheListUntilExited()
    {
        var window = Open();
        var view = Library(window);

        Find<TextBox>(view, "NeedBox").Text = "人声去齿音";
        view.StartRecommendation();

        Assert.Equal("Pro-DS", view.Shown[0].Name);
        Assert.Contains("推荐理由", view.ColumnHeaders);
        Assert.DoesNotContain("备注", view.ColumnHeaders);
        Assert.False(Find<Grid>(view, "FilterRow").IsEnabled);
        Assert.Contains("最相关的排在最前", window.Status);

        view.ExitRecommendation();
        Assert.Equal(9, view.Shown.Count);
        Assert.True(Find<Grid>(view, "FilterRow").IsEnabled);
        Assert.Contains("备注", view.ColumnHeaders);
    }

    [AvaloniaFact]
    public void CompareFindsTheNarrowestCategory()
    {
        var window = Open();
        var view = Library(window);
        view.Select("ValhallaDelay");
        Settle(window);

        Find<Button>(view, "CompareCurrentButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("延迟", (Find<ListBox>(view, "CategoryList").SelectedItem as LibraryCategory)?.Label);
        Assert.Equal(["EchoBoy", "ValhallaDelay"], Names(view).Order());
        Assert.Equal("ValhallaDelay", view.Selected?.Name);
    }

    [AvaloniaFact]
    public void NarrowWindowsHideTheLeastImportantColumns()
    {
        // 和 Windows 版一样按列表本身的宽度算：1440 宽的窗口里列表只有 600 多，已经收起线性相位。
        var wide = Library(Open(1920, 1000));
        Assert.Contains("线性相位", wide.ColumnHeaders);

        var narrow = Library(Open(1200, 800));
        Assert.DoesNotContain("线性相位", narrow.ColumnHeaders);
        Assert.DoesNotContain("延迟", narrow.ColumnHeaders);

        // 再窄就继续收列，留下的列都完整显示，不会被挤出列表。
        var tiny = Library(Open(1100, 760));
        Assert.Equal(["插件", "功能分类"], tiny.ColumnHeaders);
    }

    [AvaloniaFact]
    public void ColumnsAlwaysFitTheList()
    {
        foreach (var width in new[] { 300.0, 450, 590, 700, 860, 1200 })
        foreach (var (recommending, blacklist) in new[] { (false, false), (false, true), (true, false) })
        {
            var columns = LibraryColumn.Visible(recommending, blacklist, width);
            Assert.Contains(LibraryColumn.Plugin, columns);
            if (columns.Count > 2)
                Assert.True(columns.Sum(c => c.MinWidth) <= width, $"{width} {recommending} {blacklist}");
            if (blacklist)
                Assert.Contains(LibraryColumn.Reason, columns);
            if (recommending)
                Assert.Contains(LibraryColumn.Recommend, columns);
        }
    }

    /// <summary>有数据时插件库、选中详情、推荐和垃圾桶的样子；设置 WHATPLUGIN_SCREENSHOTS 时保存图片。</summary>
    [AvaloniaFact]
    public void RendersTheLibraryWithData()
    {
        var output = Environment.GetEnvironmentVariable("WHATPLUGIN_SCREENSHOTS");
        if (output != null)
            Directory.CreateDirectory(output);
        void Save(Window window, string name)
        {
            Settle(window);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(output ?? _dir, name + ".png"), new PngBitmapEncoderOptions());
        }

        var window = Open();
        var view = Library(window);
        foreach (var palette in ThemePalettes.All)
        {
            ThemeManager.Apply(palette.Id);
            view.Select("Pro-Q 4");
            Save(window, $"library-{palette.Id}");
        }
        ThemeManager.Apply(null);

        Find<TextBox>(view, "NeedBox").Text = "低延迟的人声压缩";
        view.StartRecommendation();
        Save(window, "library-recommend");
        view.ExitRecommendation();

        window.Navigate("Blacklist");
        Library(window, "Blacklist").Select("Old Free Comp");
        Save(window, "library-trash");

        var narrow = Open(1100, 760);
        Library(narrow).Select("ValhallaRoom");
        Save(narrow, "library-narrow");
    }
}
