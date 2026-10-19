namespace Shabana.Tests;

/// <summary>Locates the repository folders from the test output directory.</summary>
internal static class TestPaths
{
    /// <summary>The <c>native/</c> folder (the one holding Shabana.sln).</summary>
    public static string NativeDir { get; } = FindUp("Shabana.sln");

    /// <summary>The repository root, or null if the layout is not the expected one.</summary>
    public static string? RepoRoot { get; } = Directory.GetParent(NativeDir)?.FullName;

    /// <summary>Path of a doc-source file under native-conversion/ if the folder exists, else null.</summary>
    public static string? NativeConversion(params string[] relative)
    {
        if (RepoRoot is null)
        {
            return null;
        }

        var path = Path.Combine([RepoRoot, "native-conversion", .. relative]);
        return File.Exists(path) ? path : null;
    }

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string FindUp(string marker)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, marker)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not find '{marker}' above {AppContext.BaseDirectory}.");
    }
}
