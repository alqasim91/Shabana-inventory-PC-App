using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Data.Backup;

/// <summary>
/// Independent check of a <c>.shbk</c>: the archive has exactly the two expected entries, the manifest is well formed, the database
/// inside has the recorded SHA-256, opens, passes <c>PRAGMA integrity_check</c>, has the recorded schema version and the recorded
/// row count in EVERY table (04 section 9.2). Used right after creating a backup, before a restore, and by the backup list.
/// </summary>
public static class BackupVerifier
{
    public const string DatabaseEntry = "shabana.db";
    public const string ManifestEntry = "manifest.json";
    private const int MaxManifestBytes = 1 << 20;

    /// <summary>Checks the archive using a scratch file next to it, which is always removed.</summary>
    public static BackupVerification Verify(string shbkPath)
    {
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shbkPath))!, ".shbk-verify-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            return VerifyAndExtract(shbkPath, scratch);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>
    /// Verifies the archive and leaves the verified database at <paramref name="extractTo"/> (which must not exist: an existing file
    /// is never overwritten). On any failure the extracted file is removed.
    /// </summary>
    public static BackupVerification VerifyAndExtract(string shbkPath, string extractTo)
    {
        if (File.Exists(extractTo))
        {
            throw new IOException($"'{extractTo}' already exists; refusing to overwrite it.");
        }

        var created = false;
        try
        {
            using var zip = ZipFile.OpenRead(shbkPath);
            if (zip.Entries.Count != 2)
            {
                return Fail("The archive must contain exactly shabana.db and manifest.json.");
            }

            var manifestEntry = zip.GetEntry(ManifestEntry);
            var dbEntry = zip.GetEntry(DatabaseEntry);
            if (manifestEntry is null || dbEntry is null)
            {
                return Fail("The archive must contain exactly shabana.db and manifest.json.");
            }

            if (manifestEntry.Length is <= 0 or > MaxManifestBytes)
            {
                return Fail("manifest.json has an invalid size.");
            }

            BackupManifest? manifest;
            using (var ms = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(ms, BackupManifest.Json);
            }

            if (manifest is null || manifest.FormatVersion != BackupManifest.CurrentFormat)
            {
                return Fail("Unsupported or unreadable manifest.json.");
            }

            if (manifest.Sha256.Length != 64 || manifest.DbBytes <= 0 || manifest.DbBytes != dbEntry.Length)
            {
                return Fail("The manifest does not describe the database entry (size or hash missing).");
            }

            // Extract with the hash computed on the way; never write more than the manifest promises. A problem is recorded
            // and acted on AFTER the streams are closed (Windows cannot delete a file that is still open).
            string? problem = null;
            using (var input = dbEntry.Open())
            using (var output = new FileStream(extractTo, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                created = true;
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > manifest.DbBytes)
                    {
                        problem = "The database entry is larger than the manifest says.";
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }

                if (problem is null)
                {
                    output.Flush(true);
                    if (total != manifest.DbBytes)
                    {
                        problem = "The database entry is shorter than the manifest says.";
                    }
                    else if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        problem = "SHA-256 of the database does not match the manifest (the file is damaged).";
                    }
                }
            }

            if (problem is not null)
            {
                return Fail(problem, extractTo);
            }

            var inner = CheckDatabase(extractTo, manifest);
            if (inner is not null)
            {
                return Fail(inner, extractTo);
            }

            return new BackupVerification(true, manifest, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or SqliteException or NotSupportedException)
        {
            if (created)
            {
                DeleteScratch(extractTo);
            }

            return new BackupVerification(false, null, ex.GetType().Name + ": " + ex.Message);
        }

        BackupVerification Fail(string reason, string? remove = null)
        {
            if (remove is not null)
            {
                DeleteScratch(remove);
            }

            return new BackupVerification(false, null, reason);
        }
    }

    /// <summary>Opens the extracted database read-only and compares it with the manifest. Null = good.</summary>
    private static string? CheckDatabase(string path, BackupManifest manifest)
    {
        using var connection = new SqliteConnection(ConnectionFactory.ConnectionString(path, OpenMode.ReadOnly, pooling: false));
        connection.Open();
        var problems = DatabaseChecks.IntegrityCheck(connection);
        if (problems.Count > 0)
        {
            return "integrity_check failed: " + string.Join("; ", problems.Take(3));
        }

        var version = DatabaseChecks.UserVersion(connection);
        if (version != manifest.SchemaVersion)
        {
            return $"schema version {version} does not match the manifest ({manifest.SchemaVersion}).";
        }

        var counts = DatabaseChecks.RowCounts(connection);
        if (counts.Count != manifest.RowCounts.Count)
        {
            return "the set of tables does not match the manifest.";
        }

        foreach (var (table, count) in counts)
        {
            if (!manifest.RowCounts.TryGetValue(table, out var expected) || expected != count)
            {
                return $"row count of '{table}' is {count}, the manifest says {(manifest.RowCounts.TryGetValue(table, out var e) ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(absent)")}.";
            }
        }

        return null;
    }

    /// <summary>Deletes a scratch database and any SQLite side files. Only ever called on files this class created.</summary>
    internal static void DeleteScratch(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A scratch file that cannot be removed now is swept by the next backup (BackupService.SweepStaleTemporaries).
            }
        }
    }
}
