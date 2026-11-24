using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>
/// 左下角的“导出备份”“导入备份”。导出先选用途（给自己备份、分享给别人），再勾要哪些内容；
/// 导入选文件后按扩展名分辨，备份和组合分享文件再勾要导入哪些部分。
/// </summary>
public partial class MainWindow
{
    private static string SafeFileName(string text) => TransferListing.SafeFileName(text);

    // ---------- 导出 ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        FlushItems();
        FlushChain();

        var self = TransferListing.SelfOptions(_database);
        var share = TransferListing.ShareOptions(_database, _plugins.Select(p => p.Row));

        var choice = PartDialogs.Export(this, self, share, TransferListing.RememberShareMode(_database));
        if (choice == null)
            return;
        TransferListing.Remember(_database, choice);
        if (choice.Share)
            ExportShare(choice.Parts.Contains(TransferListing.ShareReviewsId), choice.Parts.Contains(TransferListing.ShareNotesId));
        else
            ExportBackup(TransferListing.ToParts(choice.Parts), choice.Password);
    }

    private void ExportBackup(BackupParts parts, string password)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出备份",
            Filter = "WhatPlugin 备份 (*.json)|*.json",
            FileName = TransferListing.BackupFileName(parts, DateTime.Now),
        };
        if (!FileDialogs.Show(dialog, this))
            return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var content = BackupRestore.Collect(_database, parts, out var locked);
            File.WriteAllText(dialog.FileName, UserDataBackup.Serialize(content, parts.HasFlag(BackupParts.Secrets) ? password : null));
            RecordUsage(() => _database.RecordUsage(UsageKinds.BackupExport));

            StatusText.Text = TransferListing.Exported(content, parts, locked);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>分享给别人：只勾一样时还是原来的评价文件或笔记文件；两样都勾时依次挑选，存成一个文件。</summary>
    private void ExportShare(bool reviews, bool notes)
    {
        if (!notes)
        {
            ExportReviews();
            return;
        }
        if (!reviews)
        {
            ExportNotes();
            return;
        }

        var reviewFile = PrepareReviews();
        if (reviewFile == null)
            return;
        var noteFile = PrepareNotes(reviewFile.Author);
        if (noteFile == null)
            return;

        var save = new SaveFileDialog
        {
            Title = "导出分享",
            FileName = TransferListing.ShareFileName(reviewFile.Author),
            DefaultExt = ShareBundles.Extension,
            Filter = $"WhatPlugin 分享 (*{ShareBundles.Extension})|*{ShareBundles.Extension}",
        };
        if (!FileDialogs.Show(save, this))
            return;
        try
        {
            File.WriteAllText(save.FileName, ShareBundles.Serialize(new ShareBundle(reviewFile, noteFile), DateTime.UtcNow), new UTF8Encoding(false));
            RecordUsage(() => _database.RecordUsage(UsageKinds.ReviewExport));
            StatusText.Text = TransferListing.ShareExported(reviewFile, noteFile, save.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
    }

    // ---------- 导入 ----------

    /// <summary>完整备份、部分备份、别人分享的评价和混音笔记都从这里选文件，按扩展名分辨。</summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        var all = $"*.json;*{ShareBundles.Extension};*{ReviewShare.Extension};*{NoteShare.Extension}";
        var dialog = new OpenFileDialog
        {
            Title = "导入",
            Filter = $"WhatPlugin 备份和分享 ({all})|{all}"
                + "|WhatPlugin 备份 (*.json)|*.json"
                + $"|WhatPlugin 分享：评价和混音笔记 (*{ShareBundles.Extension})|*{ShareBundles.Extension}"
                + $"|WhatPlugin 评价 (*{ReviewShare.Extension})|*{ReviewShare.Extension}"
                + $"|WhatPlugin 混音笔记 (*{NoteShare.Extension})|*{NoteShare.Extension}",
        };
        if (!FileDialogs.Show(dialog, this))
            return;

        switch (ImportKinds.Of(dialog.FileName))
        {
            case ImportKind.Reviews: ImportReviews(dialog.FileName); break;
            case ImportKind.Notes: ImportNotes(dialog.FileName); break;
            case ImportKind.Share: ImportShare(dialog.FileName); break;
            default: ImportBackup(dialog.FileName); break;
        }
    }

    private void ImportShare(string path)
    {
        ShareBundle bundle;
        try
        {
            bundle = ShareBundles.Read(path);
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导入失败：" + error.Message;
            return;
        }

        var options = ShareImport.For(_database).Options(bundle);
        if (options.All(o => !o.Enabled))
        {
            StatusText.Text = ShareImport.NothingText(options);
            return;
        }

        var picked = PartDialogs.Import(this, "导入分享", ShareImport.BundleMessage(bundle), options, "导入", danger: false);
        if (picked == null)
            return;

        var done = new List<string>();
        if (picked.Contains(PartDialogs.ShareReviewsId) && bundle.Reviews != null)
            done.Add(ApplyReviewFile(bundle.Reviews));
        if (picked.Contains(PartDialogs.ShareNotesId) && bundle.Notes != null)
            done.Add(ApplyNoteFile(bundle.Notes));
        StatusText.Text = string.Join("；", done);
    }

    private void ImportBackup(string path)
    {
        try
        {
            var backup = UserDataBackup.Read(File.ReadAllText(path));
            var content = backup.Content;
            var options = TransferListing.BackupOptions(backup);
            var picked = PartDialogs.Import(this, "导入备份", TransferListing.ImportBackupMessage, options, "导入", danger: true);
            if (picked == null)
                return;
            var parts = TransferListing.ToParts(picked);

            BackupSecrets? secrets = null;
            if (parts.HasFlag(BackupParts.Secrets) && backup.HasSecrets)
            {
                var password = BackupDialogs.AskPassword(this, attempt =>
                {
                    (var problem, secrets) = TransferListing.TryDecrypt(backup, attempt);
                    return problem;
                });
                if (password == null)
                    return;
                if (password.Length == 0)
                {
                    secrets = null;
                    parts &= ~BackupParts.Secrets;
                }
            }

            FlushItems();
            FlushChain();
            Mouse.OverrideCursor = Cursors.Wait;
            RestoreSummary result;
            try
            {
                result = BackupRestore.Apply(_database, content, secrets, null, parts);
                RecordUsage(() => _database.RecordUsage(UsageKinds.BackupImport, result.Plugins.Updated + result.Plugins.Added));
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            ReloadAfterImport();

            StatusText.Text = TransferListing.Imported(result, parts, content, secrets != null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusText.Text = "导入失败：" + error.Message;
        }
    }
}
