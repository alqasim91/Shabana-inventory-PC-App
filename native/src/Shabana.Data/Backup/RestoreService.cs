using System.Globalization;
using Microsoft.Data.Sqlite;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Data.Backup;

/// <summary>The steps of a restore, in order. A crash test throws after any of them (04 section 12.6).</summary>
public enum RestoreStep
{
    CandidateStaged,
    PreRestoreBackupTaken,
    LiveCheckpointed,
    JournalTempWritten,
    JournalWritten,
    LiveDatabaseMovedAside,
    SideFilesMovedAside,
    CandidateMovedIntoPlace,
    PostSwapValidated,
    JournalCommitted,
    AsideRemoved,
    JournalRemoved,
    RollbackRejectedMovedOut,
    RollbackOldRestored,

    /// <summary>After EVERY single file rename inside the multi-file loops (forward swap, rollback, commit cleanup): lets a crash test stop between any two files.</summary>
    FileMoved,
}

/// <summary>Thrown by a test hook to stand for the process dying at that instant: no handler may run any cleanup or undo for it.</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(RestoreStep step)
        : base("Simulated process death after " + step)
    {
        Step = step;
    }

    public RestoreStep Step { get; }
}

public enum RestoreFailure
{
    /// <summary>The backup file failed verification, or is not a Shabana backup.</summary>
    InvalidBackup,

    /// <summary>The backup was made by a newer version of the application: never downgrade (04 section 8.2).</summary>
    NewerThanApp,

    /// <summary>A verified pre-restore backup of the current data could not be made; nothing was changed.</summary>
    PreRestoreBackupFailed,

    /// <summary>The database is open elsewhere (checkpoint busy); nothing was changed.</summary>
    DatabaseBusy,

    /// <summary>The swap or the validation of the restored database failed; the previous database was put back.</summary>
    SwapFailed,

    /// <summary>An earlier interrupted restore could not be settled; see the journal. Nothing was changed by this call.</summary>
    PendingSwapUnresolved,
}

public sealed class RestoreFailedException : Exception
{
    public RestoreFailedException(RestoreFailure failure, string message, bool previousDatabaseRestored, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        PreviousDatabaseRestored = previousDatabaseRestored;
    }

    public RestoreFailure Failure { get; }

    /// <summary>True when the live database is exactly the one that was there before the call.</summary>
    public bool PreviousDatabaseRestored { get; }
}

public sealed record RestoreRequest(string BackupFile);

public sealed record RestoreResult(BackupManifest Restored, string? PreRestoreBackup, string? DamagedCopyKeptAt, bool CleanupPending);

/// <summary>What a restore would replace, for the confirmation screen (04 section 9.2).</summary>
public sealed record RestorePreview(BackupVerification Candidate, IReadOnlyDictionary<string, long>? CurrentRowCounts);

public sealed class RestoreOptions
{
    /// <summary>Called after every step. Tests throw <see cref="SimulatedCrashException"/> from it.</summary>
    public Action<RestoreStep>? AfterStep { get; init; }

    /// <summary>
    /// Runs on the restored database in its final place BEFORE the restore is committed: migrate an older backup, then check it.
    /// Throwing undoes the whole restore. Default: <c>quick_check</c> only.
    /// </summary>
    public Action<string>? PostSwapValidator { get; init; }
}

/// <summary>
/// Restore from a <c>.shbk</c> (04 section 9.2). The caller has closed every connection of the application. The sequence:
/// verify the backup in a staging file -> verified PRE-RESTORE backup of the current data (refuse to go on without it, unless the
/// current database is itself damaged) -> checkpoint and close -> write the swap journal -> move the old database set aside ->
/// move the candidate into place -> validate it (migrate/quick_check) -> commit the journal -> remove the old set.
/// Any failure before the commit puts the old database back; a crash is settled by <see cref="RecoverPendingSwap"/> at the next start.
/// </summary>
public sealed class RestoreService
{
    private readonly RestorePaths _paths;
    private readonly IBackupService _backups;
    private readonly string _appVersion;
    private readonly int _appSchemaVersion;
    private readonly RestoreOptions _options;
    private readonly TimeProvider _time;

