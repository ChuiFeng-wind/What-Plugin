using System.Windows;
using System.Windows.Media;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>工程列表的一行。</summary>
public sealed class ProjectViewModel(ProjectEntry entry)
{
    public ProjectEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Folder => Entry.Folder;
    public string HostText => ProjectParser.HostName(Entry.Host);
    public DateTime Modified => Entry.ModifiedUtc;
    public string ModifiedText => Entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public int Versions => Entry.Versions;
    public int EffectCount => Entry.EffectCount;
    public int MissingCount => Entry.MissingCount;
    public bool Failed => Entry.Status == ProjectScanStatus.Failed;

    public string MissingText => Failed ? "读取失败" : MissingCount > 0 ? $"缺 {MissingCount} 款" : "齐全";

    public Brush MissingBrush => (Brush)Application.Current.FindResource(
        Failed || MissingCount > 0 ? "Danger" : "Good");

    public string SearchText => (Name + " " + Entry.Path + " " + string.Join(' ', Entry.Plugins.Select(p => p.Name))).ToLowerInvariant();
}

/// <summary>工程详情里的一款插件。</summary>
public sealed class ProjectLineViewModel(ProjectPluginLine line, PluginRow? remembered = null)
{
    public ProjectPluginLine Line { get; } = line;

    /// <summary>缺失的插件以前移进垃圾桶并卸载过时，提示当时的评价。</summary>
    public Visibility RememberedVisibility => IsMissing && remembered != null ? Visibility.Visible : Visibility.Collapsed;
    public string RememberedText => remembered == null ? "" : "以前移进垃圾桶并卸载了。" + TrashMemory.Describe(remembered);
    public string Name => Line.Name;
    public string InstancesText => Line.Instances > 1 ? $"× {Line.Instances}" : "";
    public bool IsMissing => Line.State == ProjectPluginState.Missing;
    public Visibility MissingVisibility => IsMissing ? Visibility.Visible : Visibility.Collapsed;

    public Brush NameBrush => (Brush)Application.Current.FindResource(IsMissing ? "Danger" : "Text");
}

/// <summary>缺失汇总的一行。</summary>
public sealed class MissingViewModel(MissingPluginSummary summary, PluginRow? remembered = null)
{
    public MissingPluginSummary Summary { get; } = summary;
    public string Name => Summary.Name;

    /// <summary>以前移进垃圾桶并卸载过的，名字后面标一下。</summary>
    public string NameText => remembered == null ? Name : Name + "  · 曾差评";
    public string? RememberedText => remembered == null ? null : "以前移进垃圾桶并卸载了。" + TrashMemory.Describe(remembered);
    public int ProjectCount => Summary.Projects.Count;
    public string LatestText => Summary.Projects[0].Name;
    public DateTime Latest => Summary.Projects[0].ModifiedUtc;
    public string LatestDateText => Latest.ToLocalTime().ToString("yyyy-MM-dd");
    public string SearchText => (Name + " " + string.Join(' ', Summary.Projects.Select(p => p.Name))).ToLowerInvariant();
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
    public IdlePlugin Idle { get; } = idle;
    public PluginRow Row => Idle.Plugin;
    public string Name => Row.Name;
    public string Vendor => Row.Vendor.Length > 0 ? Row.Vendor : "未知";
    public string TagsText => Row.Tags.Count > 0 ? string.Join("、", Row.Tags) : "未分类";
    public int Rating => Row.User.Rating;
    public string RatingText => Rating > 0 ? new string('★', Rating) : "";
    public int ProjectCount => Idle.Usage?.Projects ?? 0;
    public DateTime LastUsed => Idle.Usage?.LastUsedUtc ?? DateTime.MinValue;
    public string LastUsedText => Idle.Usage == null ? "从未出现" : Idle.Usage.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public string SearchText => (Name + " " + Row.Vendor + " " + TagsText).ToLowerInvariant();
}

public static class UsageLabels
{
    public static string Summary(PluginUsage? usage) => PluginLabels.UsageSummary(usage);
}
