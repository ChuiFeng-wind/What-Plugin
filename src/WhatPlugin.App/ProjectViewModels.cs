using System.Windows;
using System.Windows.Media;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>工程列表的一行。文字在 Core 的 ProjectListRow 里。</summary>
public sealed class ProjectViewModel(ProjectEntry entry)
{
    private readonly ProjectListRow _row = new(entry);
    public ProjectEntry Entry => _row.Entry;
    public string Name => _row.Name;
    public string Folder => _row.Folder;
    public string HostText => _row.HostText;
    public DateTime Modified => _row.Modified;
    public string ModifiedText => _row.ModifiedText;
    public int Versions => _row.Versions;
    public int EffectCount => _row.EffectCount;
    public int MissingCount => _row.MissingCount;
    public bool Failed => _row.Failed;
    public string MissingText => _row.MissingText;
    public Brush MissingBrush => (Brush)Application.Current.FindResource(_row.Warn ? "Danger" : "Good");
    public string SearchText => _row.SearchText;
}

/// <summary>工程详情里的一款插件。</summary>
public sealed class ProjectLineViewModel(ProjectLineRow row)
{
    public ProjectLineRow Row { get; } = row;

    /// <summary>缺失的插件以前移进垃圾桶并卸载过时，提示当时的评价。</summary>
    public Visibility RememberedVisibility => Row.ShowRemembered ? Visibility.Visible : Visibility.Collapsed;
    public string RememberedText => Row.RememberedText;
    public string Name => Row.Name;
    public string InstancesText => Row.InstancesText;
    public bool IsMissing => Row.IsMissing;
    public Visibility MissingVisibility => IsMissing ? Visibility.Visible : Visibility.Collapsed;

    public Brush NameBrush => (Brush)Application.Current.FindResource(IsMissing ? "Danger" : "Text");
}

/// <summary>缺失汇总的一行。</summary>
public sealed class MissingViewModel(MissingListRow row)
{
    public MissingListRow Row { get; } = row;
    public MissingPluginSummary Summary => Row.Summary;
    public string Name => Row.Name;
    public string NameText => Row.NameText;
    public string? RememberedText => Row.RememberedText;
    public int ProjectCount => Row.ProjectCount;
    public string LatestText => Row.LatestText;
    public DateTime Latest => Row.Latest;
    public string LatestDateText => Row.LatestDateText;
    public string SearchText => Row.SearchText;
}

/// <summary>添加插件时候选列表里的一款插件。</summary>
public sealed class FavoritePluginViewModel(PluginRow row, PluginUsage? usage)
{
    public PluginRow Row { get; } = row;
    public string Key => Row.Key;
    public string Name => Row.Name;
    public string Vendor => Row.Vendor.Length > 0 ? Row.Vendor : "未知";
    public string UsageText => UsageLabels.Summary(usage);

    public override string ToString() => Name;
}

/// <summary>闲置报告的一行。</summary>
public sealed class IdleViewModel(IdlePlugin idle)
{
    private readonly IdleListRow _row = new(idle);
    public IdleListRow Info => _row;
    public IdlePlugin Idle => _row.Idle;
    public PluginRow Row => _row.Row;
    public string Name => _row.Name;
    public string Vendor => _row.Vendor;
    public string TagsText => _row.TagsText;
    public int Rating => _row.Rating;
    public string RatingText => _row.RatingText;
    public int ProjectCount => _row.ProjectCount;
    public DateTime LastUsed => _row.LastUsed;
    public string LastUsedText => _row.LastUsedText;
    public string SearchText => _row.SearchText;
}

public static class UsageLabels
{
    public static string Summary(PluginUsage? usage) => PluginLabels.UsageSummary(usage);
}
