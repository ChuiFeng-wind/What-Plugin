using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>混音笔记的导出和导入，和评价分享分开，入口在左下角的“导出备份”“导入备份”。导入的笔记放进单独的文件夹，不覆盖自己的笔记。</summary>
public partial class MainWindow
{
    /// <summary>挑要分享的笔记并生成内容，还没有保存。author 是预先填好的昵称（和评价一起分享时沿用评价那一步填的）。</summary>
    private NoteFile? PrepareNotes(string? author = null)
    {
        FlushChain();
        var chains = _database.LoadChains();
        if (chains.Count == 0)
        {
            StatusText.Text = ReviewListing.NoNotes;
            return null;
        }

        var folders = _database.LoadNoteFolders();
        var dialog = new NoteExportDialog(chains, folders, author ?? _database.GetSetting(ReviewAuthorNameSetting) ?? "") { Owner = this };
        if (dialog.ShowDialog() != true)
            return null;

        if (dialog.Author.Length > 0)
            _database.SetSetting(ReviewAuthorNameSetting, dialog.Author);
        return NoteShare.Build(dialog.Author, dialog.SelectedChains, folders, _database.LoadRoutes(), dialog.IncludeRouting, DateTime.UtcNow);
    }

    private void ExportNotes()
    {
        var file = PrepareNotes();
        if (file == null)
            return;
        var save = new SaveFileDialog
        {
            Title = "导出混音笔记",
            FileName = TransferListing.NoteFileName(file.Author),
            DefaultExt = NoteShare.Extension,
            Filter = $"WhatPlugin 混音笔记 (*{NoteShare.Extension})|*{NoteShare.Extension}",
        };
        if (!FileDialogs.Show(save, this))
            return;
        try
        {
            File.WriteAllText(save.FileName, NoteShare.Serialize(file), new UTF8Encoding(false));
            StatusText.Text = TransferListing.NotesExported(file, save.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
    }

    private void ImportNotes(string path)
    {
        NoteFile file;
        try
        {
            file = NoteShare.Read(path);
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导入失败：" + error.Message;
            return;
        }
        if (CheckNoteFile(file) is { } blocked)
        {
            StatusText.Text = blocked;
            return;
        }
        if (!Dialogs.Confirm(this, "导入混音笔记", DescribeNoteFile(file), "导入"))
            return;
        StatusText.Text = ApplyNoteFile(file);
    }

    private static string? CheckNoteFile(NoteFile file) => ShareImport.CheckNotes(file);

    private int MissingPlugins(NoteFile file) => ShareImport.For(_database).MissingPlugins(file);

    private string DescribeNoteFile(NoteFile file) => ShareImport.For(_database).DescribeNotes(file);

    /// <summary>导入到新文件夹，返回给状态栏的说明。</summary>
    private string ApplyNoteFile(NoteFile file)
    {
        FlushChain();
        var missing = MissingPlugins(file);
        try
        {
            var (folder, count) = _database.ImportSharedNotes(NoteShare.RootFolderName(file), file.Notes);
            RememberExpansion();
            LoadChains(selectFolder: folder.Id);
            return ShareImport.NotesImported(folder, count, missing);
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            return "混音笔记导入失败：" + error.Message;
        }
    }
}
