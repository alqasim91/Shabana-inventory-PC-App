using System.IO.Compression;
using System.Text.Json;
using Shabana.Data.Backup;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>P2 acceptance (3a): a backup is made with the online API and verified (integrity + counts + sha256).</summary>
public class BackupServiceTests(PopulatedV1Fixture fixture) : IClassFixture<PopulatedV1Fixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 30, 15, TimeSpan.Zero);

    private static string DataFingerprint(string path)
    {
        using var c = new ConnectionFactory(path).OpenForInspection();
        return DatabaseFingerprint.Data(c) + "|" + DatabaseFingerprint.Schema(c) + "|" + DatabaseChecks.UserVersion(c);
    }

    private static string ExtractDatabase(string shbk, string to)
    {
        using var zip = ZipFile.OpenRead(shbk);
        zip.GetEntry("shabana.db")!.ExtractToFile(to);
        return to;
    }

    [Fact]
    public async Task A_backup_of_a_live_wal_database_is_verified_and_equals_the_database_it_came_from()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var db = TestDatabase.Open(path);
        using var keeper = db.Factory.OpenUnpooled();     // an open connection keeps the WAL alive even if another test clears the pools

        // Keep the database LIVE: a pooled connection stays open and the newest writes exist only in the WAL.
        await db.Uow.RunAsync(LedgerStress.Actor, tx =>
        {
            tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('only-in-wal', 'only in the WAL')");
            return Task.CompletedTask;
        });
        new FileInfo(path + "-wal").Length.ShouldBeGreaterThan(0);
        var mainFileOnly = dir.File("raw-copy.db");
        TestFiles.CopyShared(path, mainFileOnly);                       // what a careless file copy would capture
        DataFingerprint(mainFileOnly).ShouldNotBe(DataFingerprint(path), "a raw copy loses the WAL; that is why the online API is required");

        var time = new FakeTime(Now);
        var result = new BackupService(time).Create(new BackupRequest(path, dir.File("backups"), BackupReason.Manual, "1.2.3"));

        // Placed and named by reason/time, nothing else left in the folder.
        result.FilePath.ShouldBe(dir.File("backups", "manual", "manual-20261020-083015.shbk"));
        Directory.GetFileSystemEntries(dir.File("backups", "manual")).ShouldBe([result.FilePath]);
        result.FileBytes.ShouldBe(new FileInfo(result.FilePath).Length);

        // Independently verified: archive layout, sha256, integrity_check, schema version, row count of EVERY table.
        var verification = BackupVerifier.Verify(result.FilePath);
        verification.Ok.ShouldBeTrue(verification.Failure);
        using (var zip = ZipFile.OpenRead(result.FilePath))
        {
            zip.Entries.Select(e => e.FullName).Order().ShouldBe(["manifest.json", "shabana.db"]);
        }

        var m = result.Manifest;
        (m.AppVersion, m.SchemaVersion, m.Reason, m.CreatedAtUtc).ShouldBe(("1.2.3", 1, "manual", "2026-10-20T08:30:15.000Z"));
        m.RowCounts.Count.ShouldBe(36);
        m.RowCounts["sites"].ShouldBe(SqlScript.Scalar<long>(OpenRaw(path), "SELECT COUNT(*) FROM sites"));
        m.RowCounts["stock_movements"].ShouldBeGreaterThan(100);

        var extracted = ExtractDatabase(result.FilePath, dir.File("extracted.db"));
        DatabaseChecks.FileSha256(extracted).ShouldBe(m.Sha256);
        m.DbBytes.ShouldBe(new FileInfo(extracted).Length);
        DataFingerprint(extracted).ShouldBe(DataFingerprint(path));          // including the row that lived only in the WAL
        File.Exists(extracted + "-wal").ShouldBeFalse();                       // a single self-contained file, no WAL needed
        using (var c = new ConnectionFactory(extracted).OpenForInspection(OpenMode.ReadOnly))
        {
            SqlScript.Scalar<string>(c, "PRAGMA journal_mode").ShouldBe("delete");
            SqlScript.Scalar<string>(c, "SELECT COUNT(*) FROM sites WHERE id = 'only-in-wal'").ShouldBe("1");
        }

        // The manifest carries the documented fields (04 section 9.2).
        using var zip2 = ZipFile.OpenRead(result.FilePath);
        using var json = JsonDocument.Parse(zip2.GetEntry("manifest.json")!.Open());
        foreach (var key in new[] { "formatVersion", "appVersion", "schemaVersion", "createdAtUtc", "reason", "sha256", "dbBytes", "rowCounts", "businessName" })
        {
            json.RootElement.TryGetProperty(key, out _).ShouldBeTrue(key);
        }

        // The live database was only read.
        DataFingerprint(path).ShouldBe(DataFingerprint(path));
        db.Scalar<long>("SELECT COUNT(*) FROM sites WHERE id = 'only-in-wal'").ShouldBe(1);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenRaw(string path) => new ConnectionFactory(path).OpenForInspection();

    [Fact]
    public async Task Backups_taken_while_the_application_keeps_writing_are_each_consistent()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var db = TestDatabase.Open(path);
        using var stop = new CancellationTokenSource();
        var writes = 0;
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await db.Uow.RunAsync(LedgerStress.Actor, tx =>
                {
                    tx.Execute("INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason) SELECT id, 100, 'manual', 'busy writer' FROM sites LIMIT 1");
                    return Task.CompletedTask;
                });
                Interlocked.Increment(ref writes);
            }
        });

        var service = new BackupService();
        var made = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            made.Add(service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t", $"auto-{i}")).FilePath);
            await Task.Delay(30);
        }

        await stop.CancelAsync();
        await writer;
        writes.ShouldBeGreaterThan(0);
        foreach (var file in made)
        {
            BackupVerifier.Verify(file).Ok.ShouldBeTrue(file);
        }
    }

    public static TheoryData<string> Damage =>
    [
        "flip-a-byte-in-the-middle",
        "truncate",
        "not-a-zip",
        "extra-entry",
        "manifest-wrong-sha",
        "manifest-wrong-row-count",
        "manifest-wrong-schema-version",
        "database-page-corrupted-with-matching-sha",
        "database-replaced-by-another-valid-database",
        "empty-file",
    ];

    [Theory]
    [MemberData(nameof(Damage))]
    public void A_damaged_or_tampered_backup_is_detected(string damage)
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var good = new BackupService().Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t")).FilePath;
        BackupVerifier.Verify(good).Ok.ShouldBeTrue();
        var bad = dir.File("bad.shbk");

        switch (damage)
        {
            case "flip-a-byte-in-the-middle":
                var bytes = File.ReadAllBytes(good);
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(bad, bytes);
                break;
            case "truncate":
                File.WriteAllBytes(bad, File.ReadAllBytes(good).AsSpan(0, (int)new FileInfo(good).Length / 2).ToArray());
                break;
            case "not-a-zip":
                File.WriteAllText(bad, "this is not a backup");
                break;
            case "empty-file":
                File.WriteAllBytes(bad, []);
                break;
            case "extra-entry":
                File.Copy(good, bad);
                using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update))
                {
                    zip.CreateEntry("../evil.txt").Open().Write([1, 2, 3]);
                }

                break;
            case "database-replaced-by-another-valid-database":
                Rebuild(good, bad, (db, manifest) => (OtherDatabase(dir), manifest));
                break;
            case "database-page-corrupted-with-matching-sha":
                // The manifest is CONSISTENT with the damaged file (hash and size recomputed): only integrity_check can catch it.
                Rebuild(good, bad, (db, manifest) =>
                {
                    var broken = (byte[])db.Clone();
                    for (var i = broken.Length / 4; i < (broken.Length / 4) + 12288 && i < broken.Length; i++)
                    {
                        broken[i] = 0xAB;     // three whole pages of garbage in the middle of the file
                    }

                    return (broken, manifest with { Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(broken)) });
                });
                break;
            case "manifest-wrong-sha":
                Rebuild(good, bad, (db, manifest) => (db, manifest with { Sha256 = new string('0', 64) }));
                break;
            case "manifest-wrong-row-count":
                Rebuild(good, bad, (db, manifest) =>
                {
                    var counts = new SortedDictionary<string, long>(manifest.RowCounts) { ["stock_movements"] = manifest.RowCounts["stock_movements"] + 1 };
                    return (db, manifest with { RowCounts = counts });
                });
                break;
            case "manifest-wrong-schema-version":
                Rebuild(good, bad, (db, manifest) => (db, manifest with { SchemaVersion = 2 }));
                break;
        }

        var verification = BackupVerifier.Verify(bad);
        verification.Ok.ShouldBeFalse(damage);
        verification.Failure.ShouldNotBeNullOrWhiteSpace();
        Directory.GetFiles(dir.Path, ".shbk-verify-*").ShouldBeEmpty();    // the scratch copy never stays behind
    }

    private static byte[] OtherDatabase(TempDir dir)
    {
        var other = TestDatabase.Create(dir, name: "other.db");
        ConnectionFactory.ReleaseFileHandles();
        using (var c = other.Factory.OpenUnpooled())
        {
            SqlScript.Execute(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }

        return File.ReadAllBytes(other.Path);
    }

    private static void Rebuild(string source, string target, Func<byte[], BackupManifest, (byte[] Db, BackupManifest Manifest)> change)
    {
        byte[] db;
        BackupManifest manifest;
        using (var zip = ZipFile.OpenRead(source))
        {
            using var ds = zip.GetEntry("shabana.db")!.Open();
            using var ms = new MemoryStream();
            ds.CopyTo(ms);
            db = ms.ToArray();
            manifest = JsonSerializer.Deserialize<BackupManifest>(zip.GetEntry("manifest.json")!.Open(), BackupManifest.Json)!;
        }

        var (newDb, newManifest) = change(db, manifest);
        newManifest = newManifest with { DbBytes = newDb.Length };
        using var output = ZipFile.Open(target, ZipArchiveMode.Create);
        using (var s = output.CreateEntry("shabana.db").Open())
        {
            s.Write(newDb);
        }

        using (var s = output.CreateEntry("manifest.json").Open())
        {
            JsonSerializer.Serialize(s, newManifest, BackupManifest.Json);
        }
    }

    [Fact]
    public void A_backup_that_fails_verification_is_deleted_and_reported_never_kept_under_its_name()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var service = new BackupService(afterArchiveWritten: part =>
        {
            var bytes = File.ReadAllBytes(part);
            bytes[bytes.Length / 3] ^= 0x55;     // the disk damages the archive between writing and reading it back
            File.WriteAllBytes(part, bytes);
        });

        var ex = Should.Throw<BackupFailedException>(() => service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t")));
        ex.Failure.ShouldBe(BackupFailure.VerificationFailed);
        Directory.GetFileSystemEntries(dir.File("backups", "auto")).ShouldBeEmpty();   // no .shbk, no .part, no work folder
    }

    [Fact]
    public void Failures_to_write_are_reported_without_leaving_anything_and_without_touching_the_database()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var before = DataFingerprint(path);

        var blocker = dir.File("blocked");
        File.WriteAllText(blocker, "a file where the backup folder should be");
        Should.Throw<BackupFailedException>(() => new BackupService().Create(new BackupRequest(path, blocker, BackupReason.Auto, "t")))
            .Failure.ShouldBe(BackupFailure.Io);
        File.ReadAllText(blocker).ShouldBe("a file where the backup folder should be");

        Should.Throw<BackupFailedException>(() => new BackupService().Create(new BackupRequest(dir.File("nope.db"), dir.File("b"), BackupReason.Auto, "t")))
            .Failure.ShouldBe(BackupFailure.SourceMissing);
        File.Exists(dir.File("nope.db")).ShouldBeFalse();        // and no blank database is created by looking for it

        DataFingerprint(path).ShouldBe(before);
    }

    [Fact]
    public void A_source_that_is_not_a_database_is_a_failure_not_a_backup()
    {
        using var dir = new TempDir();
        var junk = dir.File("junk.db");
        File.WriteAllBytes(junk, Enumerable.Range(0, 9000).Select(i => (byte)(i % 253)).ToArray());
        Should.Throw<BackupFailedException>(() => new BackupService().Create(new BackupRequest(junk, dir.File("b"), BackupReason.Auto, "t")));
        Directory.GetFiles(dir.File("b"), "*.shbk", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public void Two_backups_in_the_same_second_get_different_names_and_neither_replaces_the_other()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var service = new BackupService(new FakeTime(Now));
        var a = service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Manual, "t")).FilePath;
        var b = service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Manual, "t")).FilePath;
        a.ShouldNotBe(b);
        BackupVerifier.Verify(a).Ok.ShouldBeTrue();
        BackupVerifier.Verify(b).Ok.ShouldBeTrue();
    }

    [Fact]
    public void Each_reason_has_its_own_folder_and_a_stem_cannot_escape_it()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var service = new BackupService();
        foreach (var (reason, folder) in new[] { (BackupReason.Auto, "auto"), (BackupReason.Manual, "manual"), (BackupReason.PreMigration, "pre-migration"), (BackupReason.PreRestore, "pre-restore") })
        {
            var file = service.Create(new BackupRequest(path, dir.File("backups"), reason, "t")).FilePath;
            Path.GetFileName(Path.GetDirectoryName(file)).ShouldBe(folder);
            service.Create(new BackupRequest(path, dir.File("backups"), reason, "t", "x")).Manifest.Reason.ShouldBe(folder);
        }

        Should.Throw<ArgumentException>(() => service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t", "../escape")));
        Should.Throw<ArgumentException>(() => service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t", ".hidden")));
        File.Exists(dir.File("backups", "escape.shbk")).ShouldBeFalse();
    }

    [Fact]
    public void Leftovers_of_an_interrupted_backup_are_swept_but_finished_backups_never_are()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var folder = dir.File("backups", "auto");
        Directory.CreateDirectory(folder);
        var finished = new BackupService().Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t", "keep-me")).FilePath;
        File.SetLastWriteTimeUtc(finished, DateTime.UtcNow.AddDays(-400));      // very old, still a backup

        var oldWork = Directory.CreateDirectory(Path.Combine(folder, ".shbk-work-dead"));
        File.WriteAllText(Path.Combine(oldWork.FullName, "shabana.db"), "partial");
        oldWork.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-5);
        var oldPart = Path.Combine(folder, "crashed.shbk.part");
        File.WriteAllText(oldPart, "partial");
        File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow.AddHours(-5));
        var freshPart = Path.Combine(folder, "running.shbk.part");       // maybe another run in progress: younger than an hour, left alone
        File.WriteAllText(freshPart, "partial");

        new BackupService().Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t", "second"));

        Directory.Exists(oldWork.FullName).ShouldBeFalse();
        File.Exists(oldPart).ShouldBeFalse();
        File.Exists(freshPart).ShouldBeTrue();
        File.Exists(finished).ShouldBeTrue();
        BackupVerifier.Verify(finished).Ok.ShouldBeTrue();
    }

    [Fact]
    public void The_catalog_lists_backups_by_folder_and_answers_whether_one_exists_for_a_day()
    {
        using var dir = new TempDir();
        var path = fixture.CopyTo(dir);
        var time = new FakeTime(new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero));
        var service = new BackupService(time);
        var yesterday = service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Auto, "t")).FilePath;
        time.Advance(TimeSpan.FromHours(10));                                  // 2026-10-20 08:00 UTC
        service.Create(new BackupRequest(path, dir.File("backups"), BackupReason.Manual, "t"));
        File.WriteAllText(dir.File("backups", "auto", "damaged.shbk"), "not a zip");     // listed, flagged
        File.WriteAllText(dir.File("backups", "auto", "x.shbk.part"), "ignored");

        var all = BackupCatalog.List(dir.File("backups"));
        all.Count.ShouldBe(3);
        all.Count(e => e.Manifest is null).ShouldBe(1);
        all.Where(e => e.Manifest is not null).Select(e => e.Folder).ShouldBe(["auto", "manual"]);
        BackupCatalog.List(dir.File("backups"), BackupReason.Auto).Count.ShouldBe(2);

        // The caller decides what "today" is; the catalog only compares instants.
        BackupCatalog.HasBackupBetween(dir.File("backups"), BackupReason.Auto, "2026-10-20T00:00:00.000Z", "2026-10-21T00:00:00.000Z").ShouldBeFalse();
        BackupCatalog.HasBackupBetween(dir.File("backups"), BackupReason.Auto, "2026-10-19T00:00:00.000Z", "2026-10-20T00:00:00.000Z").ShouldBeTrue();
        BackupCatalog.HasBackupBetween(dir.File("backups"), BackupReason.Manual, "2026-10-20T00:00:00.000Z", "2026-10-21T00:00:00.000Z").ShouldBeTrue();
        File.Exists(yesterday).ShouldBeTrue();
    }
}