    public RestoreService(RestorePaths paths, IBackupService backups, string appVersion, int appSchemaVersion, RestoreOptions? options = null, TimeProvider? time = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _appVersion = appVersion;
        _appSchemaVersion = appSchemaVersion;
        _options = options ?? new RestoreOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Verifies a candidate backup and summarises the current database; changes nothing.</summary>
    public RestorePreview Preview(string backupFile)
    {
        var candidate = BackupVerifier.Verify(backupFile);
        IReadOnlyDictionary<string, long>? current = null;
        if (File.Exists(_paths.DatabasePath))
        {
            try
            {
                using var connection = new ConnectionFactory(_paths.DatabasePath).OpenForInspection(OpenMode.ReadWrite);   // a read-only connection would leave -wal/-shm behind
                current = DatabaseChecks.RowCounts(connection);
            }
            catch (SqliteException)
            {
                current = null;   // damaged: the screen says so
            }
        }

        return new RestorePreview(candidate, current);
    }

    public RestoreResult Restore(RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectionFactory.ReleaseFileHandles();

        // An earlier interrupted restore must be settled first; never stack a second swap on an unsettled one.
        var pending = RecoverPendingSwap(_paths);
        if (!pending.IsSettled)
        {
            throw new RestoreFailedException(RestoreFailure.PendingSwapUnresolved, pending.Detail ?? "An earlier restore is unresolved.", previousDatabaseRestored: false);
        }

        // Leftovers of an earlier swap (a committed one whose cleanup is still pending, or files whose journal was lost) are somebody's only copy
        // of the previous database as far as this code can tell: a new swap must never run into them.
        if (File.Exists(_paths.JournalPath)
            || RestorePaths.FileSetSuffixes.Any(x => File.Exists(_paths.AsidePath + x) || File.Exists(_paths.RejectedPath + x)))
        {
            throw new RestoreFailedException(RestoreFailure.PendingSwapUnresolved,
                "Files of an earlier restore are still present next to the database (" + Path.GetFileName(_paths.AsidePath) + "); they are kept until they are dealt with.", previousDatabaseRestored: false);
        }

        var live = _paths.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        DeleteOwnLeftovers();

        // 1. Verify the backup into a staging file next to the live database.
        var verification = BackupVerifier.VerifyAndExtract(request.BackupFile, _paths.CandidatePath);
        if (!verification.Ok || verification.Manifest is null)
        {
            throw new RestoreFailedException(RestoreFailure.InvalidBackup, "The backup file is not valid: " + verification.Failure, previousDatabaseRestored: true);
        }

        var manifest = verification.Manifest;
        foreach (var suffix in RestorePaths.FileSetSuffixes.Skip(1))
        {
            FileOps.DeleteIfExists(_paths.CandidatePath + suffix);     // a read-only open of a WAL-flagged file can leave these
        }

        try
        {
            if (manifest.SchemaVersion < 1 || !manifest.RowCounts.ContainsKey("app_meta") || !manifest.RowCounts.ContainsKey("business_settings"))
            {
                throw new RestoreFailedException(RestoreFailure.InvalidBackup, "The backup does not hold a Shabana database (no schema version or no application tables).", previousDatabaseRestored: true);
            }

            if (manifest.SchemaVersion > _appSchemaVersion)
            {
                throw new RestoreFailedException(RestoreFailure.NewerThanApp,
                    $"The backup is schema version {manifest.SchemaVersion}; this application understands up to {_appSchemaVersion}.", previousDatabaseRestored: true);
            }

            Step(RestoreStep.CandidateStaged);

            // 2. Safety copy of what is there now.
            var oldExisted = File.Exists(live);
            var damaged = false;
            string? preRestoreBackup = null;
            if (oldExisted)
            {
                damaged = !IsHealthy(live);
                if (!damaged)
                {
                    try
                    {
                        preRestoreBackup = _backups.Create(new BackupRequest(live, _paths.BackupRoot, BackupReason.PreRestore, _appVersion)).FilePath;
                    }
                    catch (Exception ex) when (ex is not SimulatedCrashException)
                    {
                        throw new RestoreFailedException(RestoreFailure.PreRestoreBackupFailed,
                            "Could not take a verified backup of the current data; the restore was not started.", previousDatabaseRestored: true, ex);
                    }

                    Step(RestoreStep.PreRestoreBackupTaken);
                    CheckpointAndClose(live);
                }
            }

            Step(RestoreStep.LiveCheckpointed);

            // 3. Journal BEFORE the first move.
            var journal = new SwapJournal
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Phase = SwapPhase.Swapping,
                OldExisted = oldExisted,
                OldSha256 = oldExisted ? DatabaseChecks.FileSha256(live) : null,
                CandidateSha256 = manifest.Sha256.ToLowerInvariant(),
                KeepOldAsDamaged = damaged,
                PreRestoreBackup = preRestoreBackup,
                CreatedAtUtc = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            };
            SwapJournal.Write(_paths, journal, () => Step(RestoreStep.JournalTempWritten));
            Step(RestoreStep.JournalWritten);

            // 4. The swap itself. From here on every failure is undone from the journal.
            try
            {
                SwapEngine.MoveSet(live, _paths.AsidePath, Step, RestoreStep.LiveDatabaseMovedAside, RestoreStep.SideFilesMovedAside);
                File.Move(_paths.CandidatePath, live);
                Step(RestoreStep.CandidateMovedIntoPlace);

                ConnectionFactory.ReleaseFileHandles();
                try
                {
                    (_options.PostSwapValidator ?? DefaultValidator)(live);
                }
                finally
                {
                    ConnectionFactory.ReleaseFileHandles();     // whatever the validator left pooled must not block the files from moving (Windows)
                }

                Step(RestoreStep.PostSwapValidated);

                SwapJournal.Write(_paths, journal with { Phase = SwapPhase.Committed });
                Step(RestoreStep.JournalCommitted);
            }
            catch (Exception ex) when (ex is not SimulatedCrashException)
            {
                var restored = SwapEngine.TryRollback(_paths, journal, Step);
                throw new RestoreFailedException(RestoreFailure.SwapFailed,
                    restored ? "The restore failed and the previous database was put back: " + ex.Message
                             : "The restore failed; the previous database will be put back at the next start: " + ex.Message,
                    restored, ex);
            }

            // 5. Committed: the new database is final. Cleanup problems never undo it; the next start finishes them.
            var cleanupPending = !SwapEngine.TryFinishCommit(_paths, journal, Step);
            var kept = journal.KeepOldAsDamaged ? _paths.DamagedFolder(journal.Id) : null;
            return new RestoreResult(manifest, preRestoreBackup, kept, cleanupPending);
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            // Before the journal: nothing of the live database was touched. Remove OUR staging file only.
            if (!File.Exists(_paths.JournalPath))
            {
                BackupVerifier.DeleteScratch(_paths.CandidatePath);
            }

            throw;
        }
    }

