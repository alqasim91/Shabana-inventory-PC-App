using System.IO.Compression;
using System.Text.Json;

namespace Shabana.Data.Backup;

/// <param name="FilePath">The <c>.shbk</c>.</param>
/// <param name="Folder">The reason folder it sits in (auto, manual, pre-migration, pre-restore).</param>
/// <param name="Manifest">Null when the archive or its manifest cannot be read (the list shows it as damaged).</param>
public sealed record BackupEntry(string FilePath, string Folder, long Bytes, BackupManifest? Manifest);

/// <summary>
/// What is in the backup folders, read cheaply (only <c>manifest.json</c> of each archive, no database extraction) for the backup list and for the
/// start-up policy "is there already a successful automatic backup for today" (04 section 9.2). A file under its final <c>.shbk</c> name exists only
/// because it passed verification when it was made; <see cref="BackupVerifier"/> re-checks one on demand.
/// </summary>
public static class BackupCatalog
{
    public static IReadOnlyList<BackupEntry> List(string backupRoot, BackupReason? only = null)
    {
        var entries = new List<BackupEntry>();
        foreach (var reason in Enum.GetValues<BackupReason>())
        {
            if (only is not null && only != reason)
            {
                continue;
            }

            var folder = BackupLocations.FolderFor(backupRoot, reason);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.shbk"))
            {
                entries.Add(new BackupEntry(file, BackupLocations.FolderName(reason), new FileInfo(file).Length, TryReadManifest(file)));
            }
        }

        return entries.OrderBy(e => e.Manifest?.CreatedAtUtc ?? "", StringComparer.Ordinal).ThenBy(e => e.FilePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// True when a readable backup of <paramref name="reason"/> was created in <c>[fromUtc, toUtc)</c> (both <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> text, as in the
    /// manifest). The caller passes the business day's UTC bounds from <c>BusinessCalendar</c>: the day boundary is not this library's business.
    /// </summary>
    public static bool HasBackupBetween(string backupRoot, BackupReason reason, string fromUtc, string toUtc) =>
        List(backupRoot, reason).Any(e => e.Manifest is { } m
            && string.CompareOrdinal(m.CreatedAtUtc, fromUtc) >= 0 && string.CompareOrdinal(m.CreatedAtUtc, toUtc) < 0);

    public static BackupManifest? TryReadManifest(string shbkPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(shbkPath);
            var entry = zip.GetEntry(BackupVerifier.ManifestEntry);
            if (entry is null || entry.Length is <= 0 or > (1 << 20))
            {
                return null;
            }

            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, BackupManifest.Json);
            return manifest is { FormatVersion: BackupManifest.CurrentFormat } ? manifest : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
