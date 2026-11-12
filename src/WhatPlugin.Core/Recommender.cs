namespace WhatPlugin.Core;

public sealed record Recommendation(PluginRow Plugin, double Score, IReadOnlyList<string> Reasons);

public sealed record RecommendResult(
    IReadOnlyList<Recommendation> Items,
    int TotalMatches,
    IReadOnlyList<string> Understood,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> Vendors,
    IReadOnlyList<string> NotUnderstood,
    IReadOnlyList<string>? NoPluginFor = null,
    string? AttributeHint = null);

/// <summary>
/// 按规则从已安装插件中推荐。功能匹配决定能否入选，评分和备注只影响入选插件的先后，
/// 所以高分但功能不对的插件不会被推荐。垃圾桶里和文件缺失的插件不参与。
/// </summary>
public static class Recommender
{
    private const double FunctionWeight = 10;
    private const double SceneWeight = 5;
    private const double WordWeight = 6;
    private const double RatingWeight = 2;
    private const double BadNotePenalty = 6;
    private const double GuessedTagPenalty = 1;
    private const double PrimaryTagBonus = 2;
    private const double NameAliasBonus = 3;
    private const double NameHitDiscount = 2;
    private const double SpecificNameBonus = 2;
    private const double BroadFunctionWeight = 5;
    private const double AttributeWeight = 8;

    /// <summary>混音属性需求。属性靠用户填写，大多数插件是空的，所以没填的不排除，只有填了且不符的才排除。</summary>
    private enum AttributeNeed { ZeroLatency, LowLatency, LightCpu, LinearPhase, Sidechain, Oversampling }

