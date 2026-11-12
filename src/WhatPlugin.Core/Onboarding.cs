namespace WhatPlugin.Core;

/// <summary>导览里的一站：指向左侧导航的一项。<see cref="Target"/> 是导航项的 Tag，扩展页面是 "ext:标题"。</summary>
public sealed record TourStop(string Target, string Title, string Body);

/// <summary>
/// 新手指引：第一次打开时的欢迎窗口和左侧导航的逐项导览。
/// 只对新用户自动弹出（还没有插件、也没看过）；老用户升级后直接记成看过，不打扰。
/// </summary>
public static class Onboarding
{
    public const string Setting = "onboarding.version";

    /// <summary>指引内容有大改时加一，可以让看过旧版的人再看一次。</summary>
    public const int CurrentVersion = 1;

    public const string ExtensionPrefix = "ext:";

    public static bool Seen(string? saved) =>
        int.TryParse(saved, out var version) && version >= CurrentVersion;

    /// <summary>启动时该做什么：自动弹出、只记成看过（老用户），或什么都不做。</summary>
    public static OnboardingStart OnStartup(string? saved, int pluginCount) =>
        Seen(saved) ? OnboardingStart.Nothing
        : pluginCount > 0 ? OnboardingStart.MarkSeen
        : OnboardingStart.Show;

    private static readonly Dictionary<string, (string Title, string Body)> Texts = new()
    {
        ["Plugins"] = ("插件库",
            "扫描到的效果器都在这里。左边可以按功能（EQ、压缩、混响…）或适合的音色分类；选中插件，可以写笔记、打分、打标签，标上是免费还是已购。"),
        ["Projects"] = ("工程",
            "点“添加文件夹”，选你存放工程的文件夹。软件只读不改，列出每个工程用到的插件，并汇总缺了哪些。"),
        ["Lists"] = ("常用清单",
            "按混音笔记的标签自动整理常用插件，比如“人声 常用”，按出现次数排序。也可以自己建清单。"),
        ["Chains"] = ("混音笔记",
            "把效果链记下来：每一步写首选插件、备选插件和应用思路。也可以从 Cubase、Studio One、REAPER、Ableton Live 的工程直接导入轨道。"),
        ["Idle"] = ("闲置报告",
            "扫描过工程以后，这里列出很久没用、和从来没在工程里出现过的插件。"),
        ["Health"] = ("健康检查",
            "找出重复安装、只有 32 位、扫描时读取失败、文件已经找不到等问题。只检查，不会改动文件。"),
        ["Wishes"] = ("心愿单",
            "记下想买的插件和想要程度。以前丢进垃圾桶或卸载过的插件，再加进来时会提醒你当时为什么不要它。"),
        ["Accounts"] = ("已购账号",
            "记录每个厂家用哪个账号购买，同一个厂家默认共用一个账号。密码和序列号加密保存，不会以明文写进数据库。"),
        ["Blacklist"] = ("垃圾桶",
            "不想要的插件移到这里，只在本软件里隐藏；点“卸载”才会动文件。卸载的日期和原因记进“卸载历史”。"),
        ["Settings"] = ("设置",
            "扫描插件、添加 VST2 文件夹、换主题、完整备份都在这里。想再看一遍这份指引，也在设置里。左侧导航可以按住上下拖动，调整成你习惯的顺序。"),
        [ExtensionPrefix + "成就"] = ("成就",
            "成就包的页面。根据这台电脑上的插件、笔记和心愿单解锁成就，不联网。"),
    };

    /// <summary>按导航当前的显示顺序排出导览的各站。不认识的扩展页面给一句通用说明。</summary>
    public static IReadOnlyList<TourStop> Stops(IEnumerable<string> navOrder)
    {
        var stops = new List<TourStop>();
        foreach (var target in navOrder.Distinct(StringComparer.Ordinal))
        {
            if (Texts.TryGetValue(target, out var text))
                stops.Add(new TourStop(target, text.Title, text.Body));
            else if (target.StartsWith(ExtensionPrefix, StringComparison.Ordinal) && target.Length > ExtensionPrefix.Length)
                stops.Add(new TourStop(target, target[ExtensionPrefix.Length..], "已安装的扩展页面。"));
        }
        return stops;
    }

    /// <summary>导览气泡上的进度，比如“3 / 11”。</summary>
    public static string Progress(int index, int count) => $"{index + 1} / {count}";
}

public enum OnboardingStart { Nothing, Show, MarkSeen }
