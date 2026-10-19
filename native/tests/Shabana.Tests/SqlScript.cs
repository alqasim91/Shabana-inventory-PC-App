using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Shabana.Data.Migrations;

namespace Shabana.Tests;

/// <summary>Helpers to create databases from the embedded baseline and to replay sqlite3-shell scripts.</summary>
internal static class SqlScript
{
    /// <summary>Opens a private in-memory database and applies the EMBEDDED 0001_baseline.sql (the shipped script).</summary>
    public static SqliteConnection CreateBaselineDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, EmbeddedSql.Baseline());
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>One step of a sqlite3-shell script: a SQL statement, or the expectation set by a ".print >>> expecting N error(s)" line.</summary>
    internal sealed record Step(string? Sql, int ExpectedErrorsAfter);

    private static readonly Regex Expecting = new(@"^\.print\s+>>>\s*expecting\s+(\d+)\s+error", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Splits a sqlite3-shell script into statements. Handles '...' strings with '' escapes, -- comments and
    /// dot-commands (.read, .mode, .print ...), which are skipped except the "expecting N errors" marker.
    /// </summary>
    public static List<Step> Parse(string script)
    {
        var steps = new List<Step>();
        var sb = new StringBuilder();
        var i = 0;
        while (i < script.Length)
        {
            var c = script[i];
            var atLineStart = i == 0 || script[i - 1] == '\n';
            if (atLineStart && sb.ToString().Trim().Length == 0 && c == '.')
            {
                var end = script.IndexOf('\n', i);
                var line = (end < 0 ? script[i..] : script[i..end]).TrimEnd('\r');
                var m = Expecting.Match(line);
                if (m.Success)
                {
                    steps.Add(new Step(null, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
                }

                sb.Clear();
                i = end < 0 ? script.Length : end + 1;
                continue;
            }

            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                var end = script.IndexOf('\n', i);
                i = end < 0 ? script.Length : end;
                continue;
            }

            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < script.Length)
                {
                    sb.Append(script[i]);
                    if (script[i] == '\'')
                    {
                        if (i + 1 < script.Length && script[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == ';')
            {
                var sql = sb.ToString().Trim();
                if (sql.Length > 0)
                {
                    steps.Add(new Step(sql + ";", 0));
                }

                sb.Clear();
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        if (sb.ToString().Trim().Length > 0)
        {
            steps.Add(new Step(sb.ToString().Trim(), 0));
        }

        return steps;
    }
}
