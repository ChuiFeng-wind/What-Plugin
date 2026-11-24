using System.Text;

namespace WhatPlugin.Core;

public enum UninstallMethod { Official, RecycleFiles, Skip }

/// <param name="Reason">卸载原因，记进卸载历史；空表示用移入垃圾桶的原因。</param>
public sealed record UninstallChoice(UninstallPlan Plan, UninstallMethod Method, InstalledProgram? Program, string Reason = "");

/// <summary>卸载对话框里一款插件可选的一种方式。</summary>
public sealed record UninstallOption(string Label, UninstallMethod Method, InstalledProgram? Program);

/// <summary>卸载对话框的选项和说明。Windows 版和跨平台版共用。</summary>
public static class UninstallOptions
{
    /// <summary>确定的官方卸载程序排第一，其次是移文件，可能相关的卸载程序要用户自己选，最后是不卸载。</summary>
    public static IReadOnlyList<UninstallOption> For(UninstallPlan plan)
    {
        var options = new List<UninstallOption>();
        if (plan.Exact != null)
            options.Add(new($"运行官方卸载程序：{plan.Exact.Name}", UninstallMethod.Official, plan.Exact));
        if (plan.Files.Count > 0)
            options.Add(new($"把 {plan.Files.Count} 个文件移到{PluginTrash.Name}", UninstallMethod.RecycleFiles, null));
        foreach (var possible in plan.Possible)
            options.Add(new($"可能相关，请确认后再选：{possible.Name}", UninstallMethod.Official, possible));
        options.Add(new("这款先不卸载", UninstallMethod.Skip, null));
        return options;
    }

    /// <param name="checkedPrograms">查过“已安装的应用”（Windows 版）。跨平台版不查，不能说“没有找到”。</param>
    public static string Detail(UninstallPlan plan, UninstallOption option, bool checkedPrograms = true)
    {
        var text = new StringBuilder();
        if (option is { Method: UninstallMethod.Official, Program: { } program })
        {
            text.AppendLine($"发布者：{(program.Publisher.Length > 0 ? program.Publisher : "未知")}，版本：{(program.Version.Length > 0 ? program.Version : "未知")}");
            text.AppendLine($"命令：{program.UninstallCommand}");
            text.AppendLine(program == plan.Exact && plan.AlsoInProgram.Count > 0
                ? $"同一安装包里还有：{string.Join("、", plan.AlsoInProgram.Take(12))}{(plan.AlsoInProgram.Count > 12 ? $" 等 {plan.AlsoInProgram.Count} 款" : "")}，它们可能一起被卸载。"
                : "官方卸载程序可能同时卸载同一安装包里的其他插件。");
        }
        else if (option.Method == UninstallMethod.RecycleFiles)
        {
            foreach (var file in plan.Files)
                text.AppendLine($"{PluginLabels.Format(file.Format)}，{RemovalText.Size(file.Size)}：{file.Path}");
            if (AppPlatform.IsMac || !checkedPrograms)
                text.AppendLine("只移动这些插件文件。安装包放的其他内容（预置、音色库、授权文件等）会留下。");
            else if (plan.Exact == null)
                text.AppendLine("没有找到能确定的官方卸载程序。只移动这些插件文件，安装包的其他内容和卸载记录会留下。");
        }
        else if (option.Method == UninstallMethod.Skip && plan.Files.Count == 0 && plan.Exact == null)
        {
            text.AppendLine(AppPlatform.IsMac || !checkedPrograms ? "没有可以移除的文件。" : "没有可以移除的文件，也没有找到官方卸载程序。");
        }

        if (option.Method != UninstallMethod.Skip)
        {
            foreach (var shared in plan.SharedFiles)
                text.AppendLine($"和其他插件共用，不会移动：{shared}");
            if (plan.Projects > 0)
                text.AppendLine($"有 {plan.Projects} 个工程用过这款插件，卸载后这些工程打开时可能缺插件。");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>对话框底部的一句总结；没有要做的事时不能开始。</summary>
    public static (string Text, bool CanStart) Summary(IEnumerable<UninstallChoice> choices)
    {
        var list = choices.ToList();
        var programs = list.Where(c => c.Method == UninstallMethod.Official && c.Program != null).Select(c => c.Program!.Key).Distinct().Count();
        var files = list.Where(c => c.Method == UninstallMethod.RecycleFiles).Sum(c => c.Plan.Files.Count);
        var parts = new List<string>();
        if (programs > 0)
            parts.Add($"运行 {programs} 个卸载程序");
        if (files > 0)
            parts.Add($"把 {files} 个文件移到{PluginTrash.Name}");
        return parts.Count > 0 ? ("将" + string.Join("，", parts), true) : ("没有要执行的操作", false);
    }
}

/// <summary>卸载结果：报告文字、已经没有文件的插件和要记进历史的记录。</summary>
public sealed record UninstallReport(string Text, IReadOnlyList<long> Gone, IReadOnlyList<UninstallRecord> History, string Status);

/// <summary>删除版本、卸载和恢复时界面上的文字。“回收站”在 Mac 上叫“废纸篓”。</summary>
public static class RemovalText
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.0} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    // ---------- 详情里的已安装版本 ----------

