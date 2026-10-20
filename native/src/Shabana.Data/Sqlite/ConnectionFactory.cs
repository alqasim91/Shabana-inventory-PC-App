using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Shabana.Data.Sqlite;

/// <summary>
/// Durability level for <c>PRAGMA synchronous</c>. Production uses <see cref="Full"/> (04 section 5.2: a committed sale must
/// survive a power cut). Tests that do tens of thousands of commits may use a cheaper level; that is the ONLY reason to.
/// </summary>
public enum SynchronousMode
{
    Off = 0,
    Normal = 1,
    Full = 2,
}

/// <summary>How a connection is opened. The default is what the application uses.</summary>
public sealed record ConnectionOptions
{
    /// <summary>
    /// Connection pooling. The pool keeps the file handle open after <c>Dispose()</c>; on Windows that blocks renaming or
    /// deleting the file. Code that swaps or deletes database files must call <see cref="ConnectionFactory.ReleaseFileHandles"/>
    /// first; maintenance code (migration, backup, restore) uses unpooled connections and needs no such call.
    /// </summary>
    public bool Pooling { get; init; } = true;

    public SynchronousMode Synchronous { get; init; } = SynchronousMode.Full;

    /// <summary>The schema is correct with this OFF (default) or ON (03 section 1); tests run both.</summary>
    public bool RecursiveTriggers { get; init; }

    public int BusyTimeoutMilliseconds { get; init; } = 5000;
}

/// <summary>How the database file may be opened.</summary>
public enum OpenMode
{
    /// <summary>The application's normal mode: creates an empty file if there is none (only <c>MigrationRunner</c> relies on this).</summary>
    ReadWriteCreate,

    /// <summary>Never creates a file. A missing file is an error, not a silently blank database.</summary>
    ReadWrite,

    /// <summary>Read only (verification of backups).</summary>
    ReadOnly,
}

/// <summary>
/// The only place a <see cref="SqliteConnection"/> to the business database is created. Applies the pragmas of 04 section 5.2 on
/// EVERY open (<c>foreign_keys</c> is per connection, and a pooled connection may have been changed by its previous user).
/// </summary>
public sealed class ConnectionFactory
{
    private const int RetryAttempts = 3;
    private const int RetryDelayMilliseconds = 250;

    private int _versionChecked;

    public ConnectionFactory(string databasePath, ConnectionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Options = options ?? new ConnectionOptions();
    }

    public string DatabasePath { get; }

    public ConnectionOptions Options { get; }

    /// <summary>Opens a connection with the full pragma set (WAL, foreign keys ON, ...). Creates the file if it does not exist.</summary>
    public SqliteConnection Open() => Open(OpenMode.ReadWriteCreate);

    /// <summary>Same, but never creates a file.</summary>
    public SqliteConnection OpenExisting() => Open(OpenMode.ReadWrite);

    public SqliteConnection Open(OpenMode mode) => Open(mode, Options);

    /// <summary>
    /// Opens a connection that is NOT pooled (the file handle is released by <c>Dispose()</c>) whatever the factory's option says.
    /// Used by migration, backup, restore and integrity code.
    /// </summary>
    public SqliteConnection OpenUnpooled(OpenMode mode = OpenMode.ReadWrite) => Open(mode, Options with { Pooling = false });

    /// <summary>
    /// Opens a connection for INSPECTION only: no WAL switch, no write-side pragmas, so a file we may not touch (a newer
    /// version, a foreign file) is never modified. Not pooled.
    /// </summary>
    public SqliteConnection OpenForInspection(OpenMode mode = OpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(ConnectionString(DatabasePath, mode, pooling: false, Options.BusyTimeoutMilliseconds));
        try
        {
            connection.Open();
            Exec(connection, "PRAGMA busy_timeout = " + Options.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Closes every pooled connection so that no process-wide handle keeps a database file open. Call before renaming, replacing
    /// or deleting a database file (Windows refuses to move a file that is open).
    /// </summary>
    public static void ReleaseFileHandles() => SqliteConnection.ClearAllPools();

    private SqliteConnection Open(OpenMode mode, ConnectionOptions options)
    {
        if (mode == OpenMode.ReadWriteCreate)
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        return Retry(() =>
        {
            var connection = new SqliteConnection(ConnectionString(DatabasePath, mode, options.Pooling, options.BusyTimeoutMilliseconds));
            try
            {
                connection.Open();
                ApplyPragmas(connection, options, mode);
                if (Interlocked.Exchange(ref _versionChecked, 1) == 0)
                {
                    SqliteRuntime.AssertSupported(connection);
                }

                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        });
    }

    /// <summary>
    /// <paramref name="busyTimeoutMilliseconds"/> goes into <c>Default Timeout</c> (whole seconds, at least 1, rounded up): Microsoft.Data.Sqlite
    /// keeps retrying a command that meets SQLITE_BUSY/LOCKED until that many seconds have passed (30 by default; 0 would mean forever), whatever
    /// <c>PRAGMA busy_timeout</c> says. So this value, not the pragma, is what bounds a wait.
    /// </summary>
    public static string ConnectionString(string path, OpenMode mode, bool pooling, int busyTimeoutMilliseconds = 5000)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode switch
            {
                OpenMode.ReadWriteCreate => SqliteOpenMode.ReadWriteCreate,
                OpenMode.ReadWrite => SqliteOpenMode.ReadWrite,
                _ => SqliteOpenMode.ReadOnly,
            },
            Cache = SqliteCacheMode.Private,
            Pooling = pooling,
            ForeignKeys = true,
            DefaultTimeout = Math.Max(1, (busyTimeoutMilliseconds + 999) / 1000),   // 0 would mean "wait forever" to Microsoft.Data.Sqlite
        };
        return builder.ToString();
    }

    /// <summary>04 section 5.2. Order matters: busy_timeout first so the remaining pragmas wait instead of failing.</summary>
    internal static void ApplyPragmas(SqliteConnection connection, ConnectionOptions options, OpenMode mode)
    {
        Exec(connection, "PRAGMA busy_timeout = " + options.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
        if (mode != OpenMode.ReadOnly)
        {
            var journal = Scalar(connection, "PRAGMA journal_mode = WAL");
            if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
            {
                // A network share, a read-only medium or an in-memory database: WAL cannot be used, and 04 section 5.2 forbids
                // running without it (readers must not block the writer, and crash safety is argued for WAL).
                throw new InvalidOperationException($"SQLite refused journal_mode=WAL (got '{journal}'). The database must be on a local fixed disk.");
            }

            Exec(connection, "PRAGMA synchronous = " + ((int)options.Synchronous).ToString(CultureInfo.InvariantCulture));
            Exec(connection, "PRAGMA wal_autocheckpoint = 1000");
            Exec(connection, "PRAGMA journal_size_limit = 67108864");
        }

        Exec(connection, "PRAGMA foreign_keys = ON");
        Exec(connection, "PRAGMA recursive_triggers = " + (options.RecursiveTriggers ? "ON" : "OFF"));
        Exec(connection, "PRAGMA temp_store = MEMORY");
        Exec(connection, "PRAGMA trusted_schema = ON");    // the schema uses triggers and views
        Exec(connection, "PRAGMA query_only = OFF");       // a pooled connection may come back from a read-only unit of work
    }

    internal static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>04 section 5.4: anti-virus scanners and Windows backup hold brief locks; retry SQLITE_BUSY / SQLITE_LOCKED a few times.</summary>
    internal static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (SqliteException ex) when (attempt < RetryAttempts && ex.SqliteErrorCode is 5 or 6)
            {
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }
}
