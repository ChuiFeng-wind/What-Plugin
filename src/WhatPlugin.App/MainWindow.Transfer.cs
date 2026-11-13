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
    private const string ExportModeSetting = "export.mode";
    private const string ExportPartsSetting = "export.parts";
    private const string ExportSharePartsSetting = "export.shareParts";

    private static string SafeFileName(string text) =>
        string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---------- 导出 ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
        FlushItems();
        FlushChain();

        var remembered = Enum.TryParse<BackupParts>(_database.GetSetting(ExportPartsSetting), out var saved) ? saved.Valid() : BackupParts.All;
        var plugins = _database.LoadPlugins();
        var reviewed = plugins.Count(p => UserDataBackup.KeepParts(p.User, true, false).HasData);
        var organized = plugins.Count(p => UserDataBackup.KeepParts(p.User, false, true).HasData);
        var notes = _database.LoadChains().Count;
        var wishes = _database.LoadWishes();
        var purchased = wishes.Count(w => w.Status == WishStatus.Purchased);
        var secretCount = wishes.Count(w => w.Account.Length > 0 || w.Password.Length > 0 || w.Serial.Length > 0 || w.SecretsLocked)
            + _database.LoadPlatformAccounts().Count;

        PartOption Self(BackupParts part, string label, string detail, string? parent = null) =>
            new(part.ToString(), label, detail, Checked: remembered.HasFlag(part), Parent: parent);
        var self = new List<PartOption>
        {
            Self(BackupParts.Reviews, "插件评价", $"评分、感受、我的标签、混音属性、适合音色（{reviewed} 款）"),
            Self(BackupParts.Organize, "插件整理", $"分类、厂家、标签修正、隐藏、黑名单原因、获取方式（{organized} 款）"),
            Self(BackupParts.Notes, "混音笔记", $"文件夹、标签和路由（{notes} 条）"),
            Self(BackupParts.Wishes, "心愿单和已购账号", $"心愿 {wishes.Count - purchased} 条，已购 {purchased} 条"),
            Self(BackupParts.Secrets, "账号、密码和序列号", $"{secretCount} 条，含厂家账号；用备份密码加密", nameof(BackupParts.Wishes)),
            Self(BackupParts.SharedReviews, "别人分享的评价", $"{_database.LoadReviewSources().Count} 位分享者，以及采用记录"),
            Self(BackupParts.Settings, "设置", "工程文件夹、VST2 文件夹、忽略的插件、主题和界面状态等"),
            Self(BackupParts.Usage, "使用记录和成就", $"已解锁成就 {_database.LoadAchievements().Count} 个"),
            Self(BackupParts.Uninstalls, "卸载历史", $"{_database.LoadUninstallHistory().Count} 条"),
        };

        var shareRemembered = (_database.GetSetting(ExportSharePartsSetting) ?? $"{PartDialogs.ShareReviewsId},{PartDialogs.ShareNotesId}").Split(',');
        var shareable = _plugins.Count(p => ReviewShare.HasReview(p.Row));
        var share = new List<PartOption>
        {
            new(PartDialogs.ShareReviewsId, "插件评价",
                shareable > 0 ? $"评分、感受和标签，下一步挑插件（写过评价的 {shareable} 款）" : "还没有写过评价，先给插件打分或写点感受",
                Enabled: shareable > 0, Checked: shareRemembered.Contains(PartDialogs.ShareReviewsId)),
            new(PartDialogs.ShareNotesId, "混音笔记",
                notes > 0 ? $"下一步按文件夹勾选，可选带上路由，不含工程名和路径（共 {notes} 条）" : "还没有混音笔记",
                Enabled: notes > 0, Checked: shareRemembered.Contains(PartDialogs.ShareNotesId)),
        };

        var choice = PartDialogs.Export(this, self, share, _database.GetSetting(ExportModeSetting) == "share");
        if (choice == null)
            return;
        _database.SetSetting(ExportModeSetting, choice.Share ? "share" : "self");
        if (choice.Share)
        {
            _database.SetSetting(ExportSharePartsSetting, string.Join(",", choice.Parts));
            ExportShare(choice.Parts.Contains(PartDialogs.ShareReviewsId), choice.Parts.Contains(PartDialogs.ShareNotesId));
            return;
        }
        var parts = choice.Parts.Aggregate(BackupParts.None, (all, id) => all | Enum.Parse<BackupParts>(id)).Valid();
        _database.SetSetting(ExportPartsSetting, ((int)parts).ToString());
        ExportBackup(parts, choice.Password);
    }

    private void ExportBackup(BackupParts parts, string password)
    {
        var full = (parts | BackupParts.Secrets) == BackupParts.All;
        var dialog = new SaveFileDialog
        {
            Title = "导出备份",
            Filter = "WhatPlugin 备份 (*.json)|*.json",
            FileName = full ? $"WhatPlugin-备份-{DateTime.Now:yyyyMMdd}.json" : $"WhatPlugin-部分备份-{DateTime.Now:yyyyMMdd}.json",
        };
        if (!FileDialogs.Show(dialog, this))
            return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var content = BackupRestore.Collect(_database, parts, out var locked);
            File.WriteAllText(dialog.FileName, UserDataBackup.Serialize(content, parts.HasFlag(BackupParts.Secrets) ? password : null));
            RecordUsage(() => _database.RecordUsage(UsageKinds.BackupExport));

            var done = new List<string>();
            if (parts.HasFlag(BackupParts.Reviews) || parts.HasFlag(BackupParts.Organize))
                done.Add($"{content.Plugins.Count} 款插件的" + (parts.HasFlag(BackupParts.Reviews) && parts.HasFlag(BackupParts.Organize) ? "评价和整理" : parts.HasFlag(BackupParts.Reviews) ? "评价" : "整理"));
            if (parts.HasFlag(BackupParts.Notes))
                done.Add($"{content.Chains.Count} 条混音笔记");
            if (parts.HasFlag(BackupParts.Wishes))
                done.Add($"{content.Wishes.Count} 条心愿和已购");
            if (parts.HasFlag(BackupParts.SharedReviews))
                done.Add($"{content.Reviews?.Sources?.Count ?? 0} 位分享者的评价");
            if (parts.HasFlag(BackupParts.Settings))
                done.Add("设置");
            if (parts.HasFlag(BackupParts.Usage))
                done.Add("使用记录和成就");
            if (parts.HasFlag(BackupParts.Uninstalls))
                done.Add($"{content.Uninstalls?.Count ?? 0} 条卸载历史");
            var secrets = content.Secrets == null
                ? "，没有包含账号密码"
                : $"，账号密码 {content.Secrets.WishCount} 条、厂家账号 {content.Secrets.PlatformCount} 个已用备份密码加密"
                  + (locked > 0 ? $"（{locked} 条密码是另一个 Windows 账号加密的，解不开，没有导出）" : "");
            StatusText.Text = "已导出 " + string.Join("、", done) + secrets;
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

        var safeName = SafeFileName(reviewFile.Author);
        var save = new SaveFileDialog
        {
            Title = "导出分享",
            FileName = (safeName.Length > 0 ? safeName + "-" : "") + $"评价和混音笔记{ShareBundles.Extension}",
            DefaultExt = ShareBundles.Extension,
            Filter = $"WhatPlugin 分享 (*{ShareBundles.Extension})|*{ShareBundles.Extension}",
        };
        if (!FileDialogs.Show(save, this))
            return;
        try
        {
            File.WriteAllText(save.FileName, ShareBundles.Serialize(new ShareBundle(reviewFile, noteFile), DateTime.UtcNow), new UTF8Encoding(false));
            RecordUsage(() => _database.RecordUsage(UsageKinds.ReviewExport));
            StatusText.Text = $"已导出 {reviewFile.Reviews.Count} 条评价和 {noteFile.Notes.Count} 条混音笔记到 {save.FileName}";
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

        static string Flat(string text) => text.Replace("\n\n", "");
        var options = new List<PartOption>();
        if (bundle.Reviews is { } reviews)
        {
            var blocked = CheckReviewFile(reviews);
            options.Add(new(PartDialogs.ShareReviewsId, $"插件评价（{reviews.Reviews.Count} 条）", blocked ?? Flat(DescribeReviewFile(reviews)), Enabled: blocked == null));
        }
        else
            options.Add(new(PartDialogs.ShareReviewsId, "插件评价", "这份文件里没有", Enabled: false));
        if (bundle.Notes is { } notes)
        {
            var blocked = CheckNoteFile(notes);
            options.Add(new(PartDialogs.ShareNotesId, $"混音笔记（{notes.Notes.Count} 条）", blocked ?? Flat(DescribeNoteFile(notes)), Enabled: blocked == null));
        }
        else
            options.Add(new(PartDialogs.ShareNotesId, "混音笔记", "这份文件里没有", Enabled: false));
        if (options.All(o => !o.Enabled))
        {
            StatusText.Text = "这份分享文件里没有能导入的内容：" + string.Join("；", options.Select(o => o.Detail));
            return;
        }

        var author = bundle.Reviews?.Author is { Length: > 0 } name ? name : bundle.Notes?.Author ?? "";
        var picked = PartDialogs.Import(this, "导入分享",
            (author.Length > 0 ? $"来自“{author}”的分享。" : "")
            + "勾选要导入的内容。别人的评价单独存放，笔记放进新的文件夹，都不会改动你自己的内容。",
            options, "导入", danger: false);
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
            var settings = content.Settings;

            PartOption Part(BackupParts part, string label, string detail, string? parent = null)
            {
                var present = content.Parts.HasFlag(part) && (part != BackupParts.Secrets || backup.HasSecrets);
                return new(part.ToString(), label, present ? detail : "这份备份里没有", Enabled: present, Parent: parent);
            }
            var options = new List<PartOption>
            {
                Part(BackupParts.Reviews, "插件评价", $"{content.Plugins.Count(p => UserDataBackup.KeepParts(p.ToUserData(), true, false).HasData)} 款插件的评分、感受、标签等"),
                Part(BackupParts.Organize, "插件整理", $"{content.Plugins.Count(p => UserDataBackup.KeepParts(p.ToUserData(), false, true).HasData)} 款插件的分类、厂家、隐藏、获取方式等"),
                Part(BackupParts.Notes, "混音笔记", $"{content.Chains.Count} 条，同名的会被备份内容替换"),
                Part(BackupParts.Wishes, "心愿单和已购账号", $"心愿和已购 {content.Wishes.Count} 条"),
                Part(BackupParts.Secrets, "账号、密码和序列号", "加密保存，下一步输入备份密码；不勾的话本机的账号密码保持不变", nameof(BackupParts.Wishes)),
                Part(BackupParts.SharedReviews, "别人分享的评价", $"{content.Reviews?.Sources?.Count ?? 0} 位分享者，采用记录 {content.Reviews?.Adoptions?.Count ?? 0} 条"),
                Part(BackupParts.Settings, "设置", settings == null ? "没有内容" : $"设置 {settings.Values?.Count ?? 0} 项，工程文件夹 {settings.ProjectFolders?.Count ?? 0} 个，VST2 文件夹 {settings.Vst2Folders?.Count ?? 0} 个，忽略的插件 {settings.IgnoredProjectPlugins?.Count ?? 0} 个"),
                Part(BackupParts.Usage, "使用记录和成就", $"使用记录 {content.Usage?.Events?.Count ?? 0} 条，成就 {content.Usage?.Achievements?.Count ?? 0} 个"),
                Part(BackupParts.Uninstalls, "卸载历史", $"{content.Uninstalls?.Count ?? 0} 条"),
            };
            var picked = PartDialogs.Import(this, "导入备份",
                "勾选要导入的内容。相同条目现有的内容会被覆盖；没勾的部分本机内容不动。文件夹和忽略列表只增不减，这台电脑上不存在的文件夹不会添加。",
                options, "导入", danger: true);
            if (picked == null)
                return;
            var parts = picked.Aggregate(BackupParts.None, (all, id) => all | Enum.Parse<BackupParts>(id)).Valid();

            BackupSecrets? secrets = null;
            if (parts.HasFlag(BackupParts.Secrets) && backup.HasSecrets)
            {
                var password = BackupDialogs.AskPassword(this, attempt =>
                {
                    try
                    {
                        secrets = UserDataBackup.DecryptSecrets(backup, attempt);
                        return null;
                    }
                    catch (BackupPasswordException error)
                    {
                        return error.Message;
                    }
                    catch (FormatException error)
                    {
                        return error.Message;
                    }
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

            var done = new List<string>();
            if (parts.HasFlag(BackupParts.Reviews) || parts.HasFlag(BackupParts.Organize))
                done.Add($"插件更新 {result.Plugins.Updated} 款（另有 {result.Plugins.Added} 款当前未安装，已保留记录）");
            if (parts.HasFlag(BackupParts.Wishes))
                done.Add($"心愿和已购新增 {result.Wishes.Added} 条、更新 {result.Wishes.Updated} 条");
            if (parts.HasFlag(BackupParts.Notes))
                done.Add($"混音笔记 {result.Chains} 条");
            if (parts.HasFlag(BackupParts.Settings) && settings != null)
                done.Add($"设置 {result.Settings} 项、文件夹 {result.Folders} 个" + (result.FoldersMissing > 0 ? $"（{result.FoldersMissing} 个在这台电脑上不存在，未添加）" : ""));
            if (parts.HasFlag(BackupParts.SharedReviews))
                done.Add($"别人的评价 {result.ReviewSources} 位、采用记录 {result.Adoptions} 条");
            if (secrets != null)
                done.Add($"账号密码 {result.WishSecrets} 条、厂家账号 {result.Platforms} 个");
            if (parts.HasFlag(BackupParts.Usage))
                done.Add($"使用记录 {result.UsageEvents} 条、成就 {result.Achievements} 个");
            if (parts.HasFlag(BackupParts.Uninstalls))
                done.Add($"卸载历史 {result.Uninstalls} 条");
            StatusText.Text = "已导入：" + string.Join("；", done);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            StatusText.Text = "导入失败：" + error.Message;
        }
    }
}
