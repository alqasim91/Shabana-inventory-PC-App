using System.IO.Compression;
using Shabana.Data.Backup;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>
/// A shop's database in two states: X (what the backup holds, "new" after a restore) and Y (the live database at restore time, "old").
/// Everything is under one temp directory laid out like the real data folder.
/// </summary>
internal sealed class RestoreScenario : IDisposable
{
    private RestoreScenario(TempDir dir, RestorePaths paths, string backupFile, string newFp, string? oldFp, string? oldSha)
    {
        Dir = dir;
        Paths = paths;
        BackupFile = backupFile;
        NewFp = newFp;
        OldFp = oldFp;
        OldSha = oldSha;
    }

    public TempDir Dir { get; }

    public RestorePaths Paths { get; }

    public string Live => Paths.DatabasePath;

    public string BackupFile { get; }

    /// <summary>Fingerprint (data|schema|version) of the database inside the backup.</summary>
    public string NewFp { get; }

    /// <summary>Fingerprint of the live database before the restore; null when there was none.</summary>
    public string? OldFp { get; }

    /// <summary>SHA-256 of the live main file before the restore.</summary>
    public string? OldSha { get; }

    public static RestoreScenario Create(PopulatedV1Fixture fixture, bool liveExists = true)
    {
        var dir = new TempDir();
        var live = dir.File("data", "db", "shabana.db");
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        File.Copy(fixture.Path, live);
        var paths = new RestorePaths(live, dir.File("data", "restore"), dir.File("data", "Backups"));

        var newFp = Fingerprint(live);
        var backup = new BackupService().Create(new BackupRequest(live, paths.BackupRoot, BackupReason.Manual, "t", "the-backup")).FilePath;

        // Move the live database on to state Y.
        var db = TestDatabase.Open(live);
        db.Uow.RunAsync(LedgerStress.Actor, tx =>
        {
            tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('after-the-backup', 'added after the backup')");
            tx.Execute("INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason, created_by) VALUES ('after-the-backup', 12345, 'manual', 'later', @a)", new { a = LedgerStress.Actor });
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        ConnectionFactory.ReleaseFileHandles();

        if (!liveExists)
        {
            DeleteSet(live);
            return new RestoreScenario(dir, paths, backup, newFp, null, null);
        }

        // The application is closed: nothing holds the database, the WAL is folded in.
        using (var c = new ConnectionFactory(live).OpenForInspection())
        {
            SqlScript.Execute(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }

        return new RestoreScenario(dir, paths, backup, newFp, Fingerprint(live), DatabaseChecks.FileSha256(live));
    }

    public static string Fingerprint(string path)
    {
        using var c = new ConnectionFactory(path).OpenForInspection();
        return DatabaseFingerprint.Data(c) + "|" + DatabaseFingerprint.Schema(c) + "|" + DatabaseChecks.UserVersion(c);
    }

    public RestoreService Service(RestoreOptions? options = null, IBackupService? backups = null, int schemaVersion = 1) =>
        new(Paths, backups ?? new BackupService(), "t", schemaVersion, options);

    public static void DeleteSet(string live)
    {
        foreach (var suffix in RestorePaths.FileSetSuffixes)
        {
            File.Delete(live + suffix);
        }
    }

    /// <summary>The newest verified pre-restore backup, extracted and fingerprinted.</summary>
    public string? PreRestoreBackupFingerprint()
    {
        var folder = Path.Combine(Paths.BackupRoot, "pre-restore");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var file = Directory.GetFiles(folder, "*.shbk").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        if (file is null)
        {
            return null;
        }

        BackupVerifier.Verify(file).Ok.ShouldBeTrue("the pre-restore backup must itself verify");
        var extracted = Dir.File("pre-restore-extract-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var zip = ZipFile.OpenRead(file))
        {
            zip.GetEntry("shabana.db")!.ExtractToFile(extracted);
        }

        return Fingerprint(extracted);
    }

    /// <summary>Names of everything in the live database's folder.</summary>
    public string[] DbFolderFiles() =>
        Directory.GetFileSystemEntries(Path.GetDirectoryName(Live)!).Select(Path.GetFileName).Cast<string>().Order().ToArray();

    public void Dispose() => Dir.Dispose();
}
