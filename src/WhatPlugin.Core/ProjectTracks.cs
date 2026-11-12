using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace WhatPlugin.Core;

/// <summary>
/// 工程里的一条轨道（通道）和它插入的效果器，按插槽顺序。
/// Id 只在这个工程里有意义，OutputId 和 SendIds 指向别的通道的 Id；OutputId 为空表示接主输出或读不出。
/// </summary>
public sealed record ProjectTrack(string Name, IReadOnlyList<TrackPlugin> Plugins)
{
    public string Id { get; init; } = "";
    public ChannelRole Role { get; init; } = ChannelRole.Track;
    public string? OutputId { get; init; }
    public IReadOnlyList<string> SendIds { get; init; } = [];
    public IReadOnlyList<TrackSidechain> Sidechains { get; init; } = [];
}

/// <summary>轨道上的一个插入效果。Stock 表示工程里能看出是宿主自带的（REAPER 的 Cockos、Studio One 的 Native）。</summary>
public sealed record TrackPlugin(string Name, string? Uid = null, bool Stock = false);

/// <summary>按轨道读取插入效果器，给“从工程导入轨道”用。和 Parse 一样只读。</summary>
public static partial class ProjectParser
{
    private const long MaxTrackFileBytes = 1024L * 1024 * 1024;

    /// <exception cref="InvalidDataException">文件不是预期的格式。</exception>
    public static IReadOnlyList<ProjectTrack> ParseTracks(string path, CancellationToken cancel = default)
    {
        var host = HostOf(path) ?? throw new InvalidDataException("不支持的工程格式");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return ParseTracks(host, stream, cancel);
    }

    public static IReadOnlyList<ProjectTrack> ParseTracks(ProjectHost host, Stream stream, CancellationToken cancel = default) => host switch
    {
        ProjectHost.Cubase => CubaseTracks(stream, cancel),
        ProjectHost.StudioOne => StudioOneTracks(stream, cancel),
        ProjectHost.Reaper => ReaperTracks(stream, cancel),
        ProjectHost.Ableton => AbletonTracks(stream, cancel),
        _ => throw new InvalidDataException("不支持的工程格式"),
    };

    // ---------- Cubase / Nuendo ----------
    // 真实工程（Cubase 10 到 14）里，每个调音台通道依次是：
    //   名称对象 "Name" { "String" = 通道名 } … "InsertFolder" { "Slot" { 插件 } … }
    // 下一个通道名出现之前的插入插件都属于这个通道。音源槽单独标记，照旧排除。

    private static readonly byte[] CubaseChannelName =
        "\0\0\0\u0005Name\0\0\u0002\0\u0006\0\0\0\u0001\0\0\0\u0007String\0\0\b"u8.ToArray();
    private static readonly byte[] CubaseInsertFolder = "\0\0\0\rInsertFolder\0"u8.ToArray();

