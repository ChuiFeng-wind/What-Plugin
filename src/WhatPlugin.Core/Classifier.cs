using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatPlugin.Core;

/// <summary>
/// 决定插件的类型和功能标签。优先级：资料库 > 插件名关键词与 SDK 分类。
/// 手动修改（第 2 步加入）的优先级高于这里的全部结果。
/// </summary>
public static partial class Classifier
{
    /// <param name="Pattern">匹配插件原始名称的正则（整串匹配，不分大小写）。</param>
    /// <param name="Vendor">限定厂家，避免名称过于普通时误中；插件厂家未知时不限制。</param>
    /// <param name="SetVendor">插件读不到厂家时使用的厂家名。</param>
    /// <param name="DisplayName">替换不便阅读的文件名。</param>
    public sealed record SeedEntry(string Pattern, string? Vendor, string Kind, string[] Tags, string? DisplayName, string? SetVendor);

    /// <summary>界面上提供选择的功能分类。用户也可以添加这里没有的分类。</summary>
    public static IReadOnlyList<string> TagCatalog { get; } =
    [
        "EQ", "动态", "压缩", "限制器", "门限", "去齿音", "瞬态", "多段",
        "混响", "延迟", "滤波", "调制", "失真/饱和", "磁带", "音高", "修复降噪",
        "声像/空间", "环绕声", "低频", "母带", "通道条", "分析", "监听", "工具", "发生器",
    ];

    /// <summary>适合音色，和功能分类分开保存。一款插件可以选多个。</summary>
    public static IReadOnlyList<string> TimbreCatalog { get; } =
        ["通用", "人声", "贝斯", "鼓", "吉他", "键盘", "管弦", "合成器"];

