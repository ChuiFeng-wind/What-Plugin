using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatPlugin.Core;

/// <summary>别人分享的一条评价。Key 只由插件名和厂家算出，不信任文件里写的内容。</summary>
public sealed record SharedReview(
    string Key,
    string Name,
    string Vendor,
    int Rating,
    string Note,
    IReadOnlyList<string> Tags,
    int? ProjectCount = null,
    IReadOnlyList<string>? Categories = null,
    MixAttributes? Mix = null);

public sealed record ReviewFile(string AuthorId, string Author, DateTime ExportedUtc, IReadOnlyList<SharedReview> Reviews);

public sealed record ReviewSource(long Id, string AuthorId, string Name, DateTime ImportedUtc, int Count);

public sealed record AttributedReview(ReviewSource Source, SharedReview Review);

/// <summary>采用时的快照。之后和自己的内容比较，就知道有没有改过。</summary>
public sealed record ReviewAdoption(string PluginKey, string AuthorName, int Rating, string Note, IReadOnlyList<string> Tags)
{
    /// <summary>角落里显示的文字。自己的评价已清空时不再提示来源。</summary>
    public string Describe(PluginUserData user)
    {
        var data = user.Normalized();
        if (data.Rating == 0 && data.Note.Length == 0 && data.MyTags.Count == 0)
            return "";
        return data.Rating == Rating && data.Note == Note && data.MyTags.SequenceEqual(Tags)
            ? $"采用自 {AuthorName} 的评价"
            : $"基于 {AuthorName} 的评价修改";
    }
}

public sealed record ReviewExportOptions(bool Usage = false, bool Categories = false, bool Mix = false);

/// <summary>
/// 评价分享文件。写入时只取评分、感受、标签和用户勾选的可选项，
/// 账号、密码、序列号、价格、工程名和路径都不在这里的数据来源之内。
/// 读取时文件来自别人，所有字段都要限制长度和范围。
/// </summary>
public static class ReviewShare
{
    public const string Extension = ".whatplugin-review";
    public const string FormatName = "whatplugin-review";
    public const int FormatVersion = 1;
    public const long MaxFileBytes = 20 * 1024 * 1024;

    private const int MaxReviews = 20_000;
    private const int MaxNameLength = 200;
    private const int MaxAuthorLength = 40;
    private const int MaxNoteLength = 20_000;
    private const int MaxTags = 50;
    private const int MaxTagLength = 40;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record FileDto(string? Format, int Version, string? AuthorId, string? Author, DateTime ExportedUtc, List<ReviewDto?>? Reviews);

    private sealed record ReviewDto(string? Name, string? Vendor, int Rating, string? Note, List<string?>? Tags, int? ProjectCount, List<string?>? Categories, MixAttributes? Mix);

    /// <summary>有评分、感受或标签才算写过评价。</summary>
    public static bool HasReview(PluginRow row) =>
        row.User.Rating > 0 || row.User.Note.Trim().Length > 0 || row.User.MyTags.Count > 0;

    public static string KeyOf(string name, string vendor) => $"{Normalizer.Name(name)}|{Normalizer.Vendor(vendor)}";

    public static ReviewFile Build(
        string authorId,
        string author,
        IEnumerable<PluginRow> rows,
        IReadOnlyDictionary<string, PluginUsage> usage,
        ReviewExportOptions options,
        DateTime exportedUtc)
    {
        var reviews = rows
            .Where(r => r.Kind != PluginKind.Instrument && HasReview(r))
            .Select(r => new SharedReview(
                KeyOf(r.Name, r.Vendor),
                r.Name,
                r.Vendor,
                r.User.Rating,
                r.User.Note,
                r.User.MyTags,
                options.Usage && usage.TryGetValue(r.Key, out var used) ? used.Projects : null,
                options.Categories && r.Tags.Count > 0 ? r.Tags : null,
                options.Mix && r.User.Mix is { IsEmpty: false } mix ? mix : null))
            .ToList();
        return new ReviewFile(authorId, author, exportedUtc, reviews);
    }

    public static string Serialize(ReviewFile file) => JsonSerializer.Serialize(
        new FileDto(
            FormatName,
            FormatVersion,
            file.AuthorId,
            file.Author,
            file.ExportedUtc,
            file.Reviews.Select(r => (ReviewDto?)new ReviewDto(r.Name, r.Vendor, r.Rating, r.Note, r.Tags.ToList<string?>(), r.ProjectCount, r.Categories?.ToList<string?>(), r.Mix)).ToList()),
        Options);

