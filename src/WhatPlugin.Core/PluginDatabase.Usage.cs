using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WhatPlugin.Core;

/// <summary>
/// 使用记录里的一条。只有类型、时间和数字；Subject 是插件的稳定标识或工程的散列值，
/// 不放路径、文件名、账号、密码、序列号或用户写的文字。
/// </summary>
public sealed record UsageEvent(string Kind, DateTime Utc, DateOnly LocalDate, int LocalHour, long Value, long Previous, string Subject);

/// <summary>使用记录的类型。成就包按这些判断，名字一旦发布就不能再改。</summary>
public static class UsageKinds
{
    /// <summary>每个本地自然日第一次打开。Value 是距上次打开的整天数，第一次打开为 -1。</summary>
    public const string Launch = "launch";
    /// <summary>插件扫描完整结束（没有停止、没有出错）。Value 是扫描后的效果器数。</summary>
    public const string Scan = "scan";
    public const string BackupExport = "backup.export";
    public const string ChecklistExport = "checklist.export";
    public const string ReviewExport = "review.export";
    /// <summary>导入完整备份。Value 是这次新增和更新的插件资料条数。</summary>
    public const string BackupImport = "backup.import";
    /// <summary>心愿单数量变化。Value 是变化后的数量，Previous 是变化前的。</summary>
    public const string WishCount = "wish.count";
    /// <summary>本软件移除过的文件又被扫描到。Subject 是插件标识，Previous 是那次移除的时间（Ticks）。</summary>
    public const string Reappeared = "plugin.reappeared";
    /// <summary>工程报告里一首歌的缺失数（含忽略的）。Subject 是歌的散列值，每首只留最近两条。</summary>
    public const string ProjectMissing = "project.missing";
    /// <summary>用户自己做的保存操作。每个本地小时一条，Value 是次数。</summary>
    public const string Action = "action";
    /// <summary>插件的购买方式被用户改动。Subject 是插件标识，Previous、Value 是改动前后的 <see cref="PurchaseKind"/>。</summary>
    public const string PurchaseChange = "purchase.change";
}

/// <summary>备份里的使用记录和已解锁的成就。</summary>
public sealed record UsageBackup(IReadOnlyList<UsageEvent>? Events, IReadOnlyDictionary<string, DateTime>? Achievements);

public sealed partial class PluginDatabase
{
    /// <summary>“记录使用统计（仅本机）”开关，默认开。</summary>
    public const string UsageEnabledSetting = "usage.enabled";

    /// <summary>上次打开的时间。恢复备份时不覆盖本机的，否则下次打开会被误认成“好久不见”。</summary>
    public const string LastLaunchSetting = "usage.lastLaunchUtc";

    /// <summary>
    /// 数据库里有内容被写入、修改或删除（不含使用记录和成就本身）。在写入的线程上触发，
    /// 处理时不要再访问数据库，交给界面线程稍后去做。
    /// </summary>
    public event Action? DataChanged;

    private void HookChanges()
    {
        SQLitePCL.raw.sqlite3_update_hook(_connection.Handle, (_, _, _, table, _) =>
        {
            var name = table.utf8_to_string();
            if (name is "usage_events" or "achievements")
                return;
            DataChanged?.Invoke();
        }, null);
    }

    public bool UsageEnabled
    {
        get => GetSetting(UsageEnabledSetting) != "0";
        set => SetSetting(UsageEnabledSetting, value ? "1" : "0");
    }

    /// <summary>记一条使用记录。开关关着时什么都不记。</summary>
    public void RecordUsage(string kind, long value = 1, long previous = 0, string subject = "", DateTime? utc = null)
    {
        if (!UsageEnabled)
            return;
        var now = utc ?? DateTime.UtcNow;
        lock (_gate)
            InsertUsage(null, new UsageEvent(kind, now, LocalDate(now), now.ToLocalTime().Hour, value, previous, subject));
    }

