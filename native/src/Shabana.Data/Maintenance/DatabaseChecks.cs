using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Shabana.Data.Maintenance;

/// <summary>Low-level SQLite health queries shared by backup, restore, migration and the integrity service.</summary>
public static class DatabaseChecks
{
    /// <summary>The messages of <c>PRAGMA integrity_check</c>, or an empty list when it answers <c>ok</c>. At most <paramref name="max"/> messages.</summary>
    public static IReadOnlyList<string> IntegrityCheck(SqliteConnection connection, int max = 10) => PragmaCheck(connection, "integrity_check", max);

    /// <summary>Same for the cheaper <c>PRAGMA quick_check</c> (every start-up, 04 section 9.4).</summary>
    public static IReadOnlyList<string> QuickCheck(SqliteConnection connection, int max = 10) => PragmaCheck(connection, "quick_check", max);

    private static List<string> PragmaCheck(SqliteConnection connection, string pragma, int max)
    {
        var problems = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma}({max.ToString(CultureInfo.InvariantCulture)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var line = reader.GetString(0);
            if (!string.Equals(line, "ok", StringComparison.Ordinal))
            {
                problems.Add(line);
            }
        }

        return problems;
    }

    /// <summary>Rows reported by <c>PRAGMA foreign_key_check</c> as "table(rowid) -> parent".</summary>
    public static IReadOnlyList<string> ForeignKeyViolations(SqliteConnection connection, int max = 20)
    {
        var rows = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_check";
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && rows.Count < max)
        {
            rows.Add($"{reader.GetString(0)}({(reader.IsDBNull(1) ? "?" : reader.GetInt64(1).ToString(CultureInfo.InvariantCulture))}) -> {reader.GetString(2)}");
        }

        return rows;
    }

    /// <summary>Row count of every user table, by name.</summary>
    public static SortedDictionary<string, long> RowCounts(SqliteConnection connection)
    {
        var names = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {DatabaseFingerprint.Quote(name)}";
            counts[name] = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    public static int UserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>True when the file has no tables, views, triggers or indexes of its own (a brand-new or blank database).</summary>
    public static bool IsBlank(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\'";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    /// <summary>SHA-256 (lower-case hex) of a whole file, read in blocks.</summary>
    public static string FileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
