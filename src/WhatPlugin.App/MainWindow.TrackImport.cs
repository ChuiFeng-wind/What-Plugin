using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>从 DAW 工程导入轨道的插件链，生成混音笔记。工程文件只读。</summary>
public partial class MainWindow
{
    private const string BrowseProjectText = "浏览其他工程文件…";

    private async void ImportTracks_Click(object sender, RoutedEventArgs e)
    {
        var path = PickProjectFile();
        if (path == null)
            return;

        var host = ProjectParser.HostOf(path)!.Value;
        IReadOnlyList<ProjectTrack> tracks;
        ImportTrackButton.IsEnabled = false;
        StatusText.Text = $"正在读取“{Path.GetFileName(path)}”…";
        try
        {
            tracks = await Task.Run(() => ProjectParser.ParseTracks(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = $"读取失败：{error.Message}";
            return;
        }
        finally
        {
            ImportTrackButton.IsEnabled = true;
        }

        // 音源和宿主自带的插件不进笔记。列出还剩插件的轨道，以及接到编组、效果轨上的轨道（画路由要用）。
        var matcher = new PluginNameMatcher(_database.LoadPlugins());
        var steps = tracks.ToDictionary(t => t, t => TrackImport.Steps(host, t, matcher, skipStock: true), ReferenceEqualityComparer.Instance);
        bool Routed(ProjectTrack t) => t.SendIds.Count > 0 || t.Sidechains.Count > 0
            || TrackRouting.Destinations(t, tracks).Any(d => d.Role != ChannelRole.Master)
            || t.Role is ChannelRole.Group or ChannelRole.Effect;
        var candidates = tracks.Where(t => steps[t].Count > 0 || Routed(t)).ToList();
        if (candidates.Count == 0)
        {
            StatusText.Text = $"“{Path.GetFileName(path)}”里没有找到带第三方插件的轨道";
            return;
        }

        var installed = new PluginNameMatcher(_database.LoadPlugins().Where(p => !p.IsMissing));
        var items = candidates.Select(t => (TrackLine(t, steps[t], tracks, installed), false)).ToList();
        var hasRouting = tracks.Any(t => t.OutputId != null || t.SendIds.Count > 0 || t.Sidechains.Count > 0);
        var withDestinations = true;
        var picked = Dialogs.PickMany(this, "选择要导入的轨道",
            $"{ProjectParser.HostName(host)} 工程“{Path.GetFileNameWithoutExtension(path)}”里有 {candidates.Count} 条通道用了插件或接了编组、效果轨。"
            + "每条勾选的通道会建一条混音笔记，插件按插槽顺序排好，之后可以给每个插件写应用思路。音源和宿主自带的插件不导入。",
            items, "下一步",
            hasRouting ? "连同输出、发送和侧链接到的编组、效果轨和轨道一起导入，并按工程连好路由（文件夹里可以看路由图）" : null,
            ref withDestinations);
        if (picked is not { Count: > 0 })
            return;

        if (!PickImportFolder(Path.GetFileNameWithoutExtension(path), out var folderId))
            return;

        var chosen = picked.Select(i => candidates[i]).ToList();
        if (hasRouting && withDestinations)
            chosen = TrackRouting.WithDestinations(tracks, chosen, master => steps[master].Count > 0).ToList();
        var keys = chosen.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.t.Id.Length > 0 ? x.t.Id : "#" + x.i, ReferenceEqualityComparer.Instance);
        var byId = chosen.Where(t => t.Id.Length > 0).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => keys[g.First()]);
        string? KeyOf(string? id) => id != null && byId.TryGetValue(id, out var key) ? key : null;

        try
        {
            FlushChain();
            RememberExpansion();
            var notes = chosen.Select(t => new ImportedNote(
                    keys[t], t.Name, TrackImport.Source(path, t.Name), steps[t], t.Role,
                    KeyOf(t.OutputId), t.SendIds.Select(KeyOf).OfType<string>().ToList(),
                    t.Sidechains.Select(s => (Key: KeyOf(s.TargetId), s.Plugin)).Where(s => s.Key != null).Select(s => (s.Key!, s.Plugin)).ToList()))
                .ToList();
            var result = _database.AddImportedNotes(folderId, notes);
            var created = result.Created;
            if (folderId is { } id)
                _collapsedNoteFolders.Remove(id);
            NoteTreeSearch.Text = "";
            LoadChains(selectChain: created.Count == 1 && result.Reused.Count == 0 ? created[0].Id : null,
                selectFolder: created.Count == 1 && result.Reused.Count == 0 ? null : folderId);
            var missing = created.Sum(c => c.Steps.Count(s => installed.Find(s.PluginName) == null));
            var routes = notes.Sum(n => (n.OutputKey != null ? 1 : 0) + (n.SendKeys?.Count ?? 0) + (n.Sidechains?.Count ?? 0));
            StatusText.Text = $"已导入 {created.Count} 条笔记到“{FolderPathText(folderId)}”"
                + (result.Reused.Count > 0 ? $"，{result.Reused.Count} 条以前导入过，保留原内容、只更新路由" : "")
                + (routes > 0 ? $"，连好 {routes} 条路由" : "")
                + (missing > 0 ? $"，其中 {missing} 处插件未安装" : "");
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    private static string TrackLine(ProjectTrack track, IReadOnlyList<ChainStep> steps, IReadOnlyList<ProjectTrack> all, PluginNameMatcher installed)
    {
        var plugins = string.Join(" › ", steps.Select(s => installed.Find(s.PluginName) == null ? s.PluginName + "（未安装）" : s.PluginName));
        var role = track.Role == ChannelRole.Track ? "" : $"［{TrackRouting.RoleText(track.Role)}］";
        var routing = TrackRouting.Describe(track, all);
        return $"{track.Name}{role}　{plugins}" + (routing.Length > 0 ? (plugins.Length > 0 ? "　" : "") + routing : "");
    }

    /// <summary>先列出扫描过的工程（最近修改的在前），也可以浏览别的文件。</summary>
    private string? PickProjectFile()
    {
        var recent = _database.LoadProjectFiles()
            .Where(p => p.Status == ProjectScanStatus.Ok && File.Exists(p.Path))
            .OrderByDescending(p => p.ModifiedUtc)
            .Take(40)
            .ToList();
        if (recent.Count > 0)
        {
            var options = recent
                .Select(p => $"{Path.GetFileName(p.Path)}　{p.ModifiedUtc.ToLocalTime():yyyy-MM-dd}　{Path.GetDirectoryName(p.Path)}")
                .Prepend(BrowseProjectText)
                .ToList();
            var index = Dialogs.PickOne(this, "从工程导入轨道", "选一个工程（只读取，不会改动工程文件）。下面是扫描过的工程，最近修改的在前。",
                options, "下一步", selected: 1);
            if (index == null)
                return null;
            if (index > 0)
                return recent[index.Value - 1].Path;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 DAW 工程",
            Filter = "DAW 工程 (*.cpr;*.npr;*.song;*.rpp;*.als)|*.cpr;*.npr;*.song;*.rpp;*.als",
        };
        var folder = _database.LoadProjectFolders().FirstOrDefault(Directory.Exists);
        if (folder != null)
            dialog.InitialDirectory = folder;
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>默认在当前位置新建一个以工程命名的文件夹；同名文件夹已存在就直接放进去。</summary>
    private bool PickImportFolder(string projectName, out long? folderId)
    {
        folderId = null;
        var parent = TargetFolder;
        var parentPath = parent is { } p ? MixNotes.PathOf(p, _noteFolders) : [];
        var newFolderText = $"“{FolderPathText(parent)}”下的“{projectName}”文件夹";
        var options = new List<(string Text, long? Id, bool Create)> { (newFolderText, null, true), ("最外层", null, false) };
        options.AddRange(_noteFolders
            .Select(f => (FolderPathText(f.Id), (long?)f.Id, false))
            .OrderBy(o => o.Item1, StringComparer.CurrentCulture));
        var index = Dialogs.PickOne(this, "放到哪个文件夹", "导入的笔记放在哪里？和已有笔记同名时会自动加序号，不会覆盖。",
            options.Select(o => o.Text).ToList(), "导入");
        if (index == null)
            return false;
        var choice = options[index.Value];
        folderId = choice.Create ? _database.EnsureFolderPath([.. parentPath, projectName]) : choice.Id;
        return true;
    }
}
