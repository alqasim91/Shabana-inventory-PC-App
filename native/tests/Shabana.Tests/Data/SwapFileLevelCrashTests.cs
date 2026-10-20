using Shabana.Data.Backup;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>
/// The process dies after EVERY SINGLE FILE RENAME inside the multi-file loops (the forward swap, the automatic swap back, and the start-up
/// recovery that follows), and then dies AGAIN after any single rename of the recovery. The old database has a -wal, -shm and -journal, so each
/// loop has several files. Whatever the sequence, the end state is exactly the old database with all its side files, and at no instant is the old
/// main file in a place the code deletes from (the "rejected" area).
/// </summary>
public class SwapFileLevelCrashTests(PopulatedV1Fixture fixture) : IClassFixture<PopulatedV1Fixture>
{
    private static readonly string[] Sides = ["-wal", "-shm", "-journal"];

    public static TheoryData<string, int, int> Cases()
    {
        var data = new TheoryData<string, int, int>();
        for (var first = 1; first <= 4; first++)
        {
            for (var second = 0; second <= 5; second++)
            {
                data.Add("forward", first, second);
            }
        }

        for (var first = 1; first <= 5; first++)
        {
            for (var second = 0; second <= 5; second++)
            {
                data.Add("rollback", first, second);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_crash_after_any_single_file_move_forward_or_back_and_a_second_crash_in_recovery_still_end_with_exactly_the_old_database(string mode, int firstCrash, int secondCrash)
    {
        using var s = RestoreScenario.Create(fixture);
        var oldSides = new Dictionary<string, byte[]>();
        foreach (var side in Sides)
        {
            oldSides[side] = System.Text.Encoding.ASCII.GetBytes("old " + side + " content");
        }

        var counting = false;
        var moves = 0;
        var options = new RestoreOptions
        {
            AfterStep = step =>
            {
                if (step == RestoreStep.LiveCheckpointed)
                {
                    foreach (var (side, bytes) in oldSides)
                    {
                        File.WriteAllBytes(s.Live + side, bytes);
                    }

                    counting = mode == "forward";
                }

                if (counting && step == RestoreStep.FileMoved && ++moves == firstCrash)
                {
                    throw new SimulatedCrashException(step);
                }
            },
            PostSwapValidator = mode == "rollback"
                ? _ =>
                {
                    counting = true;      // from now on the in-process swap back is what moves files
                    throw new InvalidDataException("validation failed");
                }
                : null,
        };

        Should.Throw<SimulatedCrashException>(() => s.Service(options).Restore(new RestoreRequest(s.BackupFile)));
        ConnectionFactory.ReleaseFileHandles();
        AssertOldNeverOnlyDeletable(s);

        // Next start; it may die too, after any single rename of its own.
        var recoveries = 0;
        if (secondCrash > 0)
        {
            try
            {
                RestoreService.RecoverPendingSwap(s.Paths, step =>
                {
                    if (step == RestoreStep.FileMoved && ++recoveries == secondCrash)
                    {
                        throw new SimulatedCrashException(step);
                    }
                });
            }
            catch (SimulatedCrashException)
            {
                // died again
            }

            AssertOldNeverOnlyDeletable(s);
        }

        // The start after that runs to the end.
        var final = RestoreService.RecoverPendingSwap(s.Paths);
        final.IsSettled.ShouldBeTrue(final.Detail);
        File.Exists(s.Paths.JournalPath).ShouldBeFalse();
        DatabaseChecks.FileSha256(s.Live).ShouldBe(s.OldSha);
        foreach (var (side, bytes) in oldSides)
        {
            File.ReadAllBytes(s.Live + side).ShouldBe(bytes, "the old database's " + side + " must be back with it");
        }

        File.Exists(s.Paths.AsidePath).ShouldBeFalse();
        File.Exists(s.Paths.RejectedPath).ShouldBeFalse();
        File.Exists(s.Paths.CandidatePath).ShouldBeFalse();
        RestoreService.RecoverPendingSwap(s.Paths).Outcome.ShouldBe(SwapRecoveryOutcome.NothingPending);
    }

    private static void AssertOldNeverOnlyDeletable(RestoreScenario s)
    {
        var safe = new[] { s.Live, s.Paths.AsidePath }.Where(File.Exists).Select(DatabaseChecks.FileSha256).Contains(s.OldSha);
        safe.ShouldBeTrue("the old main file must be in its place or set aside");
        if (File.Exists(s.Paths.RejectedPath))
        {
            DatabaseChecks.FileSha256(s.Paths.RejectedPath).ShouldNotBe(s.OldSha, "the old database must never sit in the rejected area");
        }
    }
}
