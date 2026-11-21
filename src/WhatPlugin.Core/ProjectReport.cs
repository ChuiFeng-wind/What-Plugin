using System.Text.RegularExpressions;

namespace WhatPlugin.Core;

public enum ProjectScanStatus { Ok, Failed }

/// <summary>数据库里的一个工程文件。一首歌通常有多个版本，每个版本一条。</summary>
public sealed record ProjectFileRecord(
    long Id,
    string Path,
    ProjectHost Host,
    long Size,
    DateTime ModifiedUtc,
    ProjectScanStatus Status,
    string? Error,
    int ParserVersion,
    IReadOnlyList<ProjectPluginUse> Plugins,
    bool OwnFolder = false);

public enum ProjectPluginState { Installed, Missing, Stock, Ignored }

public sealed record ProjectPluginLine(string Name, int Instances, ProjectPluginState State, PluginRow? Plugin);

/// <summary>一首歌：同一文件夹里同名的多个版本合成一条。缺失情况只看最新版本。</summary>
public sealed record ProjectEntry(
    string Key,
    string Name,
    string Path,
    string Folder,
    ProjectHost Host,
    DateTime ModifiedUtc,
    int Versions,
    ProjectScanStatus Status,
    string? Error,
    IReadOnlyList<ProjectPluginLine> Plugins)
{
    public int MissingCount => Plugins.Count(p => p.State == ProjectPluginState.Missing);
    public int EffectCount => Plugins.Count(p => p.State is ProjectPluginState.Installed or ProjectPluginState.Missing);
}

public sealed record MissingPluginSummary(string Name, IReadOnlyList<ProjectEntry> Projects);

public sealed record PluginUsage(int Projects, int Instances, DateTime LastUsedUtc, IReadOnlyList<string> ProjectNames);

public sealed record ProjectReportResult(
    IReadOnlyList<ProjectEntry> Projects,
    IReadOnlyList<MissingPluginSummary> Missing,
    IReadOnlyDictionary<string, PluginUsage> Usage);

public enum IdleReason { NeverUsed, LongUnused }

public sealed record IdlePlugin(PluginRow Plugin, IdleReason Reason, PluginUsage? Usage);

/// <summary>把工程里的名称对到已安装的插件。名称写法在不同格式和版本之间并不一致。</summary>
public sealed partial class PluginNameMatcher
{
    private readonly Dictionary<string, PluginRow> _exact = new();
    private readonly Dictionary<string, PluginRow?> _withoutVendor = new();

    public PluginNameMatcher(IEnumerable<PluginRow> plugins)
    {
        // 同名时优先取没有缺失的那条。
        foreach (var plugin in plugins.OrderBy(p => p.IsMissing))
        {
            var key = Normalizer.Name(plugin.Name);
            _exact.TryAdd(key, plugin);

            // "Pro-Q 3" 和 "FabFilter Pro-Q 3"：同一款插件，有的格式带厂家前缀。
            var vendor = Normalizer.Vendor(plugin.Vendor);
            if (vendor.Length > 0 && key.Length > vendor.Length && key.StartsWith(vendor, StringComparison.Ordinal))
            {
                var shortKey = key[vendor.Length..];
                // 两款插件去掉厂家后撞名，就不能靠它判断了。
                _withoutVendor[shortKey] = _withoutVendor.ContainsKey(shortKey) ? null : plugin;
            }
        }
    }

    public PluginRow? Find(string projectName)
    {
        var cleaned = CleanName(projectName);
        var key = Normalizer.Name(cleaned);
        if (_exact.TryGetValue(key, out var plugin))
            return plugin;
        if (_withoutVendor.TryGetValue(key, out var shortMatch) && shortMatch != null)
            return shortMatch;
        // 反过来：工程里带厂家，已安装的不带。
        foreach (var candidate in _exact)
        {
            var vendor = Normalizer.Vendor(candidate.Value.Vendor);
            if (vendor.Length > 0 && key == vendor + candidate.Key)
                return candidate.Value;
        }
        return null;
    }

    /// <summary>去掉 Normalizer 不处理的写法。不改 Normalizer 本身，否则已有插件的键会变，备注会对不上。</summary>
    public static string CleanName(string name)
    {
        var cleaned = ParenthesizedChannel().Replace(name, "");
        cleaned = DotArchitecture().Replace(cleaned, "");
        return cleaned.Length > 0 ? cleaned : name;
    }