    public static string VersionTitle(MergedFile file)
    {
        var title = $"{PluginLabels.Format(file.Format)}，{PluginLabels.Arch(file.Arch)}，{PluginLabels.Status(file.Status)}";
        return file.SdkCategory.Length > 0 ? title + $"，自带分类 {file.SdkCategory}" : title;
    }

    public static string VersionNote(bool exists, bool shared, Func<long> size, string? error)
    {
        var note = !exists ? "文件已不在，重新扫描后这一行会消失"
            : shared ? "和其他插件共用这个文件，不能单独删除"
            : Size(size());
        return error != null ? note + $"（{error}）" : note;
    }

    public static string VersionsNote(bool waves, bool canDelete) =>
        waves ? "Waves 插件共用 WaveShell，这里不提供删除。"
        : canDelete ? $"可以勾选不需要的版本。文件会移到{PluginTrash.Name}，可以在这里恢复；至少要留下一个可用的版本。"
        : "";

    public static string DeleteVersionsButton(int count) => count > 0 ? $"删除选中的 {count} 个版本…" : "删除选中的版本…";

    public static string ConfirmDeleteVersions(IEnumerable<MergedFile> files, int projects, bool msi)
    {
        var message = new StringBuilder($"这些文件会移到{PluginTrash.Name}，可以在这里恢复：\n");
        foreach (var file in files)
            message.Append($"\n{PluginLabels.Format(file.Format)}：{file.Path}");
        if (projects > 0)
            message.Append($"\n\n这款插件有 {projects} 个工程用过。现有记录分不清工程用的是哪个格式，旧工程可能依赖这个版本。");
        if (msi)
            message.Append("\n\n这款插件是 MSI 安装的，修复或更新时可能把文件装回来。");
        message.Append(AppPlatform.IsMac
            ? "\n\n安装包放的其他内容不会被改动；以后重新运行安装包，可能把文件装回来。"
            : "\n\n官方卸载程序和它的记录不会被改动；卸载程序遇到文件已被删除时的表现，无法保证所有厂商都一致。");
        return message.ToString();
    }

    public static string Moving => $"正在移到{PluginTrash.Name}…";

    public static string Restoring => $"正在从{PluginTrash.Name}恢复…";

    public static string MoveConfirm => $"移到{PluginTrash.Name}";

    /// <summary>删除版本后的状态栏文字。</summary>
    public static string VersionsMoved(IReadOnlyList<FileOpResult> results) =>
        Describe(results, $"已把 {{0}} 个版本移到{PluginTrash.Name}，可以在详情里恢复");

    public static string Describe(IReadOnlyList<FileOpResult> results, string success)
    {
        var ok = results.Count(r => r.Ok);
        var text = string.Format(success, ok);
        var unconfirmed = results.Count(r => r.Ok && r.Unconfirmed);
        if (unconfirmed > 0)
            text += $"；其中 {unconfirmed} 个没能在{PluginTrash.Name}里确认";
        var failed = results.FirstOrDefault(r => !r.Ok);
        if (failed != null)
            text = (ok > 0 ? text + "；" : "") + $"{results.Count(r => !r.Ok)} 个失败：{failed.Error}";
        return text;
    }

    public static string Restored(FileOpResult result) => result.Ok ? $"已恢复 {result.Path}" : $"恢复失败：{result.Error}";

