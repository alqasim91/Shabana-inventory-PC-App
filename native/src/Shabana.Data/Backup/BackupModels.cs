using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shabana.Data.Backup;

/// <summary>Why a backup was taken; also the sub-folder of the backup root it is written to (04 section 5.1).</summary>
public enum BackupReason
{
    Auto,
    Manual,
    PreMigration,
    PreRestore,
}

public static class BackupLocations
{
    public static string FolderName(BackupReason reason) => reason switch
    {
        BackupReason.Auto => "auto",
        BackupReason.Manual => "manual",
        BackupReason.PreMigration => "pre-migration",
        BackupReason.PreRestore => "pre-restore",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static string FolderFor(string backupRoot, BackupReason reason) => Path.Combine(backupRoot, FolderName(reason));
}

/// <summary><c>manifest.json</c> inside a <c>.shbk</c> (04 section 9.2).</summary>
public sealed record BackupManifest
{
    public const int CurrentFormat = 1;

    public int FormatVersion { get; init; } = CurrentFormat;

    public string AppVersion { get; init; } = "";

    /// <summary><c>PRAGMA user_version</c> of the database in the archive.</summary>
    public int SchemaVersion { get; init; }

    /// <summary><c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (the same shape as every instant in the database).</summary>
    public string CreatedAtUtc { get; init; } = "";

    /// <summary>auto | manual | pre-migration | pre-restore.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Lower-case hex SHA-256 of <c>shabana.db</c> as stored in the archive.</summary>
    public string Sha256 { get; init; } = "";

    public long DbBytes { get; init; }

    /// <summary>Row count of every user table at the moment of the snapshot.</summary>
    public SortedDictionary<string, long> RowCounts { get; init; } = [];

    public string? BusinessName { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <param name="DatabasePath">The live database (read through the online backup API; never copied as a file).</param>
/// <param name="BackupRoot">Root folder; the file goes to <c>BackupRoot\{auto|manual|pre-migration|pre-restore}\</c>.</param>
/// <param name="FileStem">Optional file name without extension, e.g. <c>pre-v1-to-v2-20261020-101500</c>.</param>
public sealed record BackupRequest(string DatabasePath, string BackupRoot, BackupReason Reason, string AppVersion, string? FileStem = null);

public sealed record BackupResult(string FilePath, BackupManifest Manifest, long FileBytes);

/// <summary>The outcome of checking a <c>.shbk</c>. Never thrown: a damaged file is an ordinary answer, not an exception.</summary>
public sealed record BackupVerification(bool Ok, BackupManifest? Manifest, string? Failure);

public enum BackupFailure
{
    SourceMissing,
    SourceUnreadable,
    DiskSpace,
    Io,
    VerificationFailed,
}

/// <summary>A backup could not be created OR could not be verified. In both cases no file remains at the final name.</summary>
public sealed class BackupFailedException : Exception
{
    public BackupFailedException(BackupFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    public BackupFailure Failure { get; }
}