    [GeneratedRegex(@"\s*\((?:mono|stereo|m|s|mono/stereo)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedChannel();

    [GeneratedRegex(@"\.(?:64|32)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotArchitecture();
}

/// <summary>
/// 宿主自带的插件。它们不在常规插件目录里，不算缺失。
/// 主要靠 <see cref="HostStock"/> 读本机宿主自己的清单（随宿主升级自动更新）；
/// 下面的名单只在本机没有那个宿主的清单时兜底，比如看别人发来的工程。
/// </summary>
public static class StockPlugins
{
    private static readonly HashSet<string> Cubase = Build(
        // 动态
        "Brickwall Limiter", "Compressor", "DeEsser", "EnvelopeShaper", "Expander", "Gate", "Limiter", "Maximizer",
        "MidiGate", "MultibandCompressor", "MultibandEnvelopeShaper", "MultibandExpander", "Noise Gate", "Squasher",
        "Tube Compressor", "VintageCompressor", "VSTDynamics", "VST Dynamics", "Dynamics", "Vocal Comp",
        "VoiceDesigner", "VocalChain", "Vocal Compressor", "Black Valve", "Ducker", "De-Esser", "De-Esser Plus",
        // 均衡
        "CurveEQ", "DJ-Eq", "DualFilter", "Frequency", "Frequency 2", "GEQ-10", "GEQ-30", "MorphFilter", "PostFilter",
        "StudioEQ", "ToneBooster", "WahWah", "AutoFilter", "SuperVision", "StepFilter", "Tonic", "HALion Filter",
        "EQ", "Input Filter", "Standard Panner", "Panner", "VST MultiPanner", "MultiPanner", "CombinedPanner",
        // 混响和延迟
        "RoomWorks", "RoomWorks SE", "REVerence", "REVelation", "Reverb", "ModMachine", "MonoDelay", "MultibandDelay",
        "MultiTap Delay", "PingPongDelay", "StereoDelay", "StudioDelay", "MixerDelay", "MultiDelay", "Tape Delay",
        // 调制
        "AutoPan", "Chopper", "Chorus", "Cloner", "Flanger", "LFO", "Macro Knob", "Envelope Follower", "Metalizer",
        "Phaser", "RingModulator", "Ring Modulator", "Rotary", "StudioChorus", "Tranceformer", "Tremolo", "Vibrato",
        "ModScripter", "Underwater",
        // 失真和音色
        "AmpSimulator", "BitCrusher", "Bitcrusher", "DaTube", "Distortion", "Distroyer", "Grungelizer", "Magneto II",
        "Magneto", "Quadrafuzz v2", "Quadrafuzz", "SoftClipper", "VST Amp Rack", "VST Bass Amp",
        // 音高和其它
        "Octaver", "Pitch Correct", "PitchCorrect", "Pitch Driver", "PitchDriver", "Pitch Shifter", "Imager",
        "Doubler", "LoopMash FX", "MixConvert V6", "MixConvert", "Mix6to2", "Mix6To2", "MonoToStereo", "StereoEnhancer",
        "Stereo Enhancer", "MatrixEncoder", "MatrixDecoder", "MultiScope", "SMPTEGenerator", "TestGenerator", "Test Generator",
        "Tuner", "UV22", "UV22HR", "Anamorph", "VST AmbiConverter", "VST AmbiDecoder", "AmbiDecoder", "VST Connect",
        "VST Connect SE", "VST Renderer", "Dolby Atmos Renderer", "External FX", "Sampler Track", "Spector");

    private static readonly HashSet<string> Empty = [];

    public static bool Contains(ProjectHost host, string name, string? uid = null, HostStock? stock = null) =>
        LookupExport.IsOwnPlugin(name)
        || (stock ?? HostStock.Current).Contains(host, name, uid)
        || (host == ProjectHost.Cubase ? Cubase : Empty).Contains(Normalizer.Name(PluginNameMatcher.CleanName(name)));

    /// <summary>混音笔记不知道是哪个宿主：自带它的宿主名，不是自带的返回 null。</summary>
    public static string? ProductOf(string name, string? uid = null, HostStock? stock = null) =>
        (stock ?? HostStock.Current).ProductOf(name, uid)
        ?? (Cubase.Contains(Normalizer.Name(PluginNameMatcher.CleanName(name))) ? "Cubase" : null);

    private static HashSet<string> Build(params string[] names) => names.Select(Normalizer.Name).ToHashSet();
}

/// <summary>由工程记录算出缺失报告、使用统计和闲置列表。纯计算，不碰文件和数据库。</summary>
public static partial class ProjectReport
{
    // 宿主为每个工程建的文件夹里才有这些子目录。有它们时，文件夹里所有工程文件都是同一首歌的版本，
    // 哪怕文件名完全不同（“霁川 混音”“霁川 音量平衡”）。
    private static readonly string[] ProjectSubfolders = ["Audio", "Edits", "Media", "Ableton Project Info"];

    public static bool IsOwnProjectFolder(string projectPath)
    {
        var folder = System.IO.Path.GetDirectoryName(projectPath);
        return folder != null && ProjectSubfolders.Any(sub => Directory.Exists(System.IO.Path.Combine(folder, sub)));
    }

    /// <summary>同一首歌的分组键。工程专用文件夹按文件夹算，否则按文件名去掉版本号后相同算。</summary>
    public static string SongKey(string path, bool ownFolder = false)
    {
        var folder = (System.IO.Path.GetDirectoryName(path) ?? "").ToLowerInvariant();
        return ownFolder ? folder + "|" : folder + "|" + SongName(path).ToLowerInvariant();
    }

    public static string SongDisplayName(string path, bool ownFolder)
    {
        var folder = ownFolder ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) : null;
        return string.IsNullOrEmpty(folder) ? SongName(path) : folder;
    }

    /// <summary>"新歌-03"、"新歌 (2)"、"新歌 - 副本" 都算 "新歌"。全是数字的文件名（如 0107）保持原样。</summary>
    public static string SongName(string path)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path).Trim();
        var current = stem;
        while (true)
        {
            var next = VersionSuffix().Replace(current, "").TrimEnd();
            if (next == current || next.Length == 0)
                break;
            current = next;
        }
        return current.Length > 0 ? current : stem;
    }

    [GeneratedRegex(@"(?:\s*[-_ ]\s*(?:副本|copy)|\s*\(\d+\)|\s*(?:副本|copy)|[-_ ]*(?:v|ver|version)?[-_ .]*\d+(?:\.\d+)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffix();

    public static ProjectReportResult Build(
        IEnumerable<ProjectFileRecord> files,
        IReadOnlyCollection<PluginRow> plugins,
        IReadOnlySet<string>? ignoredNames = null,
        HostStock? stock = null)
    {
        var matcher = new PluginNameMatcher(plugins);
        var ignored = ignoredNames ?? new HashSet<string>();
        var entries = new List<ProjectEntry>();
        var usageProjects = new Dictionary<string, Dictionary<string, (int Instances, DateTime Last, string Name)>>();

        foreach (var song in files.GroupBy(f => SongKey(f.Path, f.OwnFolder)))
        {
            var versions = song.OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var latest = versions[0];
            var name = SongDisplayName(latest.Path, latest.OwnFolder);

            // 使用统计看所有版本：只要在这首歌的任何版本里出现过，就算用过。
            foreach (var version in versions.Where(v => v.Status == ProjectScanStatus.Ok))
            {
                foreach (var use in version.Plugins)
                {
                    var plugin = matcher.Find(use.Name);
                    if (plugin == null || plugin.Kind == PluginKind.Instrument)
                        continue;
                    if (!usageProjects.TryGetValue(plugin.Key, out var songs))
                        usageProjects[plugin.Key] = songs = [];
                    var current = songs.GetValueOrDefault(song.Key);
                    songs[song.Key] = (Math.Max(current.Instances, use.Instances), Max(current.Last, version.ModifiedUtc), name);
                }
            }

            var lines = latest.Plugins
                .Select(use => ToLine(use, latest.Host, matcher, ignored, stock))
                .Where(line => line != null)
                .Select(line => line!)
                .GroupBy(line => line.Plugin?.Key ?? "?" + Normalizer.Name(PluginNameMatcher.CleanName(line.Name)))
                .Select(group => group.First() with { Instances = group.Sum(l => l.Instances) })
                .OrderBy(line => line.State == ProjectPluginState.Missing ? 0 : line.State == ProjectPluginState.Installed ? 1 : 2)
                .ThenBy(line => line.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            entries.Add(new ProjectEntry(
                song.Key, name, latest.Path, System.IO.Path.GetDirectoryName(latest.Path) ?? "", latest.Host,
                latest.ModifiedUtc, versions.Count, latest.Status, latest.Error, lines));
        }

        var missing = entries
            .SelectMany(entry => entry.Plugins
                .Where(line => line.State == ProjectPluginState.Missing)
                .Select(line => (Name: line.Name, Key: Normalizer.Name(PluginNameMatcher.CleanName(line.Name)), Entry: entry)))
            .GroupBy(item => item.Key)
            .Select(group => new MissingPluginSummary(
                group.First().Name,
                group.Select(item => item.Entry).OrderByDescending(entry => entry.ModifiedUtc).ToList()))
            .OrderByDescending(summary => summary.Projects.Count)
            .ThenBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usage = usageProjects.ToDictionary(
            pair => pair.Key,
            pair => new PluginUsage(
                pair.Value.Count,
                pair.Value.Values.Sum(v => v.Instances),
                pair.Value.Values.Max(v => v.Last),
                pair.Value.Values.OrderByDescending(v => v.Last).Select(v => v.Name).ToList()));

        return new ProjectReportResult(
            entries.OrderByDescending(entry => entry.ModifiedUtc).ToList(),
            missing,
            usage);
    }

    private static ProjectPluginLine? ToLine(ProjectPluginUse use, ProjectHost host, PluginNameMatcher matcher, IReadOnlySet<string> ignored, HostStock? stock)
    {
        var plugin = matcher.Find(use.Name);
        if (plugin != null)
        {
            if (plugin.Kind == PluginKind.Instrument)
                return null;
            if (!plugin.IsMissing)
                return new ProjectPluginLine(plugin.Name, use.Instances, ProjectPluginState.Installed, plugin);
        }

        // 自带插件优先于“已卸载”：同名记录可能是别的来源留下的。
        if (StockPlugins.Contains(host, use.Name, use.Uid, stock))
            return new ProjectPluginLine(use.Name, use.Instances, ProjectPluginState.Stock, null);
        // 没装的音源：工程没标记（Ableton）时靠资料库认出来，不进缺失列表。
        if (Classifier.Classify(use.Name, "", "", null).Kind == PluginKind.Instrument)
            return null;
        if (ignored.Contains(IgnoreKey(use.Name)))
            return new ProjectPluginLine(use.Name, use.Instances, ProjectPluginState.Ignored, null);
        return new ProjectPluginLine(use.Name, use.Instances, ProjectPluginState.Missing, plugin);
    }

    public static string IgnoreKey(string name) => Normalizer.Name(PluginNameMatcher.CleanName(name));

    /// <summary>
    /// 闲置报告：已安装的效果器里，从未在工程里出现或超过指定月数没用过的。
    /// 只是列出来，决定权在用户；垃圾桶里的不再列出。
    /// </summary>
    public static IReadOnlyList<IdlePlugin> FindIdle(
        IEnumerable<PluginRow> plugins,
        IReadOnlyDictionary<string, PluginUsage> usage,
        int months,
        DateTime nowUtc)
    {
        var cutoff = nowUtc.AddMonths(-months);
        var idle = new List<IdlePlugin>();
        foreach (var plugin in plugins)
        {
            if (plugin.IsMissing || plugin.User.Hidden || plugin.Kind == PluginKind.Instrument)
                continue;
            // 只有 AAX 的插件用在 Pro Tools 里，只有 AU 的主要用在 Logic 里，都读不了它们的工程，判断不了；WaveShell 只是外壳。
            if (plugin.Files.All(f => f.Format is PluginFormat.Aax or PluginFormat.Au) || plugin.Key == "waveshell")
                continue;
            var used = usage.GetValueOrDefault(plugin.Key);
            if (used == null)
                idle.Add(new IdlePlugin(plugin, IdleReason.NeverUsed, null));
            else if (used.LastUsedUtc < cutoff)
                idle.Add(new IdlePlugin(plugin, IdleReason.LongUnused, used));
        }
        return idle
            .OrderBy(item => item.Usage == null ? DateTime.MinValue : item.Usage.LastUsedUtc)
            .ThenBy(item => item.Plugin.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>常用清单的建议：用得多、还不在清单里、不在垃圾桶的已安装效果器。</summary>
    public static IReadOnlyList<(PluginRow Plugin, PluginUsage Usage)> SuggestForList(
        IEnumerable<PluginRow> plugins,
        IReadOnlyDictionary<string, PluginUsage> usage,
        IReadOnlySet<string> alreadyInList,
        int limit = 12)
    {
        return plugins
            .Where(p => !p.IsMissing && !p.User.Hidden && p.Kind != PluginKind.Instrument && !alreadyInList.Contains(p.Key))
            .Select(p => (Plugin: p, Usage: usage.GetValueOrDefault(p.Key)))
            .Where(item => item.Usage != null)
            .OrderByDescending(item => item.Usage!.Projects)
            .ThenByDescending(item => item.Usage!.LastUsedUtc)
            .Take(limit)
            .Select(item => (item.Plugin, item.Usage!))
            .ToList();
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

public sealed record ProjectScanProgress(int Done, int Total, string? Current);

public sealed record ProjectScanSummary(int Found, int Parsed, int Unchanged, int Failed, int Removed, bool Cancelled, TimeSpan Elapsed);

/// <summary>在用户选的文件夹里找工程并解析。对工程文件只读，已解析且没变化的跳过。</summary>
public static class ProjectScanner
{
    // 自动保存和备份不算工程，否则版本数和“最近使用”都会失真。
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Auto Saves", "AutoSaves", "Backup", "Backups", "History", "$RECYCLE.BIN", "System Volume Information",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "node_modules", ".git",
        // 宿主自带的模板不是用户的工程。
        "Project Templates", "Templates",
    };

    public static List<string> Discover(IEnumerable<string> folders, CancellationToken cancel = default)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(folders.Where(Directory.Exists));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
        };
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if (!seen.Add(folder))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", options))
                {
                    if (ProjectParser.HostOf(file) != null)
                        found.Add(file);
                }
                foreach (var child in Directory.EnumerateDirectories(folder, "*", options))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(child)))
                        pending.Push(child);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 读不了的文件夹跳过。
            }
        }
        return found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ProjectScanSummary Scan(
        PluginDatabase database,
        IProgress<ProjectScanProgress>? progress = null,
        CancellationToken cancel = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var folders = database.LoadProjectFolders();
        var parsed = 0;
        var unchanged = 0;
        var failed = 0;
        var removed = 0;
        var cancelled = false;
        var found = new List<string>();
        try
        {
            found = Discover(folders, cancel);
            var known = database.LoadProjectFileStamps();
            for (var i = 0; i < found.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var path = found[i];
                progress?.Report(new ProjectScanProgress(i, found.Count, path));
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists)
                        continue;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var modified = info.LastWriteTimeUtc;
                if (known.TryGetValue(path, out var stamp)
                    && stamp.Size == info.Length && stamp.ModifiedUtc == modified && stamp.ParserVersion == ProjectParser.Version)
                {
                    unchanged++;
                    continue;
                }

                var host = ProjectParser.HostOf(path)!.Value;
                try
                {
                    var plugins = ProjectParser.Parse(path, cancel);
                    database.SaveProjectFile(path, host, info.Length, modified, ProjectScanStatus.Ok, null, plugins, ProjectReport.IsOwnProjectFolder(path));
                    parsed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    database.SaveProjectFile(path, host, info.Length, modified, ProjectScanStatus.Failed, e.Message, [], ProjectReport.IsOwnProjectFolder(path));
                    failed++;
                }
            }

            // 只在完整扫完后清理，取消时不能把没扫到的工程当成已删除。
            removed = database.RemoveProjectFilesExcept(found);
            progress?.Report(new ProjectScanProgress(found.Count, found.Count, null));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        return new ProjectScanSummary(found.Count, parsed, unchanged, failed, removed, cancelled, clock.Elapsed);
    }
}
