using System.ComponentModel;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>插件在列表中一行的显示内容。编辑后调用 Update，这一行会就地刷新，不影响当前选择。</summary>
public sealed class PluginViewModel(PluginRow row) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public PluginRow Row { get; private set; } = row;
    public string Name => Row.Name;
    public string Vendor => Row.Vendor.Length > 0 ? Row.Vendor : "未知";
    public string KindText => Labels.Kind(Row.Kind);
    public string TagsText => Row.Tags.Count > 0 ? string.Join("、", Row.Tags) : "未分类";
    public IReadOnlyList<string> TagList => Row.Tags.Count > 0 ? Row.Tags : ["未分类"];
    public string TimbresText => string.Join("、", Row.Timbres);
    public int Rating => Row.User.Rating;
    public string RatingText => Rating > 0 ? new string('★', Rating) : "";
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

    public string SearchText => string.Join(' ',
        Row.Name, Row.Vendor, string.Join(' ', Row.Tags), string.Join(' ', Row.Timbres), string.Join(' ', Row.User.MyTags), Row.User.Note, Row.User.BlacklistReason, Row.User.Mix?.SearchText ?? "").ToLowerInvariant();

    public void Update(PluginRow row)
    {
        Row = row;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public static class Labels
{
    public static string Kind(PluginKind kind) => kind switch
    {
        PluginKind.Effect => "效果器",
        PluginKind.Instrument => "音源",
        _ => "未知",
    };

    public static string Format(PluginFormat format) => format switch
    {
        PluginFormat.Vst3 => "VST3",
        PluginFormat.Vst2 => "VST2",
        PluginFormat.Clap => "CLAP",
        PluginFormat.Aax => "AAX",
        _ => format.ToString(),
    };

    public static string Arch(Arch arch) => arch switch
    {
        Core.Arch.X64 => "64 位",
        Core.Arch.X86 => "32 位",
        Core.Arch.Arm64 => "ARM64",
        _ => "架构未知",
    };

    public static string Status(ScanStatus status) => status switch
    {
        ScanStatus.Loaded => "已读取插件清单",
        ScanStatus.ModuleInfo => "读取自模块信息",
        ScanStatus.FileInfoOnly => "仅文件信息",
        ScanStatus.LoadFailed => "读取失败",
        ScanStatus.TimedOut => "读取超时",
        _ => status.ToString(),
    };

    public static string TagSource(ClassificationSource source) => source switch
    {
        ClassificationSource.Manual => "分类由你手动修改",
        ClassificationSource.Seed => "分类来自资料库",
        ClassificationSource.SdkCategory => "分类来自插件自身",
        ClassificationSource.NameKeyword => "分类由名称推测，待确认",
        _ => "暂无分类信息",
    };
}
