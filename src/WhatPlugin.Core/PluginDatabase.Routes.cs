using Microsoft.Data.Sqlite;

namespace WhatPlugin.Core;

/// <summary>笔记之间的一条连线：From 的主输出、发送或侧链接到 To。侧链时 Plugin 是 To 上接收侧链的插件。</summary>
public sealed record NoteRoute(long From, long To, RouteKind Kind, string Plugin = "");

/// <summary>从工程导入的一个通道。Key 在这次导入里唯一，OutputKey 和 SendKeys 指向别的 Key。</summary>
public sealed record ImportedNote(
    string Key,
    string Name,
    string Source,
    IReadOnlyList<ChainStep> Steps,
    ChannelRole Role = ChannelRole.Track,
    string? OutputKey = null,
    IReadOnlyList<string>? SendKeys = null,
    IReadOnlyList<(string Key, string Plugin)>? Sidechains = null);

/// <summary>导入结果：新建的笔记，以及已经导入过、这次只更新了路由的笔记。</summary>
public sealed record ImportedNotes(IReadOnlyList<PluginChain> Created, IReadOnlyList<PluginChain> Reused);

/// <summary>混音笔记的通道类型和路由。</summary>
public sealed partial class PluginDatabase
{
    public IReadOnlyList<NoteRoute> LoadRoutes()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT r.chain_id, r.target_id, r.kind, r.plugin FROM chain_routes r
                JOIN chains a ON a.id = r.chain_id JOIN chains b ON b.id = r.target_id
                ORDER BY r.chain_id, r.kind, r.position
                """;
            using var reader = command.ExecuteReader();
            var routes = new List<NoteRoute>();
            while (reader.Read())
                routes.Add(new NoteRoute(reader.GetInt64(0), reader.GetInt64(1), (RouteKind)reader.GetInt32(2), reader.GetString(3)));
            return routes;
        }
    }

    public void SetChainRole(long id, ChannelRole role)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE chains SET role = $role WHERE id = $id";
            command.Parameters.AddWithValue("$role", (int)role);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 替换这条笔记的主输出和发送；sidechains 不为空时也替换侧链，为空时侧链不动。
    /// 指向自己或不存在的笔记的会被忽略；output 为空表示接主输出。
    /// </summary>
    public void SetRoutes(long id, long? output, IEnumerable<long> sends, IEnumerable<(long Target, string Plugin)>? sidechains = null)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            WriteRoutes(transaction, id, output, sends, sidechains);
            transaction.Commit();
        }
    }

    private void WriteRoutes(SqliteTransaction transaction, long id, long? output, IEnumerable<long> sends, IEnumerable<(long Target, string Plugin)>? sidechains)
    {
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = sidechains == null
                ? $"DELETE FROM chain_routes WHERE chain_id = $id AND kind <> {(int)RouteKind.Sidechain}"
                : "DELETE FROM chain_routes WHERE chain_id = $id";
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }
        var position = 0;
        void Insert(long target, RouteKind kind, string plugin = "")
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO chain_routes (chain_id, target_id, kind, plugin, position)
                SELECT $id, $target, $kind, $plugin, $position WHERE EXISTS (SELECT 1 FROM chains WHERE id = $target)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$target", target);
            insert.Parameters.AddWithValue("$kind", (int)kind);
            insert.Parameters.AddWithValue("$plugin", plugin);
            insert.Parameters.AddWithValue("$position", position++);
            insert.ExecuteNonQuery();
        }
        if (output is { } o && o != id)
            Insert(o, RouteKind.Output);
        foreach (var send in sends.Distinct())
        {
            if (send != id)
                Insert(send, RouteKind.Send);
        }
        foreach (var (target, plugin) in (sidechains ?? []).Select(s => (s.Target, s.Plugin.Trim())).Distinct())
        {
            if (target != id && plugin.Length > 0)
                Insert(target, RouteKind.Sidechain, plugin);
        }
    }

    /// <summary>
    /// 导入工程里的通道，并按工程连好路由。同一个文件夹里已经有来源相同的笔记（以前导入过）就沿用它，
    /// 不改它的插件和思路，只更新类型和路由；其余的新建，同名时加序号。
    /// </summary>
    public ImportedNotes AddImportedNotes(long? folderId, IReadOnlyList<ImportedNote> notes)
    {
        lock (_gate)
        {
            var existing = LoadChains().Where(c => c.FolderId == folderId).ToList();
            var taken = existing.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bySource = existing.Where(c => c.Source.Length > 0).GroupBy(c => c.Source).ToDictionary(g => g.Key, g => g.First());
            var created = new List<PluginChain>();
            var reused = new List<PluginChain>();
            var ids = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var note in notes)
            {
                if (bySource.TryGetValue(note.Source, out var old))
                {
                    reused.Add(old);
                    ids[note.Key] = old.Id;
                    continue;
                }
                var unique = UniqueName(CleanChainName(note.Name), taken);
                var chain = AddChain(unique, folderId, null, note.Source);
                SaveChain(chain.Id, "", note.Steps);
                created.Add(chain with { Steps = note.Steps, Role = note.Role });
                ids[note.Key] = chain.Id;
            }

            using var transaction = _connection.BeginTransaction();
            foreach (var note in notes)
            {
                var id = ids[note.Key];
                using (var role = _connection.CreateCommand())
                {
                    role.Transaction = transaction;
                    role.CommandText = "UPDATE chains SET role = $role WHERE id = $id";
                    role.Parameters.AddWithValue("$role", (int)note.Role);
                    role.Parameters.AddWithValue("$id", id);
                    role.ExecuteNonQuery();
                }
                long? output = note.OutputKey != null && ids.TryGetValue(note.OutputKey, out var o) ? o : null;
                var sends = (note.SendKeys ?? []).Where(ids.ContainsKey).Select(k => ids[k]);
                var sidechains = (note.Sidechains ?? []).Where(s => ids.ContainsKey(s.Key)).Select(s => (ids[s.Key], s.Plugin)).ToList();
                WriteRoutes(transaction, id, output, sends, sidechains);
            }
            transaction.Commit();
            return new ImportedNotes(created, reused);
        }
    }

    /// <summary>
    /// 导入别人分享的混音笔记：全部放进最外层一个新文件夹（同名时加序号），保留原来的子文件夹。
    /// 不覆盖已有的笔记；文件里同一文件夹有重名的也加序号。路由按原来的“文件夹路径 + 名称”接上。
    /// </summary>
    public (NoteFolder Root, int Count) ImportSharedNotes(string rootName, IReadOnlyList<ChainBackupEntry> notes)
    {
        lock (_gate)
        {
            var topNames = LoadNoteFolders().Where(f => f.ParentId == null).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var root = AddNoteFolder(null, UniqueName(CleanChainName(rootName), topNames));

            var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [""] = root.Id };
            long FolderOf(IReadOnlyList<string>? path)
            {
                var parent = root.Id;
                var key = "";
                foreach (var raw in path ?? [])
                {
                    var name = raw?.Trim() ?? "";
                    if (name.Length == 0)
                        continue;
                    key += "\u001f" + name;
                    if (!folderIds.TryGetValue(key, out var id))
                        folderIds[key] = id = AddNoteFolder(parent, name).Id;
                    parent = id;
                }
                return parent;
            }

            static string KeyOf(IReadOnlyList<string>? folder, string name) =>
                string.Join("\u001f", (folder ?? []).Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length > 0)) + "\u001e" + name.Trim().ToLowerInvariant();

            var taken = new Dictionary<long, HashSet<string>>();
            var ids = new Dictionary<string, long>();
            var created = new List<(ChainBackupEntry Entry, long Id)>();
            foreach (var entry in notes)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;
                var folderId = FolderOf(entry.Folder);
                if (!taken.TryGetValue(folderId, out var names))
                    taken[folderId] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var chain = AddChain(UniqueName(CleanChainName(entry.Name), names), folderId, entry.Tags);
                SaveChain(chain.Id, entry.Note ?? "", (entry.Steps ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s.PluginName))
                    .Select(s => s with
                    {
                        PluginKey = s.PluginKey ?? "",
                        Note = s.Note ?? "",
                        Alternates = s.Backups.Where(a => !string.IsNullOrWhiteSpace(a.PluginName)).Select(a => a with { PluginKey = a.PluginKey ?? "", Note = a.Note ?? "" }).ToList(),
                    }));
                if (entry.Role is { } role && role != ChannelRole.Track)
                    SetChainRole(chain.Id, role);
                ids.TryAdd(KeyOf(entry.Folder, entry.Name), chain.Id);
                created.Add((entry, chain.Id));
            }

            using var transaction = _connection.BeginTransaction();
            foreach (var (entry, id) in created)
            {
                if (entry.Output == null && entry.Sends == null && entry.Sidechains == null)
                    continue;
                long? output = entry.Output is { } o && ids.TryGetValue(KeyOf(o.Folder, o.Name), out var oid) ? oid : null;
                var sends = (entry.Sends ?? []).Select(s => ids.TryGetValue(KeyOf(s.Folder, s.Name), out var sid) ? sid : (long?)null).OfType<long>().ToList();
                var sidechains = (entry.Sidechains ?? [])
                    .Where(s => ids.ContainsKey(KeyOf(s.Folder, s.Name)))
                    .Select(s => (ids[KeyOf(s.Folder, s.Name)], s.Plugin ?? ""))
                    .ToList();
                WriteRoutes(transaction, id, output, sends, sidechains);
            }
            transaction.Commit();
            return (root, created.Count);
        }
    }

    /// <summary>备份里按“文件夹路径 + 名称”记下的连线，导入完笔记后再接上。</summary>
    private void ImportRoutes(IEnumerable<ChainBackupEntry> entries)
    {
        var folders = LoadNoteFolders();
        var chains = LoadChains();
        string KeyOf(IReadOnlyList<string>? folder, string name) =>
            string.Join("\u001f", (folder ?? []).Select(f => f.Trim().ToLowerInvariant())) + "\u001e" + name.Trim().ToLowerInvariant();
        var byKey = new Dictionary<string, long>();
        foreach (var chain in chains)
            byKey.TryAdd(KeyOf(MixNotes.PathOf(chain.FolderId, folders), chain.Name), chain.Id);

        using var transaction = _connection.BeginTransaction();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || !byKey.TryGetValue(KeyOf(entry.Folder, entry.Name), out var id))
                continue;
            if (entry.Role is { } role)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE chains SET role = $role WHERE id = $id";
                command.Parameters.AddWithValue("$role", (int)role);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            if (entry.Output == null && entry.Sends == null && entry.Sidechains == null)
                continue;
            long? output = entry.Output is { } o && byKey.TryGetValue(KeyOf(o.Folder, o.Name), out var oid) ? oid : null;
            var sends = (entry.Sends ?? []).Select(s => byKey.TryGetValue(KeyOf(s.Folder, s.Name), out var sid) ? sid : (long?)null).OfType<long>();
            var sidechains = entry.Sidechains?
                .Where(s => byKey.ContainsKey(KeyOf(s.Folder, s.Name)))
                .Select(s => (byKey[KeyOf(s.Folder, s.Name)], s.Plugin ?? ""))
                .ToList();
            WriteRoutes(transaction, id, output, sends, sidechains);
        }
        transaction.Commit();
    }
}
