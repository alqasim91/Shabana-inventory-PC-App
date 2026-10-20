using Microsoft.Data.Sqlite;
using Shabana.Data.Backup;
using Shabana.Data.Maintenance;
using Shabana.Data.Migrations;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>A populated schema-version-1 database (seeded random ledger data), built once and copied for each test.</summary>
public sealed class PopulatedV1Fixture : IDisposable
{
    private readonly TempDir _dir = new();

    public PopulatedV1Fixture()
    {
        Path = _dir.File("v1.db");
        var db = TestDatabase.CreateWith(Path, TestMigrations.V1(), new BackupService(), _dir.File("backups"));
        var stress = new LedgerStress(new UowExecutor(db.Uow), 7001);
        stress.SetupAsync().GetAwaiter().GetResult();
        stress.RunAsync(500).GetAwaiter().GetResult();
        using (var c = db.Factory.OpenUnpooled())
        {
            SqlScript.Execute(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }

        ConnectionFactory.ReleaseFileHandles();
    }

    public string Path { get; }

    /// <summary>A private copy of the fixture inside <paramref name="dir"/>.</summary>
    public string CopyTo(TempDir dir, string name = "shabana.db")
    {
        var target = dir.File(name);
        File.Copy(Path, target);
        return target;
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>P2 acceptance (2): the migration runner (04 section 8.2).</summary>
public class MigrationRunnerTests(PopulatedV1Fixture fixture) : IClassFixture<PopulatedV1Fixture>
{
    private sealed class ThrowingBackup(Exception failure) : IBackupService
    {
        public int Calls { get; private set; }

        public BackupResult Create(BackupRequest request)
        {
            Calls++;
            throw failure;
        }
    }

    private sealed class CountingBackup : IBackupService
    {
        private readonly BackupService _inner = new();

        public List<BackupResult> Results { get; } = [];

        public BackupResult Create(BackupRequest request)
        {
            var result = _inner.Create(request);
            Results.Add(result);
            return result;
        }
    }

    private static (string Schema, string Data, string Balances, int Version) Fingerprints(string path)
    {
        using var c = new ConnectionFactory(path).OpenForInspection();
        return (DatabaseFingerprint.Schema(c), DatabaseFingerprint.Data(c), DatabaseFingerprint.Balances(c), DatabaseChecks.UserVersion(c));
    }

    private static string FileHash(string path)
    {
        ConnectionFactory.ReleaseFileHandles();
        return DatabaseChecks.FileSha256(path);
    }

    private static MigrationRunner Runner(string path, IReadOnlyList<Migration> chain, IBackupService backups, string root, Action<MigrationProgress>? progress = null) =>
        new(path, chain, backups, root, "9.9.9", progress: progress, options: new ConnectionOptions { Synchronous = SynchronousMode.Normal });

    [Fact]
    public void A_missing_file_is_created_by_the_whole_chain_without_a_backup()
    {
        using var dir = new TempDir();
        var path = dir.File("db", "shabana.db");
        var backups = new CountingBackup();

        var result = Runner(path, TestMigrations.V1To3(), backups, dir.File("b")).Run();

        result.Outcome.ShouldBe(MigrationOutcome.Created);
        (result.FromVersion, result.ToVersion, result.PreMigrationBackup).ShouldBe((0, 3, null));
        backups.Results.ShouldBeEmpty();      // nothing to lose
        Directory.Exists(dir.File("b")).ShouldBeFalse();

        using var c = new ConnectionFactory(path).Open();
        DatabaseChecks.UserVersion(c).ShouldBe(3);
        SqlScript.Scalar<string>(c, "SELECT value FROM app_meta WHERE key = 'schema_version'").ShouldBe("3");
        SqlScript.Scalar<string>(c, "SELECT value FROM app_meta WHERE key = 'created_by_app_version'").ShouldBe("9.9.9");
        SqlScript.Scalar<string>(c, "PRAGMA journal_mode").ShouldBe("wal");
        DatabaseChecks.IntegrityCheck(c).ShouldBeEmpty();
        DatabaseChecks.ForeignKeyViolations(c).ShouldBeEmpty();
        IntegrityChecker.Full(c).IsClean.ShouldBeTrue();
    }

    [Fact]
    public void The_embedded_chain_creates_a_database_whose_catalog_is_the_one_P0_pinned()
    {
        using var dir = new TempDir();
        var path = dir.File("shabana.db");
        Runner(path, MigrationSet.Embedded(), new BackupService(), dir.File("b")).Run().ToVersion.ShouldBe(1);
        using var c = new ConnectionFactory(path).Open();
        SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").ShouldBe("36");
        SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger'").ShouldBe("108");
        SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view'").ShouldBe("13");
        using var memory = SqlScript.CreateBaselineDatabase();
        DatabaseFingerprint.Schema(c).ShouldBe(DatabaseFingerprint.Schema(memory));
    }

    [Fact]
    public void An_N_minus_1_fixture_with_real_ledger_data_upgrades_to_a_schema_equal_to_a_fresh_build_and_keeps_every_balance()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        before.Version.ShouldBe(1);
        using (var c = new ConnectionFactory(path).OpenForInspection())
        {
            DatabaseChecks.RowCounts(c)["stock_movements"].ShouldBeGreaterThan(100);   // "real-looking" data, not an empty shell
        }

        var backups = new CountingBackup();
        var result = Runner(path, TestMigrations.V1To3(), backups, dir.File("backups")).Run();

        result.Outcome.ShouldBe(MigrationOutcome.Upgraded);
        (result.FromVersion, result.ToVersion).ShouldBe((1, 3));

        // 1. Equal schema fingerprint to a database built fresh by the same chain.
        var freshPath = dir.File("fresh.db");
        Runner(freshPath, TestMigrations.V1To3(), backups, dir.File("b2")).Run();
        var upgraded = Fingerprints(path);
        upgraded.Schema.ShouldBe(Fingerprints(freshPath).Schema);
        upgraded.Schema.ShouldNotBe(before.Schema);
        upgraded.Version.ShouldBe(3);

        // 2. Every balance the owner cares about is unchanged, and every ledger invariant still holds.
        upgraded.Balances.ShouldBe(before.Balances);
        using (var c = new ConnectionFactory(path).Open())
        {
            IntegrityChecker.Full(c).IsClean.ShouldBeTrue();
            SqlScript.Scalar<string>(c, "SELECT value FROM app_meta WHERE key = 'schema_version'").ShouldBe("3");
            SqlScript.Scalar<string>(c, "SELECT value FROM app_meta WHERE key = 'last_migrated_from'").ShouldBe("1");
            SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM pragma_table_info('expense_categories') WHERE name = 'note'").ShouldBe("1");
            SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'trg_audit_expense_categories%'").ShouldBe("3");
            SqlScript.Scalar<string>(c, "PRAGMA foreign_keys").ShouldBe("1");
        }

        // 3. The row of every table that existed before is still there (the rebuild did not lose or duplicate any).
        using (var old = new ConnectionFactory(fixture.Path).OpenForInspection())
        using (var now = new ConnectionFactory(path).OpenForInspection())
        {
            var oldCounts = DatabaseChecks.RowCounts(old);
            var newCounts = DatabaseChecks.RowCounts(now);
            foreach (var (table, count) in oldCounts.Where(t => t.Key != "app_meta"))
            {
                newCounts[table].ShouldBe(count, table);
            }

            // app_meta gains only the version notes of 04 section 8.5.
            newCounts["app_meta"].ShouldBe(oldCounts["app_meta"] + 1);       // last_migrated_from

            newCounts["feature_flags"].ShouldBe(1);
        }

        // 4. A verified safety copy of the PRE-upgrade database was taken first.
        var backup = backups.Results.Single(b => b.FilePath.Contains("pre-migration", StringComparison.Ordinal));
        result.PreMigrationBackup.ShouldBe(backup.FilePath);
        Path.GetFileName(backup.FilePath).ShouldStartWith("pre-v1-to-v3-");
        backup.Manifest.SchemaVersion.ShouldBe(1);
        backup.Manifest.Reason.ShouldBe("pre-migration");
        BackupVerifier.Verify(backup.FilePath).Ok.ShouldBeTrue();
    }

    [Fact]
    public void A_script_that_fails_half_way_rolls_back_to_the_exact_prior_state()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        var bytesBefore = FileHash(path);
        var chain = TestMigrations.WithFailing("""
            ALTER TABLE sites ADD COLUMN broken TEXT;
            UPDATE sites SET name_ar = name_ar || ' (changed)';
            INSERT INTO items (id, name_ar, unit_type) VALUES ('from-migration', 'x', 'kg');
            CREATE TABLE half_done (a INTEGER);
            INSERT INTO table_that_does_not_exist VALUES (1);
            """);

        var backups = new CountingBackup();
        var ex = Should.Throw<MigrationException>(() => Runner(path, chain, backups, dir.File("backups")).Run());

        ex.Failure.ShouldBe(MigrationFailure.ScriptFailed);
        ex.Message.ShouldContain("table_that_does_not_exist");
        ex.BackupFile.ShouldNotBeNull();                       // the safety copy is left in place and reported
        File.Exists(ex.BackupFile).ShouldBeTrue();

        var after = Fingerprints(path);
        after.ShouldBe(before);                                 // schema, every row, balances, user_version: identical
        FileHash(path).ShouldBe(bytesBefore);                   // ... and the file itself, byte for byte
        File.Exists(path + "-wal").ShouldBeFalse();
        File.Exists(path + "-shm").ShouldBeFalse();
    }

    [Fact]
    public void Every_statement_of_a_script_runs_even_after_one_that_returns_rows()
    {
        // Microsoft.Data.Sqlite's ExecuteNonQuery on a multi-statement text stops silently at the first row-returning statement.
        // The runner executes statement by statement, so the failing INSERT after a SELECT still fails the upgrade.
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        var chain = TestMigrations.WithFailing("SELECT 1;\nINSERT INTO table_that_does_not_exist VALUES (1);\nUPDATE app_meta SET value = '3' WHERE key = 'schema_version';");
        var ex = Should.Throw<MigrationException>(() => Runner(path, chain, new BackupService(), dir.File("backups")).Run());
        ex.Message.ShouldContain("table_that_does_not_exist");
        Fingerprints(path).ShouldBe(before);

        // ... and a statement placed after a SELECT really executes.
        var ok = TestMigrations.WithFailing("SELECT count(*) FROM sites;\nCREATE TABLE after_select (a INTEGER) STRICT;\nUPDATE app_meta SET value = '3' WHERE key = 'schema_version';");
        Runner(path, ok, new BackupService(), dir.File("backups")).Run();
        using var c = new ConnectionFactory(path).OpenForInspection();
        SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'after_select'").ShouldBe("1");
    }

    [Theory]
    [InlineData("COMMIT;")]
    [InlineData("BEGIN;")]
    [InlineData("END TRANSACTION;")]
    [InlineData("ROLLBACK;")]
    [InlineData("SAVEPOINT x;")]
    [InlineData("VACUUM;")]
    [InlineData("ATTACH DATABASE 'other.db' AS other;")]
    public void A_script_that_controls_the_transaction_is_refused_before_anything_happens(string statement)
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        var backups = new CountingBackup();
        var chain = TestMigrations.WithFailing("CREATE TABLE sneaky (a INTEGER);\n" + statement + "\nUPDATE app_meta SET value = '3' WHERE key = 'schema_version';");
        var ex = Should.Throw<MigrationException>(() => Runner(path, chain, backups, dir.File("backups")).Run());
        ex.Failure.ShouldBe(MigrationFailure.ScriptFailed);
        backups.Results.ShouldBeEmpty();    // refused before even the safety copy
        Fingerprints(path).ShouldBe(before);
    }

