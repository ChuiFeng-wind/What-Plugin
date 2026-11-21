using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>
/// 测试用的扫描环境：插件目录、扫描器和 DAW 记录都在临时目录里，不读这台电脑上真实的插件和 DAW 配置。
/// 示例插件是带 moduleinfo.json 的 VST3 包，扫描时直接读这份文件，不会真的启动扫描器。
/// </summary>
public sealed class TestEnvironment
{
    public string Root { get; }
    public string Vst3 { get; }
    public string Vst2 { get; }
    public string ScannerPath { get; }
    public HostPluginIndex Hosts { get; set; } = HostPluginIndex.Empty;
    public List<HostBlacklistEntry> Blacklist { get; } = [];

    public TestEnvironment(string root)
    {
        Root = root;
        Vst3 = Path.Combine(root, "Plug-Ins", "VST3");
        Vst2 = Path.Combine(root, "Plug-Ins", "VST2");
        Directory.CreateDirectory(Vst3);
        Directory.CreateDirectory(Vst2);
        ScannerPath = Path.Combine(root, AppPlatform.Executable("WhatPluginScanner"));
        File.WriteAllText(ScannerPath, "");
    }

    public ScanEnvironment Scan => new(
        ScannerPath,
        () => [(Vst3, PluginFormat.Vst3), (Vst2, PluginFormat.Vst2)],
        () => Hosts,
        () => Blacklist);

    /// <summary>放一个 VST3 包，返回它的路径。</summary>
    public string AddVst3(string name, string vendor, string subCategories = "Fx|EQ")
    {
        var bundle = Path.Combine(Vst3, name + ".vst3");
        var resources = Path.Combine(bundle, "Contents", "Resources");
        Directory.CreateDirectory(resources);
        var categories = string.Join(", ", subCategories.Split('|').Select(c => $"\"{c}\""));
        File.WriteAllText(Path.Combine(resources, "moduleinfo.json"), $$"""
            {
              "Name": "{{name}}",
              "Factory Info": { "Vendor": "{{vendor}}" },
              "Classes": [
                { "Name": "{{name}}", "Category": "Audio Module Class", "Vendor": "{{vendor}}", "Version": "1.0.0", "Sub Categories": [{{categories}}] }
              ]
            }
            """);
        return bundle;
    }
}
