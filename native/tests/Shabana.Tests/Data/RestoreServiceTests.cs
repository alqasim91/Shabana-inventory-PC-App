using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Shabana.Data.Backup;
using Shabana.Data.Maintenance;
using Shabana.Data.Migrations;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>P2 acceptance (3b): restore round trip equals the original; every failure leaves the previous database exactly as it was.</summary>
public class RestoreServiceTests(PopulatedV1Fixture fixture) : IClassFixture<PopulatedV1Fixture>
{
    private sealed class FailingBackup : IBackupService
    {
        public BackupResult Create(BackupRequest request) => throw new BackupFailedException(BackupFailure.DiskSpace, "disk full");
    }

    private static void AssertOnlyTheDatabaseIsLeft(RestoreScenario s)
    {
        s.DbFolderFiles().ShouldBe(["shabana.db"], "no WAL/SHM of the old database, no staging file, no 'previous' copy");
        File.Exists(s.Paths.JournalPath).ShouldBeFalse();
        File.Exists(s.Paths.JournalTempPath).ShouldBeFalse();
    }

    [Fact]
    public void Restore_replaces_the_database_with_the_backup_and_the_round_trip_equals_the_original()
    {
        using var s = RestoreScenario.Create(fixture);
        s.OldFp.ShouldNotBe(s.NewFp);

        var result = s.Service().Restore(new RestoreRequest(s.BackupFile));

        // The live database IS the backed-up one: same schema, same every row, same version.
        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        result.Restored.SchemaVersion.ShouldBe(1);
        result.CleanupPending.ShouldBeFalse();
        result.DamagedCopyKeptAt.ShouldBeNull();
        AssertOnlyTheDatabaseIsLeft(s);

        // What it replaced was backed up first, verified, and equals the pre-restore state: nothing was lost.
        result.PreRestoreBackup.ShouldNotBeNull();
        result.PreRestoreBackup.ShouldContain(Path.Combine("Backups", "pre-restore"));
        BackupVerifier.Verify(result.PreRestoreBackup).Ok.ShouldBeTrue();
        s.PreRestoreBackupFingerprint().ShouldBe(s.OldFp);

        // The application can use it straight away.
        var db = TestDatabase.Open(s.Live);
        using (var c = db.Factory.Open())
        {
            IntegrityChecker.Full(c).IsClean.ShouldBeTrue();
            SqlScript.Scalar<string>(c, "PRAGMA journal_mode").ShouldBe("wal");
        }

        db.Scalar<long>("SELECT COUNT(*) FROM sites WHERE id = 'after-the-backup'").ShouldBe(0);
    }

    [Fact]
    public void Restoring_the_pre_restore_backup_undoes_a_restore()
    {
        using var s = RestoreScenario.Create(fixture);
        var first = s.Service().Restore(new RestoreRequest(s.BackupFile));
        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);

