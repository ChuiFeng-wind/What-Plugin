namespace WhatPlugin.Core;

/// <summary>在插件目录中查找待扫描对象。只读取文件信息，不加载任何插件。</summary>
public static class PluginDiscovery
{
    /// <summary>默认目录加上用户在设置里添加的 VST2 目录。</summary>
    public static IReadOnlyList<(string Path, PluginFormat Format)> Roots(IEnumerable<string> extraVst2Folders) =>
        DefaultRoots()
            .Concat(extraVst2Folders.Select(f => (f, PluginFormat.Vst2)))
            .DistinctBy(r => Path.TrimEndingDirectorySeparator(r.Item1), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>当前平台的默认插件目录，见 <see cref="AppPlatform.DefaultPluginRoots"/>。</summary>
    public static IReadOnlyList<(string Path, PluginFormat Format)> DefaultRoots()
    {
        return AppPlatform.DefaultPluginRoots()
            .DistinctBy(r => Path.TrimEndingDirectorySeparator(r.Item1), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<ScanTarget> Discover(IEnumerable<(string Path, PluginFormat Format)> roots)
    {
        var results = new Dictionary<string, ScanTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, format) in roots)
        {
            if (Directory.Exists(root))
                Walk(root, root, format, results);
        }
        return results.Values.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(string dir, string root, PluginFormat format, Dictionary<string, ScanTarget> results)
    {
        string[] files, subdirs;
        try
        {
            files = Directory.GetFiles(dir);
            subdirs = Directory.GetDirectories(dir);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var file in files)
        {
            var target = LookupExport.IsOwnPlugin(file) ? null : ClassifyFile(file, root, format);
            if (target != null)
                results.TryAdd(file, target);
        }

        foreach (var sub in subdirs)
        {
            // 跳过符号链接，防止循环；.vst3 / .aaxplugin / .clap 目录是一个整体，不再往里走。
            if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            var ext = Path.GetExtension(sub);
            if (LookupExport.IsOwnPlugin(sub))
                continue;
            if (ext.Equals(".vst3", StringComparison.OrdinalIgnoreCase))
                results.TryAdd(sub, new ScanTarget(sub, PluginFormat.Vst3, root, StaticInspector.DetectArch(sub, PluginFormat.Vst3)));
            else if (ext.Equals(".aaxplugin", StringComparison.OrdinalIgnoreCase))
                results.TryAdd(sub, new ScanTarget(sub, PluginFormat.Aax, root, StaticInspector.DetectArch(sub, PluginFormat.Aax)));
            else if (ext.Equals(".clap", StringComparison.OrdinalIgnoreCase))
                results.TryAdd(sub, new ScanTarget(sub, PluginFormat.Clap, root, StaticInspector.DetectArch(sub, PluginFormat.Clap)));
            else
                Walk(sub, root, format, results);
        }
    }

    private static ScanTarget? ClassifyFile(string file, string root, PluginFormat rootFormat)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        switch (ext)
        {
            case ".vst3":
                return new ScanTarget(file, PluginFormat.Vst3, root, StaticInspector.DetectArch(file, PluginFormat.Vst3));
            case ".clap":
                return new ScanTarget(file, PluginFormat.Clap, root, StaticInspector.DetectArch(file, PluginFormat.Clap));
            case ".dll" when rootFormat == PluginFormat.Vst2 && StaticInspector.LooksLikeVst2(file):
                // VST3 / AAX 目录里的 dll 都是依赖文件；VST2 目录里也只认导出了 VST 入口的 dll。
                return new ScanTarget(file, PluginFormat.Vst2, root, StaticInspector.DetectArch(file, PluginFormat.Vst2));
            default:
                return null;
        }
    }
}
