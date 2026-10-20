using System.Globalization;
using System.Text.RegularExpressions;

namespace Shabana.Data.Migrations;

/// <summary>One numbered migration script. <see cref="Version"/> is the <c>PRAGMA user_version</c> the database has AFTER the script ran.</summary>
public sealed partial record Migration(int Version, string Name, string Sql)
{
    /// <summary>
    /// A script that legitimately changes balances (a data fix) says so with this comment line; every other script must leave the
    /// balance fingerprint unchanged (04 section 8.2 step 4).
    /// </summary>
    public const string BalancesMayChangeMarker = "shabana:balances-may-change";

    public bool BalancesMayChange => BalancesMarker().IsMatch(Sql);

    [GeneratedRegex(@"^[ \t]*--[ \t]*shabana:balances-may-change[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BalancesMarker();
}

public static partial class MigrationSet
{
    /// <summary>The scripts embedded in this assembly, validated.</summary>
    public static IReadOnlyList<Migration> Embedded() =>
        Create(EmbeddedSql.ScriptNames.Select(n => (n, EmbeddedSql.ReadScript(n))));

    /// <summary>Builds and validates a chain from <c>(file name, sql)</c> pairs; names are <c>NNNN_description.sql</c>.</summary>
    public static IReadOnlyList<Migration> Create(IEnumerable<(string Name, string Sql)> scripts)
    {
        var list = new List<Migration>();
        foreach (var (name, sql) in scripts.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var m = FileName().Match(name);
            if (!m.Success)
            {
                throw new InvalidOperationException($"Migration '{name}' must be named NNNN_description.sql.");
            }

            list.Add(new Migration(int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), name, sql.Replace("\r\n", "\n")));
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException("There is no migration script.");
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Migration versions must be 1, 2, 3, ... without gaps or repeats; '{list[i].Name}' is at position {i + 1}.");
            }
        }

        return list;
    }

    [GeneratedRegex(@"^(?<n>\d{4})_[A-Za-z0-9_.\-]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();
}

/// <summary>Splits a migration into statements and refuses the ones a migration may never contain.</summary>
public static class MigrationScripts
{
    private static readonly string[] ForbiddenFirstWords = ["BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE", "VACUUM", "ATTACH", "DETACH"];

    /// <exception cref="InvalidOperationException">The script controls transactions, vacuums or attaches another database.</exception>
    public static IReadOnlyList<string> Statements(Migration migration)
    {
        var statements = SqlStatementSplitter.Split(migration.Sql);
        foreach (var statement in statements)
        {
            var word = FirstWord(statement);
            if (ForbiddenFirstWords.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Migration '{migration.Name}' contains a {word} statement. The runner owns the transaction; a script must not start, end, vacuum or attach.");
            }
        }

        return statements;
    }

    private static string FirstWord(string statement)
    {
        var i = 0;
        while (i < statement.Length)
        {
            if (char.IsWhiteSpace(statement[i]))
            {
                i++;
            }
            else if (statement[i] == '-' && i + 1 < statement.Length && statement[i + 1] == '-')
            {
                var end = statement.IndexOf('\n', i);
                i = end < 0 ? statement.Length : end;
            }
            else if (statement[i] == '/' && i + 1 < statement.Length && statement[i + 1] == '*')
            {
                var end = statement.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? statement.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        var start = i;
        while (i < statement.Length && char.IsLetter(statement[i]))
        {
            i++;
        }

        return statement[start..i];
    }
}