    /// <summary>恢复失败是因为回收处已经清空，这条记录再也用不上。</summary>
    public static bool ShouldForget(FileOpResult result) => result.Ok || result.Error == PluginTrash.Gone;

    public static string RemovedTitle(RemovedFile file) =>
        $"{PluginLabels.Format(file.Format)}，{Size(file.Size)}，{file.RemovedUtc.ToLocalTime():yyyy-MM-dd} 移除";

    public static string RemovedFilesHint => $"这些文件在{PluginTrash.Name}里。清空{PluginTrash.Name}后就不能再从这里恢复了。";

    // ---------- 垃圾桶页的按钮 ----------

    public static string UninstallButton(int count) => count > 0 ? $"卸载选中的 {count} 款…" : "卸载选中的插件…";

    public static string RemovedFilesButton(int count) => $"已移除的文件（{count}）";

    public static string HistoryButton(int count) => count > 0 ? $"卸载历史（{count}）" : "卸载历史";

    // ---------- Waves ----------

    /// <param name="fullTool">Windows 版有完全卸载工具。</param>
    public static string WavesMessage(IReadOnlyList<PluginRow> waves, bool fullTool)
    {
        var names = string.Join("、", waves.Select(w => w.Name).Take(6)) + (waves.Count > 6 ? $" 等 {waves.Count} 款" : "");
        var text = $"{names} 是 Waves 插件。它们共用 WaveShell，不能像普通插件一样删文件，否则其他 Waves 插件也会受影响。\n\n"
            + "只卸载这几款：打开 Waves Central，在已安装的产品里选中它们后卸载。";
        return fullTool
            ? text + "如果 Waves Central 看不到它们，就只能用下面的完全卸载工具。\n\n"
                + "完全卸载全部 Waves：所有 Waves 插件都会消失，残留可能影响之后重装，所以要用专门的工具。打开工具后先只读检查，你确认后才会动手。"
            : text + "卸载完成后回到这里重新扫描，垃圾桶里会显示为已卸载。";
    }

    // ---------- 卸载结果 ----------

    /// <summary>以磁盘上实际还在的文件为准写报告；文件都不在的插件记进卸载历史，并交给调用方移出垃圾桶。</summary>
    public static UninstallReport Report(IReadOnlyList<UninstallChoice> choices, IReadOnlyList<FileOpResult> moved, DateTime utc, Func<string, bool>? exists = null)
    {
        exists ??= RemovalPlanner.PathExists;
        var report = new StringBuilder();
        var gone = new List<long>();
        var history = new List<UninstallRecord>();
        foreach (var choice in choices)
        {
            var row = choice.Plan.Plugin;
            var left = row.Files.Select(f => f.Path).Where(exists).ToList();
            if (left.Count == 0)
            {
                gone.Add(row.Id);
                var way = choice.Method switch
                {
                    UninstallMethod.Official => UninstallWay.Official,
                    UninstallMethod.RecycleFiles => UninstallWay.RecycleFiles,
                    _ => UninstallWay.FoundMissing,
                };
                history.Add(UninstallHistory.From(row, way, choice.Reason, utc));
                report.AppendLine($"{row.Name}：文件已全部移除");
                continue;
            }
            report.AppendLine($"{row.Name}：还剩 {left.Count} 个文件");
            foreach (var path in left)
            {
                var failed = moved.FirstOrDefault(r => !r.Ok && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
                report.AppendLine("  " + path + (failed != null ? $"（{failed.Error}）" : ""));
            }
        }

        var unconfirmed = moved.Count(r => r.Ok && r.Unconfirmed);
        if (unconfirmed > 0)
            report.AppendLine().AppendLine($"有 {unconfirmed} 个文件已不在原位，但没能在{PluginTrash.Name}里确认。");
        if (moved.Any(r => r.Ok))
            report.AppendLine().AppendLine($"移到{PluginTrash.Name}的文件，可以在垃圾桶页的“已移除的文件”里恢复。");
        if (history.Count > 0)
            report.AppendLine().AppendLine($"已记进垃圾桶的“卸载历史”：{string.Join("、", history.Select(h => h.Name))}");
        var status = $"卸载结束：{gone.Count} 款已移除，{choices.Count - gone.Count} 款还有文件";
        return new UninstallReport(report.ToString().TrimEnd(), gone, history, status);
    }
}
