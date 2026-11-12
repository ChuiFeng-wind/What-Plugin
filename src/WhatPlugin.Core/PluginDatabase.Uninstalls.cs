using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WhatPlugin.Core;

/// <summary>卸载历史的读写。</summary>
public sealed partial class PluginDatabase
{
    public List<UninstallRecord> LoadUninstallHistory()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, plugin_key, name, vendor, way, reason, uninstalled_utc, trash_reason, rating, note
                FROM uninstall_history ORDER BY uninstalled_utc DESC, id DESC
                """;
            using var reader = command.ExecuteReader();
            var records = new List<UninstallRecord>();
            while (reader.Read())
            {
                var way = (UninstallWay)reader.GetInt32(4);
                records.Add(new UninstallRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    Enum.IsDefined(way) ? way : UninstallWay.Unknown,
                    reader.GetString(5),
                    ParseHistoryUtc(reader.GetString(6)),
                    reader.GetString(7),
                    reader.GetInt32(8),
                    reader.GetString(9)));
            }
            return records;
        }
    }

    public void AddUninstallRecords(IEnumerable<UninstallRecord> records)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var record in records)
                InsertUninstallRecord(transaction, record);
            transaction.Commit();
        }
    }

    public void SetUninstallReason(long id, string reason)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE uninstall_history SET reason = $reason WHERE id = $id";
            command.Parameters.AddWithValue("$reason", reason.Trim());
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteUninstallRecord(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM uninstall_history WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>从备份导入。插件、日期和方式都相同的记录已经有了就跳过，重复导入不会翻倍。</summary>
    public int ImportUninstallHistory(IEnumerable<UninstallRecord> records)
    {
        lock (_gate)
        {
            var existing = LoadUninstallHistory().Select(Identity).ToHashSet();
            using var transaction = _connection.BeginTransaction();
            var count = 0;
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Name) || !existing.Add(Identity(record)))
                    continue;
                InsertUninstallRecord(transaction, record);
                count++;
            }
            transaction.Commit();
            return count;
        }
    }

    private static string Identity(UninstallRecord record) =>
        $"{record.PluginKey}\u001f{Normalizer.Name(record.Name)}\u001f{FormatUtc(record.UninstalledUtc)}\u001f{(int)record.Way}";

    private void InsertUninstallRecord(SqliteTransaction transaction, UninstallRecord record)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO uninstall_history (plugin_key, name, vendor, way, reason, uninstalled_utc, trash_reason, rating, note)
            VALUES ($key, $name, $vendor, $way, $reason, $utc, $trash, $rating, $note)
            """;
        command.Parameters.AddWithValue("$key", record.PluginKey ?? "");
        command.Parameters.AddWithValue("$name", record.Name.Trim());
        command.Parameters.AddWithValue("$vendor", record.Vendor ?? "");
        command.Parameters.AddWithValue("$way", (int)(Enum.IsDefined(record.Way) ? record.Way : UninstallWay.Unknown));
        command.Parameters.AddWithValue("$reason", (record.Reason ?? "").Trim());
        command.Parameters.AddWithValue("$utc", FormatUtc(record.UninstalledUtc));
        command.Parameters.AddWithValue("$trash", record.TrashReason ?? "");
        command.Parameters.AddWithValue("$rating", Math.Clamp(record.Rating, 0, 5));
        command.Parameters.AddWithValue("$note", record.Note ?? "");
        command.ExecuteNonQuery();
    }

    private static string FormatUtc(DateTime? utc) => utc is { } value ? value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : "";

    private static DateTime? ParseHistoryUtc(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
}
