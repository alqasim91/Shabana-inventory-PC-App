using System.Diagnostics;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;
using Xunit.Abstractions;

namespace Shabana.Tests.Data;

/// <summary>
/// P2 acceptance (4b): the randomised stress run of 03 section 0 — 6,000 random operations x 4 seeds, then the 14 ledger invariants —
/// green with <c>recursive_triggers</c> OFF and ON, against the EMBEDDED baseline. The original script is not in the repository;
/// <see cref="LedgerStress"/> re-creates it from 03's description and the invariants are <see cref="LedgerInvariants"/> (the same SQL the
/// owner's integrity check will run). Both modes of a seed run side by side and must end with IDENTICAL balances.
/// SHABANA_STRESS_OPS shortens the run for quick local loops; the default is the full 6,000.
/// </summary>
[Collection("Stress")]
public abstract class StressTestBase(ITestOutputHelper output)
{
    internal static readonly int Operations = EnvInt("SHABANA_STRESS_OPS", 6000);

    internal static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var n) && n > 0 ? n : fallback;

    protected async Task RunSeedAsync(int seed)
    {
        var clock = Stopwatch.StartNew();
        // Sequential on purpose: SQLite's allocator is process-wide, so running the two in parallel is SLOWER than one after the other.
        var results = new[]
        {
            await RunMemoryAsync(seed, Operations, recursiveTriggers: false),
            await RunMemoryAsync(seed, Operations, recursiveTriggers: true),
        };

        output.WriteLine($"seed {seed}: {Operations} ops x2 in {clock.Elapsed.TotalSeconds:F1}s");
        output.WriteLine("  OFF " + results[0].Stats);
        output.WriteLine("  ON  " + results[1].Stats);

        results[0].Balances.ShouldBe(results[1].Balances, "recursive_triggers OFF and ON must produce identical balances for the same seed");
        results[0].Stats.Applied.ShouldBe(results[1].Stats.Applied);
        results[0].Stats.Rejected.ShouldBe(results[1].Stats.Rejected);

        // About one operation in seven is built to be refused (03: ~850 of 6,000); a run that rejects almost nothing proves nothing.
        var share = (double)results[0].Stats.Rejected / results[0].Stats.Attempted;
        share.ShouldBeInRange(0.04, 0.45);
        foreach (var op in new[] { "pay", "place", "convert", "transfer", "delete-payment", "reverse-conversion" })
        {
            results[0].Stats.AppliedByOp.Keys.ShouldContain(op);
        }
    }

    internal static async Task<(StressStats Stats, string Balances)> RunMemoryAsync(int seed, int operations, bool recursiveTriggers)
    {
        using var executor = new MemoryExecutor(recursiveTriggers);
        var stress = new LedgerStress(executor, seed);
        await stress.SetupAsync();
        // The 14 invariants every sixth of the run (cheap: a few milliseconds), the whole integrity check (PRAGMA integrity_check too) at the end.
        await stress.RunAsync(operations, checkEvery: Math.Max(1, operations / 6), check: () =>
        {
            AssertInvariantsOnly(executor.Connection);
            return Task.CompletedTask;
        });

        AssertInvariants(executor.Connection);
        SqlScript.Scalar<int>(executor.Connection, "PRAGMA recursive_triggers").ShouldBe(recursiveTriggers ? 1 : 0);
        return (stress.Stats, DatabaseFingerprint.Balances(executor.Connection));
    }

    internal static void AssertInvariantsOnly(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var results = LedgerInvariants.Run(connection);
        results.Count.ShouldBe(14);
        results.Where(i => !i.Holds).Select(i => $"{i.Id}={i.Violations}").ShouldBeEmpty();
    }

    internal static void AssertInvariants(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var report = IntegrityChecker.Full(connection);
        report.IntegrityProblems.ShouldBeEmpty();
        report.ForeignKeyProblems.ShouldBeEmpty();
        report.Invariants.Count.ShouldBe(14);
        report.Invariants.Where(i => !i.Holds).Select(i => $"{i.Id}={i.Violations}").ShouldBeEmpty();
    }
}

// One class per seed (named in the report); all share the "Stress" collection so they run one after the other, in parallel with the rest of the suite.
public sealed class StressSeed1(ITestOutputHelper output) : StressTestBase(output)
{
    [Fact]
    public Task Seed_20261001_keeps_all_fourteen_invariants_with_recursive_triggers_off_and_on() => RunSeedAsync(20261001);
}

public sealed class StressSeed2(ITestOutputHelper output) : StressTestBase(output)
{
    [Fact]
    public Task Seed_20261002_keeps_all_fourteen_invariants_with_recursive_triggers_off_and_on() => RunSeedAsync(20261002);
}

public sealed class StressSeed3(ITestOutputHelper output) : StressTestBase(output)
{
    [Fact]
    public Task Seed_20261003_keeps_all_fourteen_invariants_with_recursive_triggers_off_and_on() => RunSeedAsync(20261003);
}

public sealed class StressSeed4(ITestOutputHelper output) : StressTestBase(output)
{
    [Fact]
    public Task Seed_20261004_keeps_all_fourteen_invariants_with_recursive_triggers_off_and_on() => RunSeedAsync(20261004);
}

/// <summary>
/// The same random operations through the REAL stack (file database in WAL mode, pooled connections, <see cref="UnitOfWork"/> with
/// BEGIN IMMEDIATE and app_context), recursive_triggers OFF and ON. Must end with the same balances as the in-memory run of the same
/// seed, and with app_context neutral. Shorter than the full run only because a file database costs file-system calls per operation.
/// </summary>
[Collection("Stress")]
public class StressThroughUnitOfWorkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Random_operations_through_the_unit_of_work_match_the_in_memory_run_and_keep_the_invariants(bool recursiveTriggers)
    {
        const int seed = 20261001;
        var operations = Math.Min(StressTestBase.Operations, StressTestBase.EnvInt("SHABANA_STRESS_FILE_OPS", 1500));
        using var dir = new TempDir();
        var db = TestDatabase.Create(dir, new ConnectionOptions { Synchronous = SynchronousMode.Off, RecursiveTriggers = recursiveTriggers });
        var clock = Stopwatch.StartNew();
        var stress = new LedgerStress(new UowExecutor(db.Uow), seed);
        await stress.SetupAsync();
        await stress.RunAsync(operations);

        using var connection = db.Factory.OpenExisting();
        StressTestBase.AssertInvariants(connection);
        output.WriteLine($"{operations} ops through the unit of work in {clock.Elapsed.TotalSeconds:F1}s: {stress.Stats}");

        var memory = await StressTestBase.RunMemoryAsync(seed, operations, recursiveTriggers);
        DatabaseFingerprint.Balances(connection).ShouldBe(memory.Balances);
        stress.Stats.Rejected.ShouldBe(memory.Stats.Rejected);

        SqlScript.Scalar<long>(connection, "SELECT COUNT(*) FROM app_context WHERE actor_user_id IS NULL AND editing = 0").ShouldBe(1);
    }
}
