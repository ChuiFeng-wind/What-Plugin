using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public sealed class RecommenderTests
{
    private static long _nextId;

    private static PluginRow Row(string name, string[] tags, string vendor = "Acme", int rating = 0, string note = "",
        string[]? myTags = null, bool hidden = false, bool missing = false,
        ClassificationSource source = ClassificationSource.Seed) =>
        new(++_nextId, name.ToLowerInvariant(), name, vendor, PluginKind.Effect, tags, source, missing, [],
            PluginUserData.Empty with { Rating = rating, Note = note, MyTags = myTags ?? [], Hidden = hidden });

    private static List<string> Names(RecommendResult result) => result.Items.Select(i => i.Plugin.Name).ToList();

    private static readonly PluginRow[] Library =
    [
        Row("Comp A", ["压缩", "动态"]),
        Row("Vocal Comp", ["压缩", "动态", "人声"]),
        Row("Sibilance Pro", ["去齿音", "人声"]),
        Row("Doubler", ["人声", "调制"]),
        Row("Room", ["混响"]),
        Row("Tape Echo", ["延迟", "磁带"]),
        Row("Warm Drive", ["失真/饱和"]),
        Row("Sequencer Thing", ["工具"]),
    ];

    [Theory]
    [InlineData("压缩", "Comp A")]
    [InlineData("compressor", "Comp A")]
    [InlineData("COMP", "Comp A")]
    [InlineData("混响", "Room")]
    [InlineData("reverb", "Room")]
    [InlineData("回声", "Tape Echo")]
    [InlineData("我想要一个去齿音的插件", "Sibilance Pro")]
    [InlineData("有没有好用的饱和效果器", "Warm Drive")]
    public void UnderstandsChineseAndEnglish(string need, string expected) =>
        Assert.Contains(expected, Names(Recommender.Recommend(need, Library)));

    [Fact]
    public void EveryCatalogTagIsUnderstood()
    {
        foreach (var tag in Classifier.TagCatalog)
            Assert.Contains(tag, Recommender.Recommend(tag, Library).Understood);
    }

    [Fact]
    public void ConceptsOnlyUseCatalogTags() =>
        Assert.All(Recommender.ConceptTags, tag =>
            Assert.True(Classifier.TagCatalog.Contains(tag) || Classifier.InstrumentTags.Contains(tag) || Classifier.TimbreCatalog.Contains(tag), tag));

    [Fact]
    public void CatalogHasNoInstrumentTags() =>
        Assert.DoesNotContain(Classifier.TagCatalog, Classifier.InstrumentTags.Contains);

    [Fact]
    public void InstrumentTagsAreHiddenButStillFindable()
    {
        var row = Row("Zebra", ["合成器", "音源", "钢琴", "鼓"]);
        Assert.Empty(row.Tags);
        Assert.Empty(row.VisibleAutoTags);
        Assert.Equal(["鼓"], row.Timbres);
        Assert.Equal(["Zebra"], Names(Recommender.Recommend("合成器", new[] { row })));
    }

    [Fact]
    public void ManualTagEditKeepsInstrumentKnowledgeAndOldOverridesAreCleaned()
    {
        var row = Row("Zebra", ["合成器"]);
        var edited = row with { User = row.User with { TagsOverride = ["人声", "采样器"] } };
        Assert.Empty(edited.Tags);
        Assert.Equal(["人声"], edited.Timbres);
        Assert.Contains("合成器", edited.KnowledgeTags);
        Assert.Equal(["Zebra"], Names(Recommender.Recommend("合成器", new[] { edited })));
    }

    [Fact]
    public void FunctionPlusSceneRanksFullMatchFirstAndDropsSceneOnly()
    {
        var names = Names(Recommender.Recommend("人声压缩", Library));
        Assert.Equal("Vocal Comp", names[0]);
        Assert.Contains("Comp A", names);
        Assert.DoesNotContain("Doubler", names);
    }

    [Fact]
    public void SceneAloneListsThatScene()
    {
        var names = Names(Recommender.Recommend("人声", Library));
        Assert.Contains("Doubler", names);
        Assert.DoesNotContain("Comp A", names);
    }

    [Fact]
    public void LongestWordWins()
    {
        var library = new[] { Row("Gate", ["门限"]), Row("Denoiser", ["修复降噪"]), Row("Hall", ["混响"]) };
        Assert.Equal(["Gate"], Names(Recommender.Recommend("噪声门", library)));
        Assert.Equal(["Denoiser"], Names(Recommender.Recommend("去混响", library)));
    }

    [Fact]
    public void LatinWordsMustMatchWhole()
    {
        var result = Recommender.Recommend("sequencer", Library);
        Assert.DoesNotContain("EQ", result.Understood);
        Assert.Equal(["Sequencer Thing"], Names(result));
    }

    [Theory]
    [InlineData("延迟 不要磁带")]
    [InlineData("延迟，不想要磁带")]
    [InlineData("延迟 除了磁带")]
    public void NegationExcludes(string need)
    {
        var library = new[] { Row("Clean Delay", ["延迟"]), Row("Tape Echo", ["延迟", "磁带"]) };
        var result = Recommender.Recommend(need, library);
        Assert.Equal(["Clean Delay"], Names(result));
        Assert.Equal(["磁带"], result.Excluded);
    }

    [Fact]
    public void WantedAndUnwantedSameThingExcludes()
    {
        var result = Recommender.Recommend("混响 不要混响", Library);
        Assert.Empty(result.Understood);
        Assert.Equal(["混响"], result.Excluded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void BlacklistedAndMissingNeverRecommended()
    {
        var library = new[]
        {
            Row("Good", ["混响"]),
            Row("Banned", ["混响"], rating: 5, hidden: true),
            Row("Gone", ["混响"], rating: 5, missing: true),
        };
        Assert.Equal(["Good"], Names(Recommender.Recommend("混响", library)));
        Assert.Empty(Recommender.Recommend("banned", library).Items);
    }

    [Fact]
    public void RatingOrdersMatchesButNeverAddsNonMatches()
    {
        var library = new[]
        {
            Row("Low", ["混响"], rating: 1),
            Row("Plain", ["混响"]),
            Row("High", ["混响"], rating: 5),
            Row("Great Comp", ["压缩"], rating: 5),
        };
        Assert.Equal(["High", "Plain", "Low"], Names(Recommender.Recommend("混响", library)));
    }

    [Fact]
    public void FunctionMatchOutweighsRating()
    {
        var library = new[]
        {
            Row("Comp Only", ["压缩"], rating: 5),
            Row("Vocal Comp Low", ["压缩", "人声"], rating: 2),
            Row("Both Funcs Low", ["压缩", "磁带"], rating: 1),
        };
        Assert.Equal("Both Funcs Low", Names(Recommender.Recommend("磁带压缩", library))[0]);
    }

    [Fact]
    public void GoodNoteBoostsAndCanQualify()
    {
        var library = new[]
        {
            Row("Tagged", ["人声"]),
            Row("Noted", ["工具"], note: "高频发毛，压人声很顺"),
            Row("Unrelated", ["工具"], note: "鼓很好"),
        };
        var result = Recommender.Recommend("人声", library);
        Assert.Equal(["Noted", "Tagged"], Names(result));
        Assert.Contains(result.Items[0].Reasons, r => r.Contains("压人声很顺"));
    }

    [Theory]
    [InlineData("不适合人声")]
    [InlineData("人声上效果很差")]
    [InlineData("做人声没感觉")]
    public void BadNotePenalizesAndWarns(string note)
    {
        var library = new[] { Row("Disliked", ["人声"], note: note), Row("Neutral", ["人声"]) };
        var result = Recommender.Recommend("人声", library);
        Assert.Equal(["Neutral", "Disliked"], Names(result));
        Assert.Contains(result.Items[1].Reasons, r => r.StartsWith("注意你的备注") && r.Contains(note));
    }

    [Fact]
    public void BadNoteAloneDoesNotQualify()
    {
        var library = new[] { Row("Tool", ["工具"], note: "不适合人声") };
        Assert.Empty(Recommender.Recommend("人声", library).Items);
    }

    [Fact]
    public void NegativeWordInOtherClauseDoesNotCount()
    {
        var library = new[] { Row("X", ["人声"], note: "CPU 不低，人声很棒"), Row("Y", ["人声"]) };
        var result = Recommender.Recommend("人声", library);
        Assert.Equal("X", result.Items[0].Plugin.Name);
        Assert.DoesNotContain(result.Items[0].Reasons, r => r.StartsWith("注意"));
    }

    [Fact]
    public void PraiseContainingNegativeCharIsNotNegative()
    {
        var library = new[] { Row("X", ["工具"], note: "人声不错") };
        Assert.Equal(["X"], Names(Recommender.Recommend("人声", library)));
    }

    [Fact]
    public void MyTagsCountAsMatch()
    {
        var library = new[] { Row("X", ["工具"], myTags: ["主唱人声"]), Row("Y", ["工具"]) };
        Assert.Equal(["X"], Names(Recommender.Recommend("人声", library)));
    }

    [Fact]
    public void VendorRestrictsResults()
    {
        var library = new[] { Row("W Comp", ["压缩"], vendor: "Waves"), Row("F Comp", ["压缩"], vendor: "FabFilter") };
        var result = Recommender.Recommend("waves 的压缩", library);
        Assert.Equal(["W Comp"], Names(result));
        Assert.Equal(["Waves"], result.Vendors);
        Assert.Empty(result.NotUnderstood);
    }

    [Fact]
    public void VendorAloneListsVendor()
    {
        var library = new[] { Row("A", ["压缩"], vendor: "Waves"), Row("B", ["混响"], vendor: "Waves"), Row("C", ["压缩"], vendor: "Other") };
        Assert.Equal(2, Recommender.Recommend("Waves", library).TotalMatches);
    }

    [Fact]
    public void PluginNameIsSearchable() =>
        Assert.Contains("Doubler", Names(Recommender.Recommend("doubler", Library)));

    [Fact]
    public void StyleWordsMapToRelatedTags()
    {
        var names = Names(Recommender.Recommend("想要温暖一点", Library));
        Assert.Contains("Warm Drive", names);
        Assert.Contains("Tape Echo", names);
        Assert.DoesNotContain("Room", names);
    }

    [Fact]
    public void WordFoundOnlyInNoteIsUsedAndUnknownWordIsReported()
    {
        var library = new[] { Row("Plain", ["压缩"]), Row("Airy", ["压缩"], note: "声音通透") };

        var known = Recommender.Recommend("通透的压缩", library);
        Assert.Equal("Airy", known.Items[0].Plugin.Name);
        Assert.Empty(known.NotUnderstood);

        var unknown = Recommender.Recommend("压缩 蓝牙", library);
        Assert.Equal(["蓝牙"], unknown.NotUnderstood);
        Assert.Equal(2, unknown.TotalMatches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("推荐一个插件")]
    public void EmptyOrFillerOnlyNeedReturnsNothing(string need)
    {
        var result = Recommender.Recommend(need, Library);
        Assert.Empty(result.Items);
        Assert.Empty(result.NotUnderstood);
    }

    [Fact]
    public void UnknownNeedReturnsNothingAndReportsIt()
    {
        var result = Recommender.Recommend("蓝牙音箱", Library);
        Assert.Empty(result.Items);
        Assert.Equal(["蓝牙音箱"], result.NotUnderstood);
    }

    [Fact]
    public void GuessedTagsRankBelowConfirmedOnes()
    {
        var library = new[] { Row("Guess", ["混响"], source: ClassificationSource.NameKeyword), Row("Sure", ["混响"]) };
        var result = Recommender.Recommend("混响", library);
        Assert.Equal(["Sure", "Guess"], Names(result));
        Assert.Contains("分类由名称推测", result.Items[1].Reasons);
    }

    [Fact]
    public void LimitCapsItemsButKeepsTotal()
    {
        var library = Enumerable.Range(0, 30).Select(i => Row("R" + i, ["混响"])).ToList();
        var result = Recommender.Recommend("混响", library, limit: 5);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(30, result.TotalMatches);
    }

    [Fact]
    public void NegatedVendorIsExcludedNotRequired()
    {
        var library = new[] { Row("W Comp", ["压缩"], vendor: "Waves"), Row("F Comp", ["压缩"], vendor: "FabFilter") };
        var result = Recommender.Recommend("压缩 不要 waves", library);
        Assert.Equal(["F Comp"], Names(result));
        Assert.Empty(result.Vendors);
        Assert.Contains("Waves", result.Excluded);
    }

    [Fact]
    public void NegatedNameIsExcluded()
    {
        var library = new[] { Row("RVox", ["压缩"]), Row("C4", ["压缩"]) };
        var result = Recommender.Recommend("压缩 不要 rvox", library);
        Assert.Equal(["C4"], Names(result));
        Assert.Empty(result.NotUnderstood);
    }

    [Fact]
    public void NegatedUnknownWordIsReportedNotSilentlyIgnored()
    {
        var result = Recommender.Recommend("混响 不要蓝牙", Library);
        Assert.Equal(["Room"], Names(result));
        Assert.Equal(["蓝牙"], result.NotUnderstood);
    }

    [Fact]
    public void AmpNeedSkipsInstrumentsThatOnlyShareTheScene()
    {
        var library = new[] { Row("Ample Guitar", ["音源", "吉他/贝斯"]), Row("Amp Sim", ["失真/饱和", "吉他/贝斯"]) };
        Assert.Equal(["Amp Sim"], Names(Recommender.Recommend("吉他箱头", library)));
    }

    [Fact]
    public void ChineseInstrumentNameFindsLatinPluginName()
    {
        var library = new[] { Row("China Dongxiao", ["音源", "民乐"]), Row("China Qudi", ["音源", "民乐"]), Row("China Zheng", ["音源", "民乐"]) };
        var result = Recommender.Recommend("古筝音源", library);
        Assert.Equal("China Zheng", result.Items[0].Plugin.Name);
        Assert.Contains("名称对应“古筝”", result.Items[0].Reasons);
    }

    [Fact]
    public void StyleWordPrefersItsPrimaryTagAndNamesIt()
    {
        var library = new[] { Row("A Comp", ["压缩"]), Row("Z Transient", ["瞬态"]) };
        var result = Recommender.Recommend("冲击力", library);
        Assert.Equal(["Z Transient", "A Comp"], Names(result));
        Assert.Contains("功能：冲击力（瞬态）", result.Items[0].Reasons);
    }

    [Fact]
    public void DedicatedPluginBeatsMultiPurposeOnTie()
    {
        var library = new[] { Row("A Strip", ["EQ", "压缩", "通道条"]), Row("Z EQ", ["EQ"]) };
        Assert.Equal(["Z EQ", "A Strip"], Names(Recommender.Recommend("EQ", library)));
    }

    [Theory]
    [InlineData("底噪太大想降噪")]
    [InlineData("让立体声更宽")]
    [InlineData("想要贝斯更厚")]
    public void ConversationalNeedsLeaveNothingUnderstood(string need) =>
        Assert.Empty(Recommender.Recommend(need, Library).NotUnderstood);

    [Fact]
    public void ReturnsAllMatchesByDefault()
    {
        var library = Enumerable.Range(0, 300).Select(i => Row("R" + i, ["混响"])).ToList();
        var result = Recommender.Recommend("混响", library);
        Assert.Equal(300, result.Items.Count);
        Assert.Equal(300, result.TotalMatches);
    }

    [Fact]
    public void BusCompressionUnderstoodAndFindsBusModelsByName()
    {
        var library = new[]
        {
            Row("C1 comp", ["压缩"]),
            Row("API-2500", ["压缩"]),
            Row("SSL G-Master Buss Compressor", ["压缩"]),
            Row("bx_townhouse Buss Compressor", ["压缩"]),
            Row("Bus Reverb", ["混响"]),
        };
        var result = Recommender.Recommend("总线压缩", library);
        Assert.Empty(result.NotUnderstood);
        Assert.Contains("总线", result.Understood);
        Assert.Contains("压缩", result.Understood);
        var names = Names(result);
        Assert.Equal("C1 comp", names[^1]);
        Assert.DoesNotContain("Bus Reverb", names);
        Assert.Equal(4, names.Count);
    }

    [Theory]
    [InlineData("板式混响", "Abbey Road Plates")]
    [InlineData("弹簧混响", "Spring Box")]
    [InlineData("卷积混响", "Altiverb 7")]
    public void ReverbSubtypesFindModelsByNameAndRankThemFirst(string need, string expected)
    {
        var library = new[]
        {
            Row("A Generic Hall", ["混响"]),
            Row("Abbey Road Plates", ["混响"]),
            Row("Spring Box", ["混响"]),
            Row("Altiverb 7", ["混响"]),
        };
        var result = Recommender.Recommend(need, library);
        Assert.Equal(expected, result.Items[0].Plugin.Name);
        Assert.Empty(result.NotUnderstood);
    }

    [Theory]
    [InlineData("FET压缩", "CLA-76")]
    [InlineData("光电压缩", "CLA-2A")]
    [InlineData("vari-mu 压缩", "Fairchild 670")]
    [InlineData("VCA 压缩", "SSL Comp")]
    public void CompressorTypesFindClassicModels(string need, string expected)
    {
        var library = new[] { Row("Plain", ["压缩"]), Row("CLA-76", ["压缩"]), Row("CLA-2A", ["压缩"]), Row("Fairchild 670", ["压缩"]), Row("SSL Comp", ["压缩"]) };
        Assert.Equal(expected, Names(Recommender.Recommend(need, library))[0]);
    }

    [Theory]
    [InlineData("侧链压缩")]
    [InlineData("并行压缩")]
    [InlineData("动态均衡")]
    [InlineData("线性相位EQ")]
    [InlineData("中置侧边处理")]
    [InlineData("琶音器")]
    [InlineData("波表合成器")]
    [InlineData("铺底音色")]
    [InlineData("弦乐音源")]
    [InlineData("相位校正")]
    [InlineData("喷麦")]
    [InlineData("LUFS")]
    public void MixingAndArrangingTermsAreUnderstood(string need)
    {
        var result = Recommender.Recommend(need, Library);
        Assert.NotEmpty(result.Understood);
        Assert.Empty(result.NotUnderstood);
    }

    [Fact]
    public void BusNeedExcludesInstruments()
    {
        var instrument = Row("Addictive Drums", ["音源", "鼓"]) with { AutoKind = PluginKind.Instrument };
        var library = new[] { instrument, Row("Drum Strip", ["通道条", "鼓"]), Row("API-2500", ["压缩"]) };
        var names = Names(Recommender.Recommend("鼓总线", library));
        Assert.DoesNotContain("Addictive Drums", names);
        Assert.Contains("Drum Strip", names);
        Assert.Contains("API-2500", names);
    }

    [Fact]
    public void SubtypeKeepsGeneralMatchesBelowSpecificOnes()
    {
        var library = new[] { Row("A Hall", ["混响"]), Row("Z Plate", ["混响"]) };
        var result = Recommender.Recommend("板式混响", library);
        Assert.Equal(["Z Plate", "A Hall"], Names(result));
    }

    [Fact]
    public void SpecificNameBeatsGeneralTag()
    {
        var library = new[] { Row("A Keys", ["音源"]), Row("Z Strings", ["工具"]) };
        Assert.Equal("Z Strings", Names(Recommender.Recommend("弦乐音源", library))[0]);
    }

    [Fact]
    public void ReportsConceptsNoInstalledPluginCovers()
    {
        var library = new[] { Row("A Keys", ["音源"]) };
        var result = Recommender.Recommend("弦乐音源", library);
        Assert.Equal(["A Keys"], Names(result));
        Assert.Equal(["弦乐"], result.NoPluginFor);
        Assert.Empty(result.NotUnderstood);
    }

    [Theory]
    [InlineData("侧链", "Focusrite SC")]
    [InlineData("人声加倍", "Schoeps Double MS")]
    public void ShortAmbiguousNamesDoNotFalselyMatch(string need, string name) =>
        Assert.Empty(Recommender.Recommend(need, new[] { Row(name, ["工具"]) }).Items);

    [Fact]
    public void NegatingPlateExcludesPlateModels()
    {
        var library = new[] { Row("Abbey Road Plates", ["混响"]), Row("Hall", ["混响"]) };
        Assert.Equal(["Hall"], Names(Recommender.Recommend("混响 不要板式", library)));
    }

    [Fact]
    public void DynamicEqNeedsBothTagsOrName()
    {
        var library = new[] { Row("Plain EQ", ["EQ"]), Row("Both", ["EQ", "动态"]), Row("soothe2", ["工具"]) };
        var names = Names(Recommender.Recommend("动态均衡", library));
        Assert.Contains("Both", names);
        Assert.Contains("soothe2", names);
        Assert.DoesNotContain("Plain EQ", names);
    }

    // ---------- 资料库不认识的插件（别人电脑上的插件） ----------

    [Fact]
    public void UnknownPluginWithOnlyBroadTagQualifiesBelowSpecificOnes()
    {
        var library = new[]
        {
            Row("Mystery Box", ["动态"], source: ClassificationSource.SdkCategory),
            Row("Known Comp", ["压缩"]),
        };
        var result = Recommender.Recommend("压缩", library);
        Assert.Equal(["Known Comp", "Mystery Box"], Names(result));
        Assert.Contains(result.Items[1].Reasons, r => r.Contains("待确认"));
    }

    [Fact]
    public void BroadTagIgnoredWhenPluginHasAnotherFunction()
    {
        var library = new[]
        {
            Row("DeEsser", ["动态", "去齿音"], source: ClassificationSource.SdkCategory),
            Row("Mastering Dyn", ["动态", "母带"], source: ClassificationSource.SdkCategory),
        };
        Assert.Equal(["Mastering Dyn"], Names(Recommender.Recommend("压缩", library)));
    }

    [Fact]
    public void BroadTagDoesNotApplyToSeededOrManualPlugins()
    {
        var library = new[] { Row("Gate Thing", ["动态"]), Row("Mine", ["动态"], source: ClassificationSource.Manual) };
        Assert.Empty(Recommender.Recommend("限制器", library).Items);
    }

    [Fact]
    public void BroadMatchIsNotUsedForExclusion()
    {
        var library = new[] { Row("Mystery Box", ["动态"], source: ClassificationSource.SdkCategory), Row("Dyn", ["动态"]) };
        Assert.Contains("Mystery Box", Names(Recommender.Recommend("动态 不要压缩", library)));
    }

    [Fact]
    public void UnknownPluginFoundByNameWithoutAnyTags()
    {
        var library = new[] { Row("Zebra Plate Reverb", [], source: ClassificationSource.None), Row("Zebra", [], source: ClassificationSource.None) };
        var result = Recommender.Recommend("混响", library);
        Assert.Equal(["Zebra Plate Reverb"], Names(result));
        Assert.Contains(result.Items[0].Reasons, r => r.Contains("名称含"));
    }

    [Fact]
    public void CamelCaseNamesAreSplitForMatching()
    {
        var library = new[] { Row("SuperCompressor", [], source: ClassificationSource.None) };
        Assert.Equal(["SuperCompressor"], Names(Recommender.Recommend("压缩", library)));
    }

    [Fact]
    public void UnknownInstrumentMatchesInstrumentNeed()
    {
        var row = Row("Weird Synth Thing", [], source: ClassificationSource.None) with { AutoKind = PluginKind.Instrument };
        var plain = Row("Weird Thing", [], source: ClassificationSource.None) with { AutoKind = PluginKind.Instrument };
        var effect = Row("Effect Thing", [], source: ClassificationSource.None);
        var names = Names(Recommender.Recommend("音源", new[] { row, plain, effect }));
        Assert.Contains("Weird Thing", names);
        Assert.DoesNotContain("Effect Thing", names);
    }

    [Theory]
    [InlineData("Acme Compressor", "压缩")]
    [InlineData("Acme Limiter", "限制器")]
    [InlineData("Acme Gate", "门限")]
    [InlineData("Acme Tape", "磁带")]
    public void ClassifierGivesSpecificTagsFromNames(string name, string tag) =>
        Assert.Contains(tag, Classifier.ClassifyWithoutSeed(name, "", null).Tags);

    [Fact]
    public void EveryConceptHasAWayToMatch() =>
        Assert.All(Recommender.ConceptRoutes, c => Assert.True(c.Routes > 0, c.Label));

    [Fact]
    public void EveryResultExplainsItself()
    {
        var result = Recommender.Recommend("人声压缩", Library);
        Assert.All(result.Items, item => Assert.NotEmpty(item.Reasons));
    }
}
