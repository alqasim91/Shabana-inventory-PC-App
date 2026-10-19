using System.Reflection;

namespace Shabana.Data.Migrations;

/// <summary>Access to the SQL scripts embedded in this assembly.</summary>
public static class EmbeddedSql
{
    private const string ScriptsPrefix = "Shabana.Data.Migrations.Scripts.";
    private const string ReferencePrefix = "Shabana.Data.Migrations.Reference.";

    /// <summary>File name of the baseline migration (the whole schema, version 1).</summary>
    public const string BaselineName = "0001_baseline.sql";

    /// <summary>File name of the first-run seed (business settings row, first branch, default expense categories).</summary>
    public const string SeedName = "seed.sql";

    /// <summary>The numbered migration scripts, in strictly increasing (filename) order.</summary>
    public static IReadOnlyList<string> ScriptNames { get; } = typeof(EmbeddedSql).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ScriptsPrefix, StringComparison.Ordinal))
        .Select(n => n[ScriptsPrefix.Length..])
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>The complete baseline schema (<c>0001_baseline.sql</c>).</summary>
    public static string Baseline() => ReadScript(BaselineName);

    /// <summary>First-run seed data; executed once by the first-run flow, never by the migration chain.</summary>
    public static string Seed() => Read(ReferencePrefix + SeedName);

    /// <summary>Text of a numbered migration script.</summary>
    public static string ReadScript(string name) => Read(ScriptsPrefix + name);

    private static string Read(string resourceName)
    {
        using var stream = typeof(EmbeddedSql).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