    /// <summary>
    /// Start-up step 4 (04 section 4.3): settles a swap that was interrupted. Call it BEFORE any connection to the database is opened.
    /// </summary>
    public static SwapRecoveryResult RecoverPendingSwap(RestorePaths paths) => SwapEngine.Recover(paths, null);

    /// <summary>Same, with the step hook (crash tests exercise a second crash during recovery).</summary>
    public static SwapRecoveryResult RecoverPendingSwap(RestorePaths paths, Action<RestoreStep>? afterStep) => SwapEngine.Recover(paths, afterStep);

    private void Step(RestoreStep step) => _options.AfterStep?.Invoke(step);

    private void DefaultValidator(string live)
    {
        using var connection = new ConnectionFactory(live).OpenUnpooled(OpenMode.ReadWrite);
        var problems = DatabaseChecks.QuickCheck(connection);
        if (problems.Count > 0)
        {
            throw new InvalidDataException("quick_check failed on the restored database: " + string.Join("; ", problems.Take(3)));
        }
    }

    private void DeleteOwnLeftovers()
    {
        // No journal here (RecoverPendingSwap settled it), so these are leftovers of an attempt that died before the journal. They are
        // copies of a verified backup, or the temp file of a journal write.
        BackupVerifier.DeleteScratch(_paths.CandidatePath);
        FileOps.DeleteIfExists(_paths.JournalTempPath);
    }

