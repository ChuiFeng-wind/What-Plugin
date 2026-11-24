using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WhatPlugin.Core;

/// <summary>
/// 本地 SQLite 数据库。file_scans 是逐文件的原始结果（增量扫描用），
/// plugins / plugin_files 是合并后的结果。插件文件消失时只标记 is_missing，不删除行，
/// 这样备注不会因为卸载而丢失。扫描只写自动结果的列，从不改动用户填写的列。
/// 扫描在后台线程，界面保存在主线程，而连接不是线程安全的，所以所有公开方法都要持有 _gate。
/// </summary>
public sealed partial class PluginDatabase : IDisposable
{
    private const int SchemaVersion = 16;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public PluginDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        Migrate();
        HookChanges();
    }

    public static string DefaultPath => Path.Combine(
        AppPlatform.LocalDataRoot, "WhatPlugin", "plugins.db");

    public static string LegacyDefaultPath => Path.Combine(
        AppPlatform.LocalDataRoot, "PluginVault", "plugins.db");

    /// <summary>
    /// 软件改名后，把旧文件夹里的数据库复制到新位置。只在新位置还没有数据库时做一次。
    /// 旧文件保留不动，复制失败时也不留下半个文件。
    /// </summary>
    public static bool MigrateLegacyData(string path, string legacyPath)
    {
        if (File.Exists(path) || !File.Exists(legacyPath))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".migrating";
        try
        {
            if (File.Exists(legacyPath + "-wal"))
                File.Copy(legacyPath + "-wal", path + "-wal", overwrite: true);
            File.Copy(legacyPath, temporary, overwrite: true);
            File.Move(temporary, path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            File.Delete(path + "-wal");
            throw;
        }
    }

    /// <summary>
    /// 要打开的数据库。复制旧数据失败时继续用旧位置：宁可下次再试，
    /// 也不能新建一个空库，否则用户会以为数据丢了，之后也不会再迁移。
    /// </summary>
    public static string ResolveDefaultPath(string path, string legacyPath)
    {
        try
        {
            MigrateLegacyData(path, legacyPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(path) && File.Exists(legacyPath))
                return legacyPath;
        }
        return path;
    }

    public static string ResolveDefaultPath() => ResolveDefaultPath(DefaultPath, LegacyDefaultPath);

    private bool ColumnExists(SqliteTransaction transaction, string table, string column)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private void Migrate()
    {
        using var version = _connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current >= SchemaVersion)
            return;

        using var transaction = _connection.BeginTransaction();
        if (current < 1)
        {
            Execute(transaction, """
                CREATE TABLE file_scans (
                    path TEXT PRIMARY KEY COLLATE NOCASE,
                    format INTEGER NOT NULL,
                    arch INTEGER NOT NULL,
                    size INTEGER NOT NULL,
                    modified_utc TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    error TEXT,
                    classes_json TEXT NOT NULL
                );
                CREATE TABLE plugins (
                    id INTEGER PRIMARY KEY,
                    key TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    vendor TEXT NOT NULL,
                    kind INTEGER NOT NULL,
                    tags_json TEXT NOT NULL,
                    tag_source INTEGER NOT NULL,
                    is_missing INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE plugin_files (
                    plugin_id INTEGER NOT NULL REFERENCES plugins(id) ON DELETE CASCADE,
                    path TEXT NOT NULL COLLATE NOCASE,
                    format INTEGER NOT NULL,
                    arch INTEGER NOT NULL,
                    version TEXT NOT NULL,
                    sdk_category TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    error TEXT,
                    PRIMARY KEY (plugin_id, path)
                );
                """);
        }
        if (current < 2)
        {
            Execute(transaction, """
                ALTER TABLE plugins ADD COLUMN rating INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE plugins ADD COLUMN note TEXT NOT NULL DEFAULT '';
                ALTER TABLE plugins ADD COLUMN my_tags_json TEXT NOT NULL DEFAULT '[]';
                ALTER TABLE plugins ADD COLUMN tags_override_json TEXT;
                ALTER TABLE plugins ADD COLUMN kind_override INTEGER;
                ALTER TABLE plugins ADD COLUMN vendor_override TEXT;
                """);
        }
        if (current < 3)
        {
            Execute(transaction, """
                ALTER TABLE plugins ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE plugins ADD COLUMN blacklist_reason TEXT NOT NULL DEFAULT '';
                """);
        }
        if (current < 4)
        {
            Execute(transaction, """
                CREATE TABLE wishes (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    vendor TEXT NOT NULL DEFAULT '',
                    price TEXT,
                    currency TEXT NOT NULL DEFAULT '¥',
                    priority INTEGER NOT NULL DEFAULT 2,
                    reason TEXT NOT NULL DEFAULT '',
                    url TEXT NOT NULL DEFAULT '',
                    note TEXT NOT NULL DEFAULT '',
                    status INTEGER NOT NULL DEFAULT 0,
                    purchased_on TEXT,
                    paid_price TEXT,
                    account TEXT NOT NULL DEFAULT '',
                    password_protected BLOB,
                    serial_protected BLOB,
                    created_utc TEXT NOT NULL
                );
                """);
        }
        if (current < 5)
        {
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS project_folders (
                    path TEXT PRIMARY KEY COLLATE NOCASE
                );
                CREATE TABLE IF NOT EXISTS project_files (
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    host INTEGER NOT NULL,
                    size INTEGER NOT NULL,
                    modified_utc TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    error TEXT,
                    parser_version INTEGER NOT NULL,
                    own_folder INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS project_plugins (
                    project_id INTEGER NOT NULL REFERENCES project_files(id) ON DELETE CASCADE,
                    name TEXT NOT NULL,
                    uid TEXT,
                    instances INTEGER NOT NULL,
                    PRIMARY KEY (project_id, name)
                );
                CREATE TABLE IF NOT EXISTS project_ignored (
                    key TEXT PRIMARY KEY,
                    name TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS favorite_lists (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE
                );
                CREATE TABLE IF NOT EXISTS favorite_items (
                    list_id INTEGER NOT NULL REFERENCES favorite_lists(id) ON DELETE CASCADE,
                    plugin_key TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    PRIMARY KEY (list_id, plugin_key)
                );
                """);
        }
        if (current < 6)
        {
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS vst2_folders (
                    path TEXT PRIMARY KEY COLLATE NOCASE
                );
                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """);
        }
        if (current < 7)
        {
            Execute(transaction, """
                ALTER TABLE plugins ADD COLUMN mix_json TEXT;
                ALTER TABLE wishes ADD COLUMN license_kind INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE wishes ADD COLUMN activated_on TEXT NOT NULL DEFAULT '';
                ALTER TABLE wishes ADD COLUMN activations_left INTEGER;
                ALTER TABLE wishes ADD COLUMN target_price TEXT;
                ALTER TABLE wishes ADD COLUMN lowest_price TEXT;
                ALTER TABLE wishes ADD COLUMN category TEXT NOT NULL DEFAULT '';
                CREATE TABLE IF NOT EXISTS chains (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    note TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS chain_steps (
                    chain_id INTEGER NOT NULL REFERENCES chains(id) ON DELETE CASCADE,
                    position INTEGER NOT NULL,
                    plugin_key TEXT NOT NULL,
                    plugin_name TEXT NOT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (chain_id, position)
                );
                """);
        }
        if (current < 8)
        {
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS favorite_removed (
                    list_id INTEGER NOT NULL REFERENCES favorite_lists(id) ON DELETE CASCADE,
                    plugin_key TEXT NOT NULL,
                    PRIMARY KEY (list_id, plugin_key)
                );
                """);
        }
        if (current < 9)
        {
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS review_sources (
                    id INTEGER PRIMARY KEY,
                    author_id TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    imported_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS shared_reviews (
                    source_id INTEGER NOT NULL,
                    plugin_key TEXT NOT NULL,
                    plugin_name TEXT NOT NULL,
                    vendor TEXT NOT NULL,
                    rating INTEGER NOT NULL,
                    note TEXT NOT NULL,
                    tags_json TEXT NOT NULL,
                    project_count INTEGER,
                    categories_json TEXT,
                    mix_json TEXT,
                    PRIMARY KEY (source_id, plugin_key)
                );
                CREATE TABLE IF NOT EXISTS review_adoptions (
                    plugin_key TEXT PRIMARY KEY,
                    author_name TEXT NOT NULL,
                    rating INTEGER NOT NULL,
                    note TEXT NOT NULL,
                    tags_json TEXT NOT NULL
                );
                """);
        }

        if (current < 10)
        {
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS removed_files (
                    path TEXT PRIMARY KEY COLLATE NOCASE,
                    plugin_key TEXT NOT NULL,
                    plugin_name TEXT NOT NULL,
                    format INTEGER NOT NULL,
                    size INTEGER NOT NULL,
                    removed_utc TEXT NOT NULL
                );
                """);
        }

        if (current < 11)
        {
            // 以前每款单独填账号。已经填过的保持单独，没填的默认跟随厂家账号。
            // 降级过的库可能已经有这一列，先查一下。
            if (!ColumnExists(transaction, "wishes", "own_login"))
            {
                Execute(transaction, """
                    ALTER TABLE wishes ADD COLUMN own_login INTEGER NOT NULL DEFAULT 0;
                    UPDATE wishes SET own_login = 1 WHERE account <> '' OR password_protected IS NOT NULL;
                    """);
            }
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS platform_accounts (
                    platform_key TEXT PRIMARY KEY,
                    platform TEXT NOT NULL,
                    account TEXT NOT NULL DEFAULT '',
                    password_protected BLOB,
                    note TEXT NOT NULL DEFAULT '',
                    updated_utc TEXT NOT NULL
                );
                """);
        }

        if (current < 12)
        {
            if (!ColumnExists(transaction, "plugins", "timbres_override_json"))
                Execute(transaction, "ALTER TABLE plugins ADD COLUMN timbres_override_json TEXT;");
            MoveTimbresOutOfTagOverrides(transaction);

            // 想要程度从三级改成两级：1 我还能忍，2 势在必得。
            Execute(transaction, "UPDATE wishes SET priority = CASE WHEN priority >= 3 THEN 2 ELSE 1 END;");

            // 混音笔记：文件夹可以多层；不同文件夹里可以有同名笔记，所以去掉名称唯一的限制。
            // 连接默认打开了外键检查，删掉旧的 chains 表会连带删掉所有步骤（ON DELETE CASCADE），
            // 而且事务里改不了这个开关。所以先把步骤另存一份，重建之后再放回去。
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS note_folders (
                    id INTEGER PRIMARY KEY,
                    parent_id INTEGER,
                    name TEXT NOT NULL
                );
                CREATE TEMP TABLE chain_steps_keep AS SELECT * FROM chain_steps;
                CREATE TABLE chains_v12 (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    folder_id INTEGER,
                    tags_json TEXT NOT NULL DEFAULT '[]',
                    source TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO chains_v12 (id, name, note) SELECT id, name, note FROM chains;
                DROP TABLE chains;
                ALTER TABLE chains_v12 RENAME TO chains;
                DELETE FROM chain_steps;
                INSERT INTO chain_steps SELECT * FROM chain_steps_keep;
                DROP TABLE chain_steps_keep;
                """);
            if (!ColumnExists(transaction, "chain_steps", "alternates_json"))
                Execute(transaction, "ALTER TABLE chain_steps ADD COLUMN alternates_json TEXT NOT NULL DEFAULT '[]';");
        }

        if (current < 13)
        {
            // 获取方式（成就里的免费、正版统计用）、发票号、使用记录和已解锁的成就。
            if (!ColumnExists(transaction, "plugins", "purchase"))
                Execute(transaction, "ALTER TABLE plugins ADD COLUMN purchase INTEGER NOT NULL DEFAULT 0;");
            if (!ColumnExists(transaction, "wishes", "invoice"))
                Execute(transaction, "ALTER TABLE wishes ADD COLUMN invoice TEXT NOT NULL DEFAULT '';");
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS usage_events (
                    id INTEGER PRIMARY KEY,
                    kind TEXT NOT NULL,
                    utc TEXT NOT NULL,
                    local_date TEXT NOT NULL,
                    local_hour INTEGER NOT NULL,
                    value INTEGER NOT NULL DEFAULT 0,
                    previous INTEGER NOT NULL DEFAULT 0,
                    subject TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS usage_events_kind ON usage_events (kind, local_date);
                CREATE TABLE IF NOT EXISTS achievements (
                    id TEXT PRIMARY KEY,
                    unlocked_utc TEXT NOT NULL
                );
                """);
        }

        if (current < 14)
        {
            // 混音笔记的通道类型（轨道、编组、效果轨、主输出）和笔记之间的路由（主输出、发送、侧链；侧链记下目标插件）。
            if (!ColumnExists(transaction, "chains", "role"))
                Execute(transaction, "ALTER TABLE chains ADD COLUMN role INTEGER NOT NULL DEFAULT 0;");
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS chain_routes (
                    chain_id INTEGER NOT NULL REFERENCES chains(id) ON DELETE CASCADE,
                    target_id INTEGER NOT NULL REFERENCES chains(id) ON DELETE CASCADE,
                    kind INTEGER NOT NULL,
                    plugin TEXT NOT NULL DEFAULT '',
                    position INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (chain_id, target_id, kind, plugin)
                );
                CREATE INDEX IF NOT EXISTS chain_routes_target ON chain_routes (target_id);
                """);
        }

        if (current < 15)
        {
            // 卸载历史：和插件记录分开，插件记录删了也还在。uninstalled_utc 为空表示日期未知。
            Execute(transaction, """
                CREATE TABLE IF NOT EXISTS uninstall_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    plugin_key TEXT NOT NULL,
                    name TEXT NOT NULL,
                    vendor TEXT NOT NULL DEFAULT '',
                    way INTEGER NOT NULL DEFAULT 0,
                    reason TEXT NOT NULL DEFAULT '',
                    uninstalled_utc TEXT NOT NULL DEFAULT '',
                    trash_reason TEXT NOT NULL DEFAULT '',
                    rating INTEGER NOT NULL DEFAULT 0,
                    note TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS uninstall_history_key ON uninstall_history (plugin_key);
                """);
            // 1.2.3 起留在垃圾桶里、已经卸载的插件补进历史，日期未知。
            Execute(transaction, """
                INSERT INTO uninstall_history (plugin_key, name, vendor, way, reason, uninstalled_utc, trash_reason, rating, note)
                SELECT key, name, COALESCE(NULLIF(vendor_override, ''), vendor), 0, '', '', blacklist_reason, rating, note
                FROM plugins WHERE is_missing = 1 AND hidden = 1;
                """);
        }

        if (current < 16)
        {
            // Mac 的废纸篓没有回收站那样的记录可查，移走时记下文件在废纸篓里的位置，恢复时用。
            if (!ColumnExists(transaction, "removed_files", "trash_path"))
                Execute(transaction, "ALTER TABLE removed_files ADD COLUMN trash_path TEXT NOT NULL DEFAULT '';");
        }

        Execute(transaction, $"PRAGMA user_version = {SchemaVersion};");
        transaction.Commit();
    }

    /// <summary>旧版把人声、鼓、吉他/贝斯当功能分类，手动改过分类的插件要把这几个挪到适合音色里。</summary>
    private void MoveTimbresOutOfTagOverrides(SqliteTransaction transaction)
    {
        var moves = new List<(long Id, string Tags, string Timbres)>();
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, tags_override_json FROM plugins WHERE tags_override_json IS NOT NULL AND timbres_override_json IS NULL";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var tags = ReadTags(reader.GetString(1));
                var timbres = Classifier.TimbresIn(tags);
                var functions = Classifier.FunctionTagsIn(tags).ToList();
                if (functions.Count == tags.Count)
                    continue;
                moves.Add((reader.GetInt64(0), JsonSerializer.Serialize(functions), JsonSerializer.Serialize(timbres)));
            }
        }
        foreach (var (id, tags, timbres) in moves)
        {
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE plugins SET tags_override_json = $tags, timbres_override_json = $timbres WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$tags", tags);
            update.Parameters.AddWithValue("$timbres", timbres);
            update.ExecuteNonQuery();
        }
    }

    private void Execute(SqliteTransaction transaction, string sql)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public Dictionary<string, FileScanResult> LoadFileScans()
    {
        lock (_gate)
        {
            var results = new Dictionary<string, FileScanResult>(StringComparer.OrdinalIgnoreCase);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, format, arch, size, modified_utc, status, error, classes_json FROM file_scans";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                results[path] = new FileScanResult(
                    path,
                    (PluginFormat)reader.GetInt32(1),
                    (Arch)reader.GetInt32(2),
                    reader.GetInt64(3),
                    DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    (ScanStatus)reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    JsonSerializer.Deserialize<List<PluginClassInfo>>(reader.GetString(7)) ?? []);
            }
            return results;
        }
    }

    /// <summary>每扫完一个文件就保存，中途取消或崩溃时已有结果不会丢。</summary>
    public void SaveFileScan(FileScanResult scan)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO file_scans (path, format, arch, size, modified_utc, status, error, classes_json)
                VALUES ($path, $format, $arch, $size, $modified, $status, $error, $classes)
                ON CONFLICT(path) DO UPDATE SET
                    format = excluded.format, arch = excluded.arch, size = excluded.size,
                    modified_utc = excluded.modified_utc, status = excluded.status,
                    error = excluded.error, classes_json = excluded.classes_json
                """;
            command.Parameters.AddWithValue("$path", scan.Path);
            command.Parameters.AddWithValue("$format", (int)scan.Format);
            command.Parameters.AddWithValue("$arch", (int)scan.Arch);
            command.Parameters.AddWithValue("$size", scan.Size);
            command.Parameters.AddWithValue("$modified", scan.ModifiedUtc.ToString("O"));
            command.Parameters.AddWithValue("$status", (int)scan.Status);
            command.Parameters.AddWithValue("$error", (object?)scan.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$classes", JsonSerializer.Serialize(scan.Classes));
            command.ExecuteNonQuery();
        }
    }

    public void DeleteFileScan(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM file_scans WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>写入合并结果：更新已有插件、新增插件，并把不再出现的插件标记为缺失。</summary>
    public void SaveMerged(IReadOnlyList<MergedPlugin> plugins)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            Execute(transaction, "DELETE FROM plugin_files; UPDATE plugins SET is_missing = 1;");

            foreach (var plugin in plugins)
            {
                using var upsert = _connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO plugins (key, name, vendor, kind, tags_json, tag_source, is_missing)
                    VALUES ($key, $name, $vendor, $kind, $tags, $source, 0)
                    ON CONFLICT(key) DO UPDATE SET
                        name = excluded.name, vendor = excluded.vendor, kind = excluded.kind,
                        tags_json = excluded.tags_json, tag_source = excluded.tag_source, is_missing = 0
                    RETURNING id
                    """;
                upsert.Parameters.AddWithValue("$key", plugin.Key);
                upsert.Parameters.AddWithValue("$name", plugin.Name);
                upsert.Parameters.AddWithValue("$vendor", plugin.Vendor);
                upsert.Parameters.AddWithValue("$kind", (int)plugin.Classification.Kind);
                upsert.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(plugin.Classification.Tags));
                upsert.Parameters.AddWithValue("$source", (int)plugin.Classification.Source);
                var id = Convert.ToInt64(upsert.ExecuteScalar());

                foreach (var file in plugin.Files)
                {
                    using var insert = _connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO plugin_files (plugin_id, path, format, arch, version, sdk_category, status, error)
                        VALUES ($id, $path, $format, $arch, $version, $category, $status, $error)
                        """;
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$path", file.Path);
                    insert.Parameters.AddWithValue("$format", (int)file.Format);
                    insert.Parameters.AddWithValue("$arch", (int)file.Arch);
                    insert.Parameters.AddWithValue("$version", file.Version);
                    insert.Parameters.AddWithValue("$category", file.SdkCategory);
                    insert.Parameters.AddWithValue("$status", (int)file.Status);
                    insert.Parameters.AddWithValue("$error", (object?)file.Error ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    public void SaveUserData(long pluginId, PluginUserData data)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE plugins SET
                    rating = $rating, note = $note, my_tags_json = $myTags,
                    tags_override_json = $tagsOverride, kind_override = $kindOverride, vendor_override = $vendorOverride,
                    hidden = $hidden, blacklist_reason = $reason, mix_json = $mix,
                    timbres_override_json = $timbresOverride, purchase = $purchase
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", pluginId);
            AddUserDataParameters(command, data.Normalized());
            command.ExecuteNonQuery();
        }
    }

    // ---------- 移到回收站的插件文件，用于恢复和“又被装回来”的提示。 ----------

    /// <param name="trashPath">Mac：文件在废纸篓里的位置。Windows 不用（从回收站记录里找）。</param>
    public void SaveRemovedFile(string pluginKey, string pluginName, string path, PluginFormat format, long size, DateTime removedUtc, string trashPath = "")
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO removed_files (path, plugin_key, plugin_name, format, size, removed_utc, trash_path)
                VALUES ($path, $key, $name, $format, $size, $removed, $trash)
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$key", pluginKey);
            command.Parameters.AddWithValue("$name", pluginName);
            command.Parameters.AddWithValue("$format", (int)format);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$removed", removedUtc.ToString("O"));
            command.Parameters.AddWithValue("$trash", trashPath);
            command.ExecuteNonQuery();
        }
    }

    public List<RemovedFile> LoadRemovedFiles()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, plugin_key, plugin_name, format, size, removed_utc, trash_path FROM removed_files ORDER BY removed_utc DESC, path";
            using var reader = command.ExecuteReader();
            var files = new List<RemovedFile>();
            while (reader.Read())
            {
                files.Add(new RemovedFile(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (PluginFormat)reader.GetInt32(3),
                    reader.GetInt64(4),
                    DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetString(6)));
            }
            return files;
        }
    }

    public void DeleteRemovedFile(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM removed_files WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 垃圾桶里的插件卸载以后调用。写过移入原因、评分、感受等内容的留在垃圾桶里（显示为已卸载），
    /// 免得以后忘了为什么不要它；什么都没写的直接删除。文件还在（比如扫描被取消）的不处理。
    /// </summary>
    public void ForgetUninstalled(IEnumerable<long> pluginIds)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var id in pluginIds)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM plugins WHERE id = $id AND is_missing = 1
                        AND rating = 0 AND note = '' AND blacklist_reason = '' AND my_tags_json = '[]' AND tags_override_json IS NULL
                        AND kind_override IS NULL AND vendor_override IS NULL AND mix_json IS NULL AND timbres_override_json IS NULL AND purchase = 0
                        AND key NOT IN (SELECT plugin_key FROM favorite_items)
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>手动删掉一条已卸载插件的记录（垃圾桶里“删除记录”）。文件还在的不删。</summary>
    public bool DeleteUninstalledRecord(long pluginId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var files = _connection.CreateCommand();
            files.Transaction = transaction;
            files.CommandText = "DELETE FROM plugin_files WHERE plugin_id = $id AND (SELECT is_missing FROM plugins WHERE id = $id) = 1";
            files.Parameters.AddWithValue("$id", pluginId);
            files.ExecuteNonQuery();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM plugins WHERE id = $id AND is_missing = 1";
            command.Parameters.AddWithValue("$id", pluginId);
            var deleted = command.ExecuteNonQuery() > 0;
            transaction.Commit();
            return deleted;
        }
    }

    // ---------- 别人分享的评价。单独存放，不写入 plugins 表，所以不影响推荐和自己的评价。 ----------

    /// <summary>同一个分享者再次导入时，整份替换他之前的评价。</summary>
    public ReviewSource SaveReviewFile(ReviewFile file, DateTime importedUtc)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            long id;
            using (var source = _connection.CreateCommand())
            {
                source.Transaction = transaction;
                source.CommandText = """
                    INSERT INTO review_sources (author_id, name, imported_utc) VALUES ($authorId, $name, $imported)
                    ON CONFLICT(author_id) DO UPDATE SET name = excluded.name, imported_utc = excluded.imported_utc
                    RETURNING id
                    """;
                source.Parameters.AddWithValue("$authorId", file.AuthorId);
                source.Parameters.AddWithValue("$name", file.Author);
                source.Parameters.AddWithValue("$imported", importedUtc.ToString("O"));
                id = (long)source.ExecuteScalar()!;
            }

            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM shared_reviews WHERE source_id = $id";
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }

            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO shared_reviews
                    (source_id, plugin_key, plugin_name, vendor, rating, note, tags_json, project_count, categories_json, mix_json)
                VALUES ($id, $key, $name, $vendor, $rating, $note, $tags, $projects, $categories, $mix)
                """;
            foreach (var review in file.Reviews)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$key", review.Key);
                insert.Parameters.AddWithValue("$name", review.Name);
                insert.Parameters.AddWithValue("$vendor", review.Vendor);
                insert.Parameters.AddWithValue("$rating", review.Rating);
                insert.Parameters.AddWithValue("$note", review.Note);
                insert.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(review.Tags));
                insert.Parameters.AddWithValue("$projects", review.ProjectCount == null ? DBNull.Value : review.ProjectCount.Value);
                insert.Parameters.AddWithValue("$categories", review.Categories == null ? DBNull.Value : JsonSerializer.Serialize(review.Categories));
                insert.Parameters.AddWithValue("$mix", review.Mix == null ? DBNull.Value : JsonSerializer.Serialize(review.Mix));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new ReviewSource(id, file.AuthorId, file.Author, importedUtc, file.Reviews.Count);
        }
    }

    public List<ReviewSource> LoadReviewSources()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT s.id, s.author_id, s.name, s.imported_utc, COUNT(r.plugin_key)
                FROM review_sources s LEFT JOIN shared_reviews r ON r.source_id = s.id
                GROUP BY s.id ORDER BY s.name COLLATE NOCASE, s.id
                """;
            using var reader = command.ExecuteReader();
            var sources = new List<ReviewSource>();
            while (reader.Read())
            {
                sources.Add(new ReviewSource(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetInt32(4)));
            }
            return sources;
        }
    }

    public List<AttributedReview> LoadSharedReviews()
    {
        var sources = LoadReviewSources().ToDictionary(s => s.Id);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT source_id, plugin_key, plugin_name, vendor, rating, note, tags_json, project_count, categories_json, mix_json
                FROM shared_reviews
                """;
            using var reader = command.ExecuteReader();
            var reviews = new List<AttributedReview>();
            while (reader.Read())
            {
                if (!sources.TryGetValue(reader.GetInt64(0), out var source))
                    continue;
                reviews.Add(new AttributedReview(source, new SharedReview(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    ReadTags(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : ReadTags(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<MixAttributes>(reader.GetString(9)))));
            }
            return reviews;
        }
    }

    public void RemoveReviewSource(long id)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var sql in new[] { "DELETE FROM shared_reviews WHERE source_id = $id", "DELETE FROM review_sources WHERE id = $id" })
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public Dictionary<string, ReviewAdoption> LoadReviewAdoptions()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT plugin_key, author_name, rating, note, tags_json FROM review_adoptions";
            using var reader = command.ExecuteReader();
            var adoptions = new Dictionary<string, ReviewAdoption>();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                adoptions[key] = new ReviewAdoption(key, reader.GetString(1), reader.GetInt32(2), reader.GetString(3), ReadTags(reader.GetString(4)));
            }
            return adoptions;
        }
    }

    /// <summary>从备份恢复采用记录，按插件覆盖。</summary>
    public int ImportReviewAdoptions(IEnumerable<ReviewAdoption> adoptions)
    {
        lock (_gate)
        {
            var count = 0;
            using var transaction = _connection.BeginTransaction();
            foreach (var adoption in adoptions)
            {
                if (string.IsNullOrWhiteSpace(adoption.PluginKey))
                    continue;
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO review_adoptions (plugin_key, author_name, rating, note, tags_json)
                    VALUES ($key, $author, $rating, $note, $tags)
                    """;
                insert.Parameters.AddWithValue("$key", adoption.PluginKey);
                insert.Parameters.AddWithValue("$author", adoption.AuthorName ?? "");
                insert.Parameters.AddWithValue("$rating", adoption.Rating);
                insert.Parameters.AddWithValue("$note", adoption.Note ?? "");
                insert.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(adoption.Tags ?? []));
                insert.ExecuteNonQuery();
                count++;
            }
            transaction.Commit();
            return count;
        }
    }

    /// <summary>把别人的评价写入自己的数据，同时记下来源和当时的内容。两件事要么都成功，要么都不发生。</summary>
    public (PluginUserData Data, ReviewAdoption Adoption) AdoptReview(PluginRow row, SharedReview review, string authorName)
    {
        var data = (row.User with { Rating = review.Rating, Note = review.Note, MyTags = review.Tags }).Normalized();
        var adoption = new ReviewAdoption(row.Key, authorName, data.Rating, data.Note, data.MyTags);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE plugins SET rating = $rating, note = $note, my_tags_json = $myTags WHERE id = $id";
                update.Parameters.AddWithValue("$id", row.Id);
                update.Parameters.AddWithValue("$rating", data.Rating);
                update.Parameters.AddWithValue("$note", data.Note);
                update.Parameters.AddWithValue("$myTags", JsonSerializer.Serialize(data.MyTags));
                update.ExecuteNonQuery();
            }
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO review_adoptions (plugin_key, author_name, rating, note, tags_json)
                    VALUES ($key, $author, $rating, $note, $tags)
                    """;
                insert.Parameters.AddWithValue("$key", row.Key);
                insert.Parameters.AddWithValue("$author", authorName);
                insert.Parameters.AddWithValue("$rating", adoption.Rating);
                insert.Parameters.AddWithValue("$note", adoption.Note);
                insert.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(adoption.Tags));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        return (data, adoption);
    }

    /// <summary>
    /// 软件显示和推荐用的插件：乐器插件在这里过滤掉。记录仍留在数据库里，
    /// 所以导出备份时不会丢失写过的内容，也能通过资料库纠正误判。
    /// </summary>
    public IReadOnlyList<PluginRow> LoadEffects() =>
        LoadPlugins().Where(p => p.Kind != PluginKind.Instrument).ToList();

    /// <summary>
    /// 恢复备份。已有的插件覆盖其用户内容；数据库里没有的插件也保留下来（标记为缺失），
    /// 等它被扫描到时自动对上，备注不会因为插件暂时没装而丢掉。
    /// </summary>
    public ImportResult ImportUserData(IEnumerable<BackupEntry> entries)
    {
        lock (_gate)
        {
            var updated = 0;
            var added = 0;
            using var transaction = _connection.BeginTransaction();
            foreach (var entry in entries)
            {
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE plugins SET
                        rating = $rating, note = $note, my_tags_json = $myTags,
                        tags_override_json = $tagsOverride, kind_override = $kindOverride, vendor_override = $vendorOverride,
                        hidden = $hidden, blacklist_reason = $reason, mix_json = COALESCE($mix, mix_json),
                        timbres_override_json = $timbresOverride, purchase = COALESCE($purchaseImport, purchase)
                    WHERE key = $key
                    """;
                update.Parameters.AddWithValue("$key", entry.Key);
                AddUserDataParameters(update, entry.ToUserData());
                // 旧备份没有获取方式，保留本机标过的。
                update.Parameters.AddWithValue("$purchaseImport", entry.Purchase == null ? DBNull.Value : (int)entry.Purchase.Value);
                if (update.ExecuteNonQuery() > 0)
                {
                    updated++;
                    continue;
                }

                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO plugins (key, name, vendor, kind, tags_json, tag_source, is_missing,
                                         rating, note, my_tags_json, tags_override_json, kind_override, vendor_override,
                                         hidden, blacklist_reason, mix_json, timbres_override_json, purchase)
                    VALUES ($key, $name, $vendor, 0, '[]', 0, 1,
                            $rating, $note, $myTags, $tagsOverride, $kindOverride, $vendorOverride,
                            $hidden, $reason, $mix, $timbresOverride, $purchase)
                    """;
                insert.Parameters.AddWithValue("$key", entry.Key);
                insert.Parameters.AddWithValue("$name", entry.Name ?? entry.Key);
                insert.Parameters.AddWithValue("$vendor", entry.Vendor ?? "");
                AddUserDataParameters(insert, entry.ToUserData());
                insert.ExecuteNonQuery();
                added++;
            }
            transaction.Commit();
            return new ImportResult(updated, added);
        }
    }

    private static void AddUserDataParameters(SqliteCommand command, PluginUserData data)
    {
        command.Parameters.AddWithValue("$rating", data.Rating);
        command.Parameters.AddWithValue("$note", data.Note);
        command.Parameters.AddWithValue("$myTags", JsonSerializer.Serialize(data.MyTags));
        command.Parameters.AddWithValue("$tagsOverride", data.TagsOverride == null ? DBNull.Value : JsonSerializer.Serialize(data.TagsOverride));
        command.Parameters.AddWithValue("$kindOverride", data.KindOverride == null ? DBNull.Value : (int)data.KindOverride);
        command.Parameters.AddWithValue("$vendorOverride", (object?)data.VendorOverride ?? DBNull.Value);
        command.Parameters.AddWithValue("$hidden", data.Hidden ? 1 : 0);
        command.Parameters.AddWithValue("$reason", data.BlacklistReason);
        command.Parameters.AddWithValue("$mix", data.Mix == null ? DBNull.Value : JsonSerializer.Serialize(data.Mix));
        command.Parameters.AddWithValue("$timbresOverride", data.TimbresOverride == null ? DBNull.Value : JsonSerializer.Serialize(data.TimbresOverride));
        command.Parameters.AddWithValue("$purchase", (int)data.Purchase);
    }

    public List<PluginRow> LoadPlugins()
    {
        lock (_gate)
        {
            var files = new Dictionary<long, List<MergedFile>>();
            using (var fileCommand = _connection.CreateCommand())
            {
                fileCommand.CommandText = "SELECT plugin_id, path, format, arch, version, sdk_category, status, error FROM plugin_files ORDER BY format, path";
                using var reader = fileCommand.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (!files.TryGetValue(id, out var list))
                        files[id] = list = [];
                    list.Add(new MergedFile(
                        reader.GetString(1),
                        (PluginFormat)reader.GetInt32(2),
                        (Arch)reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        (ScanStatus)reader.GetInt32(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7)));
                }
            }

            var plugins = new List<PluginRow>();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, key, name, vendor, kind, tags_json, tag_source, is_missing,
                       rating, note, my_tags_json, tags_override_json, kind_override, vendor_override,
                       hidden, blacklist_reason, mix_json, timbres_override_json, purchase
                FROM plugins
                ORDER BY COALESCE(vendor_override, vendor) COLLATE NOCASE, name COLLATE NOCASE
                """;
            using var pluginReader = command.ExecuteReader();
            while (pluginReader.Read())
            {
                var id = pluginReader.GetInt64(0);
                plugins.Add(new PluginRow(
                    id,
                    pluginReader.GetString(1),
                    pluginReader.GetString(2),
                    pluginReader.GetString(3),
                    (PluginKind)pluginReader.GetInt32(4),
                    ReadTags(pluginReader.GetString(5)),
                    (ClassificationSource)pluginReader.GetInt32(6),
                    pluginReader.GetInt64(7) != 0,
                    files.GetValueOrDefault(id) ?? [],
                    new PluginUserData(
                        pluginReader.GetInt32(8),
                        pluginReader.GetString(9),
                        ReadTags(pluginReader.GetString(10)),
                        pluginReader.IsDBNull(11) ? null : ReadTags(pluginReader.GetString(11)),
                        pluginReader.IsDBNull(12) ? null : (PluginKind)pluginReader.GetInt32(12),
                        pluginReader.IsDBNull(13) ? null : pluginReader.GetString(13),
                        pluginReader.GetInt64(14) != 0,
                        pluginReader.GetString(15),
                        pluginReader.IsDBNull(16) ? null : JsonSerializer.Deserialize<MixAttributes>(pluginReader.GetString(16)),
                        pluginReader.IsDBNull(17) ? null : ReadTags(pluginReader.GetString(17)),
                        (PurchaseKind)pluginReader.GetInt32(18))));
            }
            return plugins;
        }
    }

    // ---------- 心愿单和已购账号 ----------

    public List<WishItem> LoadWishes()
    {
        lock (_gate)
        {
            var items = new List<WishItem>();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, vendor, price, currency, priority, reason, url, note, status,
                       purchased_on, paid_price, account, password_protected, serial_protected, created_utc,
                       license_kind, activated_on, activations_left, target_price, lowest_price, category, own_login, invoice
                FROM wishes
                ORDER BY vendor COLLATE NOCASE, name COLLATE NOCASE
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var password = ReadSecret(reader, 13, out var passwordLocked);
                var serial = ReadSecret(reader, 14, out var serialLocked);
                items.Add(new WishItem(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ReadDecimal(reader, 3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    (WishStatus)reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : DateOnly.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                    ReadDecimal(reader, 11),
                    reader.GetString(12),
                    password,
                    serial,
                    DateTime.Parse(reader.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    passwordLocked || serialLocked,
                    (LicenseKind)reader.GetInt32(16),
                    reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetInt32(18),
                    ReadDecimal(reader, 19),
                    ReadDecimal(reader, 20),
                    reader.GetString(21),
                    reader.GetInt64(22) != 0,
                    reader.GetString(23)));
            }
            return items;
        }
    }

    public WishItem AddWish(WishItem item)
    {
        lock (_gate)
        {
            var created = item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc;
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO wishes (name, vendor, price, currency, priority, reason, url, note, status, purchased_on, paid_price,
                                    account, password_protected, serial_protected, created_utc,
                                    license_kind, activated_on, activations_left, target_price, lowest_price, category, own_login, invoice)
                VALUES ($name, $vendor, $price, $currency, $priority, $reason, $url, $note, $status, $purchasedOn, $paid,
                        $account, $password, $serial, $created,
                        $licenseKind, $activatedOn, $activationsLeft, $targetPrice, $lowestPrice, $category, $ownLogin, $invoice)
                RETURNING id
                """;
            AddWishParameters(command, item);
            command.Parameters.AddWithValue("$password", ProtectOrNull(item.Password));
            command.Parameters.AddWithValue("$serial", ProtectOrNull(item.Serial));
            command.Parameters.AddWithValue("$created", created.ToString("O"));
            var id = Convert.ToInt64(command.ExecuteScalar());
            return item with { Id = id, CreatedUtc = created };
        }
    }

    /// <summary>解不开的密码（另一个 Windows 账号加密的）不覆盖，否则一次保存就把它变成空。</summary>
    public void SaveWish(WishItem item)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var secrets = item.SecretsLocked ? "" : ", password_protected = $password, serial_protected = $serial";
            command.CommandText = $"""
                UPDATE wishes SET
                    name = $name, vendor = $vendor, price = $price, currency = $currency, priority = $priority,
                    reason = $reason, url = $url, note = $note, status = $status, purchased_on = $purchasedOn,
                    paid_price = $paid, account = $account,
                    license_kind = $licenseKind, activated_on = $activatedOn, activations_left = $activationsLeft,
                    target_price = $targetPrice, lowest_price = $lowestPrice, category = $category, own_login = $ownLogin,
                    invoice = $invoice{secrets}
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            AddWishParameters(command, item);
            if (!item.SecretsLocked)
            {
                command.Parameters.AddWithValue("$password", ProtectOrNull(item.Password));
                command.Parameters.AddWithValue("$serial", ProtectOrNull(item.Serial));
            }
            command.ExecuteNonQuery();
        }
    }

    public void DeleteWishes(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var id in ids)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM wishes WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>
    /// 给多款插件填同一组登录信息，留空的项不改动。序列号每款单独存。
    /// 账号密码按平台处理：平台还没有账号就存成平台账号，这几款跟随它；
    /// 平台账号和填的一样也跟随；不一样或厂家未填的，存成这一款单独的账号。
    /// </summary>
    public LoginAssignment SetLogins(IEnumerable<long> ids, string account, string password, string serial)
    {
        lock (_gate)
        {
            var wanted = ids.ToHashSet();
            var items = LoadWishes().Where(w => wanted.Contains(w.Id)).Select(w => (w.Id, w.Vendor, w.OwnLogin));
            using var transaction = _connection.BeginTransaction();
            var result = AssignLogins(transaction, items, account, password, serial);
            transaction.Commit();
            return result;
        }
    }

    private LoginAssignment AssignLogins(SqliteTransaction transaction, IEnumerable<(long Id, string Vendor, bool OwnLogin)> items,
        string account, string password, string serial)
    {
        var platforms = PurchaseLogin.ByKey(LoadPlatformAccountsUnlocked());
        var created = new List<string>();
        var shared = 0;
        var own = 0;
        foreach (var (id, vendor, ownLogin) in items)
        {
            if (serial.Length > 0)
                SetLogin(transaction, id, "", "", serial);
            if (account.Length == 0 && password.Length == 0)
                continue;

            var platform = VendorCatalog.PlatformOf(vendor);
            var key = VendorCatalog.PlatformKey(platform);
            if (platform.Length > 0 && !ownLogin)
            {
                if (!platforms.TryGetValue(key, out var existing) || (existing.Account.Length == 0 && existing.Password.Length == 0 && !existing.Locked))
                {
                    var saved = new PlatformAccount(platform, account, password, existing?.Note ?? "");
                    SavePlatformAccount(transaction, saved);
                    platforms[key] = saved;
                    created.Add(platform);
                    shared++;
                    continue;
                }
                if (!existing.Locked
                    && (account.Length == 0 || existing.Account == account)
                    && (password.Length == 0 || existing.Password == password))
                {
                    shared++;
                    continue;
                }
            }
            SetLogin(transaction, id, account, password, "");
            SetOwnLogin(transaction, id, true);
            own++;
        }
        return new LoginAssignment(created, shared, own);
    }

    private void SetOwnLogin(SqliteTransaction transaction, long id, bool own)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE wishes SET own_login = $own WHERE id = $id";
        command.Parameters.AddWithValue("$own", own ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // ---------- 平台账号 ----------

    public List<PlatformAccount> LoadPlatformAccounts()
    {
        lock (_gate)
            return LoadPlatformAccountsUnlocked();
    }

    private List<PlatformAccount> LoadPlatformAccountsUnlocked()
    {
        var accounts = new List<PlatformAccount>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT platform, account, password_protected, note FROM platform_accounts ORDER BY platform COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var password = ReadSecret(reader, 2, out var locked);
            accounts.Add(new PlatformAccount(reader.GetString(0), reader.GetString(1), password, reader.GetString(3), locked));
        }
        return accounts;
    }

    /// <summary>保存一个平台的账号。解不开的旧密码（Locked）不覆盖。内容全空时删除这条。</summary>
    public void SavePlatformAccount(PlatformAccount account)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            SavePlatformAccount(transaction, account);
            transaction.Commit();
        }
    }

    public void DeletePlatformAccount(string platform)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM platform_accounts WHERE platform_key = $key";
            command.Parameters.AddWithValue("$key", VendorCatalog.PlatformKey(platform));
            command.ExecuteNonQuery();
        }
    }

    private void SavePlatformAccount(SqliteTransaction transaction, PlatformAccount account)
    {
        var key = VendorCatalog.PlatformKey(account.Platform);
        if (key.Length == 0)
            throw new ArgumentException("平台名不能为空", nameof(account));
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        if (account.IsEmpty)
        {
            command.CommandText = "DELETE FROM platform_accounts WHERE platform_key = $key";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
            return;
        }
        var password = account.Locked ? "" : ", password_protected = excluded.password_protected";
        command.CommandText = $"""
            INSERT INTO platform_accounts (platform_key, platform, account, password_protected, note, updated_utc)
            VALUES ($key, $platform, $account, $password, $note, $updated)
            ON CONFLICT(platform_key) DO UPDATE SET
                platform = excluded.platform, account = excluded.account, note = excluded.note,
                updated_utc = excluded.updated_utc{password}
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$platform", account.Platform.Trim());
        command.Parameters.AddWithValue("$account", account.Account);
        command.Parameters.AddWithValue("$password", account.Locked ? DBNull.Value : ProtectOrNull(account.Password));
        command.Parameters.AddWithValue("$note", account.Note);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 批量写入已购记录。同名记录（名称归一化后相同，且厂家不冲突）不重复建：
    /// 已购的更新登录信息，心愿单里的转为已购，原因和价格保留。
    /// </summary>
    public UpsertResult UpsertPurchased(IEnumerable<PurchaseDraft> drafts)
    {
        lock (_gate)
        {
            var existing = LoadWishes();
            var added = 0;
            var updated = 0;
            var platforms = new List<string>();
            using var transaction = _connection.BeginTransaction();
            foreach (var draft in drafts)
            {
                var match = existing.FirstOrDefault(w => IsSamePlugin(w.Name, w.Vendor, draft.Name, draft.Vendor));
                if (match == null)
                {
                    using var insert = _connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO wishes (name, vendor, status, created_utc) VALUES ($name, $vendor, 1, $created)
                        RETURNING id
                        """;
                    insert.Parameters.AddWithValue("$name", draft.Name);
                    insert.Parameters.AddWithValue("$vendor", draft.Vendor);
                    insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
                    var id = Convert.ToInt64(insert.ExecuteScalar());
                    platforms.AddRange(AssignLogins(transaction, [(id, draft.Vendor, false)], draft.Account, draft.Password, draft.Serial).NewPlatforms);
                    existing.Add(new WishItem(id, draft.Name, draft.Vendor, Status: WishStatus.Purchased));
                    added++;
                    continue;
                }

                using (var promote = _connection.CreateCommand())
                {
                    promote.Transaction = transaction;
                    promote.CommandText = "UPDATE wishes SET status = 1, vendor = CASE WHEN vendor = '' THEN $vendor ELSE vendor END WHERE id = $id";
                    promote.Parameters.AddWithValue("$vendor", draft.Vendor);
                    promote.Parameters.AddWithValue("$id", match.Id);
                    promote.ExecuteNonQuery();
                }
                var vendor = match.Vendor.Length > 0 ? match.Vendor : draft.Vendor;
                platforms.AddRange(AssignLogins(transaction, [(match.Id, vendor, match.OwnLogin)], draft.Account, draft.Password, draft.Serial).NewPlatforms);
                updated++;
            }
            transaction.Commit();
            return new UpsertResult(added, updated, platforms.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    public const string PurchaseBackfillSetting = "purchases.backfilled";

    /// <summary>
    /// 只做一次：把以前就标成“已购”、但“已购账号”里还没有的插件补进去。
    /// 以后是标成“已购”的当时就加；这里补的是加这个功能之前标的。
    /// 做过以后记下来，用户再从“已购账号”删掉的不会被加回来。返回补了几款。
    /// </summary>
    public int BackfillPurchases()
    {
        if (GetSetting(PurchaseBackfillSetting) == "1")
            return 0;
        var owned = LoadWishes().Where(w => w.Status == WishStatus.Purchased).Select(w => Normalizer.Name(w.Name)).ToHashSet();
        var drafts = LoadPlugins()
            .Where(p => p.User.Purchase == PurchaseKind.Paid && !owned.Contains(Normalizer.Name(p.Name)))
            .GroupBy(p => Normalizer.Name(p.Name))
            .Select(g => new PurchaseDraft(g.First().Name, g.First().Vendor))
            .ToList();
        if (drafts.Count > 0)
            UpsertPurchased(drafts);
        SetSetting(PurchaseBackfillSetting, "1");
        return drafts.Count;
    }

    /// <summary>
    /// 从备份恢复心愿和购买信息。没给 secrets（或某条没有）时已保存的账号密码不动；
    /// 给了的，只用备份里有内容的项覆盖。解不开的旧密码（另一个 Windows 账号加密的）不覆盖。
    /// </summary>
    public UpsertResult ImportWishes(IEnumerable<WishBackupEntry> entries, IReadOnlyList<WishSecretEntry>? secrets = null)
    {
        var byIndex = new Dictionary<int, WishSecretEntry>();
        foreach (var secret in secrets ?? [])
            byIndex[secret.Index] = secret;
        lock (_gate)
        {
            var existing = LoadWishes();
            var added = 0;
            var updated = 0;
            var index = -1;
            foreach (var entry in entries)
            {
                index++;
                byIndex.TryGetValue(index, out var secret);
                var match = existing.FirstOrDefault(w => IsSamePlugin(w.Name, w.Vendor, entry.Name, entry.Vendor ?? ""));
                if (match == null)
                {
                    var created = entry.ToItem(0);
                    if (secret != null)
                        created = created with { Account = secret.Account ?? "", Password = secret.Password ?? "", Serial = secret.Serial ?? "" };
                    existing.Add(AddWish(created));
                    added++;
                }
                else
                {
                    var item = entry.ToItem(match.Id) with
                    {
                        Account = Pick(secret?.Account, match.Account),
                        Password = match.SecretsLocked ? match.Password : Pick(secret?.Password, match.Password),
                        Serial = match.SecretsLocked ? match.Serial : Pick(secret?.Serial, match.Serial),
                        SecretsLocked = match.SecretsLocked,
                        // 旧备份没有这一项，保留现在的选择。
                        OwnLogin = entry.OwnLogin ?? match.OwnLogin,
                    };
                    // 新版备份一定写 Category。没有它说明是旧备份，不能用空值冲掉已填的授权和目标价。
                    if (entry.Category == null)
                    {
                        item = item with
                        {
                            LicenseKind = match.LicenseKind,
                            ActivatedOn = match.ActivatedOn,
                            ActivationsLeft = match.ActivationsLeft,
                            TargetPrice = match.TargetPrice,
                            LowestPrice = match.LowestPrice,
                            Category = match.Category,
                        };
                    }
                    if (entry.Invoice == null)
                        item = item with { Invoice = match.Invoice };
                    SaveWish(item);
                    updated++;
                }
            }
            return new UpsertResult(added, updated);
        }

        static string Pick(string? fromBackup, string current) => string.IsNullOrEmpty(fromBackup) ? current : fromBackup;
    }

    /// <summary>
    /// 从备份恢复厂家账号，按厂家名合并：备份里有内容的账号、密码覆盖本机的，备注用备份的。
    /// 备份里密码是空的、本机的又解不开时，保留本机那份。
    /// </summary>
    public int ImportPlatformAccounts(IEnumerable<PlatformBackupEntry> entries)
    {
        lock (_gate)
        {
            var current = PurchaseLogin.ByKey(LoadPlatformAccountsUnlocked());
            var count = 0;
            using var transaction = _connection.BeginTransaction();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Platform))
                    continue;
                current.TryGetValue(VendorCatalog.PlatformKey(entry.Platform), out var mine);
                var password = entry.Password ?? "";
                var merged = new PlatformAccount(
                    entry.Platform.Trim(),
                    string.IsNullOrEmpty(entry.Account) ? mine?.Account ?? "" : entry.Account,
                    password.Length == 0 ? mine?.Password ?? "" : password,
                    entry.Note ?? mine?.Note ?? "",
                    Locked: password.Length == 0 && mine?.Locked == true);
                if (merged.IsEmpty)
                    continue;
                SavePlatformAccount(transaction, merged);
                count++;
            }
            transaction.Commit();
            return count;
        }
    }

    // ---------- 工程扫描 ----------

    // ---------- 插件链 ----------

    public List<PluginChain> LoadChains()
    {
        lock (_gate)
        {
            var steps = new Dictionary<long, List<ChainStep>>();
            using (var stepCommand = _connection.CreateCommand())
            {
                stepCommand.CommandText = "SELECT chain_id, plugin_key, plugin_name, note, alternates_json FROM chain_steps ORDER BY chain_id, position";
                using var reader = stepCommand.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (!steps.TryGetValue(id, out var list))
                        steps[id] = list = [];
                    var alternates = JsonSerializer.Deserialize<List<ChainPick>>(reader.GetString(4)) ?? [];
                    list.Add(new ChainStep(reader.GetString(1), reader.GetString(2), reader.GetString(3), alternates));
                }
            }

            var chains = new List<PluginChain>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, name, note, folder_id, tags_json, source, role FROM chains ORDER BY name COLLATE NOCASE, id";
            using var chainReader = command.ExecuteReader();
            while (chainReader.Read())
            {
                var id = chainReader.GetInt64(0);
                chains.Add(new PluginChain(
                    id,
                    chainReader.GetString(1),
                    chainReader.GetString(2),
                    steps.GetValueOrDefault(id) ?? [],
                    chainReader.IsDBNull(3) ? null : chainReader.GetInt64(3),
                    ReadTags(chainReader.GetString(4)),
                    chainReader.GetString(5),
                    (ChannelRole)chainReader.GetInt32(6)));
            }
            return chains;
        }
    }

    /// <exception cref="ArgumentException">名称为空，或同一个文件夹里已经有这个名称。</exception>
    public PluginChain AddChain(string name, long? folderId = null, IEnumerable<string>? tags = null, string source = "")
    {
        lock (_gate)
        {
            var clean = CleanChainName(name);
            EnsureChainNameFree(clean, folderId, null);
            var tagList = CleanList(tags ?? []);
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO chains (name, folder_id, tags_json, source) VALUES ($name, $folder, $tags, $source) RETURNING id";
            command.Parameters.AddWithValue("$name", clean);
            command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(tagList));
            command.Parameters.AddWithValue("$source", source);
            var id = Convert.ToInt64(command.ExecuteScalar());
            return new PluginChain(id, clean, "", [], folderId, tagList, source);
        }
    }

    /// <exception cref="ArgumentException">名称为空，或同一个文件夹里已经有这个名称。</exception>
    public void RenameChain(long id, string name)
    {
        lock (_gate)
        {
            var clean = CleanChainName(name);
            EnsureChainNameFree(clean, ChainFolder(id), id);
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE chains SET name = $name WHERE id = $id";
            command.Parameters.AddWithValue("$name", clean);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <exception cref="ArgumentException">目标文件夹里已经有同名笔记。</exception>
    public void MoveChain(long id, long? folderId)
    {
        lock (_gate)
        {
            using (var name = _connection.CreateCommand())
            {
                name.CommandText = "SELECT name FROM chains WHERE id = $id";
                name.Parameters.AddWithValue("$id", id);
                if (name.ExecuteScalar() is string current)
                    EnsureChainNameFree(current, folderId, id);
            }
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE chains SET folder_id = $folder WHERE id = $id";
            command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void SetChainTags(long id, IEnumerable<string> tags)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE chains SET tags_json = $tags WHERE id = $id";
            command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(CleanList(tags)));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void SaveChain(long id, string note, IEnumerable<ChainStep> steps)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE chains SET note = $note WHERE id = $id";
                update.Parameters.AddWithValue("$note", note);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
            }
            WriteChainSteps(transaction, id, steps);
            transaction.Commit();
        }
    }

    public void DeleteChain(long id) => DeleteChains([id]);

    /// <summary>一次删除多条笔记，要么全删要么都不删。返回删掉的条数。</summary>
    public int DeleteChains(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var deleted = ids.Distinct().Sum(id => DeleteChainRows(transaction, id));
            transaction.Commit();
            return deleted;
        }
    }

    private int DeleteChainRows(SqliteTransaction transaction, long id)
    {
        WriteChainSteps(transaction, id, []);
        // 连接没有开外键约束，路由要自己删（id 可能被新笔记复用）。
        using (var routes = _connection.CreateCommand())
        {
            routes.Transaction = transaction;
            routes.CommandText = "DELETE FROM chain_routes WHERE chain_id = $id OR target_id = $id";
            routes.Parameters.AddWithValue("$id", id);
            routes.ExecuteNonQuery();
        }
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM chains WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
    }

    /// <summary>从工程导入的轨道：每条建一条笔记。和已有笔记同名时加序号，不覆盖。</summary>
    public IReadOnlyList<PluginChain> AddImportedChains(long? folderId, IEnumerable<(string Name, string Source, IReadOnlyList<ChainStep> Steps)> notes)
    {
        lock (_gate)
        {
            var taken = LoadChains().Where(c => c.FolderId == folderId).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var created = new List<PluginChain>();
            foreach (var (name, source, steps) in notes)
            {
                var unique = UniqueName(CleanChainName(name), taken);
                var chain = AddChain(unique, folderId, null, source);
                SaveChain(chain.Id, "", steps);
                created.Add(chain with { Steps = steps });
            }
            return created;
        }
    }

    /// <summary>同一文件夹里同名的笔记用备份内容覆盖，其余的新建。文件夹按名称路径找，没有就建。</summary>
    public int ImportChains(IEnumerable<ChainBackupEntry> entries)
    {
        lock (_gate)
        {
            var count = 0;
            var existing = LoadChains();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;
                var folderId = EnsureFolderPath(entry.Folder ?? []);
                var chain = existing.FirstOrDefault(c => c.FolderId == folderId && string.Equals(c.Name, entry.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (chain == null)
                {
                    chain = AddChain(entry.Name, folderId, entry.Tags, entry.Source ?? "");
                    existing.Add(chain);
                }
                else if (entry.Tags != null)
                {
                    SetChainTags(chain.Id, entry.Tags);
                }
                SaveChain(chain.Id, entry.Note ?? "", (entry.Steps ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s.PluginName))
                    .Select(s => s with
                    {
                        Note = s.Note ?? "",
                        Alternates = (s.Alternates ?? []).Where(a => !string.IsNullOrWhiteSpace(a.PluginName)).Select(a => a with { Note = a.Note ?? "" }).ToList(),
                    }));
                count++;
            }
            ImportRoutes(entries);
            return count;
        }
    }

    private void WriteChainSteps(SqliteTransaction transaction, long id, IEnumerable<ChainStep> steps)
    {
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM chain_steps WHERE chain_id = $id";
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }
        var position = 0;
        foreach (var step in steps)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO chain_steps (chain_id, position, plugin_key, plugin_name, note, alternates_json)
                VALUES ($id, $position, $key, $name, $note, $alternates)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$position", position++);
            insert.Parameters.AddWithValue("$key", step.PluginKey ?? "");
            insert.Parameters.AddWithValue("$name", step.PluginName);
            insert.Parameters.AddWithValue("$note", step.Note ?? "");
            insert.Parameters.AddWithValue("$alternates", JsonSerializer.Serialize(step.Backups.Select(a => a with { PluginKey = a.PluginKey ?? "", Note = a.Note ?? "" })));
            insert.ExecuteNonQuery();
        }
    }

    private long? ChainFolder(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT folder_id FROM chains WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long folder ? folder : null;
    }

    private void EnsureChainNameFree(string name, long? folderId, long? exceptId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM chains
            WHERE name = $name COLLATE NOCASE AND folder_id IS $folder AND id IS NOT $except
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$except", (object?)exceptId ?? DBNull.Value);
        if (Convert.ToInt64(command.ExecuteScalar()) > 0)
            throw new ArgumentException($"这个文件夹里已经有叫“{name}”的笔记了");
    }

    private static string CleanChainName(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
            throw new ArgumentException("名称不能为空");
        return clean;
    }

    private static List<string> CleanList(IEnumerable<string> items) =>
        items.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---------- 混音笔记的文件夹 ----------

    public List<NoteFolder> LoadNoteFolders()
    {
        lock (_gate)
        {
            var folders = new List<NoteFolder>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, parent_id, name FROM note_folders ORDER BY name COLLATE NOCASE, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                folders.Add(new NoteFolder(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2)));
            return folders;
        }
    }

    /// <exception cref="ArgumentException">名称为空，或同一层已经有这个文件夹。</exception>
    public NoteFolder AddNoteFolder(long? parentId, string name)
    {
        lock (_gate)
        {
            var clean = CleanChainName(name);
            EnsureFolderNameFree(clean, parentId, null);
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO note_folders (parent_id, name) VALUES ($parent, $name) RETURNING id";
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", clean);
            return new NoteFolder(Convert.ToInt64(command.ExecuteScalar()), parentId, clean);
        }
    }

    /// <exception cref="ArgumentException">名称为空，或同一层已经有这个文件夹。</exception>
    public void RenameNoteFolder(long id, string name)
    {
        lock (_gate)
        {
            var clean = CleanChainName(name);
            var folder = LoadNoteFolders().FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("文件夹不存在");
            EnsureFolderNameFree(clean, folder.ParentId, id);
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE note_folders SET name = $name WHERE id = $id";
            command.Parameters.AddWithValue("$name", clean);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <exception cref="ArgumentException">移到自己或自己的子文件夹里，或目标里已有同名文件夹。</exception>
    public void MoveNoteFolder(long id, long? parentId)
    {
        lock (_gate)
        {
            var folders = LoadNoteFolders();
            var folder = folders.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("文件夹不存在");
            if (parentId is { } target && MixNotes.Subtree(id, folders).Contains(target))
                throw new ArgumentException("不能把文件夹移到它自己里面");
            EnsureFolderNameFree(folder.Name, parentId, id);
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE note_folders SET parent_id = $parent WHERE id = $id";
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 删除文件夹，里面的笔记和子文件夹移到上一层，不删任何笔记。
    /// 上一层有同名的会在名称后面加编号，避免冲突。
    /// </summary>
    public void DeleteNoteFolder(long id)
    {
        lock (_gate)
        {
            var folders = LoadNoteFolders();
            var folder = folders.FirstOrDefault(f => f.Id == id);
            if (folder == null)
                return;
            var parent = folder.ParentId;
            var chains = LoadChains();
            using var transaction = _connection.BeginTransaction();

            var takenFolders = folders.Where(f => f.ParentId == parent && f.Id != id).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var child in folders.Where(f => f.ParentId == id))
            {
                var name = UniqueName(child.Name, takenFolders);
                using var move = _connection.CreateCommand();
                move.Transaction = transaction;
                move.CommandText = "UPDATE note_folders SET parent_id = $parent, name = $name WHERE id = $id";
                move.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
                move.Parameters.AddWithValue("$name", name);
                move.Parameters.AddWithValue("$id", child.Id);
                move.ExecuteNonQuery();
            }

            var takenChains = chains.Where(c => c.FolderId == parent).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var chain in chains.Where(c => c.FolderId == id))
            {
                var name = UniqueName(chain.Name, takenChains);
                using var move = _connection.CreateCommand();
                move.Transaction = transaction;
                move.CommandText = "UPDATE chains SET folder_id = $parent, name = $name WHERE id = $id";
                move.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
                move.Parameters.AddWithValue("$name", name);
                move.Parameters.AddWithValue("$id", chain.Id);
                move.ExecuteNonQuery();
            }

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM note_folders WHERE id = $id";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>
    /// 删除文件夹，连同它和所有子文件夹里的笔记一起删。返回删掉的笔记条数。
    /// 和 <see cref="DeleteNoteFolder"/> 不同，那个会把里面的内容移到上一层。
    /// </summary>
    public int DeleteNoteFolderWithNotes(long id)
    {
        lock (_gate)
        {
            var folders = LoadNoteFolders();
            if (folders.All(f => f.Id != id))
                return 0;
            var subtree = MixNotes.Subtree(id, folders);
            var chains = LoadChains().Where(c => c.FolderId is { } f && subtree.Contains(f)).Select(c => c.Id).ToList();
            using var transaction = _connection.BeginTransaction();
            var deleted = chains.Sum(chain => DeleteChainRows(transaction, chain));
            foreach (var folder in subtree)
            {
                using var delete = _connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM note_folders WHERE id = $id";
                delete.Parameters.AddWithValue("$id", folder);
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
            return deleted;
        }
    }

    /// <summary>按名称路径找文件夹，缺的逐层新建。空路径表示最外层。</summary>
    public long? EnsureFolderPath(IReadOnlyList<string> path)
    {
        lock (_gate)
        {
            long? parent = null;
            foreach (var raw in path)
            {
                var name = raw?.Trim() ?? "";
                if (name.Length == 0)
                    continue;
                var folders = LoadNoteFolders();
                var existing = folders.FirstOrDefault(f => f.ParentId == parent && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                parent = existing?.Id ?? AddNoteFolder(parent, name).Id;
            }
            return parent;
        }
    }

    private void EnsureFolderNameFree(string name, long? parentId, long? exceptId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM note_folders
            WHERE name = $name COLLATE NOCASE AND parent_id IS $parent AND id IS NOT $except
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$except", (object?)exceptId ?? DBNull.Value);
        if (Convert.ToInt64(command.ExecuteScalar()) > 0)
            throw new ArgumentException($"这一层已经有叫“{name}”的文件夹了");
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var i = 2; taken.Contains(candidate); i++)
            candidate = $"{name} ({i})";
        taken.Add(candidate);
        return candidate;
    }

    // ---------- 设置 ----------

    /// <summary>
    /// 用户移除扫描文件夹后调用：清掉不再存在、也没有任何用户内容的插件，否则列表里会留下一片“缺失”。
    /// 有评分、备注、标签、垃圾桶记录或在清单里的插件继续保留。
    /// </summary>
    public int DeleteMissingWithoutUserData()
    {
        lock (_gate)
        {
            const string Empty = """
                is_missing = 1 AND rating = 0 AND note = '' AND blacklist_reason = '' AND my_tags_json = '[]'
                AND tags_override_json IS NULL AND kind_override IS NULL AND vendor_override IS NULL
                AND hidden = 0 AND mix_json IS NULL AND timbres_override_json IS NULL AND purchase = 0 AND key NOT IN (SELECT plugin_key FROM favorite_items)
                """;
            using var transaction = _connection.BeginTransaction();
            Execute(transaction, $"DELETE FROM plugin_files WHERE plugin_id IN (SELECT id FROM plugins WHERE {Empty})");
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM plugins WHERE {Empty}";
            var deleted = command.ExecuteNonQuery();
            transaction.Commit();
            return deleted;
        }
    }

    public List<string> LoadVst2Folders()
    {
        lock (_gate)
            return ReadStrings("SELECT path FROM vst2_folders ORDER BY path COLLATE NOCASE");
    }

    public void AddVst2Folder(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO vst2_folders (path) VALUES ($path)";
            command.Parameters.AddWithValue("$path", Path.TrimEndingDirectorySeparator(path));
            command.ExecuteNonQuery();
        }
    }

    public void RemoveVst2Folder(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM vst2_folders WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO app_settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>全部设置的键值，用于备份。</summary>
    public Dictionary<string, string> LoadSettings()
    {
        lock (_gate)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM app_settings ORDER BY key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                values[reader.GetString(0)] = reader.GetString(1);
            return values;
        }
    }

    public List<string> LoadProjectFolders()
    {
        lock (_gate)
            return ReadStrings("SELECT path FROM project_folders ORDER BY path COLLATE NOCASE");
    }

    public void AddProjectFolder(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO project_folders (path) VALUES ($path)";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public void RemoveProjectFolder(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM project_folders WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public Dictionary<string, (long Size, DateTime ModifiedUtc, int ParserVersion)> LoadProjectFileStamps()
    {
        lock (_gate)
        {
            var stamps = new Dictionary<string, (long, DateTime, int)>(StringComparer.OrdinalIgnoreCase);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, size, modified_utc, parser_version FROM project_files";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                stamps[reader.GetString(0)] = (reader.GetInt64(1), ParseUtc(reader.GetString(2)), reader.GetInt32(3));
            return stamps;
        }
    }

    public void SaveProjectFile(
        string path, ProjectHost host, long size, DateTime modifiedUtc,
        ProjectScanStatus status, string? error, IReadOnlyList<ProjectPluginUse> plugins, bool ownFolder = false)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var upsert = _connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO project_files (path, host, size, modified_utc, status, error, parser_version, own_folder)
                VALUES ($path, $host, $size, $modified, $status, $error, $version, $own)
                ON CONFLICT(path) DO UPDATE SET
                    host = excluded.host, size = excluded.size, modified_utc = excluded.modified_utc,
                    status = excluded.status, error = excluded.error, parser_version = excluded.parser_version,
                    own_folder = excluded.own_folder
                RETURNING id
                """;
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$host", (int)host);
            upsert.Parameters.AddWithValue("$size", size);
            upsert.Parameters.AddWithValue("$modified", modifiedUtc.ToString("O"));
            upsert.Parameters.AddWithValue("$status", (int)status);
            upsert.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$version", ProjectParser.Version);
            upsert.Parameters.AddWithValue("$own", ownFolder ? 1 : 0);
            var id = Convert.ToInt64(upsert.ExecuteScalar());

            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM project_plugins WHERE project_id = $id";
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }
            foreach (var plugin in plugins)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO project_plugins (project_id, name, uid, instances) VALUES ($id, $name, $uid, $instances)";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$name", plugin.Name);
                insert.Parameters.AddWithValue("$uid", (object?)plugin.Uid ?? DBNull.Value);
                insert.Parameters.AddWithValue("$instances", plugin.Instances);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>删除已经不在扫描范围或不存在的工程记录，只动数据库，不碰工程文件。</summary>
    public int RemoveProjectFilesExcept(IReadOnlyCollection<string> keep)
    {
        lock (_gate)
        {
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            var gone = LoadProjectFileStamps().Keys.Where(path => !keepSet.Contains(path)).ToList();
            using var transaction = _connection.BeginTransaction();
            foreach (var path in gone)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM project_plugins WHERE project_id = (SELECT id FROM project_files WHERE path = $path);
                    DELETE FROM project_files WHERE path = $path;
                    """;
                command.Parameters.AddWithValue("$path", path);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return gone.Count;
        }
    }

    public List<ProjectFileRecord> LoadProjectFiles()
    {
        lock (_gate)
        {
            var plugins = new Dictionary<long, List<ProjectPluginUse>>();
            using (var pluginCommand = _connection.CreateCommand())
            {
                pluginCommand.CommandText = "SELECT project_id, name, uid, instances FROM project_plugins";
                using var pluginReader = pluginCommand.ExecuteReader();
                while (pluginReader.Read())
                {
                    var id = pluginReader.GetInt64(0);
                    if (!plugins.TryGetValue(id, out var list))
                        plugins[id] = list = [];
                    list.Add(new ProjectPluginUse(
                        pluginReader.GetString(1),
                        pluginReader.IsDBNull(2) ? null : pluginReader.GetString(2),
                        pluginReader.GetInt32(3)));
                }
            }

            var files = new List<ProjectFileRecord>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, path, host, size, modified_utc, status, error, parser_version, own_folder FROM project_files";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                files.Add(new ProjectFileRecord(
                    id,
                    reader.GetString(1),
                    (ProjectHost)reader.GetInt32(2),
                    reader.GetInt64(3),
                    ParseUtc(reader.GetString(4)),
                    (ProjectScanStatus)reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7),
                    plugins.GetValueOrDefault(id) ?? [],
                    reader.GetInt64(8) != 0));
            }
            return files;
        }
    }

    /// <summary>用户标记为“不算缺失”的名称。</summary>
    public HashSet<string> LoadIgnoredProjectNames()
    {
        lock (_gate)
            return ReadStrings("SELECT key FROM project_ignored").ToHashSet();
    }

    public void IgnoreProjectName(string name)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO project_ignored (key, name) VALUES ($key, $name)";
            command.Parameters.AddWithValue("$key", ProjectReport.IgnoreKey(name));
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>忽略的插件名（原样），用于备份。</summary>
    public List<string> LoadIgnoredProjectPluginNames()
    {
        lock (_gate)
            return ReadStrings("SELECT name FROM project_ignored ORDER BY name COLLATE NOCASE");
    }

    public void ClearIgnoredProjectNames()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM project_ignored";
            command.ExecuteNonQuery();
        }
    }

    // ---------- 常用清单 ----------

    public List<FavoriteList> LoadFavoriteLists()
    {
        lock (_gate)
        {
            var items = new Dictionary<long, List<string>>();
            using (var itemCommand = _connection.CreateCommand())
            {
                itemCommand.CommandText = "SELECT list_id, plugin_key FROM favorite_items ORDER BY position";
                using var itemReader = itemCommand.ExecuteReader();
                while (itemReader.Read())
                {
                    var id = itemReader.GetInt64(0);
                    if (!items.TryGetValue(id, out var list))
                        items[id] = list = [];
                    list.Add(itemReader.GetString(1));
                }
            }

            var lists = new List<FavoriteList>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, name FROM favorite_lists ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                lists.Add(new FavoriteList(id, reader.GetString(1), items.GetValueOrDefault(id) ?? []));
            }
            return lists;
        }
    }

    /// <exception cref="ArgumentException">名称为空或已被使用。</exception>
    public FavoriteList AddFavoriteList(string name)
    {
        lock (_gate)
        {
            var clean = CleanListName(name);
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO favorite_lists (name) VALUES ($name) ON CONFLICT(name) DO NOTHING RETURNING id";
            command.Parameters.AddWithValue("$name", clean);
            var id = command.ExecuteScalar();
            if (id == null)
                throw new ArgumentException($"已经有叫“{clean}”的清单了");
            return new FavoriteList(Convert.ToInt64(id), clean, []);
        }
    }

    /// <exception cref="ArgumentException">名称为空或已被使用。</exception>
    public void RenameFavoriteList(long id, string name)
    {
        lock (_gate)
        {
            var clean = CleanListName(name);
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE favorite_lists SET name = $name WHERE id = $id";
            command.Parameters.AddWithValue("$name", clean);
            command.Parameters.AddWithValue("$id", id);
            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                throw new ArgumentException($"已经有叫“{clean}”的清单了");
            }
        }
    }

    public void DeleteFavoriteList(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM favorite_items WHERE list_id = $id;
                DELETE FROM favorite_removed WHERE list_id = $id;
                DELETE FROM favorite_lists WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void SetFavoriteItems(long listId, IEnumerable<string> pluginKeys)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM favorite_items WHERE list_id = $id";
                clear.Parameters.AddWithValue("$id", listId);
                clear.ExecuteNonQuery();
            }
            var position = 0;
            foreach (var key in pluginKeys.Distinct())
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO favorite_items (list_id, plugin_key, position) VALUES ($id, $key, $position)";
                insert.Parameters.AddWithValue("$id", listId);
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$position", position++);
                insert.ExecuteNonQuery();

                // 重新放回清单，说明用户又想要它了。
                using var forget = _connection.CreateCommand();
                forget.Transaction = transaction;
                forget.CommandText = "DELETE FROM favorite_removed WHERE list_id = $id AND plugin_key = $key";
                forget.Parameters.AddWithValue("$id", listId);
                forget.Parameters.AddWithValue("$key", key);
                forget.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>记下用户手动移出的插件，之后同步使用次数时不再加回来。</summary>
    public void RecordFavoriteRemoval(long listId, string pluginKey)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO favorite_removed (list_id, plugin_key) VALUES ($id, $key)";
            command.Parameters.AddWithValue("$id", listId);
            command.Parameters.AddWithValue("$key", pluginKey);
            command.ExecuteNonQuery();
        }
    }

    public Dictionary<long, IReadOnlySet<string>> LoadFavoriteRemovals()
    {
        lock (_gate)
        {
            var removed = new Dictionary<long, HashSet<string>>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT list_id, plugin_key FROM favorite_removed";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!removed.TryGetValue(id, out var keys))
                    removed[id] = keys = [];
                keys.Add(reader.GetString(1));
            }
            return removed.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value);
        }
    }

    /// <summary>从备份恢复手动移出的记录。按清单名对上；插件已经在清单里的不记（说明后来又放回去了）。</summary>
    public int ImportFavoriteRemovals(IEnumerable<FavoriteRemovalEntry> entries)
    {
        lock (_gate)
        {
            var lists = LoadFavoriteLists().ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.PluginKey) || !lists.TryGetValue(entry.List?.Trim() ?? "", out var list))
                    continue;
                if (list.PluginKeys.Contains(entry.PluginKey))
                    continue;
                RecordFavoriteRemoval(list.Id, entry.PluginKey);
                count++;
            }
            return count;
        }
    }

    /// <summary>从备份恢复：同名清单合并，已有的插件保留。</summary>
    public int ImportFavoriteLists(IEnumerable<FavoriteBackupEntry> entries)
    {
        lock (_gate)
        {
            var count = 0;
            var existing = LoadFavoriteLists();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;
                var list = existing.FirstOrDefault(l => string.Equals(l.Name, entry.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (list == null)
                {
                    list = AddFavoriteList(entry.Name);
                    existing.Add(list);
                }
                SetFavoriteItems(list.Id, list.PluginKeys.Concat(entry.PluginKeys ?? []));
                count++;
            }
            return count;
        }
    }

    private static string CleanListName(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
            throw new ArgumentException("清单名称不能为空");
        return clean;
    }

    private List<string> ReadStrings(string sql)
    {
        var values = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static bool IsSamePlugin(string nameA, string vendorA, string nameB, string vendorB) =>
        Normalizer.Name(nameA) == Normalizer.Name(nameB)
        && (vendorA.Length == 0 || vendorB.Length == 0 || Normalizer.Vendor(vendorA) == Normalizer.Vendor(vendorB));

    private void SetLogin(SqliteTransaction transaction, long id, string account, string password, string serial)
    {
        var assignments = new List<string>();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        if (account.Length > 0)
        {
            assignments.Add("account = $account");
            command.Parameters.AddWithValue("$account", account);
        }
        if (password.Length > 0)
        {
            assignments.Add("password_protected = $password");
            command.Parameters.AddWithValue("$password", SecretProtector.Protect(password));
        }
        if (serial.Length > 0)
        {
            assignments.Add("serial_protected = $serial");
            command.Parameters.AddWithValue("$serial", SecretProtector.Protect(serial));
        }
        if (assignments.Count == 0)
            return;
        command.CommandText = $"UPDATE wishes SET {string.Join(", ", assignments)} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void AddWishParameters(SqliteCommand command, WishItem item)
    {
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$vendor", item.Vendor);
        command.Parameters.AddWithValue("$price", DecimalOrNull(item.Price));
        command.Parameters.AddWithValue("$currency", item.Currency);
        command.Parameters.AddWithValue("$priority", WishPriority.Clean(item.Priority));
        command.Parameters.AddWithValue("$reason", item.Reason);
        command.Parameters.AddWithValue("$url", item.Url);
        command.Parameters.AddWithValue("$note", item.Note);
        command.Parameters.AddWithValue("$status", (int)item.Status);
        command.Parameters.AddWithValue("$purchasedOn", item.PurchasedOn == null ? DBNull.Value : item.PurchasedOn.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$paid", DecimalOrNull(item.PaidPrice));
        command.Parameters.AddWithValue("$account", item.Account);
        command.Parameters.AddWithValue("$licenseKind", (int)item.LicenseKind);
        command.Parameters.AddWithValue("$activatedOn", item.ActivatedOn);
        command.Parameters.AddWithValue("$activationsLeft", item.ActivationsLeft == null ? DBNull.Value : item.ActivationsLeft.Value);
        command.Parameters.AddWithValue("$targetPrice", DecimalOrNull(item.TargetPrice));
        command.Parameters.AddWithValue("$lowestPrice", DecimalOrNull(item.LowestPrice));
        command.Parameters.AddWithValue("$category", item.Category);
        command.Parameters.AddWithValue("$ownLogin", item.OwnLogin ? 1 : 0);
        command.Parameters.AddWithValue("$invoice", item.Invoice.Trim());
    }

    private static object DecimalOrNull(decimal? value) =>
        value == null ? DBNull.Value : value.Value.ToString(CultureInfo.InvariantCulture);

    private static decimal? ReadDecimal(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : decimal.Parse(reader.GetString(column), CultureInfo.InvariantCulture);

    private static object ProtectOrNull(string secret) =>
        secret.Length == 0 ? DBNull.Value : SecretProtector.Protect(secret);

    private static string ReadSecret(SqliteDataReader reader, int column, out bool locked)
    {
        locked = false;
        if (reader.IsDBNull(column))
            return "";
        var secret = SecretProtector.TryUnprotect((byte[])reader.GetValue(column));
        locked = secret == null;
        return secret ?? "";
    }

    private static List<string> ReadTags(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    public void Dispose()
    {
        lock (_gate)
            _connection.Dispose();
    }
}