    public static ReviewFile Read(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new FormatException("文件太大，不像是评价文件。");
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <exception cref="FormatException">内容不是可用的评价文件。</exception>
    public static ReviewFile Parse(string json)
    {
        FileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FileDto>(json, Options);
        }
        catch (JsonException)
        {
            throw new FormatException("这不是有效的评价文件。");
        }

        if (dto == null || dto.Format != FormatName)
            throw new FormatException("这不是 What Plugin 的评价文件。");
        if (dto.Version > FormatVersion)
            throw new FormatException("这份文件来自更新版本的 What Plugin，请先升级。");

        var authorId = Clean(dto.AuthorId, 64, multiline: false);
        var author = Clean(dto.Author, MaxAuthorLength, multiline: false);
        if (authorId.Length == 0 || author.Length == 0)
            throw new FormatException("评价文件缺少分享者信息。");
        if (dto.Reviews == null)
            throw new FormatException("评价文件里没有评价内容。");
        if (dto.Reviews.Count > MaxReviews)
            throw new FormatException("评价文件的条目太多。");

        var reviews = new Dictionary<string, SharedReview>();
        foreach (var item in dto.Reviews)
        {
            var name = Clean(item?.Name, MaxNameLength, multiline: false);
            if (item == null || name.Length == 0)
                continue;
            var vendor = Clean(item.Vendor, MaxNameLength, multiline: false);
            var key = KeyOf(name, vendor);
            var rating = Math.Clamp(item.Rating, 0, 5);
            var note = Clean(item.Note, MaxNoteLength, multiline: true);
            var tags = CleanTags(item.Tags);
            // 没有评分、感受和标签的条目算不上评价，留着只会多出空卡片。
            if (rating == 0 && note.Length == 0 && tags.Count == 0)
                continue;
            reviews[key] = new SharedReview(
                key,
                name,
                vendor,
                rating,
                note,
                tags,
                item.ProjectCount is >= 0 ? item.ProjectCount : null,
                item.Categories == null ? null : CleanTags(item.Categories),
                CleanMix(item.Mix));
        }
        return new ReviewFile(authorId, author, dto.ExportedUtc, reviews.Values.ToList());
    }

    private static List<string> CleanTags(List<string?>? tags) =>
        (tags ?? [])
            .Select(t => Clean(t, MaxTagLength, multiline: false))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToList();

    private static MixAttributes? CleanMix(MixAttributes? mix)
    {
        if (mix == null)
            return null;
        var clean = new MixAttributes(
            Enum.IsDefined(mix.Latency) ? mix.Latency : LatencyLevel.Unknown,
            Enum.IsDefined(mix.Cpu) ? mix.Cpu : CpuLevel.Unknown,
            Enum.IsDefined(mix.LinearPhase) ? mix.LinearPhase : Tri.Unknown,
            Enum.IsDefined(mix.Sidechain) ? mix.Sidechain : Tri.Unknown,
            Enum.IsDefined(mix.Oversampling) ? mix.Oversampling : Tri.Unknown);
        return clean.IsEmpty ? null : clean;
    }

    /// <summary>去掉控制字符和会改变显示方向的字符，避免昵称伪装成别的文字。</summary>
    private static string Clean(string? text, int maxLength, bool multiline)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in multiline ? text.ReplaceLineEndings("\n") : text)
        {
            if (c is '\n' or '\t')
            {
                if (multiline)
                    builder.Append(c);
            }
            else if (!char.IsControl(c) && c is not (>= '‪' and <= '‮') && c is not (>= '⁦' and <= '⁩') && c is not ('‎' or '‏'))
            {
                builder.Append(c);
            }
        }
        var cleaned = builder.ToString().Trim();
        if (cleaned.Length <= maxLength)
            return cleaned;
        // 不把代理对切成两半。
        var end = char.IsHighSurrogate(cleaned[maxLength - 1]) ? maxLength - 1 : maxLength;
        return cleaned[..end];
    }
}

/// <summary>按插件名查找别人的评价。同一个人对同一款插件只取一条，不把多款同名插件混在一起。</summary>
public sealed class ReviewLibrary
{
    private readonly Dictionary<string, List<AttributedReview>> _byName = [];

    public ReviewLibrary(IEnumerable<AttributedReview> reviews)
    {
        foreach (var review in reviews)
        {
            var name = NamePart(review.Review.Key);
            if (!_byName.TryGetValue(name, out var list))
                _byName[name] = list = [];
            list.Add(review);
        }
    }

    public IReadOnlyList<AttributedReview> For(PluginRow row)
    {
        if (!_byName.TryGetValue(NamePart(row.Key), out var candidates))
            return [];

        var vendor = Normalizer.Vendor(row.Vendor);
        // 本机有多款同名插件时键里带厂家，这时厂家不同的评价不算这一款的。
        var strict = row.Key.Contains('|');
        var result = new List<AttributedReview>();
        foreach (var group in candidates.GroupBy(c => c.Source.Id))
        {
            var chosen = group.FirstOrDefault(c => VendorPart(c.Review.Key) == vendor)
                ?? (strict ? group.FirstOrDefault(c => VendorPart(c.Review.Key).Length == 0) : group.First());
            if (chosen != null)
                result.Add(chosen);
        }
        return result.OrderBy(r => r.Source.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Source.Id).ToList();
    }

    public int CountMatched(ReviewSource source, IEnumerable<PluginRow> rows) =>
        rows.Count(r => For(r).Any(a => a.Source.Id == source.Id));

    private static string NamePart(string key)
    {
        var bar = key.IndexOf('|');
        return bar < 0 ? key : key[..bar];
    }

    private static string VendorPart(string key)
    {
        var bar = key.IndexOf('|');
        return bar < 0 ? "" : key[(bar + 1)..];
    }
}
