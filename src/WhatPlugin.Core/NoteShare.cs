using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatPlugin.Core;

/// <summary>分享出去的一份混音笔记。Notes 用和备份相同的结构，但不带来源（工程名和路径）。</summary>
public sealed record NoteFile(string Author, DateTime ExportedUtc, IReadOnlyList<ChainBackupEntry> Notes)
{
    /// <summary>文件里用到的插件名（首选和备选），去重后按出现顺序。</summary>
    public IReadOnlyList<ChainPick> Picks => Notes
        .SelectMany(n => n.Steps ?? [])
        .SelectMany(s => s.AllPicks)
        .GroupBy(p => Normalizer.Name(p.PluginName))
        .Select(g => g.First())
        .ToList();

    public bool HasRoutes => Notes.Any(n => n.Output != null || n.Sends is { Count: > 0 } || n.Sidechains is { Count: > 0 });
}

/// <summary>
/// 混音笔记分享文件，和评价分享分开。写入时只取笔记名、文件夹、标签、通道类型、插件和应用思路，
/// 可选带上笔记之间的路由；笔记是从哪个工程导入的（工程名和路径）不写进去。
/// 读取时文件来自别人，所有字段都要限制长度和数量。
/// </summary>
public static class NoteShare
{
    public const string Extension = ".whatplugin-notes";
    public const string FormatName = "whatplugin-notes";
    public const int FormatVersion = 1;
    public const long MaxFileBytes = 20 * 1024 * 1024;

    private const int MaxNotes = 5_000;
    private const int MaxSteps = 200;
    private const int MaxAlternates = 50;
    private const int MaxDepth = 8;
    private const int MaxRoutes = 100;
    private const int MaxNameLength = 200;
    private const int MaxKeyLength = 400;
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

    private sealed record FileDto(string? Format, int Version, string? Author, DateTime ExportedUtc, List<NoteDto?>? Notes);

    private sealed record NoteDto(
        string? Name,
        List<string?>? Folder,
        List<string?>? Tags,
        string? Note,
        string? Role,
        List<StepDto?>? Steps,
        RefDto? Output,
        List<RefDto?>? Sends,
        List<RefDto?>? Sidechains);

    private sealed record StepDto(string? Plugin, string? Key, string? Note, List<PickDto?>? Alternates);

    private sealed record PickDto(string? Plugin, string? Key, string? Note);

    private sealed record RefDto(List<string?>? Folder, string? Name, string? Plugin);

    /// <param name="chains">要分享的笔记。路由只保留指向这些笔记的。</param>
    public static NoteFile Build(
        string author,
        IEnumerable<PluginChain> chains,
        IReadOnlyList<NoteFolder> folders,
        IReadOnlyList<NoteRoute> routes,
        bool includeRoutes,
        DateTime exportedUtc)
    {
        var entries = BackupRestore.ChainEntries(chains, folders, includeRoutes ? routes : [])
            .Select(e => e with
            {
                Source = null,
                Output = includeRoutes ? e.Output : null,
                Sends = includeRoutes ? e.Sends : null,
                Sidechains = includeRoutes ? e.Sidechains : null,
            })
            .ToList();
        return new NoteFile(author.Trim(), exportedUtc, entries);
    }

    public static string Serialize(NoteFile file) => JsonSerializer.Serialize(
        new FileDto(
            FormatName,
            FormatVersion,
            file.Author,
            file.ExportedUtc,
            file.Notes.Select(n => (NoteDto?)new NoteDto(
                n.Name,
                (n.Folder ?? []).ToList<string?>(),
                n.Tags is { Count: > 0 } tags ? tags.ToList<string?>() : null,
                string.IsNullOrEmpty(n.Note) ? null : n.Note,
                n.Role?.ToString(),
                (n.Steps ?? []).Select(s => (StepDto?)new StepDto(
                    s.PluginName,
                    s.PluginKey,
                    string.IsNullOrEmpty(s.Note) ? null : s.Note,
                    s.Backups.Count == 0 ? null : s.Backups.Select(a => (PickDto?)new PickDto(a.PluginName, a.PluginKey, string.IsNullOrEmpty(a.Note) ? null : a.Note)).ToList())).ToList(),
                ToDto(n.Output),
                n.Sends?.Select(ToDto).ToList(),
                n.Sidechains?.Select(ToDto).ToList())).ToList()),
        Options);

    private static RefDto? ToDto(ChainRouteRef? route) =>
        route == null ? null : new RefDto((route.Folder ?? []).ToList<string?>(), route.Name, route.Plugin);

