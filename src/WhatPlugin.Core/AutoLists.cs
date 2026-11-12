using System.Text.Json;

namespace WhatPlugin.Core;

/// <summary>自动清单里的一款插件：在几条笔记里当首选、几条里当备选。</summary>
public sealed record AutoListItem(PluginRow Plugin, double Score, int PrimaryNotes, int AlternateNotes, IReadOnlyList<string> NoteNames);

/// <summary>按混音笔记标签自动整理的清单，比如“人声 常用”。Hidden 是用户在这个清单里隐藏的插件。</summary>
public sealed record AutoList(string Tag, int NoteCount, IReadOnlyList<AutoListItem> Items, IReadOnlyList<AutoListItem> Hidden)
{
    public string Name => Tag + " 常用";
}

/// <summary>
/// 从混音笔记生成常用清单。每个标签一份：带这个标签的笔记里出现过的已安装插件，
/// 一条笔记里当首选记 1 分，只当备选记半分，同一条笔记里出现多次只算一次。
/// </summary>
public static class AutoLists
{
    public const double AlternateWeight = 0.5;

    /// <summary>隐藏记录存在设置里（会随设置一起备份）：{"人声": ["key1", "key2"]}。</summary>
    public const string HiddenSettingKey = "autolists.hidden";

    public static IReadOnlyList<AutoList> Build(
        IEnumerable<PluginChain> chains,
        IEnumerable<PluginRow> plugins,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? hidden = null)
    {
        var rows = plugins.ToList();
        var notes = chains.ToList();
        var tags = notes.SelectMany(c => c.TagList)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => IndexOfPreset(t))
            .ThenBy(t => t, StringComparer.CurrentCulture)
            .ToList();

        var lists = new List<AutoList>();
        foreach (var tag in tags)
        {
            var tagged = notes.Where(c => c.TagList.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
            var scores = new Dictionary<string, Tally>();
            foreach (var note in tagged)
            {
                // 同一条笔记里：当过首选就算首选，否则算备选。
                var roles = new Dictionary<string, (PluginRow Row, bool Primary)>();
                foreach (var step in note.Steps)
                {
                    Mark(step.Primary, primary: true);
                    foreach (var alternate in step.Backups)
                        Mark(alternate, primary: false);
                }
                foreach (var (key, role) in roles)
                {
                    if (!scores.TryGetValue(key, out var tally))
                        scores[key] = tally = new Tally(role.Row);
                    tally.Notes.Add(note.Name);
                    if (role.Primary)
                        tally.Primary++;
                    else
                        tally.Alternate++;
                }

                void Mark(ChainPick pick, bool primary)
                {
                    var (state, row) = ChainReport.Resolve(pick, rows);
                    if (state != ChainStepState.Installed || row == null)
                        return;
                    var was = roles.GetValueOrDefault(row.Key);
                    roles[row.Key] = (row, primary || was.Primary);
                }
            }

            var hiddenKeys = hidden?.GetValueOrDefault(tag) ?? (IReadOnlySet<string>)new HashSet<string>();
            var items = scores.Values
                .Select(s => new AutoListItem(s.Row, s.Primary + s.Alternate * AlternateWeight, s.Primary, s.Alternate,
                    s.Notes.Distinct().Order(StringComparer.CurrentCulture).ToList()))
                .OrderByDescending(i => i.Score)
                .ThenByDescending(i => i.PrimaryNotes)
                .ThenBy(i => i.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            lists.Add(new AutoList(tag, tagged.Count,
                items.Where(i => !hiddenKeys.Contains(i.Plugin.Key)).ToList(),
                items.Where(i => hiddenKeys.Contains(i.Plugin.Key)).ToList()));
        }
        return lists;
    }

    /// <summary>
    /// 从一条笔记里删掉某款插件，供自动清单的“删除”用。备选里的直接去掉；当首选的那一步由第一个备选顶上，
    /// 没有备选就删掉这一步。笔记里没有这款插件时返回 null。
    /// </summary>
    public static IReadOnlyList<ChainStep>? WithoutPlugin(PluginChain chain, string pluginKey, IReadOnlyList<PluginRow> plugins)
    {
        bool Matches(ChainPick pick) => ChainReport.Resolve(pick, plugins).Plugin?.Key == pluginKey;
        var changed = false;
        var steps = new List<ChainStep>();
        foreach (var step in chain.Steps)
        {
            var alternates = step.Backups.Where(a => !Matches(a)).ToList();
            changed |= alternates.Count != step.Backups.Count;
            if (!Matches(step.Primary))
            {
                steps.Add(ChainStep.From(step.Primary, alternates));
                continue;
            }
            changed = true;
            if (alternates.Count > 0)
                steps.Add(ChainStep.From(alternates[0], alternates.Skip(1)));
        }
        return changed ? steps : null;
    }

    private sealed class Tally(PluginRow row)
    {
        public PluginRow Row { get; } = row;
        public int Primary { get; set; }
        public int Alternate { get; set; }
        public List<string> Notes { get; } = [];
    }

    private static int IndexOfPreset(string tag)
    {
        var index = MixNotes.PresetTags.ToList().FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    public static Dictionary<string, IReadOnlySet<string>> ReadHidden(string? json)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            foreach (var (tag, keys) in raw ?? [])
            {
                if (!string.IsNullOrWhiteSpace(tag) && keys is { Count: > 0 })
                    result[tag] = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (JsonException)
        {
            // 设置坏了就当没有隐藏，不影响使用。
        }
        return result;
    }

    public static string WriteHidden(IReadOnlyDictionary<string, IReadOnlySet<string>> hidden) =>
        JsonSerializer.Serialize(hidden
            .Where(h => h.Value.Count > 0)
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .ToDictionary(h => h.Key, h => h.Value.Order(StringComparer.Ordinal).ToList()));

    /// <summary>在某个标签的清单里隐藏或恢复一款插件，返回新的隐藏记录。</summary>
    public static Dictionary<string, IReadOnlySet<string>> SetHidden(IReadOnlyDictionary<string, IReadOnlySet<string>> hidden, string tag, string key, bool hide)
    {
        var result = hidden.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        var keys = result.GetValueOrDefault(tag)?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        if (hide)
            keys.Add(key);
        else
            keys.Remove(key);
        if (keys.Count > 0)
            result[tag] = keys;
        else
            result.Remove(tag);
        return result;
    }
}
