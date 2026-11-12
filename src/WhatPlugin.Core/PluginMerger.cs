namespace WhatPlugin.Core;

/// <summary>把逐文件的扫描结果合并成一款款插件。纯函数，便于测试。</summary>
public static class PluginMerger
{
    private sealed record Entry(FileScanResult File, PluginClassInfo Class);

    public static IReadOnlyList<MergedPlugin> Merge(IEnumerable<FileScanResult> scans)
    {
        var entries = scans.SelectMany(f => f.Classes.Select(c => new Entry(f, c)));
        var merged = new List<MergedPlugin>();

        foreach (var nameGroup in entries.GroupBy(e => Normalizer.Name(e.Class.Name)))
        {
            var vendors = nameGroup
                .Select(e => Normalizer.Vendor(e.Class.Vendor))
                .Where(v => v.Length > 0)
                .Distinct()
                .ToList();

            if (vendors.Count <= 1)
            {
                // 只有一个已知厂家时，厂家未知的记录也视为同一款。
                merged.Add(Build(nameGroup.Key, nameGroup));
                continue;
            }

            // 不同厂家同名：分开保存。厂家未知的记录无法判断归属，单独成一条。
            foreach (var vendorGroup in nameGroup.GroupBy(e => Normalizer.Vendor(e.Class.Vendor)))
                merged.Add(Build($"{nameGroup.Key}|{vendorGroup.Key}", vendorGroup));
        }

        return merged
            .OrderBy(p => p.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MergedPlugin Build(string key, IEnumerable<Entry> group)
    {
        var entries = group.ToList();
        var best = entries.OrderByDescending(e => e.Class.Source).ThenBy(e => e.Class.Name.Length).First();
        var vendorEntry = entries
            .Where(e => e.Class.Vendor.Length > 0)
            .OrderByDescending(e => e.Class.Source)
            .FirstOrDefault();
        var vendor = vendorEntry?.Class.Vendor ?? "";

        var category = entries
            .Select(e => e.Class.SdkCategory)
            .Where(c => c.Length > 0)
            .OrderByDescending(c => c.Length)
            .FirstOrDefault() ?? "";
        var isInstrument = entries.OrderByDescending(e => e.Class.Source).Select(e => e.Class.IsInstrument).FirstOrDefault(i => i != null);

        var files = entries
            .Select(e => new MergedFile(e.File.Path, e.File.Format, e.File.Arch, e.Class.Version, e.Class.SdkCategory, e.File.Status, e.File.Error))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Format)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var name = Normalizer.WithoutChannelVariant(best.Class.Name);
        var seed = Classifier.FindSeed(name, vendor);
        if (vendor.Length == 0)
            vendor = seed?.SetVendor ?? "";
        // 插件自己没写厂家、只能从文件夹名猜时，厂家目录更可靠。
        if ((vendor.Length == 0 || vendorEntry?.Class.Source == InfoSource.FileName)
            && VendorCatalog.Guess(name) is { IsAmbiguous: false } guess
            && Normalizer.Vendor(guess.Vendor) != Normalizer.Vendor(vendor))
            vendor = guess.Vendor;

        return new MergedPlugin(
            key,
            seed?.DisplayName ?? name,
            Normalizer.CanonicalVendor(vendor),
            best.Class.Source,
            Classifier.Classify(name, vendor, category, isInstrument),
            files);
    }
}
