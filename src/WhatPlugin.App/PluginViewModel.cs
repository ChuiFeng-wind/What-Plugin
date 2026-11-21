using System.ComponentModel;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>插件在列表中一行的显示内容。编辑后调用 Update，这一行会就地刷新，不影响当前选择。</summary>
public sealed class PluginViewModel(PluginRow row) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public PluginRow Row { get; private set; } = row;
    public string Name => Row.Name;
    public string Vendor => PluginLabels.Vendor(Row);
    public string KindText => Labels.Kind(Row.Kind);
    public string TagsText => PluginLabels.Tags(Row);
    public IReadOnlyList<string> TagList => PluginLabels.TagList(Row);
    public string TimbresText => string.Join("、", Row.Timbres);
    public int Rating => Row.User.Rating;
    public string RatingText => PluginLabels.Rating(Rating);
    public string MyTagsText => string.Join("、", Row.User.MyTags);
    public string FormatsText => Row.IsMissing ? "文件缺失" : string.Join(" / ", Row.Files.Select(f => Labels.Format(f.Format)).Distinct());

    /// <summary>工程里的使用情况，没扫描过工程或没用过时为 null。</summary>
    public PluginUsage? Usage { get; set; }
    public int UsageCount => Usage?.Projects ?? 0;
    public string UsageCountText => Usage == null ? "" : Usage.Projects.ToString();
    public DateTime LastUsed => Usage?.LastUsedUtc ?? DateTime.MinValue;
    public string LastUsedText => Usage == null ? "" : Usage.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd");

    public string LatencyText => Row.User.Mix?.LatencyText ?? "";
    public string CpuText => Row.User.Mix?.CpuText ?? "";
    public string LinearPhaseText => MixAttributes.TriText(Row.User.Mix?.LinearPhase ?? Tri.Unknown);
    public string SidechainText => MixAttributes.TriText(Row.User.Mix?.Sidechain ?? Tri.Unknown);
    public string NoteLine => Row.User.Note.ReplaceLineEndings(" ");

    public bool IsBlacklisted => Row.User.Hidden;

    /// <summary>在垃圾桶里、已经卸载了：记录留着备忘，列表里排在后面、变淡。</summary>
    public bool IsUninstalled => TrashMemory.IsRemembered(Row);
    public System.Windows.Visibility UninstalledVisibility => IsUninstalled ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public double RowOpacity => IsUninstalled ? 0.6 : 1;
    public string BlacklistReason => Row.User.BlacklistReason;

    /// <summary>最近一次推荐给出的理由，只在推荐结果里显示。</summary>
    public string RecommendReason { get; set; } = "";

    public string SearchText => PluginLabels.SearchText(Row);

    public void Update(PluginRow row)
    {
        Row = row;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>转调 Core 里的 <see cref="PluginLabels"/>，跨平台版用同一份文字。</summary>
public static class Labels
{
    public static string Kind(PluginKind kind) => PluginLabels.Kind(kind);
    public static string Format(PluginFormat format) => PluginLabels.Format(format);
    public static string Arch(Arch arch) => PluginLabels.Arch(arch);
    public static string Status(ScanStatus status) => PluginLabels.Status(status);
    public static string TagSource(ClassificationSource source) => PluginLabels.TagSource(source);
}
