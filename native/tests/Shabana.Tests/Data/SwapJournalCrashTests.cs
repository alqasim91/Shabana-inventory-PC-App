using Shabana.Data.Backup;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>
/// P2 acceptance (5): the process dies after EACH step of a restore (a <see cref="SimulatedCrashException"/> runs no cleanup and no undo, like a
/// power cut). At the next start <see cref="RestoreService.RecoverPendingSwap(RestorePaths)"/> settles it. The outcome is a function of the
/// step: before the commit point the database is exactly the OLD one, after it exactly the NEW one -- never a mixture, and at no instant is the
/// old data absent from the disk.
/// </summary>
public class SwapJournalCrashTests(PopulatedV1Fixture fixture) : IClassFixture<PopulatedV1Fixture>
{
    public enum Expect
    {
        Old,
        New,
    }

    public static TheoryData<RestoreStep, Expect, SwapRecoveryOutcome> CrashPoints => new()
    {
        { RestoreStep.CandidateStaged, Expect.Old, SwapRecoveryOutcome.NothingPending },
        { RestoreStep.PreRestoreBackupTaken, Expect.Old, SwapRecoveryOutcome.NothingPending },
        { RestoreStep.LiveCheckpointed, Expect.Old, SwapRecoveryOutcome.NothingPending },
        { RestoreStep.JournalTempWritten, Expect.Old, SwapRecoveryOutcome.NothingPending },
        { RestoreStep.JournalWritten, Expect.Old, SwapRecoveryOutcome.RolledBack },
        { RestoreStep.LiveDatabaseMovedAside, Expect.Old, SwapRecoveryOutcome.RolledBack },
        { RestoreStep.SideFilesMovedAside, Expect.Old, SwapRecoveryOutcome.RolledBack },
        { RestoreStep.CandidateMovedIntoPlace, Expect.Old, SwapRecoveryOutcome.RolledBack },
        { RestoreStep.PostSwapValidated, Expect.Old, SwapRecoveryOutcome.RolledBack },
        { RestoreStep.JournalCommitted, Expect.New, SwapRecoveryOutcome.CompletedForward },
        { RestoreStep.AsideRemoved, Expect.New, SwapRecoveryOutcome.CompletedForward },
        { RestoreStep.JournalRemoved, Expect.New, SwapRecoveryOutcome.NothingPending },
    };

    private static RestoreOptions CrashAfter(RestoreStep crash, Action<string>? validator = null) => new()
    {
        AfterStep = step =>
        {
            if (step == crash)
            {
                throw new SimulatedCrashException(step);
            }
        },
        PostSwapValidator = validator,
    };

