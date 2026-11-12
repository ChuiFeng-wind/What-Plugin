using System.Buffers.Binary;
using System.Text;

namespace WhatPlugin.Core;

/// <summary>调音台通道的类型。编组在 Studio One 里叫总线；效果轨在 Ableton 里叫返回轨。</summary>
public enum ChannelRole
{
    Track = 0,
    Group = 1,
    Effect = 2,
    Master = 3,
}

/// <summary>通道之间的连线：主输出（每个通道最多一条）、发送，或侧链（把信号送进目标通道上某个插件的侧链输入）。</summary>
public enum RouteKind
{
    Output = 0,
    Send = 1,
    Sidechain = 2,
}

/// <summary>侧链：信号送到 TargetId 通道上名为 Plugin 的插件的侧链输入。</summary>
public sealed record TrackSidechain(string TargetId, string Plugin);

/// <summary>工程里读出的一个通道的路由，Id 只在这个工程里有意义。</summary>
internal sealed record ChannelRouting(string Id, string? OutputId, IReadOnlyList<string> SendIds, ChannelRole? Role = null, IReadOnlyList<TrackSidechain>? Sidechains = null);

public static partial class ProjectParser
{
    // ---------- Cubase / Nuendo 的路由 ----------
    // 在 Cubase 10 到 14 的真实工程里核对过。每个通道（从通道名到下一个通道名）里：
    //   OwnInputBus { Name, Bus UID }  这个通道自己的输入总线；
    //   OutputBusValue { Value }        主输出接到哪条总线（目标通道的 Bus UID），0 表示没有输出（输出总线本身）；
    //   SendFolder { … }               8 个发送依次排列：[On] Volume { Value } Output { Value }，
    //                                  没开的发送没有 On 记录；Output 为 0 表示这个发送没接。
    //   插件里的 "Sidechain Bus Id 0" { 编号 }  这个插件的侧链输入。发送的 Output 等于这个编号时，
    //                                  就是把信号送进这个插件的侧链（例如人声压混响上的 Pro-C 2）。
    // 目标找不到对应通道的（例如已删除的效果轨）不算。

    private static readonly byte[] CubaseOwnInputBus = Record("OwnInputBus");
    private static readonly byte[] CubaseBusUid = Record("Bus UID");
    private static readonly byte[] CubaseOutputBusValue = Record("OutputBusValue");
    private static readonly byte[] CubaseSendFolder = Record("SendFolder");
    private static readonly byte[] CubaseFoldbackSendFolder = Record("foldbackSendFolder");
    private static readonly byte[] CubaseVolume = Record("Volume");
    private static readonly byte[] CubaseOutput = Record("Output");
    private static readonly byte[] CubaseValue = Record("Value");
    private static readonly byte[] CubaseOn = Record("On");
    private static readonly byte[][] CubaseSidechainBus = [.. Enumerable.Range(0, 4).Select(i => Record($"Sidechain Bus Id {i}"))];

    private static byte[] Record(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        var record = new byte[4 + bytes.Length + 1];
        BinaryPrimitives.WriteInt32BigEndian(record, bytes.Length + 1);
        bytes.CopyTo(record, 4);
        return record;
    }

    /// <summary>[start, end) 里第一个 record 的位置，没有返回 -1。</summary>
    private static int Find(byte[] data, int start, int end, byte[] record)
    {
        if (start < 0 || start >= end)
            return -1;
        var hit = data.AsSpan(start, Math.Min(end, data.Length) - start).IndexOf(record);
        return hit < 0 ? -1 : start + hit;
    }

