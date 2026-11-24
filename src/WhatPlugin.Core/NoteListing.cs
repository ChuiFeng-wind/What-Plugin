namespace WhatPlugin.Core;

/// <summary>混音笔记树上的一项：文件夹或笔记。Count 是文件夹里的笔记数或笔记的步骤数，没有时为空。</summary>
public sealed record NoteTreeEntry(bool IsFolder, long Id, string Name, string Count, string ToolTip, ChannelRole Role, IReadOnlyList<NoteTreeEntry> Children);

/// <summary>文件夹详情里列出的一条笔记。</summary>
public sealed record NoteSummaryRow(PluginChain Chain, string Summary)
{
    public string Name => Chain.Name;
}

/// <summary>多选的内容：直接选中的笔记和文件夹，以及文件夹（含子文件夹）里面的笔记。</summary>
public sealed record NoteBatch(List<PluginChain> Notes, List<NoteFolder> Folders, List<PluginChain> Contained, HashSet<long> Inside)
{
    public bool IsEmpty => Notes.Count + Folders.Count == 0;

    /// <summary>加标签、去标签的对象：选中的笔记，加上选中的文件夹里面的笔记。</summary>
    public IReadOnlyList<PluginChain> TagTargets => Notes.Concat(Contained).DistinctBy(c => c.Id).ToList();

    public string Describe() =>
        string.Join("、", new[] { Notes.Count > 0 ? $"{Notes.Count} 条笔记" : "", Folders.Count > 0 ? $"{Folders.Count} 个文件夹" : "" }.Where(s => s.Length > 0));

    /// <summary>选中的文件夹和笔记。选中了文件夹又选中它里面的东西时，调用方只传最外面的那个文件夹。</summary>
    public static NoteBatch Of(IEnumerable<NoteFolder> folders, IEnumerable<PluginChain> notes, IReadOnlyList<NoteFolder> allFolders, IReadOnlyList<PluginChain> allChains)
    {
        var folderList = folders.ToList();
        var inside = folderList.SelectMany(f => MixNotes.Subtree(f.Id, allFolders)).ToHashSet();
        var contained = allChains.Where(c => c.FolderId is { } f && inside.Contains(f)).ToList();
        return new NoteBatch(notes.ToList(), folderList, contained, inside);
    }

    public string Hint => Folders.Count > 0
        ? $"选中的文件夹里一共有 {Contained.Count} 条笔记。加标签、去标签对这些笔记也生效。"
        : "点左边的勾选框，或按住 " + AppPlatform.CommandKeyName + " 点击加选、取消，Shift 点击连选，" + AppPlatform.CommandKeyName + "+A 全选，Esc 取消选择。选中的可以一起拖到文件夹上。";

    /// <summary>批量删除的确认文字；有文件夹且里面有笔记时，附带“同时删除笔记”的选项。</summary>
    public (string Title, string Message, string? Option) DeletePrompt()
    {
        if (Folders.Count == 0)
            return ("删除笔记", $"删除选中的 {Notes.Count} 条笔记？里面的步骤、备选和思路会一起删除，不能恢复。插件不受影响。", null);
        var message = $"删除 {Describe()}？"
            + (Notes.Count > 0 ? $"选中的 {Notes.Count} 条笔记会被删除，不能恢复。" : "")
            + (Contained.Count > 0 ? $"文件夹里的 {Contained.Count} 条笔记默认移到上一层，不会删除。" : "");
        return ("批量删除", message, Contained.Count > 0 ? $"同时删除文件夹里的 {Contained.Count} 条笔记（不能恢复）" : null);
    }
}

/// <summary>混音笔记页的树、文字和批量操作（Windows 版和跨平台版共用）。这里只是记录，不会写入 DAW。</summary>
public static class NoteListing
{
    public static string FolderPath(long? folderId, IReadOnlyList<NoteFolder> folders)
    {
        var path = MixNotes.PathOf(folderId, folders);
        return path.Count == 0 ? "最外层" : string.Join(" › ", path);
    }

