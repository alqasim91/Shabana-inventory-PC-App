using System.Text;
using System.Text.RegularExpressions;

namespace Shabana.Data.Migrations;

/// <summary>A trigger or view of the baseline's second phase.</summary>
public sealed record SchemaObject(string Kind, string Name, string Sql);

/// <summary>The baseline cut in two: PHASE 1 = tables, indexes and reference data; PHASE 2 = triggers and views (04 section 8.1).</summary>
public sealed partial class BaselineSplit
{
    internal BaselineSplit(string phase1, string phase2)
    {
        Phase1 = phase1;
        Phase2 = phase2;
    }

    /// <summary>Tables, indexes and reference data: what a database needs before it is filled row by row (salvage, 04 section 9.4).</summary>
    public string Phase1 { get; }

    /// <summary>Triggers and views only.</summary>
    public string Phase2 { get; }

    /// <summary>The CREATE TRIGGER / CREATE VIEW statements of phase 2, in file order.</summary>
    public IReadOnlyList<SchemaObject> Phase2Objects => _objects ??= ParseObjects(Phase2);

    private IReadOnlyList<SchemaObject>? _objects;

    /// <summary>
    /// Phase 2 as an idempotent script: <c>DROP ... IF EXISTS</c> before every <c>CREATE</c>. Safe to run on a database that lacks
    /// some of the triggers or views (repair, 04 section 9.4); it never touches a table, so no ledger row can change.
    /// </summary>
    public string Phase2Idempotent()
    {
        var sb = new StringBuilder();
        foreach (var o in Phase2Objects)
        {
            sb.Append("DROP ").Append(o.Kind).Append(" IF EXISTS \"").Append(o.Name).Append("\";\n");
            sb.Append(o.Sql).Append('\n');
        }

        return sb.ToString();
    }

    internal static bool IsInsert(string statement) => LeadingInsert().IsMatch(statement);

    [GeneratedRegex(@"^(?:\s|--[^\n]*(?:\n|$)|/\*.*?\*/)*INSERT\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LeadingInsert();

    private static List<SchemaObject> ParseObjects(string phase2)
    {
        var list = new List<SchemaObject>();
        foreach (var statement in SqlStatementSplitter.Split(phase2))
        {
            var m = ObjectHeader().Match(statement);
            if (!m.Success)
            {
                throw new InvalidOperationException("Phase 2 of the baseline must hold only CREATE TRIGGER / CREATE VIEW statements; found: "
                    + statement[..Math.Min(80, statement.Length)]);
            }

            var sql = statement.EndsWith(';') ? statement : statement + ";";
            list.Add(new SchemaObject(m.Groups["kind"].Value.ToUpperInvariant(), m.Groups["name"].Value.Trim('"', '`', '[', ']'), sql));
        }

        return list;
    }

    [GeneratedRegex(@"^(?:\s|--[^\n]*(?:\n|$)|/\*.*?\*/)*CREATE\s+(?:TEMP\s+|TEMPORARY\s+)?(?<kind>TRIGGER|VIEW)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>""[^""]+""|`[^`]+`|\[[^\]]+\]|\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ObjectHeader();
}

/// <summary>Cuts <c>0001_baseline.sql</c> at the line holding the phase marker. The file must contain the marker exactly once.</summary>
public static partial class BaselineSplitter
{
    public const string Marker = ">>> PHASE 1 ENDS HERE";

    [GeneratedRegex(@"^--[ \t]*>>> PHASE 1 ENDS HERE[^\n]*\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLine();

    public static BaselineSplit SplitEmbedded() => Split(EmbeddedSql.Baseline());

    public static BaselineSplit Split(string baselineScript)
    {
        ArgumentNullException.ThrowIfNull(baselineScript);
        var text = baselineScript.Replace("\r\n", "\n");   // CRLF checkout: the regex anchors on '\n'
        var matches = MarkerLine().Matches(text);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"The baseline must contain the line '-- {Marker}' exactly once; found {matches.Count}.");
        }

        var m = matches[0];
        var before = text[..m.Index];
        var after = text[(m.Index + m.Length)..];

        // schema.sql puts its marker BEFORE the reference-data section (section 10), although the marker's own text says phase 1 includes
        // reference data. Assign the statements after it by KIND: INSERT -> phase 1 (reference data), CREATE TRIGGER / VIEW -> phase 2.
        var phase1 = new StringBuilder(before);
        var phase2 = new StringBuilder();
        foreach (var statement in SqlStatementSplitter.Split(after))
        {
            var sql = statement.EndsWith(';') ? statement : statement + ";";
            if (BaselineSplit.IsInsert(statement))
            {
                phase1.Append(sql).Append('\n');
            }
            else
            {
                phase2.Append(sql).Append('\n');     // validated as TRIGGER / VIEW when the objects are parsed
            }
        }

        var split = new BaselineSplit(phase1.ToString(), phase2.ToString());
        _ = split.Phase2Objects;     // fail now, not later, if phase 2 holds anything but triggers and views
        return split;
    }
}