    [Fact]
    public void A_script_that_leaves_a_foreign_key_violation_is_rolled_back()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        var chain = TestMigrations.WithFailing("""
            INSERT INTO contact_phones (contact_id, phone) VALUES ('ghost-contact', '0100');   -- foreign_keys is OFF during a migration
            UPDATE app_meta SET value = '3' WHERE key = 'schema_version';
            """);

        var ex = Should.Throw<MigrationException>(() => Runner(path, chain, new BackupService(), dir.File("backups")).Run());
        ex.Failure.ShouldBe(MigrationFailure.ScriptFailed);
        ex.Message.ShouldContain("foreign_key_check");
        Fingerprints(path).ShouldBe(before);
    }

    [Fact]
    public void A_script_that_changes_balances_without_declaring_it_is_rolled_back_and_with_the_marker_is_allowed()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        const string Fix = """
            INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, note)
              SELECT site_id, item_id, 1, 'adjustment', 'data fix' FROM v_stock_balance LIMIT 1;
            UPDATE app_meta SET value = '3' WHERE key = 'schema_version';
            """;

        var ex = Should.Throw<MigrationException>(() => Runner(path, TestMigrations.WithFailing(Fix), new BackupService(), dir.File("backups")).Run());
        ex.Message.ShouldContain("balances");
        Fingerprints(path).ShouldBe(before);

        Runner(path, TestMigrations.WithFailing("-- shabana:balances-may-change\n" + Fix), new BackupService(), dir.File("backups")).Run().ToVersion.ShouldBe(3);
        var after = Fingerprints(path);
        after.Balances.ShouldNotBe(before.Balances);
        after.Version.ShouldBe(3);
    }

    [Fact]
    public void A_last_script_that_forgets_the_schema_version_mirror_is_rolled_back()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        var ex = Should.Throw<MigrationException>(() =>
            Runner(path, TestMigrations.WithFailing("CREATE TABLE forgot_mirror (a INTEGER) STRICT;"), new BackupService(), dir.File("backups")).Run());
        ex.Message.ShouldContain("schema_version");
        Fingerprints(path).ShouldBe(before);
    }

    [Fact]
    public void A_failing_backup_aborts_the_upgrade_before_anything_is_touched()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        var bytesBefore = FileHash(path);

        // (a) the backup service fails (disk full, folder missing ...)
        var failing = new ThrowingBackup(new BackupFailedException(BackupFailure.DiskSpace, "no space"));
        var ex = Should.Throw<MigrationException>(() => Runner(path, TestMigrations.V1To3(), failing, dir.File("b")).Run());
        ex.Failure.ShouldBe(MigrationFailure.BackupFailed);
        ex.InnerException.ShouldBeOfType<BackupFailedException>();
        failing.Calls.ShouldBe(1);

        // (b) the new backup fails VERIFICATION
        var unverified = new ThrowingBackup(new BackupFailedException(BackupFailure.VerificationFailed, "hash mismatch"));
        Should.Throw<MigrationException>(() => Runner(path, TestMigrations.V1To3(), unverified, dir.File("b")).Run()).Failure.ShouldBe(MigrationFailure.BackupFailed);

        // (c) the REAL backup service cannot write to the chosen folder (a file sits where the folder should be)
        var blocker = dir.File("not-a-folder");
        File.WriteAllText(blocker, "x");
        Should.Throw<MigrationException>(() => Runner(path, TestMigrations.V1To3(), new BackupService(), blocker).Run()).Failure.ShouldBe(MigrationFailure.BackupFailed);

        Fingerprints(path).ShouldBe(before);                    // not one script ran
        FileHash(path).ShouldBe(bytesBefore);
    }

    [Fact]
    public void A_database_newer_than_the_application_is_refused_and_not_touched()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        using (var c = new ConnectionFactory(path).OpenUnpooled())
        {
            SqlScript.Execute(c, "PRAGMA user_version = 99");
            SqlScript.Execute(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }

        var bytesBefore = FileHash(path);
        var backups = new CountingBackup();
        var ex = Should.Throw<MigrationException>(() => Runner(path, TestMigrations.V1To3(), backups, dir.File("b")).Run());

        ex.Failure.ShouldBe(MigrationFailure.NewerThanApp);
        (ex.DatabaseVersion, ex.AppVersion).ShouldBe((99, 3));
        backups.Results.ShouldBeEmpty();
        FileHash(path).ShouldBe(bytesBefore);
        File.Exists(path + "-wal").ShouldBeFalse();
    }

    [Fact]
    public void A_database_a_version_ahead_of_the_chain_is_refused_too()
    {
        using var dir = new TempDir();
        var path = dir.File("shabana.db");
        Runner(path, TestMigrations.V1To3(), new BackupService(), dir.File("b")).Run();
        var before = Fingerprints(path);
        var ex = Should.Throw<MigrationException>(() => Runner(path, TestMigrations.V1To2(), new BackupService(), dir.File("b")).Run());
        ex.Failure.ShouldBe(MigrationFailure.NewerThanApp);
        Fingerprints(path).ShouldBe(before);
    }

    [Fact]
    public void Files_that_are_not_a_shabana_database_are_refused_untouched()
    {
        using var dir = new TempDir();
        var junk = dir.File("junk.db");
        File.WriteAllBytes(junk, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31 % 251)).ToArray());
        var junkHash = FileHash(junk);
        Should.Throw<MigrationException>(() => Runner(junk, TestMigrations.V1(), new BackupService(), dir.File("b")).Run()).Failure.ShouldBe(MigrationFailure.NotRecognized);
        FileHash(junk).ShouldBe(junkHash);

        // A real SQLite file with data but no schema version.
        var foreign = dir.File("foreign.db");
        using (var c = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            c.Open();
            SqlScript.Execute(c, "CREATE TABLE somebody_elses (a TEXT); INSERT INTO somebody_elses VALUES ('precious')");
        }

        var foreignHash = FileHash(foreign);
        Should.Throw<MigrationException>(() => Runner(foreign, TestMigrations.V1(), new BackupService(), dir.File("b")).Run()).Failure.ShouldBe(MigrationFailure.NotRecognized);
        FileHash(foreign).ShouldBe(foreignHash);

        // A zero-byte file IS a valid empty SQLite database and gets the schema.
        var empty = dir.File("empty.db");
        File.WriteAllBytes(empty, []);
        Runner(empty, TestMigrations.V1(), new BackupService(), dir.File("b")).Run().Outcome.ShouldBe(MigrationOutcome.Created);
    }

    [Fact]
    public async Task A_stale_wal_without_its_database_is_set_aside_not_replayed_into_the_new_file()
    {
        using var dir = new TempDir();
        var live = fixture.CopyTo(dir, "old.db");
        var db = TestDatabase.Open(live);
        var keeper = db.Factory.OpenUnpooled();
        await db.Uow.RunAsync(LedgerStress.Actor, tx => { tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('ghost-site', 'x')"); return Task.CompletedTask; });
        var path = dir.File("shabana.db");
        TestFiles.CopyShared(live + "-wal", path + "-wal");           // the database was deleted by hand, its WAL was not
        keeper.Dispose();
        ConnectionFactory.ReleaseFileHandles();
        var staleHash = DatabaseChecks.FileSha256(path + "-wal");

        Runner(path, TestMigrations.V1(), new BackupService(), dir.File("b")).Run().Outcome.ShouldBe(MigrationOutcome.Created);

        using (var c = new ConnectionFactory(path).OpenForInspection())
        {
            SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sites").ShouldBe("0");      // nothing replayed
            DatabaseChecks.IntegrityCheck(c).ShouldBeEmpty();
        }

        var kept = Directory.GetFiles(dir.Path, "shabana.db.orphaned-*-wal");
        kept.Length.ShouldBe(1);                                                            // renamed, never deleted
        DatabaseChecks.FileSha256(kept[0]).ShouldBe(staleHash);
    }

    [Fact]
    public void A_database_that_is_up_to_date_is_not_backed_up_or_migrated()
    {
        using var dir = new TempDir();
        var path = dir.File("shabana.db");
        Runner(path, TestMigrations.V1To2(), new BackupService(), dir.File("b")).Run();
        var before = Fingerprints(path);
        var backups = new CountingBackup();
        var result = Runner(path, TestMigrations.V1To2(), backups, dir.File("b")).Run();
        result.Outcome.ShouldBe(MigrationOutcome.UpToDate);
        backups.Results.ShouldBeEmpty();
        Fingerprints(path).Schema.ShouldBe(before.Schema);
        Fingerprints(path).Balances.ShouldBe(before.Balances);
    }

    [Fact]
    public void An_image_of_the_database_taken_in_the_middle_of_an_upgrade_opens_as_the_old_version()
    {
        // What a power cut during the upgrade leaves on disk: the main file and the WAL as they are at that instant. The first script
        // writes enough rows to push uncommitted pages into the WAL; SQLite's recovery must ignore them.
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = Fingerprints(path);
        var crashDir = dir.File("crash");
        Directory.CreateDirectory(crashDir);
        var bulk = """
            CREATE TABLE bulk (a INTEGER, pad TEXT) STRICT;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 60000) INSERT INTO bulk SELECT i, hex(randomblob(60)) FROM n;
            UPDATE app_meta SET value = '2' WHERE key = 'schema_version';
            """;
        var chain = TestMigrations.Chain(("0001_baseline.sql", TestMigrations.Baseline), ("0002_bulk.sql", bulk));

        var imaged = false;
        Runner(path, chain, new BackupService(), dir.File("backups"), p =>
        {
            if (p is { Stage: "script-done", Version: 2 })
            {
                TestFiles.CopyShared(path, Path.Combine(crashDir, "shabana.db"));
                TestFiles.CopyShared(path + "-wal", Path.Combine(crashDir, "shabana.db-wal"));   // not the -shm: it is rebuilt from the WAL on open
                imaged = true;
            }
        }).Run();

        imaged.ShouldBeTrue();
        new FileInfo(Path.Combine(crashDir, "shabana.db-wal")).Length.ShouldBeGreaterThan(1_000_000);   // there WERE uncommitted pages in the WAL
        Fingerprints(Path.Combine(crashDir, "shabana.db")).ShouldBe(before);                              // and recovery discards them
        Fingerprints(path).Version.ShouldBe(2);                                                           // while the real run completed
    }

    [Fact]
    public void The_chain_must_be_numbered_without_gaps_and_the_scripts_released_so_far_are_pinned_by_checksum()
    {
        Should.Throw<InvalidOperationException>(() => TestMigrations.Chain(("0001_a.sql", "x"), ("0003_c.sql", "x")));
        Should.Throw<InvalidOperationException>(() => TestMigrations.Chain(("0002_a.sql", "x")));
        Should.Throw<InvalidOperationException>(() => TestMigrations.Chain(("baseline.sql", "x")));
        Should.Throw<InvalidOperationException>(() => MigrationSet.Create([]));

        // 04 section 8.1: a changed released script fails CI. The pins live in Fixtures/migration-checksums.json.
        var pins = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(TestPaths.ReadText(TestPaths.Fixture("migration-checksums.json")))!;
        EmbeddedSql.ScriptNames.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(pins.Keys.OrderBy(n => n, StringComparer.Ordinal), "every released script needs a pin, and a pin needs its script");
        foreach (var name in EmbeddedSql.ScriptNames)
        {
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(EmbeddedSql.ReadScript(name))));
            sha.ShouldBe(pins[name], $"{name} was changed after release: add a NEW numbered migration instead");
        }

        MigrationSet.Embedded().Select(m => m.Version).ShouldBe([1]);
    }

    [Fact]
    public void The_balances_marker_is_recognised_only_as_a_whole_comment_line()
    {
        new Migration(2, "x", "-- shabana:balances-may-change\nSELECT 1;").BalancesMayChange.ShouldBeTrue();
        new Migration(2, "x", "  --   shabana:balances-may-change  \nSELECT 1;").BalancesMayChange.ShouldBeTrue();
        new Migration(2, "x", "SELECT '-- shabana:balances-may-change';").BalancesMayChange.ShouldBeFalse();
        new Migration(2, "x", "-- see shabana:balances-may-change docs\nSELECT 1;").BalancesMayChange.ShouldBeFalse();
    }
}