    private static bool IsHealthy(string live)
    {
        try
        {
            using var connection = new ConnectionFactory(live).OpenForInspection(OpenMode.ReadWrite);
            return DatabaseChecks.QuickCheck(connection, 1).Count == 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            // Locked is not damaged: never treat a database in use as one that may be replaced without a backup.
            throw new RestoreFailedException(RestoreFailure.DatabaseBusy, "The database is in use by another connection; close it and try again.", previousDatabaseRestored: true, ex);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void CheckpointAndClose(string live)
    {
        try
        {
            using var connection = new ConnectionFactory(live).OpenForInspection(OpenMode.ReadWrite);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && reader.GetInt64(0) != 0)
            {
                throw new RestoreFailedException(RestoreFailure.DatabaseBusy, "The database is in use by another connection; close it and try again.", previousDatabaseRestored: true);
            }
        }
        catch (SqliteException ex)
        {
            throw new RestoreFailedException(RestoreFailure.DatabaseBusy, "The database could not be checkpointed: " + ex.Message, previousDatabaseRestored: true, ex);
        }
    }
}

public enum SwapRecoveryOutcome
{
    /// <summary>No journal: nothing was pending.</summary>
    NothingPending,

    /// <summary>The interrupted restore had not been committed; the old database is back.</summary>
    RolledBack,

    /// <summary>The restore had been committed; the leftovers of the old database were removed.</summary>
    CompletedForward,

