using System.Globalization;
using Microsoft.Data.Sqlite;
using Shabana.Data.Backup;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Data.Migrations;

public enum MigrationFailure
{
    /// <summary>The database was written by a newer version of the application. It is not touched (04 section 8.2 step 1).</summary>
    NewerThanApp,

    /// <summary>The file is not a Shabana database, or its version history does not fit this application. It is not touched.</summary>
    NotRecognized,

    /// <summary>The pre-migration backup could not be made or verified. The upgrade did not start.</summary>
    BackupFailed,

    /// <summary>A script or a post-check failed. Everything was rolled back.</summary>
    ScriptFailed,

    /// <summary>The file exists but cannot be opened (permissions, locked by another program, I/O error). Nothing was changed.</summary>
    CannotOpen,
}

public sealed class MigrationException : Exception
{
    public MigrationException(MigrationFailure failure, string message, Exception? inner = null, int? databaseVersion = null, int? appVersion = null, string? backupFile = null)
        : base(message, inner)
    {
        Failure = failure;
        DatabaseVersion = databaseVersion;
        AppVersion = appVersion;
        BackupFile = backupFile;
    }

    public MigrationFailure Failure { get; }

    public int? DatabaseVersion { get; }

    public int? AppVersion { get; }

    /// <summary>The verified pre-migration backup, when one was taken before the failure.</summary>
    public string? BackupFile { get; }
}

public enum MigrationOutcome
{
    /// <summary>The file did not exist or was blank: the whole chain created it. No backup is needed (nothing to lose).</summary>
    Created,
    UpToDate,
    Upgraded,
}

public sealed record MigrationResult(MigrationOutcome Outcome, int FromVersion, int ToVersion, string? PreMigrationBackup);

/// <summary>Progress notification, for the start-up screen and for crash tests.</summary>
public sealed record MigrationProgress(string Stage, int Version);

/// <summary>
/// Brings the database to the newest schema (04 section 8.2). The whole chain is ONE transaction:
/// <list type="number">
/// <item>inspect without writing; refuse a database that is newer than the application or not recognised;</item>
/// <item>verified pre-migration backup (the upgrade does not start without it);</item>
/// <item><c>foreign_keys=OFF</c>, <c>BEGIN IMMEDIATE</c>, fingerprint, scripts in order (each sets <c>user_version</c>), then
///   <c>foreign_key_check</c> = empty, <c>integrity_check</c> = ok, <c>app_meta.schema_version</c> mirrors the version, balance fingerprint unchanged;</item>
/// <item>COMMIT; any failure before it is a ROLLBACK that leaves the database exactly as it was.</item>
/// </list>
/// </summary>
public sealed class MigrationRunner
{
    private readonly string _databasePath;
    private readonly IReadOnlyList<Migration> _migrations;
    private readonly IBackupService _backups;
    private readonly string _backupRoot;
    private readonly string _appVersion;
    private readonly TimeProvider _time;
    private readonly Action<MigrationProgress>? _progress;
    private readonly ConnectionOptions _options;

