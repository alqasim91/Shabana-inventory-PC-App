namespace Shabana.Data;

/// <summary>
/// Where things live on disk (04 section 5.1). <c>C:\ProgramData\ShabanaInventory\</c> in production
/// (<see cref="Environment.SpecialFolder.CommonApplicationData"/>); tests pass a temporary root.
/// </summary>
public sealed class DataLocations
{
    public DataLocations(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The production root: <c>%ProgramData%\ShabanaInventory</c>.</summary>
    public static DataLocations Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ShabanaInventory"));

    public string Root { get; }

    public string DatabaseFolder => Path.Combine(Root, "db");

    public string DatabasePath => Path.Combine(DatabaseFolder, "shabana.db");

    /// <summary>Default backup root; the owner may change it in Settings (stored in config\machine.json by a later phase).</summary>
    public string DefaultBackupRoot => Path.Combine(Root, "Backups");

    /// <summary>Holds the restore swap journal (<c>swap.json</c>).</summary>
    public string RestoreFolder => Path.Combine(Root, "restore");

    public string LogsFolder => Path.Combine(Root, "logs");

    public string ConfigFolder => Path.Combine(Root, "config");
}