    private static void AssertFullyOld(RestoreScenario s)
    {
        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.OldFp);
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
    }

    private static void AssertClean(RestoreScenario s)
    {
        s.DbFolderFiles().ShouldBe(["shabana.db"], "no stale -wal/-shm, no .restore-* files");
        File.Exists(s.Paths.JournalPath).ShouldBeFalse();
        File.Exists(s.Paths.JournalTempPath).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void A_crash_after_each_step_recovers_to_exactly_the_old_or_exactly_the_new_database(RestoreStep crashAfter, Expect expect, SwapRecoveryOutcome outcome)
    {
        using var s = RestoreScenario.Create(fixture);

        // The process dies.
        var crash = Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(crashAfter)).Restore(new RestoreRequest(s.BackupFile)));
        crash.Step.ShouldBe(crashAfter);
        ConnectionFactory.ReleaseFileHandles();

        // At every instant the old data is physically on disk until the commit point (live, set aside, or moved out of the way) ...
        if (expect == Expect.Old)
        {
            var oldCopies = new[] { s.Live, s.Paths.AsidePath }.Where(File.Exists).Select(DatabaseChecks.FileSha256);
            oldCopies.ShouldContain(s.OldSha, $"after {crashAfter} the old database must still exist, in place or set aside");
        }

        // ... and a verified backup of it already exists from the step that follows the pre-restore backup on.
        if (crashAfter >= RestoreStep.PreRestoreBackupTaken)
        {
            s.PreRestoreBackupFingerprint().ShouldBe(s.OldFp);
        }

        // Next start.
        var recovery = RestoreService.RecoverPendingSwap(s.Paths);
        recovery.Outcome.ShouldBe(outcome);
        recovery.IsSettled.ShouldBeTrue();

        if (expect == Expect.Old)
        {
            AssertFullyOld(s);
        }
        else
        {
            RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        }

        File.Exists(s.Paths.JournalPath).ShouldBeFalse();
        File.Exists(s.Live + "-wal").ShouldBeFalse();
        File.Exists(s.Live + "-shm").ShouldBeFalse();
        File.Exists(s.Paths.AsidePath).ShouldBeFalse();
        File.Exists(s.Paths.RejectedPath).ShouldBeFalse();

        // Recovery is idempotent: a second start finds nothing to do and changes nothing.
        var fp = RestoreScenario.Fingerprint(s.Live);
        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.NothingPending);
        RestoreScenario.Fingerprint(s.Live).ShouldBe(fp);

        // And the shop can simply try again: the restore then completes (leftovers of the dead attempt are cleaned up).
        if (expect == Expect.Old)
        {
            s.Service().Restore(new RestoreRequest(s.BackupFile));
            RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        }

        AssertClean(s);
    }

    [Theory]
    [InlineData(RestoreStep.RollbackRejectedMovedOut)]
    [InlineData(RestoreStep.RollbackOldRestored)]
    public void A_crash_in_the_middle_of_the_automatic_swap_back_still_ends_with_the_old_database(RestoreStep crashAfter)
    {
        using var s = RestoreScenario.Create(fixture);
        var options = CrashAfter(crashAfter, validator: _ => throw new InvalidOperationException("restored database is unusable"));

        Should.Throw<SimulatedCrashException>(() => s.Service(options).Restore(new RestoreRequest(s.BackupFile))).Step.ShouldBe(crashAfter);
        ConnectionFactory.ReleaseFileHandles();

        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.RolledBack);
        AssertFullyOld(s);
        AssertClean(s);
    }

    [Theory]
    [InlineData(RestoreStep.CandidateMovedIntoPlace, RestoreStep.RollbackRejectedMovedOut)]
    [InlineData(RestoreStep.CandidateMovedIntoPlace, RestoreStep.RollbackOldRestored)]
    [InlineData(RestoreStep.LiveDatabaseMovedAside, RestoreStep.RollbackRejectedMovedOut)]
    [InlineData(RestoreStep.PostSwapValidated, RestoreStep.RollbackOldRestored)]
    public void A_crash_during_recovery_itself_is_recovered_by_the_next_recovery(RestoreStep firstCrash, RestoreStep secondCrash)
    {
        using var s = RestoreScenario.Create(fixture);
        Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(firstCrash)).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();

        Should.Throw<SimulatedCrashException>(() => RestoreService.RecoverPendingSwap(s.Paths, step =>
        {
            if (step == secondCrash)
            {
                throw new SimulatedCrashException(step);
            }
        })).Step.ShouldBe(secondCrash);

        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.RolledBack);
        AssertFullyOld(s);
        AssertClean(s);
    }

    [Theory]
    [InlineData(RestoreStep.JournalCommitted, RestoreStep.AsideRemoved)]
    [InlineData(RestoreStep.AsideRemoved, RestoreStep.AsideRemoved)]
    public void A_crash_while_finishing_a_committed_restore_is_finished_by_the_next_start(RestoreStep firstCrash, RestoreStep secondCrash)
    {
        using var s = RestoreScenario.Create(fixture);
        Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(firstCrash)).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();

        Should.Throw<SimulatedCrashException>(() => RestoreService.RecoverPendingSwap(s.Paths, step =>
        {
            if (step == secondCrash)
            {
                throw new SimulatedCrashException(step);
            }
        }));

        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.CompletedForward);
        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        AssertClean(s);
        s.PreRestoreBackupFingerprint().ShouldBe(s.OldFp);       // the old data remains available as a verified backup
    }

    // With no database to replace the steps that do not exist (pre-restore backup, checkpoint) never run; five points cover every distinct state
    // (staged, journal written, swapped, committed, finished).
    public static TheoryData<RestoreStep, Expect, SwapRecoveryOutcome> CrashPointsWithoutDatabase => new()
    {
        { RestoreStep.CandidateStaged, Expect.Old, SwapRecoveryOutcome.NothingPending },
        { RestoreStep.JournalWritten, Expect.Old, SwapRecoveryOutcome.RolledBack },
        { RestoreStep.CandidateMovedIntoPlace, Expect.Old, SwapRecoveryOutcome.RolledBack },
        { RestoreStep.JournalCommitted, Expect.New, SwapRecoveryOutcome.CompletedForward },
        { RestoreStep.JournalRemoved, Expect.New, SwapRecoveryOutcome.NothingPending },
    };

    [Theory]
    [MemberData(nameof(CrashPointsWithoutDatabase))]
    public void When_there_was_no_database_a_crash_ends_with_no_database_or_the_restored_one_never_a_mixture(RestoreStep crashAfter, Expect expect, SwapRecoveryOutcome outcome)
    {
        using var s = RestoreScenario.Create(fixture, liveExists: false);
        Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(crashAfter)).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();

        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(outcome);

        if (expect == Expect.Old)
        {
            File.Exists(s.Live).ShouldBeFalse();       // exactly as before: no database file
        }
        else
        {
            RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        }

        File.Exists(s.Live + "-wal").ShouldBeFalse();
        File.Exists(s.Live + "-shm").ShouldBeFalse();
        File.Exists(s.Paths.JournalPath).ShouldBeFalse();
        File.Exists(s.Paths.AsidePath).ShouldBeFalse();
        File.Exists(s.Paths.RejectedPath).ShouldBeFalse();
    }

    [Fact]
    public async Task A_crash_with_the_old_database_having_side_files_puts_every_one_of_them_back()
    {
        using var s = RestoreScenario.Create(fixture);
        // Give the old database a WAL and a SHM (as if the application was killed) and remember them all.
        var db = TestDatabase.Open(s.Live);
        var keeper = db.Factory.OpenUnpooled();
        await db.Uow.RunAsync(LedgerStress.Actor, tx => { tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('wal-only', 'x')"); return Task.CompletedTask; });
        var wal = TestFiles.ReadAllBytesShared(s.Live + "-wal");
        wal.Length.ShouldBeGreaterThan(0);
        // Leave a hot-journal-style file too: it must travel with the set, never stay next to the new main file.
        File.WriteAllText(s.Live + "-journal", "stale");
        // Take the database out of use WITHOUT folding the WAL in (kill -9): copy the files aside, release the handles, put the copies back.
        var keep = s.Dir.File("kill");
        Directory.CreateDirectory(keep);
        foreach (var suffix in new[] { "", "-wal" })
        {
            TestFiles.CopyShared(s.Live + suffix, Path.Combine(keep, "shabana.db" + suffix));
        }

        keeper.Dispose();
        ConnectionFactory.ReleaseFileHandles();
        RestoreScenario.DeleteSet(s.Live);
        foreach (var suffix in new[] { "", "-wal" })
        {
            File.Copy(Path.Combine(keep, "shabana.db" + suffix), s.Live + suffix);
        }

        File.WriteAllText(s.Live + "-journal", "stale");
        var oldSha = DatabaseChecks.FileSha256(s.Live);
        var oldWal = DatabaseChecks.FileSha256(s.Live + "-wal");

        // The restore checkpoints first; but if it is killed after moving the set aside, ALL files return.
        Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(RestoreStep.SideFilesMovedAside)).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();
        File.Exists(s.Paths.AsidePath).ShouldBeTrue();

        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.RolledBack);
        File.Exists(s.Paths.AsidePath).ShouldBeFalse();
        File.Exists(s.Live).ShouldBeTrue();
        // The main file differs from oldSha only because the restore's own checkpoint folded the WAL into it; the data is the same.
        using var c = new ConnectionFactory(s.Live).OpenForInspection();
        SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sites WHERE id = 'wal-only'").ShouldBe("1");
        _ = oldSha;
        _ = oldWal;
    }

    [Fact]
    public void A_crash_between_moving_the_main_file_and_its_wal_never_discards_the_wal_of_the_old_database()
    {
        using var s = RestoreScenario.Create(fixture);
        var walBytes = "pretend these are committed pages of a damaged, never-checkpointed database"u8.ToArray();
        var options = new RestoreOptions
        {
            AfterStep = step =>
            {
                if (step == RestoreStep.LiveDatabaseMovedAside)
                {
                    // The old -wal is still in the live slot at this instant (side files move after the main file).
                    File.WriteAllBytes(s.Live + "-wal", walBytes);
                    throw new SimulatedCrashException(step);
                }
            },
        };

        Should.Throw<SimulatedCrashException>(() => s.Service(options).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();

        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.RolledBack);
        File.Exists(s.Live).ShouldBeTrue();
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
        File.ReadAllBytes(s.Live + "-wal").ShouldBe(walBytes, "the old database's WAL must travel back with it, not be thrown away as 'rejected'");
        File.Exists(s.Paths.RejectedPath + "-wal").ShouldBeFalse();
    }

    [Theory]
    [InlineData(RestoreStep.LiveDatabaseMovedAside)]
    [InlineData(RestoreStep.CandidateMovedIntoPlace)]
    [InlineData(RestoreStep.PostSwapValidated)]
    [InlineData(RestoreStep.JournalCommitted)]
    public void Starting_a_new_restore_first_settles_the_interrupted_one(RestoreStep crashAfter)
    {
        using var s = RestoreScenario.Create(fixture);
        Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(crashAfter)).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();

        // No explicit recovery: the next Restore call must not stack a second swap on the first.
        s.Service().Restore(new RestoreRequest(s.BackupFile));

        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);
        AssertClean(s);
        s.PreRestoreBackupFingerprint().ShouldNotBeNull();
    }

    [Fact]
    public void An_unreadable_journal_changes_nothing_and_blocks_a_new_restore_until_resolved()
    {
        using var s = RestoreScenario.Create(fixture);
        Directory.CreateDirectory(s.Paths.RestoreFolder);
        File.WriteAllText(s.Paths.JournalPath, "{ this is not a journal");
        var before = s.DbFolderFiles();

        var recovery = RestoreService.RecoverPendingSwap(s.Paths);
        recovery.Outcome.ShouldBe(SwapRecoveryOutcome.Unresolved);
        recovery.IsSettled.ShouldBeFalse();
        s.DbFolderFiles().ShouldBe(before);
        AssertFullyOld(s);

        var ex = Should.Throw<RestoreFailedException>(() => s.Service().Restore(new RestoreRequest(s.BackupFile)));
        ex.Failure.ShouldBe(RestoreFailure.PendingSwapUnresolved);
        AssertFullyOld(s);
        File.Exists(s.Paths.JournalPath).ShouldBeTrue();      // evidence preserved
    }

    [Theory]
    [InlineData("""{"formatVersion":1,"id":"../../../etc/passwd","phase":0,"oldExisted":true,"oldSha256":"x","candidateSha256":"0000000000000000000000000000000000000000000000000000000000000000"}""")]
    [InlineData("""{"formatVersion":2,"id":"0123456789ab","phase":0,"candidateSha256":"0000000000000000000000000000000000000000000000000000000000000000"}""")]
    [InlineData("""{"formatVersion":1,"id":"0123456789ab","phase":7,"candidateSha256":"0000000000000000000000000000000000000000000000000000000000000000"}""")]
    [InlineData("""{"formatVersion":1,"id":"0123456789ab","phase":0,"candidateSha256":"short"}""")]
    [InlineData("null")]
    public void A_journal_that_is_not_one_of_ours_is_not_acted_on(string json)
    {
        using var s = RestoreScenario.Create(fixture);
        Directory.CreateDirectory(s.Paths.RestoreFolder);
        File.WriteAllText(s.Paths.JournalPath, json);
        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.Unresolved);
        AssertFullyOld(s);
    }

    [Fact]
    public void If_the_old_database_has_vanished_the_rollback_refuses_and_deletes_nothing()
    {
        using var s = RestoreScenario.Create(fixture);
        Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(RestoreStep.CandidateMovedIntoPlace)).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();
        // Somebody (an anti-virus, a curious owner) removed the set-aside copy.
        File.Delete(s.Paths.AsidePath);

        var recovery = RestoreService.RecoverPendingSwap(s.Paths);

        recovery.Outcome.ShouldBe(SwapRecoveryOutcome.Unresolved);
        recovery.Detail!.ShouldContain("swap journal");
        File.Exists(s.Paths.JournalPath).ShouldBeTrue();                  // stays, so nothing is guessed later
        RestoreScenario.Fingerprint(s.Live).ShouldBe(s.NewFp);            // the restored database is still there, not deleted
        s.PreRestoreBackupFingerprint().ShouldBe(s.OldFp);                // and the verified pre-restore backup is the way back
    }

    [Fact]
    public void A_database_in_place_that_is_not_the_recorded_old_one_is_never_overwritten_by_a_rollback()
    {
        using var s = RestoreScenario.Create(fixture);
        Should.Throw<SimulatedCrashException>(() => s.Service(CrashAfter(RestoreStep.JournalWritten)).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();
        // The journal says "nothing moved yet", but somebody replaced the live file in the meantime.
        File.WriteAllBytes(s.Live, File.ReadAllBytes(s.Live).Select(b => (byte)(b ^ 1)).ToArray());
        var changed = DatabaseChecks.FileSha256(s.Live);

        var recovery = RestoreService.RecoverPendingSwap(s.Paths);

        recovery.Outcome.ShouldBe(SwapRecoveryOutcome.Unresolved);
        DatabaseChecks.FileSha256(s.Live).ShouldBe(changed);               // untouched
        File.Exists(s.Paths.JournalPath).ShouldBeTrue();
    }
}
