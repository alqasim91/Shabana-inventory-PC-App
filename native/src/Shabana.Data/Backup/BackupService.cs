using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Data.Backup;

/// <summary>Creates verified backups. Behind an interface so the migration runner and the restore service can be tested with a failing one.</summary>
public interface IBackupService
{
    /// <summary>
    /// Takes a backup and verifies it. Returns only a backup that passed <see cref="BackupVerifier"/>; on any failure throws
    /// <see cref="BackupFailedException"/> and leaves no file at the final name.
    /// </summary>
    BackupResult Create(BackupRequest request);
}

/// <summary>
/// <c>.shbk</c> = ZIP(<c>shabana.db</c>, <c>manifest.json</c>) made with the SQLite ONLINE BACKUP API (04 sections 5.4, 9.2). A live WAL
/// database is never copied as a file. Order of events (data-safety critical):
/// <list type="number">
/// <item>everything is built under a private work folder and a <c>*.shbk.part</c> name, never under the final name;</item>
/// <item>the snapshot copy is checked (integrity, row counts against the source snapshot, SHA-256);</item>
/// <item>the finished archive is verified AGAIN by <see cref="BackupVerifier"/> (reads it back from disk);</item>
/// <item>only then is it renamed to its final name. An unverified archive is deleted and reported as a failure.</item>
/// </list>
/// Nothing but this operation's own temporary files is ever deleted here.
/// </summary>
public sealed class BackupService : IBackupService
{
    private const string PartSuffix = ".shbk.part";
    private const string WorkPrefix = ".shbk-work-";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly Action<string>? _afterArchiveWritten;

    /// <param name="time">Clock for names and the manifest.</param>
    /// <param name="afterArchiveWritten">Test seam: called with the path of the finished <c>.part</c> archive BEFORE it is verified.</param>
    public BackupService(TimeProvider? time = null, Action<string>? afterArchiveWritten = null)
    {
        _time = time ?? TimeProvider.System;
        _afterArchiveWritten = afterArchiveWritten;
    }

    public BackupResult Create(BackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = Path.GetFullPath(request.DatabasePath);
        if (!File.Exists(source))
        {
            throw new BackupFailedException(BackupFailure.SourceMissing, $"Database '{source}' does not exist.");
        }

        var folder = BackupLocations.FolderFor(Path.GetFullPath(request.BackupRoot), request.Reason);
        var work = Path.Combine(folder, WorkPrefix + Guid.NewGuid().ToString("N"));
        string? part = null;
        try
        {
            Directory.CreateDirectory(folder);
            SweepStaleTemporaries(folder);
            Directory.CreateDirectory(work);
            EnsureFreeSpace(folder, new FileInfo(source).Length + WalLength(source));

            var snapshotPath = Path.Combine(work, BackupVerifier.DatabaseEntry);
            var manifest = Snapshot(source, snapshotPath, request);

            var stem = request.FileStem ?? $"{BackupLocations.FolderName(request.Reason)}-{_time.GetUtcNow().UtcDateTime:yyyyMMdd-HHmmss}";
            if (stem != Path.GetFileName(stem) || stem.Length == 0 || stem.StartsWith('.'))
            {
                throw new ArgumentException("FileStem must be a plain file name.", nameof(request));
            }

            part = Path.Combine(folder, stem + PartSuffix);
            if (File.Exists(part))
            {
                part = Path.Combine(folder, stem + "-" + Guid.NewGuid().ToString("N")[..8] + PartSuffix);
            }

            WriteArchive(part, snapshotPath, manifest);
            _afterArchiveWritten?.Invoke(part);

            var verification = BackupVerifier.Verify(part);
            if (!verification.Ok)
            {
                throw new BackupFailedException(BackupFailure.VerificationFailed, "The new backup failed verification: " + verification.Failure);
            }

            var final = UniqueName(folder, stem);
            File.Move(part, final);      // no overwrite: a name collision is resolved above, never by replacing a backup
            part = null;
            return new BackupResult(final, manifest, new FileInfo(final).Length);
        }
        catch (BackupFailedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException or JsonException)
        {
            var kind = ex is SqliteException ? BackupFailure.SourceUnreadable : BackupFailure.Io;
            throw new BackupFailedException(kind, $"Backup to '{folder}' failed: {ex.Message}", ex);
        }
        finally
        {
            if (part is not null)
            {
                TryDelete(part);       // our own unfinished/unverified archive
            }

            TryDeleteDirectory(work);  // our own work folder
        }
    }