    /// <summary>打开软件。同一天只记第一次；Value 是距上次打开的天数，供“好久不见”判断。</summary>
    public void RecordLaunch(DateTime? utc = null)
    {
        if (!UsageEnabled)
            return;
        var now = utc ?? DateTime.UtcNow;
        var last = GetSetting(LastLaunchSetting);
        long gap = -1;
        if (last != null && DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastUtc) && lastUtc <= now)
            gap = (long)(now - lastUtc).TotalDays;
        SetSetting(LastLaunchSetting, now.ToString("O", CultureInfo.InvariantCulture));
        lock (_gate)
        {
            if (CountUsage(UsageKinds.Launch, LocalDate(now)) > 0)
                return;
            InsertUsage(null, new UsageEvent(UsageKinds.Launch, now, LocalDate(now), now.ToLocalTime().Hour, gap, 0, ""));
        }
    }

    /// <summary>用户自己的保存操作，按本地小时累加，不逐条记。</summary>
    public void RecordAction(DateTime? utc = null)
    {
        if (!UsageEnabled)
            return;
        var now = utc ?? DateTime.UtcNow;
        var date = LocalDate(now);
        var hour = now.ToLocalTime().Hour;
        lock (_gate)
        {
            using var update = _connection.CreateCommand();
            update.CommandText = "UPDATE usage_events SET value = value + 1 WHERE kind = $kind AND local_date = $date AND local_hour = $hour";
            update.Parameters.AddWithValue("$kind", UsageKinds.Action);
            update.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$hour", hour);
            if (update.ExecuteNonQuery() == 0)
                InsertUsage(null, new UsageEvent(UsageKinds.Action, now, date, hour, 1, 0, ""));
        }
    }

    /// <summary>心愿单数量和上次记的不同时才记。</summary>
    public void RecordWishCount(int count, DateTime? utc = null)
    {
        if (!UsageEnabled)
            return;
        var previous = LatestUsage(UsageKinds.WishCount, null)?.Value;
        if (previous == count)
            return;
        RecordUsage(UsageKinds.WishCount, count, previous ?? 0, "", utc);
    }

    /// <summary>一首歌的缺失数变了才记，每首只留最近两条，够判断“从有缺失到不缺”。</summary>
    public void RecordProjectMissing(string songKey, int missing, DateTime? utc = null)
    {
        if (!UsageEnabled)
            return;
        var subject = SongSubject(songKey);
        var latest = LatestUsage(UsageKinds.ProjectMissing, subject);
        if (latest?.Value == missing)
            return;
        RecordUsage(UsageKinds.ProjectMissing, missing, latest?.Value ?? -1, subject, utc);
        lock (_gate)
        {
            using var trim = _connection.CreateCommand();
            trim.CommandText = """
                DELETE FROM usage_events WHERE kind = $kind AND subject = $subject
                    AND id NOT IN (SELECT id FROM usage_events WHERE kind = $kind AND subject = $subject ORDER BY id DESC LIMIT 2)
                """;
            trim.Parameters.AddWithValue("$kind", UsageKinds.ProjectMissing);
            trim.Parameters.AddWithValue("$subject", subject);
            trim.ExecuteNonQuery();
        }
    }

    /// <summary>工程路径不进使用记录，只用它的散列值区分是不是同一首歌。</summary>
    public static string SongSubject(string songKey)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(songKey.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 12);
    }

    public List<UsageEvent> LoadUsage(string? kind = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT kind, utc, local_date, local_hour, value, previous, subject FROM usage_events"
                + (kind == null ? "" : " WHERE kind = $kind") + " ORDER BY utc, id";
            if (kind != null)
                command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            var events = new List<UsageEvent>();
            while (reader.Read())
                events.Add(ReadUsage(reader));
            return events;
        }
    }

    public int CountUsageEvents()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM usage_events";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    /// <summary>清空使用记录。已经解锁的成就保留。</summary>
    public void ClearUsage()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM usage_events; DELETE FROM app_settings WHERE key = $last;";
            command.Parameters.AddWithValue("$last", LastLaunchSetting);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>从备份恢复使用记录：完全相同的不重复加。返回新加的条数。</summary>
    public int ImportUsage(IEnumerable<UsageEvent> events)
    {
        lock (_gate)
        {
            var added = 0;
            using var transaction = _connection.BeginTransaction();
            foreach (var e in events)
            {
                if (string.IsNullOrWhiteSpace(e.Kind))
                    continue;
                using var exists = _connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM usage_events WHERE kind = $kind AND utc = $utc AND subject = $subject AND value = $value";
                exists.Parameters.AddWithValue("$kind", e.Kind);
                exists.Parameters.AddWithValue("$utc", e.Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                exists.Parameters.AddWithValue("$subject", e.Subject ?? "");
                exists.Parameters.AddWithValue("$value", e.Value);
                if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
                    continue;
                InsertUsage(transaction, e with { Subject = e.Subject ?? "" });
                added++;
            }
            transaction.Commit();
            return added;
        }
    }

    // ---------- 已解锁的成就。由成就包写入，放在主数据库里，备份时一起带走。 ----------

    public Dictionary<string, DateTime> LoadAchievements()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, unlocked_utc FROM achievements";
            using var reader = command.ExecuteReader();
            var unlocked = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            while (reader.Read())
                unlocked[reader.GetString(0)] = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return unlocked;
        }
    }

    /// <summary>记下解锁。已经解锁的保留更早的时间。返回这次新解锁的。</summary>
    public IReadOnlyList<string> UnlockAchievements(IEnumerable<string> ids, DateTime utc)
    {
        var map = ids.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => utc, StringComparer.Ordinal);
        return ImportAchievements(map);
    }

    /// <summary>合并成就解锁记录，同一个成就取更早的解锁时间。返回原来没有的。</summary>
    public IReadOnlyList<string> ImportAchievements(IReadOnlyDictionary<string, DateTime> unlocked)
    {
        lock (_gate)
        {
            var added = new List<string>();
            using var transaction = _connection.BeginTransaction();
            foreach (var (id, when) in unlocked)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO achievements (id, unlocked_utc) VALUES ($id, $utc) ON CONFLICT(id) DO NOTHING";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$utc", when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                if (command.ExecuteNonQuery() > 0)
                {
                    added.Add(id);
                    continue;
                }
                using var earlier = _connection.CreateCommand();
                earlier.Transaction = transaction;
                earlier.CommandText = "UPDATE achievements SET unlocked_utc = $utc WHERE id = $id AND unlocked_utc > $utc";
                earlier.Parameters.AddWithValue("$id", id);
                earlier.Parameters.AddWithValue("$utc", when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                earlier.ExecuteNonQuery();
            }
            transaction.Commit();
            return added;
        }
    }

    private static DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(utc.ToLocalTime());

    private int CountUsage(string kind, DateOnly date)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_events WHERE kind = $kind AND local_date = $date";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private UsageEvent? LatestUsage(string kind, string? subject)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT kind, utc, local_date, local_hour, value, previous, subject FROM usage_events WHERE kind = $kind"
                + (subject == null ? "" : " AND subject = $subject") + " ORDER BY id DESC LIMIT 1";
            command.Parameters.AddWithValue("$kind", kind);
            if (subject != null)
                command.Parameters.AddWithValue("$subject", subject);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadUsage(reader) : null;
        }
    }

    private void InsertUsage(SqliteTransaction? transaction, UsageEvent e)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage_events (kind, utc, local_date, local_hour, value, previous, subject)
            VALUES ($kind, $utc, $date, $hour, $value, $previous, $subject)
            """;
        command.Parameters.AddWithValue("$kind", e.Kind);
        command.Parameters.AddWithValue("$utc", e.Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$date", e.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$hour", Math.Clamp(e.LocalHour, 0, 23));
        command.Parameters.AddWithValue("$value", e.Value);
        command.Parameters.AddWithValue("$previous", e.Previous);
        command.Parameters.AddWithValue("$subject", e.Subject);
        command.ExecuteNonQuery();
    }

    private static UsageEvent ReadUsage(SqliteDataReader reader) => new(
        reader.GetString(0),
        DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.GetInt32(3),
        reader.GetInt64(4),
        reader.GetInt64(5),
        reader.GetString(6));
}