    /// <summary>Marks 表示这个词已经说完了，不再往下匹配功能，否则“低延迟”会被当成要延迟效果器。</summary>
    private static readonly (string Word, AttributeNeed Need, string Label, bool Marks)[] AttributeWords =
    [
        ("零延迟", AttributeNeed.ZeroLatency, "无延迟", true),
        ("无延迟", AttributeNeed.ZeroLatency, "无延迟", true),
        ("没有延迟", AttributeNeed.ZeroLatency, "无延迟", true),
        ("没延迟", AttributeNeed.ZeroLatency, "无延迟", true),
        ("不延迟", AttributeNeed.ZeroLatency, "无延迟", true),
        ("zero latency", AttributeNeed.ZeroLatency, "无延迟", true),
        ("低延迟", AttributeNeed.LowLatency, "低延迟", true),
        ("延迟低", AttributeNeed.LowLatency, "低延迟", true),
        ("延迟小", AttributeNeed.LowLatency, "低延迟", true),
        ("low latency", AttributeNeed.LowLatency, "低延迟", true),
        ("不吃cpu", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("不占cpu", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("省cpu", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("低cpu", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("cpu占用低", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("cpu低", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("不吃资源", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("省资源", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("轻量", AttributeNeed.LightCpu, "CPU 占用轻", true),
        ("过采样", AttributeNeed.Oversampling, "过采样", true),
        ("oversampling", AttributeNeed.Oversampling, "过采样", true),
        // 这两个同时也是能按名称识别的功能，留给后面的概念继续匹配。
        ("线性相位", AttributeNeed.LinearPhase, "线性相位", false),
        ("linear phase", AttributeNeed.LinearPhase, "线性相位", false),
        ("侧链", AttributeNeed.Sidechain, "侧链", false),
        ("旁链", AttributeNeed.Sidechain, "侧链", false),
        ("sidechain", AttributeNeed.Sidechain, "侧链", false),
        ("side chain", AttributeNeed.Sidechain, "侧链", false),
        ("side-chain", AttributeNeed.Sidechain, "侧链", false),
    ];

    private enum AttributeFit { Unknown, Fits, Conflicts }

    private static AttributeFit Fit(MixAttributes? mix, AttributeNeed need)
    {
        if (mix == null)
            return AttributeFit.Unknown;
        static AttributeFit FromTri(Tri value) => value switch
        {
            Tri.Yes => AttributeFit.Fits,
            Tri.No => AttributeFit.Conflicts,
            _ => AttributeFit.Unknown,
        };
        return need switch
        {
            AttributeNeed.ZeroLatency => mix.Latency switch
            {
                LatencyLevel.Zero => AttributeFit.Fits,
                LatencyLevel.Low or LatencyLevel.High => AttributeFit.Conflicts,
                _ => AttributeFit.Unknown,
            },
            AttributeNeed.LowLatency => mix.Latency switch
            {
                LatencyLevel.Zero or LatencyLevel.Low => AttributeFit.Fits,
                LatencyLevel.High => AttributeFit.Conflicts,
                _ => AttributeFit.Unknown,
            },
            AttributeNeed.LightCpu => mix.Cpu switch
            {
                CpuLevel.Light => AttributeFit.Fits,
                CpuLevel.Heavy => AttributeFit.Conflicts,
                _ => AttributeFit.Unknown,
            },
            AttributeNeed.LinearPhase => FromTri(mix.LinearPhase),
            AttributeNeed.Sidechain => FromTri(mix.Sidechain),
            AttributeNeed.Oversampling => FromTri(mix.Oversampling),
            _ => AttributeFit.Unknown,
        };
    }

    private sealed record AttributeWant(AttributeNeed Need, string Label, bool Wanted);

    /// <summary>
    /// 一个需求概念，来自 Data/need-terms.json。匹配方式从强到弱：
    /// Tags（有其一）或 AllTags（全都有）、用户标签 → 插件名称或型号（NameHints 和英文 Words）→
    /// BroadTags 或 Instrument（只对资料库不认识的插件生效，它们往往只有“动态”这样的粗分类）。
    /// </summary>
    private sealed record Concept(
        string Label,
        string Kind,
        string[]? Tags,
        string[]? AllTags,
        string[]? BroadTags,
        bool Instrument,
        bool EffectOnly,
        string[]? Words,
        string[]? NameHints)
    {
        public bool Scene => Kind == "scene";

        /// <summary>只能靠名称识别的细分类型（板式、FET、弦乐）。名字对上了比泛泛的分类更说明问题。</summary>
        public bool Specific => (Tags?.Length ?? 0) == 0 && (AllTags?.Length ?? 0) == 0;

        public string[] TagList => Tags ?? [];
        public string[] AllWords => [Label, .. Words ?? []];
        public IEnumerable<string> NameTerms => (NameHints ?? []).Concat(AllWords).Select(w => w.ToLowerInvariant()).Distinct();
    }

    private static readonly Concept[] Concepts = LoadConcepts();

    private static readonly HashSet<string> SceneTags =
        Concepts.Where(c => c.Scene).SelectMany(c => c.TagList).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Concept[] LoadConcepts()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().First(n => n.EndsWith("need-terms.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return System.Text.Json.JsonSerializer.Deserialize<Concept[]>(stream,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static readonly (string Word, Concept Concept)[] WordTable = Concepts
        .SelectMany(c => c.AllWords.Select(w => (Word: w.ToLowerInvariant(), Concept: c)))
        .OrderByDescending(x => x.Word.Length)
        .ToArray();

    /// <summary>中文乐器名对应插件英文名里的写法，让“古筝”优先找到 Ample China Zheng。</summary>
    private static readonly (string Word, string[] Aliases)[] NameAliases =
    [
        ("古筝", ["zheng"]),
        ("二胡", ["erhu"]),
        ("琵琶", ["pipa"]),
        ("笛子", ["dizi", "qudi"]),
        ("洞箫", ["dongxiao", "xiao"]),
    ];

    private static readonly string[] Negations = ["不想要", "不想用", "不需要", "不要", "不想", "不用", "别用", "排除", "除了", "别"];

    /// <summary>对判断需求没有帮助的词。先长后短，单字放最后，避免把词拆开。</summary>
    private static readonly string[] Fillers =
    [
        "不想要", "不想用", "不需要", "不要", "不想", "不用", "别用", "排除", "除了", "别",
        "我想要", "帮我", "给我", "有没有", "有什么", "什么", "哪个", "哪些", "推荐", "一个", "一款", "一些",
        "插件", "效果器", "效果", "用来", "用于", "适合", "可以", "能够", "比较", "一点", "东西", "处理", "想要", "需要", "我想",
        "还有", "以及", "或者", "并且", "那种", "这种", "好用", "好的", "音色", "声音", "类型", "风格", "软件",
        "想", "太", "更", "给", "做", "的", "和", "跟", "与", "或", "还", "要", "用", "找", "很", "我", "上", "时", "在", "再", "加", "点", "个", "款", "把", "让", "吗", "呢", "啊",
    ];

    private static readonly char[] ClauseSeparators = ['，', '。', ',', '.', ';', '；', '！', '!', '？', '?', '\n', '\r', '、'];

    private static readonly string[] NegativeMarkers = ["不", "没", "别", "差", "难用", "翻车", "一般", "糟"];
    private static readonly string[] NotActuallyNegative = ["不错", "不愧", "差不多", "没得说", "没问题"];

    /// <summary>所有需求概念会用到的分类，用于校验词表和分类目录保持一致。</summary>
    public static IReadOnlyList<string> ConceptTags { get; } =
        Concepts.SelectMany(c => c.TagList.Concat(c.AllTags ?? []).Concat(c.BroadTags ?? [])).Distinct().ToList();

    /// <summary>每个概念的名称和可匹配内容条数，用于校验术语表：没有任何匹配途径的概念等于摆设。</summary>
    public static IReadOnlyList<(string Label, int Routes)> ConceptRoutes { get; } = Concepts
        .Select(c => (c.Label, c.TagList.Length + (c.AllTags?.Length ?? 0) + (c.NameHints?.Length ?? 0) + (c.Words?.Length ?? 0)))
        .ToList();

    public static RecommendResult Recommend(string need, IEnumerable<PluginRow> plugins, int limit = int.MaxValue)
    {
        var rows = plugins.Where(p => !p.User.Hidden && !p.IsMissing).ToList();
        var parsed = Parse(need, rows);

        var understood = parsed.Wanted.Select(c => c.Label)
            .Concat(parsed.Attributes.Where(a => a.Wanted).Select(a => a.Label))
            .Distinct().ToList();
        var excluded = parsed.Unwanted.Select(c => c.Label).Concat(parsed.ExcludedVendors).Concat(parsed.ExcludedWords).ToList();

        if (parsed.Wanted.Count == 0 && parsed.Words.Count == 0 && parsed.Vendors.Count == 0 && parsed.Attributes.Count == 0)
            return new RecommendResult([], 0, understood, excluded, [], parsed.ExcludedWords);

        var allWords = parsed.Words.Concat(parsed.ExcludedWords).ToList();
        var unmatchedWords = new HashSet<string>(allWords, StringComparer.OrdinalIgnoreCase);
        var scored = new List<Recommendation>();
        foreach (var row in rows)
        {
            var match = Score(row, parsed);
            unmatchedWords.ExceptWith(match.MatchedWords);
            if (match.Eligible)
                scored.Add(new Recommendation(row, match.Score, match.Reasons));
        }

        var ordered = scored
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Plugin.User.Rating)
            .ThenBy(r => r.Plugin.KnowledgeTags.Count) // 分类少的更专注，专用 EQ 排在通道条前面
            .ThenBy(r => r.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RecommendResult(
            ordered.Take(limit).ToList(), ordered.Count, understood, excluded, parsed.Vendors,
            allWords.Where(unmatchedWords.Contains).ToList(),
            parsed.Wanted
                .Where(c => !rows.Any(r => FindHit(r, c, NameText(r)).Kind != HitKind.None))
                .Select(c => c.Label).ToList(),
            DescribeAttributes(parsed.Attributes, rows));
    }

    private static string? DescribeAttributes(List<AttributeWant> attributes, List<PluginRow> rows)
    {
        var wanted = attributes.Where(a => a.Wanted).ToList();
        if (wanted.Count == 0)
            return null;
        var parts = wanted.Select(a =>
        {
            var filled = rows.Count(r => Fit(r.User.Mix, a.Need) != AttributeFit.Unknown);
            return filled == 0 ? $"还没有插件填过“{a.Label}”" : $"“{a.Label}”只有 {filled} 款填过";
        });
        return string.Join("，", parts) + "，没填的插件不排除";
    }

    // ---------- 理解需求 ----------

    private sealed record ParsedNeed(
        string Text,
        List<Concept> Wanted,
        List<Concept> Unwanted,
        List<string> Vendors,
        List<string> ExcludedVendors,
        List<string> Words,
        List<string> ExcludedWords,
        List<AttributeWant> Attributes);

    private static ParsedNeed Parse(string need, List<PluginRow> rows)
    {
        var text = (need ?? "").Trim().ToLowerInvariant();
        var used = new bool[text.Length];
        var wanted = new List<Concept>();
        var unwanted = new List<Concept>();

        var attributes = new List<AttributeWant>();
        foreach (var (word, attribute, label, marks) in AttributeWords)
        {
            var index = FindWord(text, word, used);
            if (index < 0)
                continue;
            if (marks)
                Mark(used, index, word.Length);
            var want = new AttributeWant(attribute, label, !IsNegated(text, index));
            if (!attributes.Contains(want))
                attributes.Add(want);
        }

        // 厂家名优先，防止厂家名里的单词被当成功能。
        var vendors = new List<string>();
        var excludedVendors = new List<string>();
        foreach (var vendor in rows.Select(r => r.Vendor).Where(v => v.Length >= 3)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Length))
        {
            var index = FindWord(text, vendor.ToLowerInvariant(), used);
            if (index < 0)
                continue;
            Mark(used, index, vendor.Length);
            (IsNegated(text, index) ? excludedVendors : vendors).Add(vendor);
        }

        foreach (var (word, concept) in WordTable)
        {
            int index;
            while ((index = FindWord(text, word, used)) >= 0)
            {
                Mark(used, index, word.Length);
                var target = IsNegated(text, index) ? unwanted : wanted;
                if (!target.Contains(concept))
                    target.Add(concept);
            }
        }

        // 既要又不要时以排除为准，免得结果自相矛盾。
        wanted.RemoveAll(unwanted.Contains);

        var leftovers = LeftoverWords(text, used);
        var excludedWords = leftovers.Where(w => IsNegated(text, text.IndexOf(w, StringComparison.Ordinal))).ToList();
        return new ParsedNeed(text, wanted, unwanted, vendors, excludedVendors, leftovers.Except(excludedWords).ToList(), excludedWords, attributes);
    }

    private static bool IsNegated(string text, int index)
    {
        var before = text[..index].TrimEnd();
        return Negations.Any(before.EndsWith);
    }

    private static List<string> LeftoverWords(string text, bool[] used)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (used[i])
                chars[i] = ' ';
        }

        var rest = new string(chars);
        foreach (var filler in Fillers)
            rest = rest.Replace(filler, " ");

        return rest
            .Split([' ', '\t', '，', '。', ',', '.', ';', '；', '、', '！', '!', '？', '?', '\n', '\r', '/', '(', ')', '（', '）', '“', '”', '"'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('-', '_'))
            .Where(w => w.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Mark(bool[] used, int index, int length) => Array.Fill(used, true, index, length);

    /// <summary>查找未被占用的词。英文词要求完整匹配，否则 eq 会命中 sequencer。</summary>
    private static int FindWord(string text, string word, bool[]? used = null)
    {
        var from = 0;
        while (from <= text.Length - word.Length)
        {
            var index = text.IndexOf(word, from, StringComparison.Ordinal);
            if (index < 0)
                return -1;
            var free = used == null || !used.AsSpan(index, word.Length).Contains(true);
            if (free && HasWordBoundaries(text, index, word.Length))
                return index;
            from = index + 1;
        }
        return -1;
    }

    private static bool HasWordBoundaries(string text, int index, int length)
    {
        var startsLatin = IsLatin(text[index]);
        var endsLatin = IsLatin(text[index + length - 1]);
        if (startsLatin && index > 0 && IsLatin(text[index - 1]))
            return false;
        return !endsLatin || index + length >= text.Length || !IsLatin(text[index + length]);
    }

    private static bool IsLatin(char c) => char.IsAsciiLetterOrDigit(c);

    // ---------- 打分 ----------

    private sealed record Match(bool Eligible, double Score, IReadOnlyList<string> Reasons, IReadOnlyList<string> MatchedWords);

    private static readonly Match NoMatch = new(false, 0, [], []);

    private static Match Score(PluginRow row, ParsedNeed need)
    {
        var matchedWords = new List<string>();
        var nameMatched = false;
        var wordReasons = new List<string>();
        var wordScore = 0.0;
        var noteLower = row.User.Note.ToLowerInvariant();

        foreach (var word in need.Words)
        {
            var inName = FindWord(row.Name.ToLowerInvariant(), word) >= 0;
            var inVendor = FindWord(row.Vendor.ToLowerInvariant(), word) >= 0;
            var inTags = row.KnowledgeTags.Concat(row.User.MyTags).Any(t => FindWord(t.ToLowerInvariant(), word) >= 0);
            var note = ReadNote(row.User.Note, noteLower, [word]);

            if (!inName && !inVendor && !inTags && note.Kind == NoteKind.None)
                continue;
            matchedWords.Add(word);

            if (inName || inVendor || inTags)
            {
                nameMatched |= inName;
                wordScore += WordWeight;
                wordReasons.Add($"名称或标签含“{word}”");
            }
            if (note.Kind == NoteKind.Good)
            {
                wordScore += WordWeight;
                wordReasons.Add($"你的备注：“{note.Clause}”");
            }
            else if (note.Kind == NoteKind.Bad)
            {
                wordScore -= BadNotePenalty;
                wordReasons.Add($"注意你的备注：“{note.Clause}”");
            }
        }
        var goodWords = matchedWords.Count > 0 && wordScore > 0;

        var nameLower = row.Name.ToLowerInvariant();
        var nameText = NameText(row);
        var vendorLower = row.Vendor.ToLowerInvariant();
        var excludedHits = need.ExcludedWords.Where(w => FindWord(nameLower, w) >= 0 || FindWord(vendorLower, w) >= 0).ToList();
        matchedWords.AddRange(excludedHits);
        if (excludedHits.Count > 0 || need.ExcludedVendors.Contains(row.Vendor, StringComparer.OrdinalIgnoreCase))
            return new Match(false, 0, [], matchedWords);

        if (need.Vendors.Count > 0 && !need.Vendors.Contains(row.Vendor, StringComparer.OrdinalIgnoreCase))
            return new Match(false, 0, [], matchedWords);
        // 排除只看明确的命中：只知道是“动态”的插件不能算压缩器而被排除。
        if (need.Unwanted.Any(c => FindHit(row, c, nameText).Kind is HitKind.Tag or HitKind.Name))
            return new Match(false, 0, [], matchedWords);

        // “鼓总线”要的是处理总线的效果器，鼓音源不算。
        if (row.Kind == PluginKind.Instrument && need.Wanted.Any(c => c.EffectOnly))
            return new Match(false, 0, [], matchedWords);

        var attributeReasons = new List<string>();
        var attributeScore = 0.0;
        foreach (var attribute in need.Attributes)
        {
            var fit = Fit(row.User.Mix, attribute.Need);
            if (fit == (attribute.Wanted ? AttributeFit.Conflicts : AttributeFit.Fits))
                return new Match(false, 0, [], matchedWords);
            if (attribute.Wanted && fit == AttributeFit.Fits)
            {
                attributeScore += AttributeWeight;
                attributeReasons.Add($"你标记为{attribute.Label}");
            }
        }

        var score = wordScore + attributeScore;
        var reasons = new List<string>();
        var functionHits = 0;
        var sceneHits = 0;

        foreach (var concept in need.Wanted)
        {
            var hit = FindHit(row, concept, nameText);
            var note = ReadNote(row.User.Note, noteLower, concept.AllWords.Select(w => w.ToLowerInvariant()));
            var counted = hit.Kind != HitKind.None || note.Kind == NoteKind.Good;

            if (counted)
            {
                if (concept.Scene)
                    sceneHits++;
                else
                    functionHits++;
                var weight = concept.Scene ? SceneWeight : FunctionWeight;
                score += hit.Kind switch
                {
                    HitKind.Name => concept.Specific ? weight + SpecificNameBonus : weight - NameHitDiscount,
                    HitKind.Broad => concept.Scene ? weight / 2 : BroadFunctionWeight,
                    _ => weight,
                };
            }

            var prefix = concept.Scene ? "适用：" : "功能：";
            switch (hit.Kind)
            {
                case HitKind.Tag:
                {
                    var label = concept.Label;
                    var viaTags = row.KnowledgeTags.Where(t => concept.TagList.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (viaTags.Count > 0 && !viaTags.Contains(concept.Label))
                        label += $"（{string.Join("、", viaTags)}）";
                    reasons.Add(prefix + label);

                    // 听感词对应多类插件时，排在最前面的一类最对路（冲击力首选瞬态，其次才是压缩）。
                    if (concept.TagList.Length > 1 && row.KnowledgeTags.Contains(concept.TagList[0], StringComparer.OrdinalIgnoreCase))
                        score += PrimaryTagBonus;
                    break;
                }
                case HitKind.Name:
                    reasons.Add($"{prefix}{concept.Label}（名称含 {hit.Detail}）");
                    break;
                case HitKind.Broad:
                    reasons.Add($"{prefix}{concept.Label}（只知道是{hit.Detail}，待确认）");
                    break;
            }

            if (note.Kind == NoteKind.Good)
            {
                score += 1;
                reasons.Add($"你的备注：“{note.Clause}”");
            }
            else if (note.Kind == NoteKind.Bad)
            {
                score -= BadNotePenalty;
                reasons.Add($"注意你的备注：“{note.Clause}”");
            }
        }

        // “侧链”“线性相位”既是功能词也是属性：名称里没写，但你标记过有，也算符合。
        var attributeIsFunction = need.Attributes.Any(a => a.Wanted && Fit(row.User.Mix, a.Need) == AttributeFit.Fits
            && need.Wanted.Any(c => c.Label == a.Label));
        var hasFunctions = need.Wanted.Any(c => !c.Scene);
        var hasScenes = need.Wanted.Any(c => c.Scene);
        bool eligible;
        if (hasFunctions)
            eligible = functionHits > 0 || nameMatched || attributeIsFunction;
        else if (hasScenes)
            eligible = sceneHits > 0 || nameMatched || attributeIsFunction;
        else if (need.Words.Count > 0)
            eligible = goodWords;
        else if (need.Vendors.Count > 0)
            eligible = true;
        else
            eligible = attributeReasons.Count > 0;

        if (!eligible)
            return new Match(false, 0, [], matchedWords);

        reasons.AddRange(wordReasons);
        reasons.AddRange(attributeReasons);
        foreach (var (word, aliases) in NameAliases)
        {
            if (need.Text.Contains(word) && aliases.Any(a => nameLower.Contains(a)))
            {
                score += NameAliasBonus;
                reasons.Add($"名称对应“{word}”");
            }
        }
        if (row.User.Rating > 0)
        {
            score += (row.User.Rating - 3) * RatingWeight;
            reasons.Add($"你评了 {row.User.Rating} 星");
        }
        if (row.TagSource == ClassificationSource.NameKeyword)
        {
            score -= GuessedTagPenalty;
            reasons.Add("分类由名称推测");
        }

        return new Match(true, score, reasons.Distinct().ToList(), matchedWords);
    }

    private static string NameText(PluginRow row) =>
        row.Name.ToLowerInvariant() + " " + string.Join(' ', Classifier.SplitWords(row.Name)).ToLowerInvariant();

    private enum HitKind { None, Broad, Name, Tag }

    private readonly record struct Hit(HitKind Kind, string? Detail = null);

    private static Hit FindHit(PluginRow row, Concept concept, string nameText)
    {
        if (row.KnowledgeTags.Any(t => concept.TagList.Contains(t, StringComparer.OrdinalIgnoreCase)))
            return new Hit(HitKind.Tag);
        if (concept.AllTags is { Length: > 0 } all && all.All(t => row.KnowledgeTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            return new Hit(HitKind.Tag);

        // 用户写的标签可能是一句话（如“主唱人声”），按词查找。
        var inMyTags = row.User.MyTags.Any(tag =>
        {
            var lower = tag.ToLowerInvariant();
            return concept.AllWords.Any(w => FindWord(lower, w.ToLowerInvariant()) >= 0);
        });
        if (inMyTags)
            return new Hit(HitKind.Tag);

        foreach (var term in concept.NameTerms)
        {
            if (FindWord(nameText, term) >= 0)
                return new Hit(HitKind.Name, term);
        }

        // 资料库认识或你手动分过类的插件，分类已经够具体，不再猜。
        if (row.TagSource is ClassificationSource.Seed or ClassificationSource.Manual)
            return default;
        // 已经有其他功能分类的插件（比如去齿音）用途是明确的，不能因为也带“动态”就算成压缩器。
        var broad = concept.BroadTags?.FirstOrDefault(t => row.KnowledgeTags.Contains(t, StringComparer.OrdinalIgnoreCase));
        if (broad != null && row.KnowledgeTags.All(t => t.Equals(broad, StringComparison.OrdinalIgnoreCase) || SceneTags.Contains(t)))
            return new Hit(HitKind.Broad, broad);
        return concept.Instrument && row.Kind == PluginKind.Instrument ? new Hit(HitKind.Broad, "乐器") : default;
    }

    private enum NoteKind { None, Good, Bad }

    private readonly record struct NoteSignal(NoteKind Kind, string Clause);

    /// <summary>
    /// 只在提到需求词的那一小句里判断褒贬。“高频发毛，人声很棒”对人声算好评；
    /// “不适合人声”算差评。只要有一句是差评就按差评算，宁可少推荐也不要推荐你明确嫌弃的用法。
    /// </summary>
    private static NoteSignal ReadNote(string note, string noteLower, IEnumerable<string> words)
    {
        if (noteLower.Length == 0)
            return default;

        var wordList = words.ToList();
        var result = default(NoteSignal);
        foreach (var clause in note.Split(ClauseSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lower = clause.ToLowerInvariant();
            if (!wordList.Any(w => FindWord(lower, w) >= 0))
                continue;

            var cleaned = NotActuallyNegative.Aggregate(lower, (text, phrase) => text.Replace(phrase, ""));
            if (NegativeMarkers.Any(cleaned.Contains))
                return new NoteSignal(NoteKind.Bad, clause);
            if (result.Kind == NoteKind.None)
                result = new NoteSignal(NoteKind.Good, clause);
        }
        return result;
    }
}