    /// <summary>The journal exists but cannot be read, or the files do not match it. NOTHING was changed; the recovery screen must take over.</summary>
    Unresolved,
}

public sealed record SwapRecoveryResult(SwapRecoveryOutcome Outcome, string? Detail)
{
    /// <summary>True when the database may be opened.</summary>
    public bool IsSettled => Outcome != SwapRecoveryOutcome.Unresolved;
}

/// <summary>The journaled file operations, shared by <see cref="RestoreService"/> and start-up recovery. Idempotent: each can be re-run after a crash at any point.</summary>
internal static class SwapEngine
{
    public static SwapRecoveryResult Recover(RestorePaths paths, Action<RestoreStep>? afterStep)
    {
        SwapJournal? journal;
        try
        {
            journal = SwapJournal.Read(paths);
        }
        catch (InvalidDataException ex)
        {
            return new SwapRecoveryResult(SwapRecoveryOutcome.Unresolved, "swap.json is unreadable: " + ex.Message);
        }

        if (journal is null)
        {
            FileOps.DeleteIfExists(paths.JournalTempPath);    // a crash between "temp written" and "renamed": the swap never began
            return new SwapRecoveryResult(SwapRecoveryOutcome.NothingPending, null);
        }

        void Step(RestoreStep s) => afterStep?.Invoke(s);

        if (journal.Phase == SwapPhase.Committed)
        {
            if (!File.Exists(paths.DatabasePath))
            {
                return new SwapRecoveryResult(SwapRecoveryOutcome.Unresolved, "The restore was committed but the restored database is missing; the old files were left untouched.");
            }

            var finished = TryFinishCommit(paths, journal, Step);
            return new SwapRecoveryResult(SwapRecoveryOutcome.CompletedForward, finished ? null : "Some leftovers of the old database could not be removed yet; they are retried at the next start.");
        }

        try
        {
            Rollback(paths, journal, Step);
            return new SwapRecoveryResult(SwapRecoveryOutcome.RolledBack, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new SwapRecoveryResult(SwapRecoveryOutcome.Unresolved, ex.Message);
        }
    }

    /// <summary>Moves <c>live</c> and its side files to <c>target</c> (+ suffix). The main file first. Nothing is overwritten.</summary>
    public static void MoveSet(string live, string target, Action<RestoreStep> step, RestoreStep afterMain, RestoreStep afterSideFiles)
    {
        MoveOne(live, target, step);
        step(afterMain);
        foreach (var suffix in RestorePaths.FileSetSuffixes.Skip(1))
        {
            MoveOne(live + suffix, target + suffix, step);
        }

        step(afterSideFiles);
    }

    /// <summary>Renames a file when it exists. Refuses to overwrite: a clash means state this code did not expect.</summary>
    private static void MoveOne(string from, string to, Action<RestoreStep> step)
    {
        if (!File.Exists(from))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: false);
                step(RestoreStep.FileMoved);
                return;
            }
            catch (IOException) when (attempt < 3 && File.Exists(from) && !File.Exists(to))
            {
                Thread.Sleep(250);     // an anti-virus scanner may hold the file for a moment (04 section 5.4)
            }
        }
    }

    /// <summary>
    /// Undoes a swap that is not committed. Every decision is made from the CONTENT of the live main file, never from which directory happens
    /// to hold some file, so it can be re-run after a crash between any two renames (including renames of its own).
    /// Rules:
    /// <list type="bullet">
    /// <item>A live main file whose SHA-256 equals <c>OldSha256</c> IS the old database and is never moved anywhere.</item>
    /// <item>Any other live main file is the candidate (or what validation made of it) and goes to the rejected area, side files first and the main
    ///   file LAST. So "live main absent" means the candidate set is already out (or was never in), and any side file still in the live slot then
    ///   belongs to the old database (forward swap: main moves first, side files after).</item>
    /// <item>The old set goes home main file first; side files still aside come home when the old main is home.</item>
    /// <item>Nothing is deleted until the old main file is verified in place, and the old main file is never deleted from the rejected area.</item>
    /// </list>
    /// </summary>
    public static void Rollback(RestorePaths paths, SwapJournal journal, Action<RestoreStep> step)
    {
        ConnectionFactory.ReleaseFileHandles();
        var live = paths.DatabasePath;
        var sideSuffixes = RestorePaths.FileSetSuffixes.Skip(1).ToArray();

        if (journal.OldExisted)
        {
            var liveMain = File.Exists(live);
            var liveIsOld = liveMain && IsOldMain(live, journal);
            if (!liveIsOld)
            {
                if (!File.Exists(paths.AsidePath))
                {
                    throw new InvalidDataException("The previous database recorded in the swap journal is in neither its place nor the staging area; nothing was deleted.");
                }

                if (liveMain)
                {
                    MoveSetOut(paths, journal, step);
                }
                else
                {
                    // Candidate not in place: side files left in the live slot are the old database's own.
                    foreach (var suffix in sideSuffixes)
                    {
                        if (File.Exists(live + suffix))
                        {
                            if (File.Exists(paths.AsidePath + suffix))
                            {
                                throw new InvalidDataException("Both the live slot and the staging area hold a '" + suffix + "' file of the old database; nothing was deleted.");
                            }

                            MoveOne(live + suffix, paths.AsidePath + suffix, step);
                        }
                    }
                }

                step(RestoreStep.RollbackRejectedMovedOut);
                PutOldSetHome(paths, step);
            }
            else
            {
                if (File.Exists(paths.AsidePath))
                {
                    throw new InvalidDataException("The old database is in place and another copy of its main file is set aside; nothing was deleted.");
                }

                PutOldSetHome(paths, step);
            }

            step(RestoreStep.RollbackOldRestored);

            if (!File.Exists(live) || !IsOldMain(live, journal))
            {
                throw new InvalidDataException("The database in place is not the one recorded in the swap journal; nothing was deleted.");
            }
        }
        else
        {
            // There was no database before: whatever sits in the live slot came from the candidate.
            MoveSetOut(paths, journal, step);
            step(RestoreStep.RollbackRejectedMovedOut);
        }

        foreach (var suffix in RestorePaths.FileSetSuffixes)
        {
            if (suffix.Length == 0 && journal.OldExisted && File.Exists(paths.RejectedPath) && IsOldMain(paths.RejectedPath, journal))
            {
                throw new InvalidDataException("A copy of the old database is in the rejected area; it was not deleted.");
            }

            BestEffortDelete(paths.RejectedPath + suffix);
        }

        BestEffortDelete(paths.CandidatePath);
        FileOps.DeleteIfExists(paths.JournalPath);
        FileOps.DeleteIfExists(paths.JournalTempPath);
    }

    private static bool IsOldMain(string path, SwapJournal journal) =>
        string.Equals(DatabaseChecks.FileSha256(path), journal.OldSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves the live slot's files (the candidate) to the rejected area: side files first, main file last.</summary>
    private static void MoveSetOut(RestorePaths paths, SwapJournal journal, Action<RestoreStep> step)
    {
        foreach (var suffix in RestorePaths.FileSetSuffixes.Reverse())
        {
            var from = paths.DatabasePath + suffix;
            if (!File.Exists(from))
            {
                continue;
            }

            var target = paths.RejectedPath + suffix;
            if (File.Exists(target))
            {
                if (journal.OldExisted && IsOldMain(target, journal) && suffix.Length == 0)
                {
                    throw new InvalidDataException("A copy of the old database is in the rejected area; nothing was deleted.");
                }

                FileOps.DeleteIfExists(target);    // an earlier rollback attempt's copy of the candidate: derived from a backup, not user data
            }

            MoveOne(from, target, step);
        }
    }

    /// <summary>Old set home: main file first, then whichever side files are still aside. Refuses to overwrite.</summary>
    private static void PutOldSetHome(RestorePaths paths, Action<RestoreStep> step)
    {
        foreach (var suffix in RestorePaths.FileSetSuffixes)
        {
            if (File.Exists(paths.AsidePath + suffix) && File.Exists(paths.DatabasePath + suffix))
            {
                throw new InvalidDataException("A '" + suffix + "' file exists both in the live slot and in the staging area; nothing was deleted.");
            }

            MoveOne(paths.AsidePath + suffix, paths.DatabasePath + suffix, step);
        }
    }

    public static bool TryRollback(RestorePaths paths, SwapJournal journal, Action<RestoreStep> step)
    {
        try
        {
            Rollback(paths, journal, step);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;     // the journal stays; RecoverPendingSwap retries at the next start
        }
    }

    /// <summary>
    /// The restore is committed: remove the old database (or keep it, if it was damaged), then the journal. Returns false when something
    /// could not be removed; the journal then stays and the next start repeats this.
    /// </summary>
    public static bool TryFinishCommit(RestorePaths paths, SwapJournal journal, Action<RestoreStep> step)
    {
        try
        {
            var live = paths.DatabasePath;
            if (!File.Exists(live))
            {
                return false;      // committed but the new database is not there: never delete the old one then
            }

            var anyAside = RestorePaths.FileSetSuffixes.Any(s => File.Exists(paths.AsidePath + s));
            if (anyAside)
            {
                // A normal (healthy) old database is redundant ONLY while its verified pre-restore backup still exists.
                var protectedByBackup = !journal.KeepOldAsDamaged && journal.PreRestoreBackup is not null && File.Exists(journal.PreRestoreBackup);
                if (protectedByBackup || !journal.OldExisted)
                {
                    foreach (var suffix in RestorePaths.FileSetSuffixes)
                    {
                        FileOps.DeleteIfExists(paths.AsidePath + suffix);
                    }
                }
                else
                {
                    var folder = paths.DamagedFolder(journal.Id);
                    Directory.CreateDirectory(folder);
                    foreach (var suffix in RestorePaths.FileSetSuffixes)
                    {
                        MoveOne(paths.AsidePath + suffix, Path.Combine(folder, "shabana.db" + suffix), step);
                    }
                }
            }

            step(RestoreStep.AsideRemoved);
            FileOps.DeleteIfExists(paths.JournalPath);
            FileOps.DeleteIfExists(paths.JournalTempPath);
            step(RestoreStep.JournalRemoved);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void BestEffortDelete(string path)
    {
        try
        {
            FileOps.DeleteIfExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover copy of the candidate: harmless, deleted by the next restore.
        }
    }
}

/// <summary>Small file helpers that tolerate a missing directory (File.Delete throws DirectoryNotFoundException for one).</summary>
internal static class FileOps
{
    public static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