    /// <summary>Online backup of <paramref name="source"/> into <paramref name="snapshotPath"/>, checked, with its manifest.</summary>
    private BackupManifest Snapshot(string source, string snapshotPath, BackupRequest request)
    {
        var factory = new ConnectionFactory(source);
        SortedDictionary<string, long> sourceCounts;
        int schemaVersion;
        string? businessName;
        using (var src = factory.OpenForInspection(OpenMode.ReadWrite))
        {
            // One read snapshot for the counts AND the copy, so a concurrent writer cannot make them disagree.
            using var snapshot = src.BeginTransaction(deferred: true);
            sourceCounts = DatabaseChecks.RowCounts(src);
            schemaVersion = DatabaseChecks.UserVersion(src);
            businessName = ReadBusinessName(src);

            using (var dst = new SqliteConnection(ConnectionFactory.ConnectionString(snapshotPath, OpenMode.ReadWriteCreate, pooling: false)))
            {
                dst.Open();
                src.BackupDatabase(dst);
                // The copy inherits the WAL flag of the source's header; the archive must be a single self-contained file.
                var mode = ConnectionFactory.Scalar(dst, "PRAGMA journal_mode = DELETE");
                if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"Could not switch the backup copy to journal_mode=DELETE (got '{mode}').");
                }
            }

            snapshot.Rollback();
            TryCheckpoint(src);
        }

        foreach (var side in new[] { "-wal", "-shm", "-journal" })
        {
            if (File.Exists(snapshotPath + side))
            {
                throw new IOException("The backup copy left a side file (" + side + "); refusing to package it.");
            }
        }

        // Verify the copy against the SOURCE snapshot before it is packaged.
        using (var copy = new SqliteConnection(ConnectionFactory.ConnectionString(snapshotPath, OpenMode.ReadOnly, pooling: false)))
        {
            copy.Open();
            var problems = DatabaseChecks.IntegrityCheck(copy);
            if (problems.Count > 0)
            {
                throw new BackupFailedException(BackupFailure.VerificationFailed, "integrity_check of the backup copy failed: " + string.Join("; ", problems.Take(3)));
            }

            var copyCounts = DatabaseChecks.RowCounts(copy);
            if (!copyCounts.SequenceEqual(sourceCounts) || DatabaseChecks.UserVersion(copy) != schemaVersion)
            {
                throw new BackupFailedException(BackupFailure.VerificationFailed, "The backup copy does not have the same tables, row counts and schema version as the source.");
            }
        }

        return new BackupManifest
        {
            AppVersion = request.AppVersion,
            SchemaVersion = schemaVersion,
            CreatedAtUtc = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Reason = BackupLocations.FolderName(request.Reason),
            Sha256 = DatabaseChecks.FileSha256(snapshotPath),
            DbBytes = new FileInfo(snapshotPath).Length,
            RowCounts = sourceCounts,
            BusinessName = businessName,
        };
    }

    private static string? ReadBusinessName(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT business_name FROM business_settings WHERE id = 1";
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;     // not a Shabana schema (yet): the manifest simply has no name
        }
    }

    private static void WriteArchive(string part, string snapshotPath, BackupManifest manifest)
    {
        using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntryFromFile(snapshotPath, BackupVerifier.DatabaseEntry, CompressionLevel.Optimal);
                var entry = zip.CreateEntry(BackupVerifier.ManifestEntry, CompressionLevel.Optimal);
                using var writer = entry.Open();
                JsonSerializer.Serialize(writer, manifest, BackupManifest.Json);
            }

            stream.Flush(true);   // fsync before we call it a backup
        }
    }

    private static string UniqueName(string folder, string stem)
    {
        var candidate = Path.Combine(folder, stem + ".shbk");
        for (var n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{stem}-{n}.shbk");
        }

        return candidate;
    }

    private static long WalLength(string source) => File.Exists(source + "-wal") ? new FileInfo(source + "-wal").Length : 0;

    /// <summary>The copy (uncompressed) and the archive (at most the same size) live on the destination volume at the same time.</summary>
    private static void EnsureFreeSpace(string folder, long databaseBytes)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(folder) ?? folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return;   // cannot tell (exotic mount); the write itself will fail loudly if the disk is full
        }

        var needed = (databaseBytes * 2) + (16L << 20);
        if (free < needed)
        {
            throw new BackupFailedException(BackupFailure.DiskSpace, $"Not enough free space in '{folder}': {free} bytes free, about {needed} needed.");
        }
    }

    private static void TryCheckpoint(SqliteConnection connection)
    {
        try
        {
            ConnectionFactory.Exec(connection, "PRAGMA wal_checkpoint(TRUNCATE)");    // 04 section 5.2
        }
        catch (SqliteException)
        {
            // Busy because the application holds the database: harmless, the WAL is folded in later.
        }
    }

    /// <summary>
    /// Removes what an interrupted backup left behind: work folders and <c>.part</c> archives older than an hour. Never a
    /// finished <c>.shbk</c>.
    /// </summary>
    public static void SweepStaleTemporaries(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder, WorkPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    TryDeleteDirectory(dir);
                }
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*" + PartSuffix).Concat(Directory.EnumerateFiles(folder, ".shbk-verify-*")))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept later by SweepStaleTemporaries.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept later by SweepStaleTemporaries.
        }
    }
}