    /// <summary>record 后面跟着的整数（类型 0x0001，8 字节大端）。</summary>
    private static long? IntAfter(byte[] data, int recordAt, byte[] record)
    {
        var at = recordAt + record.Length;
        if (at + 10 > data.Length || data[at] != 0 || data[at + 1] != 1)
            return null;
        return BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at + 2, 8));
    }

    /// <summary>在 [start, start + window) 里找 name 记录，再在它后面 60 字节内找 Value 读整数。</summary>
    private static long? ValueIn(byte[] data, int start, int window, byte[] name)
    {
        var at = Find(data, start, start + window, name);
        if (at < 0)
            return null;
        var value = Find(data, at + name.Length, at + name.Length + 60, CubaseValue);
        return value < 0 ? null : IntAfter(data, value, CubaseValue);
    }

    private sealed class CubaseChannel(int start, int end)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public long Own { get; set; }
        public HashSet<long> Buses { get; } = [];
        public long? Output { get; set; }
        public List<long> SendsOn { get; } = [];
        public List<long> SendsAll { get; } = [];
        public List<(long Bus, string Plugin)> SidechainInputs { get; } = [];
    }

    private static CubaseChannel ReadCubaseChannel(byte[] data, int start, int end)
    {
        var channel = new CubaseChannel(start, end);
        var own = Find(data, start, end, CubaseOwnInputBus);
        if (own < 0)
            return channel;
        var uid = Find(data, own, Math.Min(end, own + 400), CubaseBusUid);
        if (uid >= 0 && IntAfter(data, uid, CubaseBusUid) is { } ownUid)
            channel.Own = ownUid;
        for (var at = Find(data, start, end, CubaseBusUid); at >= 0; at = Find(data, at + 1, end, CubaseBusUid))
        {
            if (IntAfter(data, at, CubaseBusUid) is { } bus and > 0)
                channel.Buses.Add(bus);
        }
        var output = Find(data, start, end, CubaseOutputBusValue);
        if (output >= 0)
        {
            var value = Find(data, output, Math.Min(end, output + 60), CubaseValue);
            channel.Output = value < 0 ? null : IntAfter(data, value, CubaseValue);
        }

        foreach (var record in CubaseSidechainBus)
        {
            for (var at = Find(data, start, end, record); at >= 0; at = Find(data, at + 1, end, record))
            {
                if (IntAfter(data, at, record) is not { } bus || bus <= 0)
                    continue;
                var marker = data.AsSpan(start, at - start).LastIndexOf(CubaseMarker);
                if (marker >= 0 && ReadCubasePlugin(data, start + marker) is { } plugin)
                    channel.SidechainInputs.Add((bus, plugin.Name));
            }
        }

        var folder = Find(data, start, end, CubaseSendFolder);
        if (folder >= 0)
        {
            var folderEnd = Find(data, folder, end, CubaseFoldbackSendFolder);
            if (folderEnd < 0)
                folderEnd = end;
            var previous = folder;
            for (var volume = Find(data, folder, folderEnd, CubaseVolume); volume >= 0; volume = Find(data, volume + 1, folderEnd, CubaseVolume))
            {
                var on = Find(data, previous, volume, CubaseOn);
                var enabled = on >= 0 && IntAfter(data, on, CubaseOn) == 1;
                var target = ValueIn(data, volume, 200, CubaseOutput);
                var outputAt = Find(data, volume, Math.Min(folderEnd, volume + 200), CubaseOutput);
                previous = outputAt >= 0 ? outputAt : volume;
                if (target is not { } bus || bus == 0)
                    continue;
                channel.SendsAll.Add(bus);
                if (enabled)
                    channel.SendsOn.Add(bus);
            }
        }
        return channel;
    }

    /// <summary>
    /// 按通道读出路由，算出每个通道的类型：被别的通道主输出接入的是编组，被发送接入的是效果轨，
    /// 没有输出、但有通道输出到它的是主输出。
    /// </summary>
    private static List<ChannelRouting> CubaseRoutings(byte[] data, IReadOnlyList<int> starts)
    {
        var channels = new List<CubaseChannel>();
        for (var i = 0; i < starts.Count; i++)
            channels.Add(ReadCubaseChannel(data, starts[i], i + 1 < starts.Count ? starts[i + 1] : data.Length));

        // 总线编号 → 通道。先认自己的输入总线，再认通道里的其他总线。
        var owner = new Dictionary<long, int>();
        for (var i = 0; i < channels.Count; i++)
        {
            if (channels[i].Own > 0)
                owner.TryAdd(channels[i].Own, i);
        }
        for (var i = 0; i < channels.Count; i++)
        {
            if (channels[i].Own == 0)
                continue;
            foreach (var bus in channels[i].Buses)
                owner.TryAdd(bus, i);
        }
        int? Target(long bus) => owner.TryGetValue(bus, out var index) ? index : null;
        var sidechain = new Dictionary<long, (int Index, string Plugin)>();
        for (var i = 0; i < channels.Count; i++)
        {
            if (channels[i].Own == 0)
                continue;
            foreach (var (bus, plugin) in channels[i].SidechainInputs)
                sidechain.TryAdd(bus, (i, plugin));
        }

        var outputTargets = new HashSet<int>();
        var sendTargets = new HashSet<int>();
        for (var i = 0; i < channels.Count; i++)
        {
            if (channels[i].Output is { } output && output != 0 && Target(output) is { } target && target != i)
                outputTargets.Add(target);
            foreach (var send in channels[i].SendsAll)
            {
                if (Target(send) is { } t && t != i)
                    sendTargets.Add(t);
            }
        }

        var routings = new List<ChannelRouting>();
        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            var id = channel.Own > 0 ? "bus:" + channel.Own : "ch:" + i;
            ChannelRole? role = null;
            if (channel.Own > 0)
            {
                if (channel.Output == 0)
                    role = outputTargets.Contains(i) ? ChannelRole.Master : ChannelRole.Track;
                else if (outputTargets.Contains(i))
                    role = ChannelRole.Group;
                else if (sendTargets.Contains(i))
                    role = ChannelRole.Effect;
            }
            string? Id(long bus) => Target(bus) is { } t && t != i && channels[t].Own > 0 ? "bus:" + channels[t].Own : null;
            var outputId = channel.Output is { } o && o != 0 ? Id(o) : null;
            var sends = channel.SendsOn.Select(Id).OfType<string>().Distinct().ToList();
            var sidechains = channel.SendsOn
                .Where(bus => Target(bus) == null && sidechain.TryGetValue(bus, out var input) && input.Index != i)
                .Select(bus => new TrackSidechain("bus:" + channels[sidechain[bus].Index].Own, sidechain[bus].Plugin))
                .Distinct()
                .ToList();
            routings.Add(new ChannelRouting(id, outputId, sends, role, sidechains));
        }
        return routings;
    }
}