        s.Service().Restore(new RestoreRequest(first.PreRestoreBackup!));
        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.OldFp);
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public void A_lost_database_is_restored_without_a_pre_restore_backup()
    {
        using var s = RestoreScenario.Create(fixture, liveExists: false);
        File.Exists(s.Live).ShouldBeFalse();

        var result = s.Service().Restore(new RestoreRequest(s.BackupFile));

        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        result.PreRestoreBackup.ShouldBeNull();
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public async Task A_live_database_with_a_pending_wal_is_backed_up_whole_and_leaves_no_stale_side_files()
    {
        using var s = RestoreScenario.Create(fixture);
        // Make the live database look like after a crash: committed rows that exist only in the WAL, no connection open.
        var crashed = s.Dir.File("crash-image");
        Directory.CreateDirectory(crashed);
        var db = TestDatabase.Open(s.Live);
        var keeper = db.Factory.OpenUnpooled();
        await db.Uow.RunAsync(LedgerStress.Actor, tx =>
        {
            tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('only-in-wal', 'x')");
            return Task.CompletedTask;
        });
        TestFiles.CopyShared(s.Live, Path.Combine(crashed, "shabana.db"));
        TestFiles.CopyShared(s.Live + "-wal", Path.Combine(crashed, "shabana.db-wal"));
        keeper.Dispose();
        ConnectionFactory.ReleaseFileHandles();
        RestoreScenario.DeleteSet(s.Live);
        File.Move(Path.Combine(crashed, "shabana.db"), s.Live);
        File.Move(Path.Combine(crashed, "shabana.db-wal"), s.Live + "-wal");
        File.Exists(s.Live + "-wal").ShouldBeTrue();

        var s2 = new RestorePaths(s.Live, s.Paths.RestoreFolder, s.Paths.BackupRoot);
        var result = new RestoreService(s2, new BackupService(), "t", 1).Restore(new RestoreRequest(s.BackupFile));

        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        AssertOnlyTheDatabaseIsLeft(s);     // in particular: no shabana.db-wal / -shm that would corrupt the restored file
        using var extracted = new ConnectionFactory(ExtractPreRestore(s, result)).OpenForInspection();
        SqlScript.Scalar<string>(extracted, "SELECT COUNT(*) FROM sites WHERE id = 'only-in-wal'").ShouldBe("1");   // the WAL-only row was saved
    }

    private static string ExtractPreRestore(RestoreScenario s, RestoreResult result)
    {
        var to = s.Dir.File("pre.db");
        using var zip = ZipFile.OpenRead(result.PreRestoreBackup!);
        zip.GetEntry("shabana.db")!.ExtractToFile(to);
        return to;
    }

    [Fact]
    public void A_corrupt_backup_is_refused_and_the_live_database_is_untouched()
    {
        using var s = RestoreScenario.Create(fixture);
        var bytes = File.ReadAllBytes(s.BackupFile);
        bytes[bytes.Length / 2] ^= 0xFF;
        var bad = s.Dir.File("bad.shbk");
        File.WriteAllBytes(bad, bytes);

        var ex = Should.Throw<RestoreFailedException>(() => s.Service().Restore(new RestoreRequest(bad)));

        ex.Failure.ShouldBe(RestoreFailure.InvalidBackup);
        ex.PreviousDatabaseRestored.ShouldBeTrue();
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
        AssertOnlyTheDatabaseIsLeft(s);
        Directory.Exists(Path.Combine(s.Paths.BackupRoot, "pre-restore")).ShouldBeFalse();    // not even a pre-restore backup was needed
    }

    [Fact]
    public void A_backup_from_a_newer_version_of_the_application_is_refused()
    {
        using var s = RestoreScenario.Create(fixture);
        // Same backup, but the application only understands "version 0" -- i.e. the backup is newer than the application.
        var ex = Should.Throw<RestoreFailedException>(() => s.Service(schemaVersion: 0).Restore(new RestoreRequest(s.BackupFile)));
        ex.Failure.ShouldBe(RestoreFailure.NewerThanApp);
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public void If_the_current_data_cannot_be_backed_up_first_nothing_is_replaced()
    {
        using var s = RestoreScenario.Create(fixture);
        var ex = Should.Throw<RestoreFailedException>(() => s.Service(backups: new FailingBackup()).Restore(new RestoreRequest(s.BackupFile)));
        ex.Failure.ShouldBe(RestoreFailure.PreRestoreBackupFailed);
        ex.PreviousDatabaseRestored.ShouldBeTrue();
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public void A_restored_database_that_fails_validation_is_swapped_back_automatically()
    {
        using var s = RestoreScenario.Create(fixture);
        var options = new RestoreOptions { PostSwapValidator = _ => throw new InvalidOperationException("migration of the restored database failed") };

        var ex = Should.Throw<RestoreFailedException>(() => s.Service(options).Restore(new RestoreRequest(s.BackupFile)));

        ex.Failure.ShouldBe(RestoreFailure.SwapFailed);
        ex.PreviousDatabaseRestored.ShouldBeTrue();
        ex.Message.ShouldContain("migration of the restored database failed");
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);       // byte for byte the database that was there
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public async Task A_database_in_use_is_not_replaced()
    {
        using var s = RestoreScenario.Create(fixture);
        var db = TestDatabase.Open(s.Live);
        using var reader = db.Factory.Open();
        using var snapshot = reader.BeginTransaction(deferred: true);
        SqlScript.Scalar<long>(reader, "SELECT COUNT(*) FROM sites");                 // a reader pins the old WAL snapshot
        await db.Uow.RunAsync(LedgerStress.Actor, tx => { tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('while-open', 'x')"); return Task.CompletedTask; });
        var openFp = RestoreScenario.Fingerprint(s.Live);

        var ex = Should.Throw<RestoreFailedException>(() => s.Service().Restore(new RestoreRequest(s.BackupFile)));

        ex.Failure.ShouldBe(RestoreFailure.DatabaseBusy);
        ex.PreviousDatabaseRestored.ShouldBeTrue();
        File.Exists(s.Paths.JournalPath).ShouldBeFalse();
        File.Exists(s.Paths.CandidatePath).ShouldBeFalse();
        snapshot.Rollback();
        reader.Close();
        ConnectionFactory.ReleaseFileHandles();
        RestoreScenario.Fingerprint(s.Live).ShouldBe(openFp);       // logically unchanged
    }

    [Fact]
    public void A_damaged_live_database_is_replaced_but_kept_for_salvage_never_deleted()
    {
        using var s = RestoreScenario.Create(fixture);
        var damaged = File.ReadAllBytes(s.Live);
        for (var i = damaged.Length / 4; i < (damaged.Length / 4) + 12288; i++)
        {
            damaged[i] = 0xAB;
        }

        File.WriteAllBytes(s.Live, damaged);
        var damagedSha = DatabaseChecks.FileSha256(s.Live);

        var result = s.Service().Restore(new RestoreRequest(s.BackupFile));

        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        result.PreRestoreBackup.ShouldBeNull();                       // a damaged database cannot be backed up and verified
        result.DamagedCopyKeptAt.ShouldNotBeNull();
        DatabaseChecks.FileSha256(Path.Combine(result.DamagedCopyKeptAt, "shabana.db")).ShouldBe(damagedSha);   // byte for byte
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public void A_backup_of_an_older_schema_is_migrated_as_part_of_the_restore()
    {
        using var s = RestoreScenario.Create(fixture);
        var chain = TestMigrations.V1To2();
        MigrationRunner Runner() => new(s.Live, chain, new BackupService(), s.Paths.BackupRoot, "t");

        // The live database has already been upgraded to v2 (and changed since); the backup is v1.
        Runner().Run().ToVersion.ShouldBe(2);
        var options = new RestoreOptions
        {
            PostSwapValidator = live =>
            {
                Runner().Run();
                IntegrityChecker.QuickCheck(live).ShouldBeEmpty();
            },
        };

        s.Service(options, schemaVersion: 2).Restore(new RestoreRequest(s.BackupFile));

        using (var c = new ConnectionFactory(s.Live).OpenForInspection())
        {
            DatabaseChecks.UserVersion(c).ShouldBe(2);                    // the restored v1 data, upgraded
            SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM feature_flags").ShouldBe("1");
            SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sites WHERE id = 'after-the-backup'").ShouldBe("0");
        }

        Directory.GetFiles(Path.Combine(s.Paths.BackupRoot, "pre-migration"), "pre-v1-to-v2-*.shbk").Length.ShouldBeGreaterThan(0);
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public void Preview_reports_what_the_backup_holds_and_what_the_current_database_has_without_changing_anything()
    {
        using var s = RestoreScenario.Create(fixture);
        var preview = s.Service().Preview(s.BackupFile);
        preview.Candidate.Ok.ShouldBeTrue();
        preview.Candidate.Manifest!.RowCounts["sites"].ShouldBe(preview.CurrentRowCounts!["sites"] - 1);   // the live one has the site added later
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
        AssertOnlyTheDatabaseIsLeft(s);

        File.WriteAllText(s.Dir.File("junk.shbk"), "x");
        s.Service().Preview(s.Dir.File("junk.shbk")).Candidate.Ok.ShouldBeFalse();
    }

    [Fact]
    public void Files_of_an_earlier_swap_are_never_overwritten_by_a_new_restore()
    {
        using var s = RestoreScenario.Create(fixture);
        File.WriteAllText(s.Paths.AsidePath, "somebody's previous database");      // no journal: the journal was lost
        var before = DatabaseChecks.FileSha256(s.Paths.AsidePath);

        var ex = Should.Throw<RestoreFailedException>(() => s.Service().Restore(new RestoreRequest(s.BackupFile)));

        ex.Failure.ShouldBe(RestoreFailure.PendingSwapUnresolved);
        DatabaseChecks.FileSha256(s.Paths.AsidePath).ShouldBe(before);
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
    }

    [Fact]
    public void A_backup_that_does_not_hold_a_shabana_database_is_refused_so_it_cannot_wipe_the_shop()
    {
        using var s = RestoreScenario.Create(fixture);
        // A perfectly valid .shbk of a BLANK database (no tables, schema version 0).
        var blank = s.Dir.File("blank", "shabana.db");
        Directory.CreateDirectory(Path.GetDirectoryName(blank)!);
        using (var c = new ConnectionFactory(blank).Open())
        {
            SqlScript.Execute(c, "CREATE TABLE t (a INTEGER); PRAGMA user_version = 3");
        }

        var emptyish = new BackupService().Create(new BackupRequest(blank, s.Dir.File("blank-backups"), BackupReason.Manual, "t")).FilePath;
        BackupVerifier.Verify(emptyish).Ok.ShouldBeTrue();

        var ex = Should.Throw<RestoreFailedException>(() => s.Service(schemaVersion: 5).Restore(new RestoreRequest(emptyish)));
        ex.Failure.ShouldBe(RestoreFailure.InvalidBackup);
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
        AssertOnlyTheDatabaseIsLeft(s);
    }

    [Fact]
    public void Leftovers_of_an_attempt_that_died_before_its_journal_are_cleaned_by_the_next_restore()
    {
        using var s = RestoreScenario.Create(fixture);
        File.WriteAllText(s.Paths.CandidatePath, "half-written staging file");
        Directory.CreateDirectory(s.Paths.RestoreFolder);
        File.WriteAllText(s.Paths.JournalTempPath, "{ torn");

        s.Service().Restore(new RestoreRequest(s.BackupFile));

        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        AssertOnlyTheDatabaseIsLeft(s);
    }
}
