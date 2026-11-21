using System.ComponentModel;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>插件库列表里的一行，对应 Windows 版的 PluginViewModel。保存后调用 Update，这一行就地刷新。</summary>
public sealed class PluginItem(PluginRow row) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public PluginRow Row { get; private set; } = row;

    /// <summary>工程里的使用情况，没扫描过工程或没用过时为 null。</summary>
    public PluginUsage? Usage { get; set; }

    /// <summary>最近一次推荐给出的理由，只在推荐结果里显示。</summary>
    public string RecommendReason { get; set; } = "";

    public string Name => Row.Name;
    public string Vendor => PluginLabels.Vendor(Row);
    public IReadOnlyList<string> TagList => PluginLabels.TagList(Row);
    public string TagsText => PluginLabels.Tags(Row);
    public string RatingText => PluginLabels.Rating(Row.User.Rating);
    public string UsageCountText => Usage == null ? "" : Usage.Projects.ToString();
    public string LastUsedText => Usage == null ? "" : Usage.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public string LatencyText => Row.User.Mix?.LatencyText ?? "";
    public string CpuText => Row.User.Mix?.CpuText ?? "";
    public string LinearPhaseText => MixAttributes.TriText(Row.User.Mix?.LinearPhase ?? Tri.Unknown);
    public string SidechainText => MixAttributes.TriText(Row.User.Mix?.Sidechain ?? Tri.Unknown);
    public string NoteLine => Row.User.Note.ReplaceLineEndings(" ");
    public string BlacklistReason => Row.User.BlacklistReason.ReplaceLineEndings(" ");

    /// <summary>在垃圾桶里、已经卸载了：记录留着备忘，列表里排在后面、变淡。</summary>
    public bool IsUninstalled => TrashMemory.IsRemembered(Row);

    public void Update(PluginRow row)
    {
        Row = row;
        Changed();
    }

    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
