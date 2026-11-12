using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatPlugin.Core;

/// <summary>按插件名猜到的厂家。Candidates 多于一个时表示同名插件属于不同厂家，不能自动填。</summary>
public sealed record VendorGuess(string Vendor, IReadOnlyList<string> Candidates)
{
    public bool IsAmbiguous => Candidates.Count > 1;
}

/// <summary>
/// 内置的厂家目录：产品名 → 厂家，厂家 → 登录账号所属平台。
/// 只用来补空白：插件自带的厂家和用户手动改过的厂家都优先于这里。
/// 不改变插件的合并键，所以目录更新后已有的评分、备注不会脱钩。
/// </summary>
public static partial class VendorCatalog
{
    /// <param name="Prefixes">插件名以它开头就能确定厂家的词，例如 Waves 的 "CLA"、UA 的 "UAD"、iZotope 的 "Ozone"。</param>
    /// <param name="UsesILok">官网说明需要 iLok 账号激活。</param>
    public sealed record Entry(string Name, string[] Aliases, string Platform, string[] Products, string[]? Prefixes = null, bool UsesILok = false);

    private sealed record Prefix(string Words, string Vendor, bool FromVendorName);

    private sealed record Index(
        List<Entry> Entries,
        Dictionary<string, Entry> ByVendor,
        Dictionary<string, HashSet<string>> ByProduct,
        Dictionary<string, HashSet<string>> ByBase,
        Dictionary<string, HashSet<string>> ByCode,
        List<Prefix> Prefixes);

    // 太普通的名字不用来认厂家，几乎每家都有。
    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
    {
        "compressor", "limiter", "reverb", "delay", "eq", "equalizer", "gate", "chorus", "flanger", "phaser",
        "tremolo", "filter", "saturator", "saturation", "distortion", "exciter", "maximizer", "deesser", "tuner",
        "analyzer", "meter", "spectrum", "utility", "gain", "pan", "panner", "stereo", "imager", "one", "bass",
        "vocal", "vocals", "master", "mastering", "clipper", "transient", "echo", "tape", "amp", "drive",
        "transientshaper", "multiband", "declick", "declip", "denoise", "dereverb", "dehum", "deplosive", "noisegate",
        "channelstrip", "busscompressor", "buscompressor", "plate", "spring", "room", "hall", "doubler", "widener",
    };

    // 厂家名是普通英文词时，后半截还得是这家的产品才算。
    private static readonly HashSet<string> CommonWordVendors = new(StringComparer.Ordinal)
    {
        "output", "cradle", "decidedly", "spectral", "eventual", "venom", "initial audio",
    };

    private static Lazy<Index> _index = new(() => Build(LoadEmbedded()));

    /// <summary>测试用：换成指定的目录内容。传 null 恢复内置目录。</summary>
    public static void UseForTests(IReadOnlyList<Entry>? entries) =>
        _index = new Lazy<Index>(() => Build(entries?.ToList() ?? LoadEmbedded()));

    public static IReadOnlyList<Entry> Entries => _index.Value.Entries;

    /// <summary>
    /// 按插件名猜厂家，认不出返回 null。依次尝试：
    /// 以厂家名或厂家专用前缀开头（"FabFilter Pro-L 2"、"UAD Pultec"、"CLA-76"）；
    /// 完整产品名；去掉版本号的产品系列（"Pro-Q 2" → Pro-Q）；
    /// 系列名加版本号再加模块（"Ozone 11 Imager"）；带数字的型号开头（"C1 comp" → C1）。
    /// </summary>
    public static VendorGuess? Guess(string pluginName)
    {
        var index = _index.Value;
        var name = pluginName.Trim();
        if (name.Length == 0)
            return null;

        var spaced = " " + Words(name) + " ";
        foreach (var prefix in index.Prefixes)
        {
            if (!spaced.StartsWith(" " + prefix.Words + " ", StringComparison.Ordinal))
                continue;
            // 名字就是这个前缀本身（"Q10"、"TRACT"）也算；厂家名是普通英文词时，后半截还得是这家的产品。
            var rest = spaced[(prefix.Words.Length + 1)..].Trim();
            if (prefix.FromVendorName && CommonWordVendors.Contains(prefix.Words)
                && (rest.Length == 0 || !IsProductOf(index, rest, prefix.Vendor)))
                continue;
            return new VendorGuess(prefix.Vendor, [prefix.Vendor]);
        }

        var key = Normalizer.Name(name);
        if (key.Length < 2 || GenericNames.Contains(key))
            return null;
        if (index.ByProduct.TryGetValue(key, out var exact))
            return ToGuess(exact);
        var baseKey = BaseKey(name);
        if (baseKey.Length >= 3 && !GenericNames.Contains(baseKey) && index.ByBase.TryGetValue(baseKey, out var family))
            return ToGuess(family);

        var tokens = name.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        // "Ozone 11 Imager"、"Neutron 3 Compressor"：系列名后面跟版本号，再跟模块名。
        for (var i = 1; i < Math.Min(tokens.Length, 4); i++)
        {
            if (!VersionToken().IsMatch(tokens[i]))
                continue;
            var series = Normalizer.Name(string.Join(' ', tokens[..i]));
            if (series.Length >= 2 && !GenericNames.Contains(series)
                && (index.ByBase.TryGetValue(series, out var seriesVendors) || index.ByProduct.TryGetValue(series, out seriesVendors)))
                return ToGuess(seriesVendors);
            break;
        }
        // "C1 comp"、"L3-LL Multi"：开头是带数字的型号，和某家产品的型号一样。
        if (tokens.Length > 1 && CodeKey(tokens[0]) is { Length: > 0 } code && index.ByCode.TryGetValue(code, out var coded))
            return ToGuess(coded);
        return null;
    }