/// <summary>导入轨道时用到的路由计算。纯计算。</summary>
public static class TrackRouting
{
    /// <summary>这个通道主输出、发送和侧链接到的通道（在 all 里能找到的）。</summary>
    public static IEnumerable<ProjectTrack> Destinations(ProjectTrack track, IReadOnlyList<ProjectTrack> all)
    {
        var ids = track.SendIds.Concat(track.Sidechains.Select(s => s.TargetId)).Prepend(track.OutputId ?? "")
            .Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        return all.Where(t => t.Id.Length > 0 && ids.Contains(t.Id) && !ReferenceEquals(t, track));
    }

    /// <summary>
    /// 勾选的轨道加上它们（一层层）输出和发送到的编组、效果轨，按工程里的顺序。
    /// 主输出通道只有带插件（includeMaster 判断）时才带上，否则笔记里显示为“主输出”。
    /// </summary>
    public static IReadOnlyList<ProjectTrack> WithDestinations(IReadOnlyList<ProjectTrack> all, IEnumerable<ProjectTrack> picked, Func<ProjectTrack, bool> includeMaster)
    {
        var chosen = new HashSet<ProjectTrack>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<ProjectTrack>(picked);
        while (queue.TryDequeue(out var track))
        {
            if (!chosen.Add(track))
                continue;
            foreach (var next in Destinations(track, all))
            {
                if (next.Role != ChannelRole.Master || includeMaster(next))
                    queue.Enqueue(next);
            }
        }
        return all.Where(chosen.Contains).ToList();
    }

    /// <summary>导入列表里的一句说明，例如“→ Lead Vox（编组） · 发送 → Plate、Delay”。没有就返回空。</summary>
    public static string Describe(ProjectTrack track, IReadOnlyList<ProjectTrack> all)
    {
        var parts = new List<string>();
        var output = track.OutputId == null ? null : all.FirstOrDefault(t => t.Id == track.OutputId && !ReferenceEquals(t, track));
        if (output != null && output.Role != ChannelRole.Master)
            parts.Add($"→ {output.Name}（{RoleText(output.Role)}）");
        var sends = all.Where(t => !ReferenceEquals(t, track) && t.Id.Length > 0 && track.SendIds.Contains(t.Id)).Select(t => t.Name).ToList();
        if (sends.Count > 0)
            parts.Add("发送 → " + string.Join("、", sends));
        var sidechains = track.Sidechains
            .Select(s => (Target: all.FirstOrDefault(t => !ReferenceEquals(t, track) && t.Id == s.TargetId), s.Plugin))
            .Where(s => s.Target != null)
            .Select(s => $"{s.Target!.Name}（{s.Plugin}）")
            .ToList();
        if (sidechains.Count > 0)
            parts.Add("侧链 → " + string.Join("、", sidechains));
        return string.Join(" · ", parts);
    }

    public static string RoleText(ChannelRole role) => role switch
    {
        ChannelRole.Group => "编组",
        ChannelRole.Effect => "效果轨",
        ChannelRole.Master => "主输出",
        _ => "轨道",
    };
}