    public MigrationRunner(string databasePath, IReadOnlyList<Migration> migrations, IBackupService backups, string backupRoot, string appVersion,
        TimeProvider? time = null, Action<MigrationProgress>? progress = null, ConnectionOptions? options = null)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _backupRoot = backupRoot;
        _appVersion = appVersion;
        _time = time ?? TimeProvider.System;
        _progress = progress;
        _options = options ?? new ConnectionOptions();
        if (_migrations.Count == 0)
        {
            throw new ArgumentException("At least one migration is required.", nameof(migrations));
        }
    }

    /// <summary>The schema version this application expects (the highest script).</summary>
    public int TargetVersion => _migrations[^1].Version;

    public MigrationResult Run()
    {
        var target = TargetVersion;
        var factory = new ConnectionFactory(_databasePath, _options);

        // 1. Inspect without changing anything (no WAL switch, no pragmas that write).
        int current;
        var blank = false;
        if (!File.Exists(_databasePath))
        {
            current = 0;
            blank = true;
        }
        else
        {
            try
            {
                using var probe = factory.OpenForInspection(OpenMode.ReadWrite);
                current = DatabaseChecks.UserVersion(probe);
                blank = DatabaseChecks.IsBlank(probe);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                throw new MigrationException(MigrationFailure.NotRecognized, "The file is not a readable SQLite database: " + ex.Message, ex);
            }
            catch (SqliteException ex)
            {
                throw new MigrationException(MigrationFailure.CannotOpen, "The database file could not be opened: " + ex.Message, ex);
            }
        }

        if (current > target)
        {
            throw new MigrationException(MigrationFailure.NewerThanApp,
                $"The database is schema version {current}; this application understands up to {target}. Update the application.", null, current, target);
        }

        if (current == 0 && !blank)
        {
            throw new MigrationException(MigrationFailure.NotRecognized,
                "The file holds data but has no schema version: it is not a Shabana database.", null, current, target);
        }

        if (current < 0)
        {
            throw new MigrationException(MigrationFailure.NotRecognized, "The database reports a negative schema version.", null, current, target);
        }

        if (current == target)
        {
            RecordOpened(factory);
            return new MigrationResult(MigrationOutcome.UpToDate, current, target, null);
        }

        var pending = _migrations.Where(m => m.Version > current).ToList();
        if (pending.Count != target - current)
        {
            throw new MigrationException(MigrationFailure.NotRecognized, "The migration chain has a gap after the database's version.", null, current, target);
        }

        // Refuse a script that manages transactions itself BEFORE touching anything: a COMMIT in a script would end the all-or-nothing chain.
        Dictionary<int, IReadOnlyList<string>> statementsByVersion;
        try
        {
            statementsByVersion = pending.ToDictionary(m => m.Version, m => MigrationScripts.Statements(m));
        }
        catch (InvalidOperationException ex)
        {
            throw new MigrationException(MigrationFailure.ScriptFailed, ex.Message, ex, current, target);
        }

        // 2. Pre-migration backup, unless there is nothing to lose.
        string? backupFile = null;
        if (current > 0)
        {
            Report("backup", current);
            try
            {
                var stem = $"pre-v{current}-to-v{target}-{_time.GetUtcNow().UtcDateTime:yyyyMMdd-HHmmss}";
                backupFile = _backups.Create(new BackupRequest(_databasePath, _backupRoot, BackupReason.PreMigration, _appVersion, stem)).FilePath;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new MigrationException(MigrationFailure.BackupFailed,
                    $"The safety backup before the upgrade could not be made or verified, so the upgrade was not started: {ex.Message}", ex, current, target);
            }
        }

        // 3. The chain, in one transaction, on an unpooled connection that is closed (file released) when we are done.
        try
        {
            Apply(factory, current, target, pending, statementsByVersion);
        }
        catch (MigrationException ex)
        {
            // Everything was rolled back; tell the caller where the safety copy is.
            throw new MigrationException(ex.Failure, ex.Message, ex.InnerException, ex.DatabaseVersion, ex.AppVersion, backupFile);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            throw new MigrationException(MigrationFailure.ScriptFailed, "The upgrade failed and was rolled back: " + ex.Message, ex, current, target, backupFile);
        }

        return new MigrationResult(current == 0 ? MigrationOutcome.Created : MigrationOutcome.Upgraded, current, target, backupFile);
    }

    private void Apply(ConnectionFactory factory, int from, int target, List<Migration> pending, Dictionary<int, IReadOnlyList<string>> statementsByVersion)
    {
        if (from == 0 && !File.Exists(_databasePath))
        {
            QuarantineOrphanedSideFiles();
        }

        using var connection = factory.OpenUnpooled(from == 0 ? OpenMode.ReadWriteCreate : OpenMode.ReadWrite);

        // Must be set OUTSIDE a transaction (it is a no-op inside one): the table-rebuild recipe of 03 section 6 row 9 needs it OFF.
        ConnectionFactory.Exec(connection, "PRAGMA foreign_keys = OFF");
        ConnectionFactory.Exec(connection, "PRAGMA legacy_alter_table = OFF");
        using (var transaction = connection.BeginTransaction(deferred: false))   // BEGIN IMMEDIATE
        {
            try
            {
                string? balancesBefore = from > 0 ? DatabaseFingerprint.Balances(connection) : null;
                var mayChangeBalances = false;

                foreach (var migration in pending)
                {
                    Report("script", migration.Version);
                    // One command PER statement. Microsoft.Data.Sqlite's ExecuteNonQuery on a multi-statement text silently stops at the first
                    // statement that returns rows (a stray SELECT would skip the rest of the script without an error).
                    foreach (var statement in statementsByVersion[migration.Version])
                    {
                        ConnectionFactory.Exec(connection, statement);
                    }

                    // The runner, not the script, owns the version. (Baseline also says PRAGMA user_version = 1; the same value.)
                    ConnectionFactory.Exec(connection, "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture));
                    // A table-rebuild script turns this ON for its own duration (ALTER TABLE ... RENAME otherwise re-parses every trigger
                    // and view and fails while the table is momentarily dropped); it must not leak into the next script.
                    ConnectionFactory.Exec(connection, "PRAGMA legacy_alter_table = OFF");
                    mayChangeBalances |= migration.BalancesMayChange;
                    Report("script-done", migration.Version);
                }

                Report("checks", target);
                var fk = DatabaseChecks.ForeignKeyViolations(connection, 5);
                if (fk.Count > 0)
                {
                    throw new MigrationException(MigrationFailure.ScriptFailed, "foreign_key_check found: " + string.Join("; ", fk), null, from, target);
                }

                var problems = DatabaseChecks.IntegrityCheck(connection, 5);
                if (problems.Count > 0)
                {
                    throw new MigrationException(MigrationFailure.ScriptFailed, "integrity_check failed after the upgrade: " + string.Join("; ", problems), null, from, target);
                }

                var version = DatabaseChecks.UserVersion(connection);
                var mirror = ScalarOrNull(connection, "SELECT value FROM app_meta WHERE key = 'schema_version'");
                if (version != target || mirror != target.ToString(CultureInfo.InvariantCulture))
                {
                    throw new MigrationException(MigrationFailure.ScriptFailed,
                        $"user_version is {version} and app_meta.schema_version is '{mirror}', both must be {target}; the last script must write app_meta.schema_version.", null, from, target);
                }

                if (balancesBefore is not null && !mayChangeBalances && DatabaseFingerprint.Balances(connection) != balancesBefore)
                {
                    throw new MigrationException(MigrationFailure.ScriptFailed,
                        $"The upgrade changed stock, cash, order or contact balances. A script that does so on purpose must contain '-- {Migration.BalancesMayChangeMarker}'.", null, from, target);
                }

                WriteVersionNotes(connection, from);

                Report("commit", target);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
                {
                    // SQLite already rolled back; disposing the connection finishes it.
                }

                throw;
            }
        }

        ConnectionFactory.Exec(connection, "PRAGMA foreign_keys = ON");
        try
        {
            // 04 section 8.2 step 6: fold the WAL into the main file and refresh statistics. Housekeeping: failure is not an upgrade failure.
            ConnectionFactory.Exec(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            ConnectionFactory.Exec(connection, "PRAGMA optimize");
        }
        catch (SqliteException)
        {
            // Retried by the next start.
        }

    }

    /// <summary>
    /// A <c>-wal</c>/<c>-shm</c>/<c>-journal</c> with no database file next to it belongs to a database that is gone. SQLite would try to replay it
    /// into the NEW file. Rename (never delete) such files so they cannot be mistaken for ours.
    /// </summary>
    private void QuarantineOrphanedSideFiles()
    {
        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var orphan = _databasePath + suffix;
            if (File.Exists(orphan))
            {
                File.Move(orphan, $"{_databasePath}.orphaned-{stamp}{suffix}", overwrite: false);
            }
        }
    }

    private void WriteVersionNotes(SqliteConnection connection, int from)
    {
        // 04 section 8.5. Same transaction as the schema change, so they are never out of step with it.
        Upsert(connection, "last_opened_by_app_version", _appVersion);
        if (from == 0)
        {
            Upsert(connection, "created_by_app_version", _appVersion);
        }
        else
        {
            Upsert(connection, "last_migrated_from", from.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void RecordOpened(ConnectionFactory factory)
    {
        try
        {
            using var connection = factory.OpenUnpooled(OpenMode.ReadWrite);
            Upsert(connection, "last_opened_by_app_version", _appVersion);
        }
        catch (SqliteException)
        {
            // Bookkeeping only: a busy database must not stop the application from starting.
        }
    }

    private static void Upsert(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO app_meta (key, value) VALUES (@k, @v) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private static string? ScalarOrNull(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private void Report(string stage, int version) => _progress?.Invoke(new MigrationProgress(stage, version));
}