    /// <summary>
    /// 输入厂家时的候选：名称（或别名）以输入开头的在前，名称里含有输入的在后，各自按字母排。
    /// extra 是插件库和心愿单里已有的厂家，目录里没有的也能选到。输入为空时不给候选。
    /// </summary>
    public static IReadOnlyList<string> Suggest(string text, IEnumerable<string> extra, int max = 40)
    {
        var typed = text.Trim();
        if (typed.Length == 0)
            return [];
        var names = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            names[entry.Name] = entry.Aliases;
        foreach (var vendor in extra)
        {
            var display = DisplayVendor(vendor);
            if (display.Length > 0 && !names.ContainsKey(display))
                names[display] = [];
        }

        bool Starts(string value) => value.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
        bool Contains(string value) => value.Contains(typed, StringComparison.OrdinalIgnoreCase);
        var starts = names.Where(n => Starts(n.Key) || n.Value.Any(Starts)).Select(n => n.Key).Order(StringComparer.OrdinalIgnoreCase);
        var contains = names.Where(n => !(Starts(n.Key) || n.Value.Any(Starts)) && (Contains(n.Key) || n.Value.Any(Contains)))
            .Select(n => n.Key).Order(StringComparer.OrdinalIgnoreCase);
        return starts.Concat(contains).Take(max).ToList();
    }

    /// <summary>目录里的厂家显示名；不认识的厂家按原来的方式统一写法。</summary>
    public static string DisplayVendor(string vendor)
    {
        if (vendor.Trim().Length == 0)
            return "";
        return _index.Value.ByVendor.TryGetValue(Key(vendor), out var entry) ? entry.Name : Normalizer.CanonicalVendor(vendor);
    }

    /// <summary>
    /// 登录账号所属平台，例如 brainworx 的插件用 Plugin Alliance 账号。
    /// 目录里没有的厂家，平台就是厂家自己。厂家为空时返回空串。
    /// </summary>
    public static string PlatformOf(string vendor)
    {
        if (vendor.Trim().Length == 0)
            return "";
        return _index.Value.ByVendor.TryGetValue(Key(vendor), out var entry) && entry.Platform.Length > 0
            ? entry.Platform
            : DisplayVendor(vendor);
    }

    /// <summary>这个厂家（或平台）的插件是否用 iLok 账号激活。目录里没有的厂家返回 false。</summary>
    public static bool UsesILok(string vendor) =>
        vendor.Trim().Length > 0 && _index.Value.ByVendor.TryGetValue(Key(vendor), out var entry) && entry.UsesILok;

    /// <summary>
    /// 名字和目录里某个效果器产品完全对上（可以差版本号）。目录只收效果器，所以对上了就能当效果器；
    /// 只靠厂家前缀认出来的不算，因为同一厂家也做音源。
    /// </summary>
    public static bool IsKnownEffect(string name)
    {
        var index = _index.Value;
        foreach (var candidate in new[] { name, Bitness().Replace(name, "") })
        {
            var key = Normalizer.Name(candidate);
            if (key.Length >= 3 && !GenericNames.Contains(key) && index.ByProduct.ContainsKey(key))
                return true;
            var baseKey = BaseKey(candidate);
            if (baseKey.Length >= 3 && !GenericNames.Contains(baseKey) && index.ByBase.ContainsKey(baseKey))
                return true;
        }
        return false;
    }

