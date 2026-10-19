using Shabana.Data.Migrations;

namespace Shabana.Tests;

/// <summary>
/// P0 acceptance (3): sqlite/smoke-test.sql, executed through the app's own SQLite against the EMBEDDED baseline
/// (so the shipped script is the tested script, 04 section 12.1), must PASS entirely.
/// The sqlite3 shell prints "Runtime error" for the expected rejections; here every statement must succeed
/// except exactly those announced by ".print >>> expecting N error(s)", which must fail.
/// </summary>
public class SmokeScriptTests
{
    [Fact]
    public void Smoke_test_script_passes_every_check_and_raises_exactly_the_announced_errors()
    {
        using var db = SqlScript.CreateBaselineDatabase();
        var steps = SqlScript.Parse(File.ReadAllText(TestPaths.Fixture("smoke-test.sql")));

        var pending = 0;
        var announced = 0;
        var raised = 0;
        foreach (var step in steps)
        {
            if (step.Sql is null)
            {
                pending = step.ExpectedErrorsAfter;
                announced += step.ExpectedErrorsAfter;
                continue;
            }

            try
            {
                SqlScript.Execute(db, step.Sql);
                pending.ShouldBe(0, $"statement was expected to be rejected but succeeded: {step.Sql}");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                pending.ShouldBeGreaterThan(0, $"unexpected SQLite error '{ex.Message}' for: {step.Sql}");
                pending--;
                raised++;
            }
        }

        pending.ShouldBe(0);
        raised.ShouldBe(announced);
        announced.ShouldBeGreaterThan(0);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, ok FROM smoke ORDER BY rowid";
        var results = new List<(string Name, long Ok)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        results.Count.ShouldBeGreaterThanOrEqualTo(10);
        var failed = results.Where(r => r.Ok != 1).Select(r => r.Name).ToList();
        failed.ShouldBeEmpty();
    }

    [Fact]
    public void Smoke_fixture_is_identical_to_native_conversion_copy()
    {
        var source = TestPaths.NativeConversion("sqlite", "smoke-test.sql");
        if (source is null)
        {
            return;
        }

        File.ReadAllText(TestPaths.Fixture("smoke-test.sql")).ShouldBe(File.ReadAllText(source));
    }

    [Fact]
    public void Parser_handles_comments_dot_commands_and_quoted_semicolons()
    {
        var steps = SqlScript.Parse("""
            .read schema.sql
            -- a comment; with a semicolon
            SELECT 'a;b''c' ; -- trailing; comment
            .print >>> expecting 2 errors
            SELECT 2;
            """);
        steps.Select(s => s.Sql).ShouldBe(["SELECT 'a;b''c';", null, "SELECT 2;"]);
        steps[1].ExpectedErrorsAfter.ShouldBe(2);
    }
}