    /// <summary>笔记的搜索内容：名称、思路、标签和用到的插件（插件改过名时用插件库里的名字）。</summary>
    public static bool Matches(PluginChain chain, string search, IReadOnlyDictionary<string, string> pluginNames)
    {
        var words = search.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return true;
        var text = string.Join(' ', [chain.Name, chain.Note, .. chain.TagList,
            .. chain.Steps.SelectMany(s => s.AllPicks).Select(p => pluginNames.GetValueOrDefault(p.PluginKey) ?? p.PluginName)]).ToLowerInvariant();
        return words.All(text.Contains);
    }

    /// <summary>文件夹在前、笔记在后。搜索时只留有匹配笔记的文件夹。</summary>
    public static IReadOnlyList<NoteTreeEntry> Tree(IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders, string search, IReadOnlyDictionary<string, string> pluginNames)
    {
        var searching = search.Trim().Length > 0;
        List<NoteTreeEntry> Children(long? parent)
        {
            var nodes = new List<NoteTreeEntry>();
            foreach (var folder in folders.Where(f => f.ParentId == parent))
            {
                var ids = MixNotes.Subtree(folder.Id, folders);
                var count = chains.Count(c => c.FolderId is { } f && ids.Contains(f) && Matches(c, search, pluginNames));
                if (searching && count == 0)
                    continue;
                nodes.Add(new NoteTreeEntry(true, folder.Id, folder.Name, count > 0 ? count.ToString() : "", $"{folder.Name}：{count} 条笔记", ChannelRole.Track, Children(folder.Id)));
            }
            foreach (var chain in chains.Where(c => c.FolderId == parent && Matches(c, search, pluginNames)))
                nodes.Add(Note(chain));
            return nodes;
        }
        return Children(null);
    }

    public static NoteTreeEntry Note(PluginChain chain) =>
        new(false, chain.Id, chain.Name, chain.Steps.Count > 0 ? chain.Steps.Count.ToString() : "",
            chain.TagList.Count > 0 ? $"{chain.Name}  ·  {string.Join("、", chain.TagList)}" : chain.Name, chain.Role, []);

    public static string EmptyText(bool nothingYet, bool searching, bool nothingFound) =>
        nothingYet ? "在左下角新建第一条笔记，比如“人声总线”；也可以先建文件夹，比如混音师的名字"
        : searching && nothingFound ? "没有符合搜索的笔记"
        : "在左边选一条笔记或一个文件夹";

    public static string FolderMeta(NoteFolder folder, IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders)
    {
        var ids = MixNotes.Subtree(folder.Id, folders);
        var notes = chains.Count(c => c.FolderId is { } f && ids.Contains(f));
        var subfolders = ids.Count - 1;
        return $"{FolderPath(folder.Id, folders)}  ·  {notes} 条笔记" + (subfolders > 0 ? $"，{subfolders} 个子文件夹" : "");
    }

    /// <summary>文件夹（含子文件夹）里的笔记，按所在位置和名称排。</summary>
    public static IReadOnlyList<PluginChain> NotesIn(NoteFolder folder, IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders)
    {
        var ids = MixNotes.Subtree(folder.Id, folders);
        return chains.Where(c => c.FolderId is { } f && ids.Contains(f)).ToList();
    }

