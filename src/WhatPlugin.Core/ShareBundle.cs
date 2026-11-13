using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhatPlugin.Core;

/// <summary>一个文件里同时分享评价和混音笔记。两部分各自就是完整的评价文件、笔记文件，读的时候分别交给它们检查。</summary>
public sealed record ShareBundle(ReviewFile? Reviews, NoteFile? Notes);

public static class ShareBundles
{
    public const string Extension = ".whatplugin-share";
    private const string FormatName = "whatplugin-share";
    private const int FormatVersion = 1;
    private const long MaxFileBytes = 40 * 1024 * 1024;

    public static string Serialize(ShareBundle bundle, DateTime exportedUtc)
    {
        if (bundle.Reviews == null && bundle.Notes == null)
            throw new ArgumentException("评价和混音笔记至少要有一样", nameof(bundle));
        var root = new JsonObject
        {
            ["Format"] = FormatName,
            ["Version"] = FormatVersion,
            ["ExportedUtc"] = exportedUtc,
        };
        if (bundle.Reviews != null)
            root["Reviews"] = JsonNode.Parse(ReviewShare.Serialize(bundle.Reviews));
        if (bundle.Notes != null)
            root["Notes"] = JsonNode.Parse(NoteShare.Serialize(bundle.Notes));
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public static ShareBundle Read(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new FormatException("文件太大，不像是分享文件。");
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <exception cref="FormatException">不是分享文件，或者里面的评价、笔记有一部分不可用。</exception>
    public static ShareBundle Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new FormatException("这不是有效的分享文件。");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != FormatName)
                throw new FormatException("这不是有效的分享文件。");
            if (root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() > FormatVersion)
                throw new FormatException("这份文件来自更新版本的软件，当前版本无法读取。");

            ReviewFile? reviews = null;
            NoteFile? notes = null;
            if (root.TryGetProperty("Reviews", out var r) && r.ValueKind == JsonValueKind.Object)
                reviews = ReviewShare.Parse(r.GetRawText());
            if (root.TryGetProperty("Notes", out var n) && n.ValueKind == JsonValueKind.Object)
                notes = NoteShare.Parse(n.GetRawText());
            if (reviews == null && notes == null)
                throw new FormatException("这份分享文件里没有评价，也没有混音笔记。");
            return new ShareBundle(reviews, notes);
        }
    }
}
