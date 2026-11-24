using System.Text;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

/// <summary>
/// 左下角“导出备份”“导入备份”的流程，对应 Windows 版的 MainWindow.Transfer、Reviews、NoteShare。
/// 导出先选用途（给自己备份、分享给别人），再勾要哪些内容；导入选文件后按扩展名分辨。
/// 不直接碰界面：问用户用 IDialogs，结果用事件告诉主窗口。
/// </summary>
public sealed class TransferActions(PluginDatabase database, IDialogs dialogs)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public IDialogs Dialogs { get; set; } = dialogs;

    /// <summary>工程里的使用情况，导出评价时可以带上使用次数。</summary>
    public Func<IReadOnlyDictionary<string, PluginUsage>> Usage { get; set; } = () => new Dictionary<string, PluginUsage>();

    /// <summary>导出、导入前把还在等待自动保存的内容写进数据库。</summary>
    public Action? Flush { get; set; }

    /// <summary>状态栏文字。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>导入了备份，所有页面都要重新读。</summary>
    public event Action? BackupImported;

    /// <summary>导入了别人的评价，插件详情和设置页的来源列表要重新读。</summary>
    public event Action? ReviewsImported;

    /// <summary>导入了别人的混音笔记，参数是新文件夹。</summary>
    public event Action<NoteFolder>? NotesImported;

    public string Status { get; private set; } = "";

    private void Report(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }

    private void Quietly(Action record)
    {
        try
        {
            record();
        }
        catch (SqliteException)
        {
        }
    }

    // ---------- 导出 ----------

    public async Task ExportAsync()
    {
        Flush?.Invoke();
        var self = TransferListing.SelfOptions(database);
        var share = TransferListing.ShareOptions(database, database.LoadEffects());
        var choice = await Dialogs.ExportParts(self, share, TransferListing.RememberShareMode(database));
        if (choice == null)
            return;
        TransferListing.Remember(database, choice);
        if (choice.Share)
            await ExportShareAsync(choice.Parts.Contains(TransferListing.ShareReviewsId), choice.Parts.Contains(TransferListing.ShareNotesId));
        else
            await ExportBackupAsync(TransferListing.ToParts(choice.Parts), choice.Password);
    }

    private async Task ExportBackupAsync(BackupParts parts, string password)
    {
        var path = await Dialogs.SaveFile("导出备份", TransferListing.BackupFileName(parts, DateTime.Now), "WhatPlugin 备份", ".json");
        if (path == null)
            return;
        try
        {
            var content = BackupRestore.Collect(database, parts, out var locked);
            File.WriteAllText(path, UserDataBackup.Serialize(content, parts.HasFlag(BackupParts.Secrets) ? password : null), Utf8);
            Quietly(() => database.RecordUsage(UsageKinds.BackupExport));
            Report(TransferListing.Exported(content, parts, locked));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report("导出失败：" + error.Message);
        }
    }

    /// <summary>分享给别人：只勾一样时是评价文件或笔记文件；两样都勾时依次挑选，存成一个文件。</summary>
    private async Task ExportShareAsync(bool reviews, bool notes)
    {
        ReviewFile? reviewFile = null;
        if (reviews && (reviewFile = await PrepareReviewsAsync()) == null)
            return;
        NoteFile? noteFile = null;
        if (notes && (noteFile = await PrepareNotesAsync(reviewFile?.Author)) == null)
            return;

        var (title, name, filter, extension, text) = (reviewFile, noteFile) switch
        {
            ({ } r, { } n) => ("导出分享", TransferListing.ShareFileName(r.Author), "WhatPlugin 分享", ShareBundles.Extension,
                ShareBundles.Serialize(new ShareBundle(r, n), DateTime.UtcNow)),
            ({ } r, null) => ("导出评价", TransferListing.ReviewFileName(r.Author), "WhatPlugin 评价", ReviewShare.Extension, ReviewShare.Serialize(r)),
            (null, { } n) => ("导出混音笔记", TransferListing.NoteFileName(n.Author), "WhatPlugin 混音笔记", NoteShare.Extension, NoteShare.Serialize(n)),
            _ => default,
        };
        if (text == null)
            return;
        var path = await Dialogs.SaveFile(title, name, filter, extension);
        if (path == null)
            return;
        try
        {
            File.WriteAllText(path, text, Utf8);
            if (reviewFile != null)
                Quietly(() => database.RecordUsage(UsageKinds.ReviewExport));
            Report(reviewFile != null && noteFile != null ? TransferListing.ShareExported(reviewFile, noteFile, path)
                : reviewFile != null ? TransferListing.ReviewsExported(reviewFile, path)
                : TransferListing.NotesExported(noteFile!, path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report("导出失败：" + error.Message);
        }
    }

    /// <summary>挑要分享的评价并生成内容，还没有保存。取消或没有评价时返回 null。</summary>
    private async Task<ReviewFile?> PrepareReviewsAsync()
    {
        var rows = database.LoadEffects().Where(ReviewShare.HasReview).ToList();
        if (rows.Count == 0)
        {
            Report(ReviewListing.NoReviews);
            return null;
        }
        var picked = await Dialogs.ExportReviews(rows, database.GetSetting(ReviewListing.AuthorNameSetting) ?? "");
        if (picked == null)
            return null;
        var authorId = ReviewListing.RememberAuthor(database, picked.Author);
        return ReviewShare.Build(authorId, picked.Author, picked.Rows, Usage(), picked.Options, DateTime.UtcNow);
    }

    /// <summary>挑要分享的笔记并生成内容。author 是预先填好的昵称（和评价一起分享时沿用评价那一步填的）。</summary>
    private async Task<NoteFile?> PrepareNotesAsync(string? author)
    {
        var chains = database.LoadChains();
        if (chains.Count == 0)
        {
            Report(ReviewListing.NoNotes);
            return null;
        }
        var folders = database.LoadNoteFolders();
        var picked = await Dialogs.ExportNotes(chains, folders, author ?? database.GetSetting(ReviewListing.AuthorNameSetting) ?? "");
        if (picked == null)
            return null;
        if (picked.Author.Length > 0)
            database.SetSetting(ReviewListing.AuthorNameSetting, picked.Author);
        return NoteShare.Build(picked.Author, picked.Chains, folders, database.LoadRoutes(), picked.IncludeRouting, DateTime.UtcNow);
    }

    // ---------- 导入 ----------

    /// <summary>完整备份、部分备份、别人分享的评价和混音笔记都从这里选文件，按扩展名分辨。</summary>
    public async Task ImportAsync()
    {
        Flush?.Invoke();
        var path = await Dialogs.OpenFile("导入", TransferListing.ImportFilterName, TransferListing.ImportExtensions);
        if (path != null)
            await ImportFileAsync(path);
    }

    public async Task ImportFileAsync(string path)
    {
        switch (ImportKinds.Of(path))
        {
            case ImportKind.Reviews: await ImportReviewsAsync(path); break;
            case ImportKind.Notes: await ImportNotesAsync(path); break;
            case ImportKind.Share: await ImportShareAsync(path); break;
            default: await ImportBackupAsync(path); break;
        }
    }

    private async Task ImportReviewsAsync(string path)
    {
        ReviewFile file;
        try
        {
            file = ReviewShare.Read(path);
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            Report("导入失败：" + error.Message);
            return;
        }
        var check = ShareImport.For(database);
        if (check.CheckReviews(file) is { } blocked)
        {
            Report(blocked);
            return;
        }
        if (await Dialogs.Confirm("导入评价", check.DescribeReviews(file), "导入"))
            Report(ApplyReviews(file));
    }

    private async Task ImportNotesAsync(string path)
    {
        NoteFile file;
        try
        {
            file = NoteShare.Read(path);
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            Report("导入失败：" + error.Message);
            return;
        }
        if (ShareImport.CheckNotes(file) is { } blocked)
        {
            Report(blocked);
            return;
        }
        if (await Dialogs.Confirm("导入混音笔记", ShareImport.For(database).DescribeNotes(file), "导入"))
            Report(ApplyNotes(file));
    }

    private async Task ImportShareAsync(string path)
    {
        ShareBundle bundle;
        try
        {
            bundle = ShareBundles.Read(path);
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            Report("导入失败：" + error.Message);
            return;
        }
        var options = ShareImport.For(database).Options(bundle);
        if (options.All(o => !o.Enabled))
        {
            Report(ShareImport.NothingText(options));
            return;
        }
        var picked = await Dialogs.ImportParts("导入分享", ShareImport.BundleMessage(bundle), options, "导入", danger: false);
        if (picked == null)
            return;
        var done = new List<string>();
        if (picked.Contains(TransferListing.ShareReviewsId) && bundle.Reviews != null)
            done.Add(ApplyReviews(bundle.Reviews));
        if (picked.Contains(TransferListing.ShareNotesId) && bundle.Notes != null)
            done.Add(ApplyNotes(bundle.Notes));
        Report(string.Join("；", done));
    }

    /// <summary>保存别人的评价，返回给状态栏的说明。</summary>
    private string ApplyReviews(ReviewFile file)
    {
        try
        {
            database.SaveReviewFile(file, DateTime.UtcNow);
        }
        catch (SqliteException error)
        {
            return "评价导入失败：" + error.Message;
        }
        ReviewsImported?.Invoke();
        return ShareImport.ReviewsImported(file);
    }

    /// <summary>导入到新文件夹，返回给状态栏的说明。</summary>
    private string ApplyNotes(NoteFile file)
    {
        var missing = ShareImport.For(database).MissingPlugins(file);
        try
        {
            var (folder, count) = database.ImportSharedNotes(NoteShare.RootFolderName(file), file.Notes);
            NotesImported?.Invoke(folder);
            return ShareImport.NotesImported(folder, count, missing);
        }
        catch (Exception error) when (error is SqliteException or ArgumentException)
        {
            return "混音笔记导入失败：" + error.Message;
        }
    }

    private async Task ImportBackupAsync(string path)
    {
        LoadedBackup backup;
        try
        {
            backup = UserDataBackup.Read(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            Report("导入失败：" + error.Message);
            return;
        }
        var content = backup.Content;
        var picked = await Dialogs.ImportParts("导入备份", TransferListing.ImportBackupMessage, TransferListing.BackupOptions(backup), "导入", danger: true);
        if (picked == null)
            return;
        var parts = TransferListing.ToParts(picked);

        BackupSecrets? secrets = null;
        if (parts.HasFlag(BackupParts.Secrets) && backup.HasSecrets)
        {
            var password = await Dialogs.BackupPassword(attempt =>
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

        Flush?.Invoke();
        try
        {
            var result = BackupRestore.Apply(database, content, secrets, null, parts);
            Quietly(() => database.RecordUsage(UsageKinds.BackupImport, result.Plugins.Updated + result.Plugins.Added));
            BackupImported?.Invoke();
            Report(TransferListing.Imported(result, parts, content, secrets != null));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or SqliteException)
        {
            Report("导入失败：" + error.Message);
        }
    }
}
