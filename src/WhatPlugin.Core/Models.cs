namespace WhatPlugin.Core;

/// <summary>数据库里按编号存，新格式只能加在最后。AU 只有 Mac 上有。</summary>
public enum PluginFormat { Vst3, Vst2, Clap, Aax, Au }

public enum Arch { Unknown, X64, X86, Arm64 }

public enum ScanStatus
{
    /// <summary>加载模块读到了插件清单（不创建插件实例）。</summary>
    Loaded,
    /// <summary>从 moduleinfo.json 读到了信息，没有加载插件。</summary>
    ModuleInfo,
    /// <summary>按设计不加载（VST2、CLAP、AAX、32 位），只有文件信息。</summary>
    FileInfoOnly,
    /// <summary>尝试加载失败，退回到文件信息。</summary>
    LoadFailed,
    /// <summary>加载超时被终止，退回到文件信息。</summary>
    TimedOut,
}

/// <summary>信息可信度从低到高；合并同一插件的多个来源时取最高的。宿主缓存里的 VST2 信息和 ModuleInfo 同级。</summary>
public enum InfoSource { FileName = 0, FileInfo = 1, ModuleInfo = 2, PluginLoad = 3 }

/// <summary>磁盘上发现的一个待扫描对象（文件或 .vst3 / .aaxplugin 目录）。</summary>
public sealed record ScanTarget(string Path, PluginFormat Format, string Root, Arch Arch);

/// <summary>文件中的一个插件。一个文件可以包含多个插件。</summary>
public sealed record PluginClassInfo(
    string Name,
    string Vendor,
    string Version,
    string SdkCategory,
    bool? IsInstrument,
    InfoSource Source);

public sealed record FileScanResult(
    string Path,
    PluginFormat Format,
    Arch Arch,
    long Size,
    DateTime ModifiedUtc,
    ScanStatus Status,
    string? Error,
    IReadOnlyList<PluginClassInfo> Classes);

public enum PluginKind { Unknown, Effect, Instrument }

public enum ClassificationSource { None, SdkCategory, NameKeyword, Seed, Manual }

/// <summary>插件是怎么得到的。只能手动标，默认未知；成就里的免费、正版统计只看明确标过的。</summary>
public enum PurchaseKind { Unknown = 0, Free = 1, Paid = 2, Other = 3 }

public sealed record Classification(PluginKind Kind, IReadOnlyList<string> Tags, ClassificationSource Source);

public sealed record MergedFile(
    string Path,
    PluginFormat Format,
    Arch Arch,
    string Version,
    string SdkCategory,
    ScanStatus Status,
    string? Error);

/// <summary>合并后的插件：同一款插件的不同格式合成一条。</summary>
public sealed record MergedPlugin(
    string Key,
    string Name,
    string Vendor,
    InfoSource InfoSource,
    Classification Classification,
    IReadOnlyList<MergedFile> Files);

/// <summary>
/// 用户自己填写的内容。Override 为 null 表示沿用自动结果；重新扫描不会改动这里的任何字段。
/// </summary>
public sealed record PluginUserData(
    int Rating,
    string Note,
    IReadOnlyList<string> MyTags,
    IReadOnlyList<string>? TagsOverride,
    PluginKind? KindOverride,
    string? VendorOverride,
    bool Hidden = false,
    string BlacklistReason = "",
    MixAttributes? Mix = null,
    IReadOnlyList<string>? TimbresOverride = null,
    PurchaseKind Purchase = PurchaseKind.Unknown)
{
    public static PluginUserData Empty { get; } = new(0, "", [], null, null, null);

    public bool HasOverrides => TagsOverride != null || TimbresOverride != null || KindOverride != null || VendorOverride != null;

    public bool HasData => Rating != 0 || Note.Length > 0 || MyTags.Count > 0 || HasOverrides || Hidden || BlacklistReason.Length > 0 || Mix is { IsEmpty: false } || Purchase != PurchaseKind.Unknown;

    /// <summary>限定评分范围，清理标签和文本中的空白与重复。</summary>
    public PluginUserData Normalized()
    {
        var vendor = VendorOverride?.Trim();
        return new PluginUserData(
            Math.Clamp(Rating, 0, 5),
            Note.Trim(),
            CleanTags(MyTags),
            TagsOverride == null ? null : CleanTags(TagsOverride),
            KindOverride,
            string.IsNullOrEmpty(vendor) ? null : vendor,
            Hidden,
            (BlacklistReason ?? "").Trim(),
            Mix is { IsEmpty: false } ? Mix : null,
            TimbresOverride == null ? null : Classifier.CleanTimbres(TimbresOverride),
            Enum.IsDefined(Purchase) ? Purchase : PurchaseKind.Unknown);
    }

    private static List<string> CleanTags(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>数据库中的插件，供界面显示。Auto 开头的是扫描得到的结果，手动修改优先于它们。</summary>
public sealed record PluginRow(
    long Id,
    string Key,
    string Name,
    string AutoVendor,
    PluginKind AutoKind,
    IReadOnlyList<string> AutoTags,
    ClassificationSource AutoTagSource,
    bool IsMissing,
    IReadOnlyList<MergedFile> Files,
    PluginUserData User)
{
    public string Vendor => User.VendorOverride ?? AutoVendor;
    public PluginKind Kind => User.KindOverride ?? AutoKind;

    /// <summary>界面显示和编辑用的自动功能分类，不含音源分类和音色。</summary>
    public IReadOnlyList<string> VisibleAutoTags => Classifier.FunctionTagsIn(AutoTags).Where(t => !Classifier.InstrumentTags.Contains(t)).ToList();

    /// <summary>界面显示的功能分类，不含音源分类和音色。</summary>
    public IReadOnlyList<string> Tags => Classifier.FunctionTagsIn(User.TagsOverride ?? AutoTags).Where(t => !Classifier.InstrumentTags.Contains(t)).ToList();

    /// <summary>自动得到的适合音色：资料库和插件名里提到的人声、鼓、吉他等。</summary>
    public IReadOnlyList<string> AutoTimbres => Classifier.TimbresIn(AutoTags);

    /// <summary>适合音色。手动选过就用手动的。</summary>
    public IReadOnlyList<string> Timbres => User.TimbresOverride ?? Classifier.TimbresIn(User.TagsOverride ?? AutoTags);

    /// <summary>推荐用的全部分类：手动修改后也保留资料库对音源的认识，否则改一次分类就搜不到“合成器”了。音色也算在内，“人声压缩”要靠它。</summary>
    public IReadOnlyList<string> KnowledgeTags => Tags.Concat(AutoTags.Where(Classifier.InstrumentTags.Contains)).Concat(Timbres).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public ClassificationSource TagSource => User.TagsOverride != null ? ClassificationSource.Manual : AutoTagSource;
}

/// <summary>用户自建的常用清单，比如“人声常用”。存插件的稳定标识，重新扫描后仍然对得上。</summary>
public sealed record FavoriteList(long Id, string Name, IReadOnlyList<string> PluginKeys);

public sealed record ScanProgress(int Done, int Total, string? Current);

public sealed record ScanSummary(
    int Discovered,
    int Skipped,
    int Loaded,
    int ModuleInfo,
    int FileInfoOnly,
    int LoadFailed,
    int TimedOut,
    int Removed,
    int Plugins,
    bool Cancelled,
    TimeSpan Elapsed);
