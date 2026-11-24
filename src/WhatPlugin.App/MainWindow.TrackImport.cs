using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>从 DAW 工程导入轨道的插件链，生成混音笔记。工程文件只读。</summary>
public partial class MainWindow
{
    private async void ImportTracks_Click(object sender, RoutedEventArgs e)
    {
        var path = PickProjectFile();
        if (path == null)
            return;

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

        var plan = new TrackImportPlan(path, tracks, _database.LoadPlugins());
        if (plan.Candidates.Count == 0)
        {
            StatusText.Text = plan.NothingFound;
            return;
        }

        var withDestinations = true;
        var picked = Dialogs.PickMany(this, "选择要导入的轨道", plan.Prompt,
            plan.Candidates.Select(t => (plan.Line(t), false)).ToList(), "下一步",
            plan.HasRouting ? TrackImportPlan.RoutingOption : null,
            ref withDestinations);
        if (picked is not { Count: > 0 })
            return;

        if (!PickImportFolder(plan.ProjectName, out var folderId))
            return;

        try
        {
            FlushChain();
            RememberExpansion();
            var notes = plan.Notes(picked, withDestinations);
            var result = _database.AddImportedNotes(folderId, notes);
            var created = result.Created;
            if (folderId is { } id)
                _collapsedNoteFolders.Remove(id);
            NoteTreeSearch.Text = "";
            LoadChains(selectChain: created.Count == 1 && result.Reused.Count == 0 ? created[0].Id : null,
                selectFolder: created.Count == 1 && result.Reused.Count == 0 ? null : folderId);
            StatusText.Text = plan.Result(result, notes, FolderPathText(folderId));
        }
        catch (ArgumentException error)
        {
            StatusText.Text = error.Message;
        }
    }

    /// <summary>先列出扫描过的工程（最近修改的在前），也可以浏览别的文件。</summary>
    private string? PickProjectFile()
    {
        var recent = TrackImportPlan.RecentFiles(_database);
        if (recent.Count > 0)
        {
            var options = recent.Select(TrackImportPlan.FileLine).Prepend(TrackImportPlan.BrowseText).ToList();
            var index = Dialogs.PickOne(this, TrackImportPlan.FilePickTitle, TrackImportPlan.FilePickPrompt, options, "下一步", selected: 1);
            if (index == null)
                return null;
            if (index > 0)
                return recent[index.Value - 1].Path;
        }

        var patterns = string.Join(";", TrackImportPlan.Extensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = "选择 DAW 工程",
            Filter = $"DAW 工程 ({patterns})|{patterns}",
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
        var options = TrackImportPlan.FolderOptions(projectName, parent, _noteFolders);
        var index = Dialogs.PickOne(this, "放到哪个文件夹", TrackImportPlan.FolderPrompt, options.Select(o => o.Text).ToList(), "导入");
        if (index == null)
            return false;
        folderId = TrackImportPlan.ResolveFolder(_database, options[index.Value], projectName, parent, _noteFolders);
        return true;
    }
}