    public static NoteFile Read(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new FormatException("文件太大，不像是混音笔记文件。");
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <exception cref="FormatException">内容不是可用的混音笔记文件。</exception>
    public static NoteFile Parse(string json)
    {
        FileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FileDto>(json, Options);
        }
        catch (JsonException)
        {
            throw new FormatException("这不是有效的混音笔记文件。");
        }

        if (dto == null || dto.Format != FormatName)
            throw new FormatException("这不是 WhatPlugin 的混音笔记文件。");
        if (dto.Version > FormatVersion)
            throw new FormatException("这份文件来自更新版本的 WhatPlugin，请先升级。");
        if (dto.Notes == null)
            throw new FormatException("文件里没有混音笔记。");
        if (dto.Notes.Count > MaxNotes)
            throw new FormatException("文件里的笔记太多。");

        var author = Text.Clean(dto.Author, MaxAuthorLength, multiline: false);
        var notes = new List<ChainBackupEntry>();
        foreach (var item in dto.Notes)
        {
            var name = Text.Clean(item?.Name, MaxNameLength, multiline: false);
            if (item == null || name.Length == 0)
                continue;
            var steps = (item.Steps ?? [])
                .Take(MaxSteps)
                .Select(CleanStep)
                .OfType<ChainStep>()
                .ToList();
            notes.Add(new ChainBackupEntry(
                name,
                Text.Clean(item.Note, MaxNoteLength, multiline: true),
                steps,
                CleanPath(item.Folder),
                CleanTags(item.Tags),
                null,
                ParseRole(item.Role),
                CleanRef(item.Output),
                CleanRefs(item.Sends),
                CleanRefs(item.Sidechains)));
        }
        return new NoteFile(author, dto.ExportedUtc, notes);
    }

    /// <summary>不认识的通道类型当成普通轨道，不让整个文件读不了。</summary>
    private static ChannelRole? ParseRole(string? role) =>
        Enum.TryParse<ChannelRole>(role, ignoreCase: true, out var value) && Enum.IsDefined(value) && !int.TryParse(role, out _) ? value : null;

    private static ChainStep? CleanStep(StepDto? step)
    {
        var primary = CleanPick(step == null ? null : new PickDto(step.Plugin, step.Key, step.Note));
        if (primary == null)
            return null;
        var alternates = (step!.Alternates ?? []).Take(MaxAlternates).Select(CleanPick).OfType<ChainPick>().ToList();
        return ChainStep.From(primary, alternates);
    }

    private static ChainPick? CleanPick(PickDto? pick)
    {
        var name = Text.Clean(pick?.Plugin, MaxNameLength, multiline: false);
        if (name.Length == 0)
            return null;
        return new ChainPick(Text.Clean(pick!.Key, MaxKeyLength, multiline: false), name, Text.Clean(pick.Note, MaxNoteLength, multiline: true));
    }

    private static List<string> CleanPath(List<string?>? path) =>
        (path ?? []).Select(p => Text.Clean(p, MaxNameLength, multiline: false)).Where(p => p.Length > 0).Take(MaxDepth).ToList();

    private static List<string> CleanTags(List<string?>? tags) =>
        (tags ?? [])
            .Select(t => Text.Clean(t, MaxTagLength, multiline: false))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToList();

    private static ChainRouteRef? CleanRef(RefDto? route)
    {
        var name = Text.Clean(route?.Name, MaxNameLength, multiline: false);
        if (name.Length == 0)
            return null;
        var plugin = Text.Clean(route!.Plugin, MaxNameLength, multiline: false);
        return new ChainRouteRef(CleanPath(route.Folder), name, plugin.Length > 0 ? plugin : null);
    }

    private static List<ChainRouteRef>? CleanRefs(List<RefDto?>? routes)
    {
        var list = (routes ?? []).Take(MaxRoutes).Select(CleanRef).OfType<ChainRouteRef>().ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>导入时放笔记的文件夹名。</summary>
    public static string RootFolderName(NoteFile file) =>
        file.Author.Length > 0 ? $"来自 {file.Author} 的笔记" : "导入的笔记";

    /// <summary>文字清理，和评价分享的规则一样：去掉控制字符和会改变显示方向的字符。</summary>
    private static class Text
    {
        public static string Clean(string? text, int maxLength, bool multiline)
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
            var end = char.IsHighSurrogate(cleaned[maxLength - 1]) ? maxLength - 1 : maxLength;
            return cleaned[..end];
        }
    }
}
