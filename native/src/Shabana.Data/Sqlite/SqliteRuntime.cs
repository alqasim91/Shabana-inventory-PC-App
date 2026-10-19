using Microsoft.Data.Sqlite;

namespace Shabana.Data.Sqlite;

/// <summary>
/// The SQLite engine the schema needs. 04 section 2: STRICT tables (3.37), generated columns (3.31) and
/// RETURNING (3.35) are used, so the app refuses to start on anything older than 3.38.
/// Microsoft.Data.Sqlite ships its own engine (SQLitePCLRaw bundle), so this is a regression guard on package updates.
/// </summary>
public static class SqliteRuntime
{
    public static readonly Version MinimumVersion = new(3, 38, 0);

    /// <summary>Version reported by <c>sqlite_version()</c> of the engine behind <paramref name="connection"/>.</summary>
    public static Version GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var text = (string)cmd.ExecuteScalar()!;
        return Version.Parse(text);
    }

    /// <summary>Throws when the engine is older than <see cref="MinimumVersion"/>.</summary>
    public static Version AssertSupported(SqliteConnection connection)
    {
        var actual = GetVersion(connection);
        if (actual < MinimumVersion)
        {
            throw new InvalidOperationException(
                $"SQLite {actual} is older than the required minimum {MinimumVersion}.");
        }

        return actual;
    }
}