    private static IReadOnlyList<ProjectTrack> CubaseTracks(Stream stream, CancellationToken cancel)
    {
        var data = ReadAll(stream);
        if (data.Length < 16 || !(data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || data.AsSpan(0, 4).SequenceEqual("RIF2"u8)))
            throw new InvalidDataException("不是 Cubase 工程文件");

        var tracks = new List<ProjectTrack>();
        var starts = new List<int>();
        string? name = null;
        List<TrackPlugin>? plugins = null;
        var inInserts = false;
        var start = 0;

        void Flush()
        {
            if (name != null)
            {
                tracks.Add(new ProjectTrack(name, plugins!));
                starts.Add(start);
            }
        }

        var position = 0;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var rest = data.AsSpan(position);
            var nextName = rest.IndexOf(CubaseChannelName);
            var nextFolder = rest.IndexOf(CubaseInsertFolder);
            var nextPlugin = rest.IndexOf(CubaseMarker);
            var next = Min(nextName, nextFolder, nextPlugin);
            if (next < 0)
                break;
            var hit = position + next;

            if (next == nextName)
            {
                var valueAt = hit + CubaseChannelName.Length;
                if (TryReadValue(data, ref valueAt, out var channel) && !string.IsNullOrWhiteSpace(channel))
                {
                    Flush();
                    name = channel.Trim();
                    plugins = [];
                    inInserts = false;
                    start = hit;
                }
                position = hit + CubaseChannelName.Length;
            }
            else if (next == nextFolder)
            {
                inInserts = name != null;
                position = hit + CubaseInsertFolder.Length;
            }
            else
            {
                if (inInserts && ReadCubasePlugin(data, hit) is { } plugin)
                    plugins!.Add(new TrackPlugin(plugin.Name, plugin.Uid));
                position = hit + CubaseMarker.Length;
            }
        }
        Flush();
        var routings = CubaseRoutings(data, starts);
        for (var i = 0; i < tracks.Count; i++)
            tracks[i] = WithRouting(tracks[i], routings[i]);
        return MergeAdjacent(tracks);
    }

    private static ProjectTrack WithRouting(ProjectTrack track, ChannelRouting routing) => track with
    {
        Id = routing.Id,
        Role = routing.Role ?? track.Role,
        OutputId = routing.OutputId,
        SendIds = routing.SendIds,
        Sidechains = routing.Sidechains ?? [],
    };

    /// <summary>
    /// 乐器轨在工程里出现两次（乐器部分和音频通道），名字相同、插件只在其中一条上。
    /// 相邻同名、插件相同或有一条为空的合成一条。
    /// </summary>
    private static List<ProjectTrack> MergeAdjacent(List<ProjectTrack> tracks)
    {
        var merged = new List<ProjectTrack>();
        foreach (var track in tracks)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Name == track.Name
                && (last.Plugins.Count == 0 || track.Plugins.Count == 0 || last.Plugins.SequenceEqual(track.Plugins)))
            {
                // 插件取有插件的那条，路由两条合起来（乐器部分没有路由，音频通道上有）。
                var keep = last.Plugins.Count == 0 ? track : last;
                var other = ReferenceEquals(keep, track) ? last : track;
                merged[^1] = keep with
                {
                    Id = keep.Id.Length > 0 ? keep.Id : other.Id,
                    Role = keep.Role != ChannelRole.Track ? keep.Role : other.Role,
                    OutputId = keep.OutputId ?? other.OutputId,
                    SendIds = keep.SendIds.Concat(other.SendIds).Distinct().ToList(),
                    Sidechains = keep.Sidechains.Concat(other.Sidechains).Distinct().ToList(),
                };
                continue;
            }
            merged.Add(track);
        }
        return merged;
    }

    private static int Min(params int[] values)
    {
        var best = -1;
        foreach (var value in values)
        {
            if (value >= 0 && (best < 0 || value < best))
                best = value;
        }
        return best;
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaxTrackFileBytes)
            throw new InvalidDataException("工程文件太大");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // ---------- Studio One ----------
    // Devices/audiomixer.xml：<AudioTrackChannel label="轨道名"> 里的 <Attributes x:id="Inserts">，
    // 每个插件有一个 x:id="classInfo" 的元素，带 name 和 subCategory（Studio One 5.1 真实工程）。
    // 路由（还没有真实工程核对）：通道的 uniqueID；通道里 x:id="destination" 的连接带 objectID="{目标 uniqueID}/…"，
    // 在 x:id="Sends" 里面的是发送，其余的是主输出。通道类型看元素名：…Group…/…Bus… 是总线，
    // …Effect…/…FX… 是效果通道，…Output…/…Main… 是主输出。

    private static IReadOnlyList<ProjectTrack> StudioOneTracks(Stream stream, CancellationToken cancel)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("不是 Studio One 工程文件");
        }
        using (zip)
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("Devices/audiomixer.xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("不是 Studio One 工程文件");
            if (entry.Length > MaxArchiveEntryBytes)
                throw new InvalidDataException("工程文件太大");
            using var content = entry.Open();
            return ReadStudioOneTracks(content, cancel);
        }
    }

    private static IReadOnlyList<ProjectTrack> ReadStudioOneTracks(Stream content, CancellationToken cancel)
    {
        var tracks = new List<ProjectTrack>();
        using var reader = new XmlTextReader(content) { Namespaces = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        string? name = null;
        List<TrackPlugin>? plugins = null;
        var channelDepth = -1;
        var insertsDepth = -1;
        var sendsDepth = -1;
        string id = "";
        string? output = null;
        List<string> sends = [];
        var role = ChannelRole.Track;

        ProjectTrack Current() => new(name!, plugins!) { Id = id, Role = role, OutputId = output, SendIds = sends };
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == insertsDepth)
                        insertsDepth = -1;
                    if (reader.Depth == sendsDepth)
                        sendsDepth = -1;
                    if (reader.Depth == channelDepth)
                    {
                        tracks.Add(Current());
                        channelDepth = -1;
                    }
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                cancel.ThrowIfCancellationRequested();

                if (channelDepth < 0)
                {
                    var label = reader.GetAttribute("label");
                    if (reader.LocalName.EndsWith("Channel", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(label))
                    {
                        name = label.Trim();
                        plugins = [];
                        id = StudioOneGuid(reader.GetAttribute("uniqueID")) ?? "";
                        output = null;
                        sends = [];
                        role = StudioOneRole(reader.LocalName);
                        if (reader.IsEmptyElement)
                            tracks.Add(Current());
                        else
                            channelDepth = reader.Depth;
                    }
                    continue;
                }

                var xid = reader.GetAttribute("x:id");
                if (xid == "Sends" && !reader.IsEmptyElement && sendsDepth < 0)
                    sendsDepth = reader.Depth;
                if (string.Equals(xid, "destination", StringComparison.OrdinalIgnoreCase)
                    && StudioOneGuid(reader.GetAttribute("objectID")) is { } target && target != id)
                {
                    if (sendsDepth >= 0)
                    {
                        if (!sends.Contains(target))
                            sends.Add(target);
                    }
                    else
                    {
                        output ??= target;
                    }
                    continue;
                }
                if (insertsDepth < 0)
                {
                    if (xid == "Inserts" && !reader.IsEmptyElement)
                        insertsDepth = reader.Depth;
                    continue;
                }
                if (xid != "classInfo")
                    continue;
                var plugin = reader.GetAttribute("name");
                var category = reader.GetAttribute("subCategory") ?? "";
                if (string.IsNullOrWhiteSpace(plugin) || IsInstrumentCategory(category + "|" + reader.GetAttribute("category")))
                    continue;
                plugins!.Add(new TrackPlugin(plugin.Trim(), reader.GetAttribute("classID"), !IsStudioOneThirdParty(category)));
            }
        }
        catch (XmlException e)
        {
            throw new InvalidDataException("工程内容无法读取：" + e.Message);
        }
        // 只留指向工程里存在的通道的连线。
        var known = tracks.Where(t => t.Id.Length > 0).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tracks.Select(t => t with
        {
            OutputId = t.OutputId != null && known.Contains(t.OutputId) ? t.OutputId : null,
            SendIds = t.SendIds.Where(known.Contains).ToList(),
        }).ToList();
    }

    /// <summary>“{GUID}”或“{GUID}/Input”里的 GUID（大写、不带花括号）。</summary>
    private static string? StudioOneGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var start = value.IndexOf('{');
        var end = value.IndexOf('}', start + 1);
        return start >= 0 && end > start ? value[(start + 1)..end].ToUpperInvariant() : null;
    }

    private static ChannelRole StudioOneRole(string element) =>
        element.Contains("Group", StringComparison.OrdinalIgnoreCase) || element.Contains("Bus", StringComparison.OrdinalIgnoreCase) ? ChannelRole.Group
        : element.Contains("Effect", StringComparison.OrdinalIgnoreCase) || element.Contains("FX", StringComparison.Ordinal) ? ChannelRole.Effect
        : element.Contains("Output", StringComparison.OrdinalIgnoreCase) || element.Contains("Main", StringComparison.OrdinalIgnoreCase) ? ChannelRole.Master
        : ChannelRole.Track;

    // ---------- REAPER ----------
    // <TRACK> 里的 NAME 是轨道名，<FXCHAIN> 里是插入效果。<FXCHAIN_REC>（录音输入效果）和 <TAKEFX>（片段效果）不算。
    // 主控轨的效果在 <MASTERFXLIST> 里。
    // 路由：ISBUS 1 1 开始一个文件夹（这条轨道是文件夹轨，相当于编组），ISBUS 2 -n 结束 n 层；
    // MAINSEND 1 … 表示送到上一级（文件夹轨或主控）。AUXRECV 源轨序号(从 0 起) 模式 音量 声像 静音 … 写在接收方，
    // 静音的不算。接收发送的轨道算效果轨。

    private static IReadOnlyList<ProjectTrack> ReaperTracks(Stream stream, CancellationToken cancel)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var first = reader.ReadLine();
        if (first == null || !first.TrimStart().StartsWith("<REAPER_PROJECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不是 REAPER 工程文件");

        var tracks = new List<ProjectTrack>();
        var master = new List<TrackPlugin>();
        var blocks = new List<string> { "REAPER_PROJECT" };
        string? name = null;
        List<TrackPlugin>? plugins = null;
        var number = 0;
        var folders = new Stack<string>();
        var parents = new Dictionary<string, string?>();
        var mainSend = new Dictionary<string, bool>();
        var receives = new List<(int Source, string Target)>();
        var folderChange = 0;
        while (reader.ReadLine() is { } raw)
        {
            cancel.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.StartsWith('<'))
            {
                var tag = line[1..].Split(' ', 2)[0].ToUpperInvariant();
                var parent = blocks[^1];
                if (tag == "TRACK" && blocks.Count == 1)
                {
                    number++;
                    name = null;
                    plugins = [];
                    folderChange = 0;
                    parents["t" + number] = folders.Count > 0 ? folders.Peek() : null;
                    mainSend["t" + number] = true;
                }
                else if (parent is "FXCHAIN" or "MASTERFXLIST" && blocks.Count >= 2)
                {
                    var inTrack = parent == "FXCHAIN" && blocks.Count == 3 && blocks[1] == "TRACK";
                    var inMaster = parent == "MASTERFXLIST" && blocks.Count == 2;
                    if ((inTrack || inMaster) && ReaperPlugin().Match(raw) is { Success: true } match)
                    {
                        var kind = match.Groups["kind"].Value;
                        if (!kind.EndsWith('i') && !kind.EndsWith('I'))
                        {
                            var stock = match.Groups["vendor"].Value.Equals("Cockos", StringComparison.OrdinalIgnoreCase);
                            (inTrack ? plugins! : master).Add(new TrackPlugin(match.Groups["name"].Value.Trim(), null, stock));
                        }
                    }
                }
                blocks.Add(tag);
                continue;
            }
            if (line == ">")
            {
                if (blocks.Count > 1)
                {
                    if (blocks.Count == 2 && blocks[1] == "TRACK" && plugins != null)
                    {
                        tracks.Add(new ProjectTrack(string.IsNullOrWhiteSpace(name) ? $"Track {number}" : name, plugins) { Id = "t" + number });
                        plugins = null;
                        if (folderChange > 0)
                            folders.Push("t" + number);
                        for (var i = 0; i < -folderChange && folders.Count > 0; i++)
                            folders.Pop();
                    }
                    blocks.RemoveAt(blocks.Count - 1);
                }
                continue;
            }
            if (blocks.Count != 2 || blocks[1] != "TRACK")
                continue;
            if (line.StartsWith("NAME ", StringComparison.Ordinal))
                name = Unquote(line[5..].Trim());
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields[0] == "ISBUS" && fields.Length >= 3 && int.TryParse(fields[2], out var change))
                folderChange = change;
            else if (fields[0] == "MAINSEND" && fields.Length >= 2)
                mainSend["t" + number] = fields[1] != "0";
            else if (fields[0] == "AUXRECV" && fields.Length >= 6 && int.TryParse(fields[1], out var source) && fields[5] != "1")
                receives.Add((source + 1, "t" + number));
        }
        if (master.Count > 0)
            tracks.Add(new ProjectTrack("Master", master) { Id = "master", Role = ChannelRole.Master });

        var hasMaster = master.Count > 0;
        var groups = parents.Values.OfType<string>().ToHashSet();
        var effects = receives.Select(r => r.Target).ToHashSet();
        return tracks.Select(t => t.Id == "master" ? t : t with
        {
            Role = groups.Contains(t.Id) ? ChannelRole.Group : effects.Contains(t.Id) ? ChannelRole.Effect : ChannelRole.Track,
            OutputId = !mainSend.GetValueOrDefault(t.Id, true) ? null : parents.GetValueOrDefault(t.Id) ?? (hasMaster ? "master" : null),
            SendIds = receives.Where(r => "t" + r.Source == t.Id && r.Target != t.Id).Select(r => r.Target).Distinct().ToList(),
        }).ToList();
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && (text[0] is '"' or '\'' or '`') && text[^1] == text[0] ? text[1..^1] : text;

    // ---------- Ableton Live ----------
    // <AudioTrack>/<MidiTrack>/<GroupTrack>/<ReturnTrack>/<MasterTrack> 下：<Name><EffectiveName Value="…"/>，
    // 设备在 <DeviceChain> 里，第三方插件是 <PluginDevice>，名字在 Vst3PluginInfo/Name 或 VstPluginInfo/PlugName。
    // 工程里没有可靠的乐器标记，导入时再按插件库判断。
    // 路由（还没有真实工程核对）：轨道的 Id 属性；<TrackGroupId Value> 是所在的编组轨；DeviceChain/AudioOutputRouting/Target 的 Value
    // 是 AudioOut/GroupTrack、AudioOut/Master（Main）或 AudioOut/Track.{Id}/…；
    // DeviceChain/Mixer/Sends 下第 n 个 TrackSendHolder 对应第 n 条返回轨，Send/Manual 大于最小值（约 -70 dB）才算开着。

    private static readonly HashSet<string> AbletonTrackTags = ["AudioTrack", "MidiTrack", "GroupTrack", "ReturnTrack", "MasterTrack", "MainTrack"];

    private static IReadOnlyList<ProjectTrack> AbletonTracks(Stream stream, CancellationToken cancel)
    {
        var tracks = new List<ProjectTrack>();
        var raw = new List<(ProjectTrack Track, string? Group, string? Target, List<bool> Sends)>();
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = XmlReader.Create(gzip, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var sawRoot = false;
            string? name = null;
            string? kind = null;
            List<TrackPlugin>? plugins = null;
            var trackDepth = -1;
            string? nameElement = null;
            var infoDepth = 0;
            string id = "";
            string? group = null;
            string? target = null;
            var inOutputRouting = false;
            List<bool> sends = [];
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == trackDepth)
                    {
                        var role = kind switch
                        {
                            "Master" => ChannelRole.Master,
                            "GroupTrack" => ChannelRole.Group,
                            "ReturnTrack" => ChannelRole.Effect,
                            _ => ChannelRole.Track,
                        };
                        raw.Add((new ProjectTrack(name ?? kind!, plugins!) { Id = id, Role = role }, group, target, sends));
                        trackDepth = -1;
                    }
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                cancel.ThrowIfCancellationRequested();
                if (!sawRoot)
                {
                    if (reader.LocalName != "Ableton")
                        throw new InvalidDataException("不是 Ableton Live 工程文件");
                    sawRoot = true;
                    continue;
                }
                if (trackDepth < 0)
                {
                    if (AbletonTrackTags.Contains(reader.LocalName) && !reader.IsEmptyElement)
                    {
                        trackDepth = reader.Depth;
                        kind = reader.LocalName is "MasterTrack" or "MainTrack" ? "Master" : reader.LocalName;
                        name = null;
                        plugins = [];
                        id = kind == "Master" ? "master" : reader.GetAttribute("Id") ?? "";
                        group = null;
                        target = null;
                        inOutputRouting = false;
                        sends = [];
                    }
                    continue;
                }
                // DeviceChain 下先有 AudioInputRouting/Target（输入），只认 AudioOutputRouting 里的 Target。
                if (reader.Depth == trackDepth + 2)
                    inOutputRouting = reader.LocalName == "AudioOutputRouting";
                switch (reader.LocalName)
                {
                    case "TrackGroupId" when reader.Depth == trackDepth + 1:
                        group = reader.GetAttribute("Value");
                        break;
                    case "Target" when reader.Depth == trackDepth + 3 && inOutputRouting && target == null:
                        target = reader.GetAttribute("Value");
                        break;
                    case "TrackSendHolder" when reader.Depth == trackDepth + 4:
                        sends.Add(false);
                        break;
                    case "Manual" when reader.Depth == trackDepth + 6 && sends.Count > 0:
                        sends[^1] = double.TryParse(reader.GetAttribute("Value"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var level)
                            && level > AbletonSendOff;
                        break;
                    case "EffectiveName" when name == null && reader.Depth == trackDepth + 2:
                        var value = reader.GetAttribute("Value");
                        if (!string.IsNullOrWhiteSpace(value))
                            name = value.Trim();
                        break;
                    case "Vst3PluginInfo" or "AuPluginInfo":
                        nameElement = "Name";
                        infoDepth = reader.Depth;
                        break;
                    case "VstPluginInfo":
                        nameElement = "PlugName";
                        infoDepth = reader.Depth;
                        break;
                    default:
                        if (nameElement != null && reader.Depth == infoDepth + 1 && reader.LocalName == nameElement)
                        {
                            var plugin = reader.GetAttribute("Value");
                            if (!string.IsNullOrWhiteSpace(plugin))
                                plugins!.Add(new TrackPlugin(plugin.Trim()));
                            nameElement = null;
                        }
                        break;
                }
            }
        }
        catch (XmlException e)
        {
            throw new InvalidDataException("工程内容无法读取：" + e.Message);
        }
        var returns = raw.Where(r => r.Track.Role == ChannelRole.Effect).Select(r => r.Track.Id).ToList();
        var ids = raw.Select(r => r.Track.Id).Where(i => i.Length > 0).ToHashSet();
        foreach (var (track, group, target, sends) in raw)
        {
            string? output = null;
            if (target != null && track.Role != ChannelRole.Master)
            {
                if (target.StartsWith("AudioOut/GroupTrack", StringComparison.OrdinalIgnoreCase) && group is { } g && g != "-1")
                    output = g;
                else if (target.StartsWith("AudioOut/Master", StringComparison.OrdinalIgnoreCase) || target.StartsWith("AudioOut/Main", StringComparison.OrdinalIgnoreCase))
                    output = "master";
                else if (target.StartsWith("AudioOut/Track.", StringComparison.OrdinalIgnoreCase))
                    output = target["AudioOut/Track.".Length..].Split('/')[0];
            }
            var sendIds = sends.Select((on, i) => on && i < returns.Count ? returns[i] : null).OfType<string>().Where(s => s != track.Id).ToList();
            tracks.Add(track with { OutputId = output != null && ids.Contains(output) ? output : null, SendIds = sendIds });
        }
        return tracks;
    }

    /// <summary>Ableton 发送旋钮最小值（-inf）存为 0.0003162277571。</summary>
    private const double AbletonSendOff = 0.000317;
}

