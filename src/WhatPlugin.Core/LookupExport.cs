using System.Text.Encodings.Web;
using System.Text.Json;

namespace WhatPlugin.Core;

/// <summary>
/// 给 DAW 里的查询窗口用的数据。窗口是 C++ 写的插件，不直接碰数据库，
/// 而是调用命令行工具拿这份只读 JSON。音源和垃圾桶里的插件不在里面。
/// </summary>
public static class LookupExport
{
    /// <summary>扫描时跳过的文件名，也是这个查询窗口自己。它不是要管理的插件。</summary>
    public const string OwnPluginName = "WhatPluginLookup";

    /// <summary>改名前的名字（PluginVault 时期、1.2.6 及以前带空格的名字）。老工程里可能还记着它们，不能报成缺失。</summary>
    public static readonly string[] LegacyOwnPluginNames = ["What Plugin Lookup", "PluginVault Lookup"];

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool IsOwnPlugin(string pathOrName)
    {
        var name = Path.GetFileNameWithoutExtension(pathOrName);
        return name.Equals(OwnPluginName, StringComparison.OrdinalIgnoreCase)
            || LegacyOwnPluginNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>查询窗口靠这个文件找到命令行工具。用 --db 试验时不写，免得指向测试版本。</summary>
    public static void PublishCliPath(string databasePath, string defaultDatabasePath, string appFolder)
    {
        var full = Path.GetFullPath(databasePath);
        if (!full.Equals(Path.GetFullPath(defaultDatabasePath), StringComparison.OrdinalIgnoreCase)
            && !full.Equals(Path.GetFullPath(PluginDatabase.LegacyDefaultPath), StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var cli = Path.Combine(appFolder, "WhatPlugin.Cli.exe");
            if (File.Exists(cli))
            {
                // 查询插件只看新文件夹，暂时用着旧数据库时也写在这里。
                var folder = Path.GetDirectoryName(Path.GetFullPath(defaultDatabasePath))!;
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "cli-path.txt"), cli);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 只影响 DAW 里的查询窗口，窗口会提示。
        }
    }

    public static string Dump(PluginDatabase database)
    {
        var all = database.LoadPlugins();
        var report = ProjectReport.Build(database.LoadProjectFiles(), all, database.LoadIgnoredProjectNames());
        var plugins = database.LoadEffects()
            .Where(p => !p.User.Hidden && !p.IsMissing && !IsOwnPlugin(p.Name))
            .OrderByDescending(p => report.Usage.GetValueOrDefault(p.Key)?.Projects ?? 0)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var usage = report.Usage.GetValueOrDefault(p.Key);
                var mix = p.User.Mix;
                var mixText = new[]
                {
                    mix?.LatencyText,
                    mix is { Cpu: not CpuLevel.Unknown } ? "CPU " + mix.CpuText : null,
                    mix?.LinearPhase == Tri.Yes ? "线性相位" : null,
                    mix?.Sidechain == Tri.Yes ? "侧链" : null,
                    mix?.Oversampling == Tri.Yes ? "过采样" : null,
                }.Where(t => !string.IsNullOrEmpty(t));
                return new
                {
                    p.Key,
                    p.Name,
                    p.Vendor,
                    Tags = string.Join("、", p.Tags),
                    MyTags = string.Join("、", p.User.MyTags),
                    p.User.Rating,
                    p.User.Note,
                    Mix = string.Join("、", mixText),
                    Usage = usage == null ? "工程里没用过" : $"{usage.Projects} 个工程，最近 {usage.LastUsedUtc.ToLocalTime():yyyy-MM-dd}",
                };
            })
            .ToList();

        var chains = database.LoadChains().Select(chain => new
        {
            chain.Name,
            chain.Note,
            Steps = ChainReport.Resolve(chain, all)
                .Where(s => s.State != ChainStepState.Instrument)
                .Select(s => new
                {
                    Name = s.Plugin?.Name ?? s.Step.PluginName,
                    s.Step.Note,
                    State = s.State switch
                    {
                        ChainStepState.Missing => "未安装",
                        ChainStepState.Stock => (StockPlugins.ProductOf(s.Step.PluginName) ?? "宿主") + " 自带",
                        ChainStepState.Blacklisted => "在垃圾桶",
                        _ => "",
                    },
                }).ToList(),
        }).ToList();

        var theme = ThemePalettes.Find(database.GetSetting("theme"));
        return JsonSerializer.Serialize(new { Ok = true, Theme = theme.Colors, Plugins = plugins, Chains = chains }, Options);
    }

    public static string Recommend(PluginDatabase database, string need)
    {
        var rows = database.LoadEffects().Where(p => !p.User.Hidden && !p.IsMissing && !IsOwnPlugin(p.Name)).ToList();
        var result = Recommender.Recommend(need, rows, 200);
        var parts = new List<string>();
        if (result.Understood.Count > 0)
            parts.Add("需求：" + string.Join("、", result.Understood));
        if (result.Vendors.Count > 0)
            parts.Add("厂家：" + string.Join("、", result.Vendors));
        if (result.Excluded.Count > 0)
            parts.Add("排除：" + string.Join("、", result.Excluded));
        if (result.NotUnderstood.Count > 0)
            parts.Add("没有理解：" + string.Join("、", result.NotUnderstood));
        if (result.AttributeHint != null)
            parts.Add(result.AttributeHint);
        parts.Add(result.TotalMatches == 0 ? "没有找到匹配的插件" : $"找到 {result.TotalMatches} 款");

        return JsonSerializer.Serialize(new
        {
            Ok = true,
            Summary = string.Join("  |  ", parts),
            Items = result.Items.Select(i => new { i.Plugin.Key, Reason = string.Join("；", i.Reasons) }).ToList(),
        }, Options);
    }

    public static string Error(string message) => JsonSerializer.Serialize(new { Ok = false, Error = message }, Options);
}
