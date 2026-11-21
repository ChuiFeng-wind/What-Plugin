using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WhatPlugin.Desktop;

/// <summary>插件库表格的一列。宽度、最小宽度和 Windows 版的 DataGrid 列一样。</summary>
public sealed record LibraryColumn(string Key, string Header, GridLength Width, double MinWidth, Func<PluginItem, string>? Text = null)
{
    public static readonly LibraryColumn Plugin = new("Plugin", "插件", new(2, GridUnitType.Star), 150);
    public static readonly LibraryColumn Tags = new("Tags", "功能分类", new(1.6, GridUnitType.Star), 110);
    public static readonly LibraryColumn Rating = new("Rating", "评分", new(84), 84, p => p.RatingText);
    public static readonly LibraryColumn Usage = new("Usage", "工程", new(56), 56, p => p.UsageCountText);
    public static readonly LibraryColumn LastUsed = new("LastUsed", "最近使用", new(96), 96, p => p.LastUsedText);
    public static readonly LibraryColumn Latency = new("Latency", "延迟", new(66), 66, p => p.LatencyText);
    public static readonly LibraryColumn Cpu = new("Cpu", "CPU", new(50), 50, p => p.CpuText);
    public static readonly LibraryColumn LinearPhase = new("LinearPhase", "线性相位", new(70), 70, p => p.LinearPhaseText);
    public static readonly LibraryColumn Sidechain = new("Sidechain", "侧链", new(50), 50, p => p.SidechainText);
    public static readonly LibraryColumn Note = new("Note", "备注", new(1.4, GridUnitType.Star), 90, p => p.NoteLine);
    public static readonly LibraryColumn Reason = new("Reason", "移入原因", new(1.5, GridUnitType.Star), 100, p => p.BlacklistReason);
    public static readonly LibraryColumn Recommend = new("Recommend", "推荐理由", new(3, GridUnitType.Star), 150, p => p.RecommendReason);

    /// <summary>
    /// 当前要显示的列。推荐时只看名称、分类、评分和推荐理由；垃圾桶多一列移入原因。
    /// 窗口窄时和 Windows 版一样先收起线性相位和侧链，再窄就收起延迟和 CPU；
    /// 还放不下就依次收起最近使用、工程、备注、评分和分类，不让列被挤出列表看不见。
    /// 推荐理由和移入原因是这两种视图要看的东西，不收。
    /// </summary>
    public static IReadOnlyList<LibraryColumn> Visible(bool recommending, bool blacklist, double width)
    {
        var columns = recommending
            ? new List<LibraryColumn> { Plugin, Tags, Rating, Recommend }
            : [Plugin, Tags, Rating, Usage, LastUsed, Latency, Cpu, LinearPhase, Sidechain, Note];
        if (!recommending && blacklist)
            columns.Add(Reason);
        if (width <= 0)
            return columns;
        if (width < 860)
            columns.RemoveAll(c => c == LinearPhase || c == Sidechain);
        if (width < 700)
            columns.RemoveAll(c => c == Latency || c == Cpu);
        foreach (var column in (LibraryColumn[])[LastUsed, Usage, Note, Rating, Tags])
        {
            if (columns.Sum(c => c.MinWidth) <= width)
                break;
            columns.Remove(column);
        }
        return columns;
    }

    /// <summary>单元格左右留白，和 Windows 版 DataGrid 的单元格一样。表头用同样的留白，两边才对得齐。</summary>
    public static readonly Thickness CellPadding = new(14, 0, 8, 0);

    public static Grid CreateGrid(IReadOnlyList<LibraryColumn> columns)
    {
        var grid = new Grid();
        foreach (var column in columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition(column.Width) { MinWidth = column.MinWidth });
        return grid;
    }
}

/// <summary>
/// 列表里的一行。Avalonia 12 已经没有按名字反射的绑定，这里直接订阅 <see cref="PluginItem.PropertyChanged"/> 刷新文字；
/// 行控件会被复用，换了数据就换订阅。
/// </summary>
public sealed class PluginRowView : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    private readonly IReadOnlyList<LibraryColumn> _columns;
    private readonly Dictionary<string, TextBlock> _texts = [];
    private readonly TextBlock _name = new() { FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _vendor = new() { FontSize = 12, Margin = new(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _uninstalled;
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    private PluginItem? _item;

    public PluginRowView(IReadOnlyList<LibraryColumn> columns)
    {
        _columns = columns;
        Height = 52;
        Background = Brushes.Transparent;
        _vendor.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Muted"));

        var badgeText = new TextBlock { Text = "已卸载", FontSize = 11 };
        badgeText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Danger"));
        _uninstalled = new Border { CornerRadius = new(4), Padding = new(5, 0), Margin = new(0, 0, 6, 0), Child = badgeText, VerticalAlignment = VerticalAlignment.Center };
        _uninstalled.Bind(BackgroundProperty, this.GetResourceObservable("DangerSoft"));
        ToolTip.SetTip(_uninstalled, "已经卸载，留着当时的原因和评价备忘");

        var grid = LibraryColumn.CreateGrid(columns);
        for (var i = 0; i < columns.Count; i++)
        {
            var cell = CreateCell(columns[i]);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        Child = grid;
    }

    private Control CreateCell(LibraryColumn column)
    {
        Control content;
        if (column == LibraryColumn.Plugin)
        {
            var title = new DockPanel();
            DockPanel.SetDock(_uninstalled, Dock.Left);
            title.Children.Add(_uninstalled);
            title.Children.Add(_name);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(title);
            stack.Children.Add(_vendor);
            content = stack;
        }
        else if (column == LibraryColumn.Tags)
        {
            content = _tags;
        }
        else
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
            // 苹方的“★”比 Segoe UI 宽，12.5 号时 5 颗星在 Mac 上放不进 84 宽的评分列。
            if (column == LibraryColumn.Rating)
            {
                text.FontSize = 11;
                text.TextTrimming = TextTrimming.None;
            }
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(column == LibraryColumn.Rating ? "Accent" : "TextDim"));
            _texts[column.Key] = text;
            content = text;
        }
        return new Border { Padding = LibraryColumn.CellPadding, Child = content, ClipToBounds = true };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_item != null)
            _item.PropertyChanged -= OnItemChanged;
        _item = DataContext as PluginItem;
        if (_item != null)
            _item.PropertyChanged += OnItemChanged;
        Refresh();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_item is not { } item)
            return;
        _name.Text = item.Name;
        _vendor.Text = item.Vendor;
        _uninstalled.IsVisible = item.IsUninstalled;
        Opacity = item.IsUninstalled ? 0.6 : 1;
        foreach (var column in _columns)
        {
            if (column.Text != null && _texts.TryGetValue(column.Key, out var text))
            {
                text.Text = column.Text(item);
                ToolTip.SetTip(text, column == LibraryColumn.Note || column == LibraryColumn.Reason || column == LibraryColumn.Recommend
                    ? NullIfEmpty(text.Text) : null);
            }
        }
        _tags.Children.Clear();
        foreach (var tag in item.TagList)
        {
            var label = new TextBlock { Text = tag, FontSize = 12 };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextDim"));
            var pill = new Border { CornerRadius = new(5), Padding = new(7, 2), Margin = new(0, 0, 5, 0), Child = label };
            pill.Bind(BackgroundProperty, this.GetResourceObservable("SurfaceActive"));
            _tags.Children.Add(pill);
        }
        ToolTip.SetTip(_tags, item.TagsText);
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