    /// <summary>VST2 文件名常带的位数后缀："Serum_x64"、"Pro-Q 3 (64 bit)"。</summary>
    [GeneratedRegex(@"[\s_-]*(\(?\s*(x64|x86_64|64[\s-]?bit|win64)\s*\)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Bitness();

    /// <summary>比较平台用的键。</summary>
    public static string PlatformKey(string platform) => Key(platform);

    private static bool IsProductOf(Index index, string rest, string vendor)
    {
        var key = Normalizer.Name(rest);
        var baseKey = BaseKey(rest);
        return (index.ByProduct.TryGetValue(key, out var exact) && exact.Contains(vendor))
            || (baseKey.Length > 0 && index.ByBase.TryGetValue(baseKey, out var family) && family.Contains(vendor));
    }

    private static VendorGuess ToGuess(HashSet<string> vendors)
    {
        var list = vendors.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new VendorGuess(list.Count == 1 ? list[0] : "", list);
    }

    private static string Key(string vendor) => Normalizer.Vendor(vendor);

    private static string Words(string text) =>
        string.Join(' ', NonWord().Split(text.ToLowerInvariant()).Where(w => w.Length > 0));

    /// <summary>去掉末尾的版本号："Pro-Q 4" → "proq"，"bx_digital V3" → "bxdigital"，"Saturn 2" → "saturn"。</summary>
    private static string BaseKey(string name)
    {
        var stripped = TrailingVersion().Replace(name.Trim(), "");
        if (stripped.Length == name.Trim().Length)
            return "";
        return Normalizer.Name(stripped);
    }

    /// <summary>型号：字母和数字都有的短词，例如 C1、L3-LL、PS22、CLA-76。纯数字（1176）各家都用，不算。</summary>
    private static string CodeKey(string token)
    {
        var key = Normalizer.Name(token);
        return key.Length is >= 2 and <= 8 && key.Any(char.IsAsciiDigit) && key.Any(char.IsAsciiLetter) ? key : "";
    }

    private static Index Build(List<Entry> entries)
    {
        var byVendor = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var prefixes = new List<Prefix>();
        foreach (var entry in entries)
        {
            foreach (var alias in entry.Aliases.Prepend(entry.Name))
            {
                var key = Key(alias);
                if (key.Length == 0)
                    continue;
                byVendor.TryAdd(key, entry);
                var words = Words(alias);
                // 太短的厂家名（"SSL"、"UA"）当前缀容易误中，只认 4 个字符以上的。
                if (words.Length >= 4 && prefixes.All(p => p.Words != words))
                    prefixes.Add(new Prefix(words, entry.Name, true));
            }
            foreach (var prefix in entry.Prefixes ?? [])
            {
                var words = Words(prefix);
                if (words.Length > 0 && prefixes.All(p => p.Words != words))
                    prefixes.Add(new Prefix(words, entry.Name, false));
            }
        }
        // 长的先试，"Universal Audio" 要先于 "Universal"。
        prefixes.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length));

        var byProduct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byBase = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byCode = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var product in entry.Products)
            {
                var key = Normalizer.Name(product);
                if (key.Length < 2 || GenericNames.Contains(key))
                    continue;
                Add(byProduct, key, entry.Name);
                var baseKey = BaseKey(product);
                if (baseKey.Length >= 2)
                    Add(byBase, baseKey, entry.Name);
                var first = product.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
                if (CodeKey(first) is { Length: > 0 } code)
                    Add(byCode, code, entry.Name);
            }
        }
        return new Index(entries, byVendor, byProduct, byBase, byCode, prefixes);
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string vendor)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(vendor);
    }

    private sealed record CatalogFile(List<CatalogVendor> Vendors);
    private sealed record CatalogVendor(string Name, string[]? Aliases, string? Platform, string[]? Products, string[]? Prefixes, bool Ilok);

    private static List<Entry> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WhatPlugin.Core.Data.vendor-catalog.json");
        if (stream == null)
            return [];
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return file?.Vendors
            .Select(v => new Entry(v.Name, v.Aliases ?? [], v.Platform ?? "", v.Products ?? [], v.Prefixes ?? [], v.Ilok))
            .ToList() ?? [];
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"[\s_-]+(v|mk\s?)?(\d+(\.\d+)?|ii|iii|iv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"^(v|mk)?\d+(\.\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionToken();
}