    public static IReadOnlyList<NoteSummaryRow> FolderNotes(NoteFolder folder, IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders) =>
        NotesIn(folder, chains, folders)
            .OrderBy(c => FolderPath(c.FolderId, folders), StringComparer.CurrentCulture)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new NoteSummaryRow(c, Summary(c, c.FolderId == folder.Id ? "" : FolderPath(c.FolderId, folders))))
            .ToList();

    public static string Summary(PluginChain chain, string path)
    {
        var parts = new List<string>();
        if (path.Length > 0)
            parts.Add(path);
        parts.Add(chain.Steps.Count == 0 ? "还没有插件" : string.Join(" → ", chain.Steps.Select(s => s.PluginName)));
        if (chain.TagList.Count > 0)
            parts.Add(string.Join("、", chain.TagList));
        return string.Join("  ·  ", parts);
    }

    /// <summary>笔记标题下面的一行：位置、步数、备选、没安装的、在垃圾桶的、导入来源。</summary>
    public static string ChainMeta(PluginChain chain, IReadOnlyList<NoteFolder> folders, int steps, int alternates, int missing, int trashed)
    {
        var parts = new List<string> { FolderPath(chain.FolderId, folders) };
        parts.Add(steps == 0 ? "还没有插件" : $"{steps} 步，按从上到下的顺序");
        if (alternates > 0)
            parts.Add($"{alternates} 个备选");
        if (missing > 0)
            parts.Add($"首选里 {missing} 款没安装");
        if (trashed > 0)
            parts.Add($"{trashed} 款在垃圾桶");
        if (chain.Source.Length > 0)
            parts.Add("导入自 " + chain.Source);
        return string.Join("  ·  ", parts);
    }

    public static string WishMissingText(int count) => count > 1 ? $"未安装的 {count} 款加入心愿单" : "未安装的加入心愿单";

    /// <summary>新建的笔记和文件夹放在哪里：选中的文件夹，或选中笔记所在的文件夹，都没有就放最外层。</summary>
    public static long? TargetFolder(NoteTreeEntry? selected, IReadOnlyList<PluginChain> chains) => selected switch
    {
        { IsFolder: true } node => node.Id,
        { IsFolder: false } node => chains.FirstOrDefault(c => c.Id == node.Id)?.FolderId,
        _ => null,
    };

    /// <summary>移动时可以选的文件夹：最外层，加上不在 exclude 里的文件夹（自己和子文件夹不能选）。</summary>
    public static IReadOnlyList<(string Text, long? Id)> MoveTargets(IReadOnlyList<NoteFolder> folders, IReadOnlySet<long> exclude)
    {
        var options = new List<(string Text, long? Id)> { ("最外层", null) };
        options.AddRange(folders
            .Where(f => !exclude.Contains(f.Id))
            .Select(f => (FolderPath(f.Id, folders), (long?)f.Id))
            .OrderBy(o => o.Item1, StringComparer.CurrentCulture));
        return options;
    }

    /// <summary>
    /// 删除文件夹的确认文字。里面没有笔记时只问一句；有笔记时默认把内容移到上一层，并给出“同时删除笔记”的选项。
    /// </summary>
    public static (string Message, string? Option) DeleteFolderPrompt(NoteFolder folder, IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders)
    {
        var ids = MixNotes.Subtree(folder.Id, folders);
        var notes = chains.Count(c => c.FolderId == folder.Id);
        var subfolders = folders.Count(f => f.ParentId == folder.Id);
        var allNotes = chains.Count(c => c.FolderId is { } f && ids.Contains(f));
        var parent = FolderPath(folder.ParentId, folders);
        if (allNotes == 0)
            return (subfolders == 0
                ? $"删除空文件夹“{folder.Name}”？"
                : $"删除文件夹“{folder.Name}”？里面的 {subfolders} 个子文件夹会移到“{parent}”。", null);
        var option = ids.Count > 1
            ? $"同时删除笔记：这个文件夹和子文件夹里的 {allNotes} 条笔记、{ids.Count - 1} 个子文件夹一起删除，不能恢复"
            : $"同时删除笔记：里面的 {allNotes} 条笔记一起删除，不能恢复";
        var inside = string.Join("和", new[] { notes > 0 ? $"{notes} 条笔记" : "", subfolders > 0 ? $"{subfolders} 个子文件夹" : "" }.Where(s => s.Length > 0));
        return ($"删除文件夹“{folder.Name}”？里面的 {inside}会移到“{parent}”，不会删除任何笔记。", option);
    }

    /// <summary>
    /// 把选中的文件夹和笔记移到 target。那里已经有同名的就跳过，返回状态栏上的一句话。
    /// </summary>
    public static string MoveBatch(PluginDatabase database, NoteBatch batch, long? target, IReadOnlyList<NoteFolder> folders)
    {
        var moved = 0;
        var failed = new List<string>();
        foreach (var folder in batch.Folders.Where(f => f.ParentId != target))
        {
            try
            {
                database.MoveNoteFolder(folder.Id, target);
                moved++;
            }
            catch (ArgumentException)
            {
                failed.Add(folder.Name);
            }
        }
        foreach (var chain in batch.Notes.Where(c => c.FolderId != target))
        {
            try
            {
                database.MoveChain(chain.Id, target);
                moved++;
            }
            catch (ArgumentException)
            {
                failed.Add(chain.Name);
            }
        }
        return $"已把 {moved} 项移到“{FolderPath(target, folders)}”"
            + (failed.Count > 0 ? $"；{failed.Count} 项没有移动，那里已经有同名的：{string.Join("、", failed.Take(5))}" + (failed.Count > 5 ? " 等" : "") : "");
    }

    /// <summary>批量删除，返回状态栏上的一句话。withNotes 为 false 时文件夹里的笔记移到上一层。</summary>
    public static string DeleteBatch(PluginDatabase database, NoteBatch batch, bool withNotes)
    {
        var deleted = database.DeleteChains(batch.Notes.Select(c => c.Id));
        foreach (var folder in batch.Folders)
        {
            if (withNotes)
                deleted += database.DeleteNoteFolderWithNotes(folder.Id);
            else
                database.DeleteNoteFolder(folder.Id);
        }
        return $"已删除 {deleted} 条笔记" + (batch.Folders.Count > 0 ? $"、{batch.Folders.Count} 个文件夹" : "")
            + (batch.Folders.Count > 0 && !withNotes && batch.Contained.Count > 0 ? "，文件夹里的笔记移到了上一层" : "");
    }

    /// <summary>批量加标签时可以勾选的：预设标签和已经用过的标签。</summary>
    public static IReadOnlyList<string> KnownTags(IReadOnlyList<PluginChain> chains, IEnumerable<string>? extra = null) =>
        MixNotes.PresetTags.Concat(extra ?? []).Concat(chains.SelectMany(c => c.TagList))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static void ChangeTags(PluginDatabase database, IEnumerable<PluginChain> targets, Func<IEnumerable<string>, IEnumerable<string>> change)
    {
        foreach (var chain in targets)
            database.SetChainTags(chain.Id, change(chain.TagList).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ---------- 一步里的插件 ----------

    public static string StateText(ChainStepState state) => state switch
    {
        ChainStepState.Missing => "未安装",
        ChainStepState.Stock => "宿主自带",
        ChainStepState.Blacklisted => "在垃圾桶",
        ChainStepState.Instrument => "音源",
        _ => "",
    };

    /// <summary>插件名下面的一行：厂家和工程使用情况，宿主自带的说明是哪个宿主。</summary>
    public static string PickDetail(ChainPick pick, ChainStepState state, PluginRow? plugin, PluginUsage? usage)
    {
        if (state == ChainStepState.Stock)
            return (StockPlugins.ProductOf(pick.PluginName) ?? "宿主") + " 自带的插件，不需要另外安装";
        if (plugin == null)
            return "这款插件已不在扫描结果里";
        var vendor = plugin.Vendor.Length > 0 ? plugin.Vendor : "未知";
        return $"{vendor}  ·  {PluginLabels.UsageSummary(usage)}";
    }

    /// <summary>没安装的插件以前移进垃圾桶并卸载过时，返回那条记录。</summary>
    public static PluginRow? Remembered(ChainPick pick, ChainStepState state, PluginRow? plugin, PluginNameMatcher remembered) =>
        state != ChainStepState.Missing ? null
        : plugin is { } row && TrashMemory.IsRemembered(row) ? row
        : TrashMemory.Find(pick.PluginName, remembered);

    /// <summary>右边的候选：没有搜索时按工程使用次数建议，搜索时按名称、厂家等匹配，用得多的在前。</summary>
    public static IReadOnlyList<(PluginRow Plugin, PluginUsage? Usage)> Candidates(IReadOnlyList<PluginRow> effects, IReadOnlyDictionary<string, PluginUsage> usage, string search)
    {
        var words = search.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return ProjectReport.SuggestForList(effects, usage, new HashSet<string>()).Select(s => (s.Plugin, (PluginUsage?)s.Usage)).ToList();
        return effects
            .Where(p => !p.User.Hidden && !p.IsMissing && words.All(PluginLabels.SearchText(p).Contains))
            .OrderByDescending(p => usage.GetValueOrDefault(p.Key)?.Projects ?? 0)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(p => (p, usage.GetValueOrDefault(p.Key)))
            .ToList();
    }

    public const string CandidateHint = "点 + 加成新的一步；先在中间点选某一步，再点 ⇄ 可以加为它的备选";

    public static string CandidateHintFor(int number, string name) => $"点 + 加成新的一步；点 ⇄ 加为第 {number} 步（{name}）的备选";
}

/// <summary>把笔记里没安装的插件加入心愿单（两边共用）。</summary>
public static class NoteWishes
{
    /// <summary>心愿单里每个名字的状态；同名的既有想买又有已购时按“想买”算。</summary>
    public static Dictionary<string, WishStatus> Statuses(PluginDatabase database) => database.LoadWishes()
        .GroupBy(w => Normalizer.Name(w.Name))
        .Where(g => g.Key.Length > 0)
        .ToDictionary(g => g.Key, g => g.Min(w => w.Status));

    /// <summary>装着的同名插件只有一个厂家就用它，否则查厂家目录，猜不准就留空。</summary>
    public static string GuessVendor(string name, IEnumerable<PluginRow> rows)
    {
        var key = Normalizer.Name(name);
        var installed = rows.Where(p => p.Vendor.Length > 0 && Normalizer.Name(p.Name) == key)
            .Select(p => p.Vendor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (installed.Count == 1)
            return installed[0];
        if (installed.Count > 1)
            return "";
        return VendorCatalog.Guess(name) is { IsAmbiguous: false } guess ? guess.Vendor : "";
    }

    /// <summary>以前移进垃圾桶又卸载了的插件：名称和当时的评价。</summary>
    public static (string Name, string Text)? FindRemembered(string name, IReadOnlyList<PluginRow> rows, IReadOnlyList<UninstallRecord> history)
    {
        var row = TrashMemory.Find(name, rows);
        var record = row != null ? UninstallHistory.ForPlugin(row, history) : UninstallHistory.Find(name, history, rows);
        if (row == null && record == null)
            return null;
        return (row?.Name ?? record!.Name, UninstallHistory.Reminder(row, record));
    }

    /// <summary>要加入的插件里有以前不要过的，先把当时的评价摆出来确认。没有就返回 null。</summary>
    public static string? RememberedPrompt(IEnumerable<string> names, IReadOnlyList<PluginRow> rows, IReadOnlyList<UninstallRecord> history)
    {
        var found = names.Select(n => FindRemembered(n, rows, history)).OfType<(string Name, string Text)>().DistinctBy(f => f.Name).ToList();
        if (found.Count == 0)
            return null;
        var head = found.Count == 1
            ? $"你以前把 {found[0].Name} 移进垃圾桶，后来卸载了。"
            : $"下面 {found.Count} 款插件你以前都移进垃圾桶，后来卸载了。";
        var lines = string.Join("\n", found.Select(f => $"· {f.Name}：{f.Text}"));
        return $"{head}\n\n{lines}\n\n还要加入心愿单吗？";
    }

    /// <summary>心愿单里还没有的名字（按规范化后的名称比较，去掉重复）。</summary>
    public static IReadOnlyList<string> Pending(IEnumerable<string> names, IReadOnlyDictionary<string, WishStatus> existing) =>
        names.Where(n => Normalizer.Name(n) is { Length: > 0 } key && !existing.ContainsKey(key))
            .DistinctBy(Normalizer.Name).ToList();

    /// <summary>加入心愿单，原因写“混音笔记里用到”。existing 会更新；返回加入的名字。</summary>
    public static IReadOnlyList<string> Add(PluginDatabase database, IEnumerable<string> names, string chainName, Dictionary<string, WishStatus> existing, IReadOnlyList<PluginRow> rows)
    {
        var added = new List<string>();
        foreach (var name in names)
        {
            var key = Normalizer.Name(name);
            if (key.Length == 0 || existing.ContainsKey(key))
                continue;
            database.AddWish(new WishItem(0, name, GuessVendor(name, rows), Reason: $"混音笔记“{chainName}”里用到"));
            existing[key] = WishStatus.Wished;
            added.Add(name);
        }
        return added;
    }

    public static string Added(IReadOnlyList<string> added) => added.Count switch
    {
        0 => "这些插件已经在心愿单里了",
        1 => $"已把 {added[0]} 加入心愿单",
        _ => $"已把 {added.Count} 款未安装的插件加入心愿单：{string.Join("、", added)}",
    };
}
