using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shabana.Data.Backup;

/// <summary>Where a restore reads, writes and journals. Every file the restore touches is DERIVED from these three paths, never from the journal's contents.</summary>
public sealed partial class RestorePaths
{
    /// <param name="databasePath">The live database, e.g. <c>...\db\shabana.db</c>.</param>
    /// <param name="restoreFolder">Holds <c>swap.json</c>.</param>
    /// <param name="backupRoot">Backup root; the pre-restore backup and a kept damaged database go to <c>pre-restore\</c> below it.</param>
    public RestorePaths(string databasePath, string restoreFolder, string backupRoot)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        RestoreFolder = Path.GetFullPath(restoreFolder);
        BackupRoot = Path.GetFullPath(backupRoot);
    }

    public string DatabasePath { get; }

    public string RestoreFolder { get; }

    public string BackupRoot { get; }

    public string JournalPath => Path.Combine(RestoreFolder, "swap.json");

    public string JournalTempPath => JournalPath + ".tmp";

    // The staging files sit NEXT TO the live database so that every move of the swap is a same-directory rename (atomic).
    public string CandidatePath => DatabasePath + ".restore-candidate";

    public string AsidePath => DatabasePath + ".restore-previous";

    public string RejectedPath => DatabasePath + ".restore-rejected";

    /// <summary>The main file and the SQLite side files that belong to it; they always move together.</summary>
    public static readonly string[] FileSetSuffixes = ["", "-wal", "-shm", "-journal"];

    public string DamagedFolder(string id)
    {
        if (!JournalId().IsMatch(id))
        {
            throw new InvalidDataException("Invalid swap id.");
        }

        return Path.Combine(BackupRoot, "pre-restore", "damaged-" + id);
    }

    [GeneratedRegex("^[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    internal static partial Regex JournalId();
}

public enum SwapPhase
{
    /// <summary>The swap may be partly done. Recovery ROLLS BACK to the old database.</summary>
    Swapping,

    /// <summary>The restored database is validated and final. Recovery ROLLS FORWARD (removes what is left of the old one).</summary>
    Committed,
}

/// <summary>
/// <c>restore\swap.json</c>: the record that makes a restore crash-safe. Written (temp file, fsync, atomic rename) BEFORE the first file is
/// moved and removed only after the swap is finished or undone. It states intent and the SHA-256 of both databases; recovery decides what
/// to do from this plus the facts on disk (which files exist), so a crash between any two steps ends fully old or fully new.
/// </summary>
public sealed record SwapJournal
{
    public int FormatVersion { get; init; } = 1;

    /// <summary>12 lower-case hex characters.</summary>
    public string Id { get; init; } = "";

    public SwapPhase Phase { get; init; }

    /// <summary>False when there was no database file before the restore (a lost database being replaced).</summary>
    public bool OldExisted { get; init; }

    /// <summary>SHA-256 of the old main database file, taken after a TRUNCATE checkpoint with every connection closed.</summary>
    public string? OldSha256 { get; init; }

    public string CandidateSha256 { get; init; } = "";

    /// <summary>The old database could not be backed up because it is damaged: it is kept, not deleted, when the restore is final.</summary>
    public bool KeepOldAsDamaged { get; init; }

    public string? PreRestoreBackup { get; init; }

    public string CreatedAtUtc { get; init; } = "";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Atomic write: temp file + fsync + rename. <paramref name="afterTemp"/> is a hook for crash tests.</summary>
    public static void Write(RestorePaths paths, SwapJournal journal, Action? afterTemp = null)
    {
        Directory.CreateDirectory(paths.RestoreFolder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        using (var stream = new FileStream(paths.JournalTempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        afterTemp?.Invoke();
        if (File.Exists(paths.JournalPath))
        {
            File.Replace(paths.JournalTempPath, paths.JournalPath, null);
        }
        else
        {
            File.Move(paths.JournalTempPath, paths.JournalPath);
        }
    }

    /// <summary>The journal, null when there is none, or throws <see cref="InvalidDataException"/> when it exists but cannot be trusted.</summary>
    public static SwapJournal? Read(RestorePaths paths)
    {
        if (!File.Exists(paths.JournalPath))
        {
            return null;
        }

        try
        {
            var journal = JsonSerializer.Deserialize<SwapJournal>(File.ReadAllBytes(paths.JournalPath), Json);
            if (journal is null || journal.FormatVersion != 1 || !RestorePaths.JournalId().IsMatch(journal.Id)
                || journal.CandidateSha256.Length != 64 || !Enum.IsDefined(journal.Phase))
            {
                throw new InvalidDataException("swap.json is not a valid swap journal.");
            }

            return journal;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("swap.json is not valid JSON.", ex);
        }
    }
}