/// <summary>把工程轨道变成混音笔记的步骤。纯计算，不碰文件和数据库。</summary>
public static class TrackImport
{
    /// <summary>
    /// 已安装的对到插件库里那一款；对不上的按工程里的名字记下，笔记里会标成未安装。
    /// 音源不进笔记；宿主自带的插件按需要跳过。
    /// </summary>
    public static IReadOnlyList<ChainStep> Steps(ProjectHost host, ProjectTrack track, PluginNameMatcher matcher, bool skipStock)
    {
        var steps = new List<ChainStep>();
        foreach (var use in track.Plugins)
        {
            var plugin = matcher.Find(use.Name);
            if (plugin?.Kind == PluginKind.Instrument)
                continue;
            if (plugin == null && skipStock && (use.Stock || StockPlugins.Contains(host, use.Name)))
                continue;
            steps.Add(plugin is { IsMissing: false }
                ? new ChainStep(plugin.Key, plugin.Name)
                : new ChainStep(plugin?.Key ?? "", PluginNameMatcher.CleanName(use.Name)));
        }
        return steps;
    }

    /// <summary>笔记的来源说明："工程名 · 轨道名"。</summary>
    public static string Source(string projectPath, string trackName) =>
        Path.GetFileNameWithoutExtension(projectPath) + " · " + trackName;
}