    /// <summary>
    /// 资料库、关键词和旧版手动分类里表示音色的词。它们不再算功能分类，而是换成适合音色。
    /// “合成器”不在这里：在功能分类里它表示音源，不是“适合合成器音色”。
    /// </summary>
    private static readonly Dictionary<string, string[]> TimbreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["人声"] = ["人声"],
        ["鼓"] = ["鼓"],
        ["吉他/贝斯"] = ["吉他", "贝斯"],
        ["吉他"] = ["吉他"],
        ["贝斯"] = ["贝斯"],
        ["键盘"] = ["键盘"],
        ["管弦"] = ["管弦"],
        ["通用"] = ["通用"],
    };

    /// <summary>分类里属于适合音色的部分，按 <see cref="TimbreCatalog"/> 的顺序。</summary>
    public static IReadOnlyList<string> TimbresIn(IEnumerable<string> tags)
    {
        var found = tags.SelectMany(t => TimbreWords.GetValueOrDefault(t) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TimbreCatalog.Where(found.Contains).ToList();
    }

    /// <summary>去掉表示音色的词，剩下的才是功能分类。</summary>
    public static IEnumerable<string> FunctionTagsIn(IEnumerable<string> tags) => tags.Where(t => !TimbreWords.ContainsKey(t));

    /// <summary>只保留目录里的音色，按目录顺序。</summary>
    public static List<string> CleanTimbres(IEnumerable<string> timbres)
    {
        var set = timbres.Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TimbreCatalog.Where(set.Contains).ToList();
    }

    /// <summary>只用于音源的分类。不在界面上显示，但保留在数据里，推荐时还能据此找到音源。</summary>
    public static IReadOnlySet<string> InstrumentTags { get; } =
        new HashSet<string>(["合成器", "采样器", "音源", "钢琴", "民乐"], StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> SdkTags =new(StringComparer.OrdinalIgnoreCase)
    {
        ["Analyzer"] = "分析",
        ["Delay"] = "延迟",
        ["Distortion"] = "失真/饱和",
        ["Dynamics"] = "动态",
        ["EQ"] = "EQ",
        ["Filter"] = "滤波",
        ["Generator"] = "发生器",
        ["Mastering"] = "母带",
        ["Modulation"] = "调制",
        ["Pitch Shift"] = "音高",
        ["Restoration"] = "修复降噪",
        ["Reverb"] = "混响",
        ["Spatial"] = "声像/空间",
        ["Surround"] = "环绕声",
        ["Up-Downmix"] = "环绕声",
        ["Tools"] = "工具",
        ["Network"] = "工具",
        ["Drum"] = "鼓",
        ["Piano"] = "钢琴",
        ["Sampler"] = "采样器",
        ["Synth"] = "合成器",
        ["External"] = "工具",
    };

    // 词必须完整匹配，防止短词误伤（例如 "amp" 不能命中 "sample"）。
    private static readonly (string Pattern, string Tag, bool Instrument)[] Keywords =
    [
        (@"de[-\s]?ess(er)?|sibilance", "去齿音", false),
        (@"compressor|comp|limiter|maximi[sz]er|gate|expander|dynamics?|transient", "动态", false),
        (@"compressor|comp", "压缩", false),
        (@"limiter|maximi[sz]er", "限制器", false),
        (@"gate|expander", "门限", false),
        (@"transient", "瞬态", false),
        (@"multiband", "多段", false),
        (@"tape", "磁带", false),
        (@"eq|equali[sz]er", "EQ", false),
        (@"reverb|breverb|verb|hall|plate", "混响", false),
        (@"delay|echo", "延迟", false),
        (@"saturat(or|ion)|distortion|overdrive|tape|exciter|clipper", "失真/饱和", false),
        (@"chorus|flanger|phaser|tremolo|vibrato|rotary", "调制", false),
        (@"tune|pitch|melodyne|repitch|harmony|harmonizer", "音高", false),
        (@"denoise|de-?noise|declick|de-?click|dereverb|de-?reverb|rx", "修复降噪", false),
        (@"analy[sz]er|meter|spectrum|loudness", "分析", false),
        // 不含 stereo：插件名里的 Stereo 通常只表示声道版本，不代表功能。
        (@"imager|widener|panner", "声像/空间", false),
        (@"amp|cab|cabinet|pedal|guitar\s?rig", "吉他", false),
        (@"bass\s?amp|bass\s?di|sansamp", "贝斯", false),
        (@"vocal|vox|voice", "人声", false),
        (@"master|mastering", "母带", false),
        (@"synth|synthesizer", "合成器", true),
        (@"sampler", "采样器", true),
        (@"piano", "钢琴", true),
        (@"drum|drums", "鼓", true),
    ];

    private static readonly Lazy<List<(Regex Pattern, SeedEntry Entry)>> Seed = new(LoadSeed);

    public static IReadOnlyList<SeedEntry> SeedEntries => Seed.Value.Select(s => s.Entry).ToList();

    public static SeedEntry? FindSeed(string name, string vendor)
    {
        foreach (var (pattern, entry) in Seed.Value)
        {
            if (!pattern.IsMatch(name))
                continue;
            if (entry.Vendor == null || vendor.Length == 0 || Normalizer.Vendor(entry.Vendor) == Normalizer.Vendor(vendor))
                return entry;
        }
        return null;
    }

    public static Classification Classify(string name, string vendor, string sdkCategory, bool? isInstrument)
    {
        // 插件或宿主明说是音源时以它为准，资料库可能把同名效果器当成一类。
        if (isInstrument != true && FindSeed(name, vendor) is { } seed)
        {
            var seedKind = seed.Kind == "instrument" ? PluginKind.Instrument : PluginKind.Effect;
            return new Classification(seedKind, seed.Tags, ClassificationSource.Seed);
        }

        return ClassifyWithoutSeed(name, sdkCategory, isInstrument);
    }

    /// <summary>不查资料库的分类，也就是资料库不认识的插件得到的结果。</summary>
    public static Classification ClassifyWithoutSeed(string name, string sdkCategory, bool? isInstrument)
    {
        var tags = new List<string>();
        var source = ClassificationSource.None;
        var kindHint = isInstrument ?? StaticInspector.IsInstrumentCategory(sdkCategory);
        var nameSaysInstrument = false;

        foreach (var part in sdkCategory.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (SdkTags.TryGetValue(part, out var tag) && !tags.Contains(tag))
            {
                tags.Add(tag);
                source = ClassificationSource.SdkCategory;
            }
        }

        // 同时保留带连字符的写法，否则 "De-Esser" 被拆开后匹配不到 de-?ess。
        var words = " " + string.Join(' ', SplitWords(name)) + " " + name.Replace('_', ' ') + " ";
        foreach (var (pattern, tag, instrument) in Keywords)
        {
            if (!Regex.IsMatch(words, $@" ({pattern}) ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;
            if (!tags.Contains(tag))
            {
                tags.Add(tag);
                if (source == ClassificationSource.None)
                    source = ClassificationSource.NameKeyword;
            }
            nameSaysInstrument |= instrument;
        }

        var kind = kindHint switch
        {
            true => PluginKind.Instrument,
            false => PluginKind.Effect,
            // 乐器插件会被整个隐藏，只凭名字不能下这个结论：Drum Bus、Piano Verb 都是效果器。
            null => tags.Count > 0 && !nameSaysInstrument ? PluginKind.Effect : PluginKind.Unknown,
        };
        return new Classification(kind, tags, source);
    }

    /// <summary>"ProQ3_x64" → ["Pro","Q3","x64"]，让关键词能匹配驼峰和下划线命名。</summary>
    public static IEnumerable<string> SplitWords(string name)
    {
        var spaced = CamelBoundary().Replace(name, " ");
        return WordSeparators().Split(spaced).Where(w => w.Length > 0);
    }

    private static List<(Regex Pattern, SeedEntry Entry)> LoadSeed()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().First(n => n.EndsWith("plugin-seed.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var entries = JsonSerializer.Deserialize<SeedEntry[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return entries
            .Select(e => (new Regex($"^(?:{e.Pattern})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), e))
            .ToList();
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"[\s_\-\.\(\)\[\]/]+")]
    private static partial Regex WordSeparators();
}
