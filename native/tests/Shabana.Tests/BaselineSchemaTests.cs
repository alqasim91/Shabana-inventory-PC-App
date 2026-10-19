using Microsoft.Data.Sqlite;
using Shabana.Data.Migrations;
using Shabana.Data.Sqlite;

namespace Shabana.Tests;

/// <summary>P0 acceptance (3): the embedded 0001_baseline.sql builds the database 03/04 describe.</summary>
public class BaselineSchemaTests
{
    [Fact]
    public void Embedded_baseline_is_the_only_numbered_script_so_far()
    {
        EmbeddedSql.ScriptNames.ShouldBe([EmbeddedSql.BaselineName]);
    }

    [Fact]
    public void Baseline_is_byte_identical_to_native_conversion_schema_sql()
    {
        var source = TestPaths.NativeConversion("sqlite", "schema.sql");
        if (source is null)
        {
            return; // the docs folder was removed after the cut-over; the checksum test (P2) takes over
        }

        EmbeddedSql.Baseline().ShouldBe(File.ReadAllText(source));
    }

    [Fact]
    public void Baseline_keeps_the_phase_split_marker_exactly_once()
    {
        // BaselineSplitter (P2) depends on this marker (04 section 8.1).
        var text = EmbeddedSql.Baseline();
        System.Text.RegularExpressions.Regex.Matches(text, ">>> PHASE 1 ENDS HERE").Count.ShouldBe(1);
    }

    [Fact]
    public void Sqlite_catalog_matches_schema_sql_counts()
    {
        using var db = SqlScript.CreateBaselineDatabase();

        int Count(string where) => SqlScript.Scalar<int>(db, $"SELECT COUNT(*) FROM sqlite_master WHERE {where}");

        // Hand-verified against schema.sql (python sqlite3 3.45.1): 36 / 13 / 108 / 61.
        Count("type = 'table' AND name NOT LIKE 'sqlite_%'").ShouldBe(36);
        Count("type = 'view'").ShouldBe(13);
        Count("type = 'trigger'").ShouldBe(108);

        // 61 = explicit CREATE INDEX statements. SQLite additionally keeps 33 automatic
        // sqlite_autoindex_* entries (PRIMARY KEY / UNIQUE) that have sql IS NULL; they are not "indexes" in schema.sql.
        Count("type = 'index' AND sql IS NOT NULL").ShouldBe(61);
        Count("type = 'index' AND sql IS NULL").ShouldBe(33);
    }

    [Fact]
    public void Baseline_sets_user_version_1_and_passes_integrity_and_fk_checks()
    {
        using var db = SqlScript.CreateBaselineDatabase();
        SqlScript.Scalar<int>(db, "PRAGMA user_version").ShouldBe(1);
        SqlScript.Scalar<string>(db, "PRAGMA integrity_check").ShouldBe("ok");
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM pragma_foreign_key_check").ShouldBe(0);
    }

    [Fact]
    public void Baseline_reference_data_is_present()
    {
        using var db = SqlScript.CreateBaselineDatabase();
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM permission_keys").ShouldBe(38);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM role_presets WHERE role = 'manager' AND perm = 'purchases.edit'").ShouldBe(0);
    }

    [Fact]
    public void Sqlite_engine_is_at_least_the_minimum_named_in_04()
    {
        SqliteRuntime.MinimumVersion.ShouldBe(new Version(3, 38, 0));
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        var actual = SqliteRuntime.AssertSupported(db);
        actual.ShouldBeGreaterThanOrEqualTo(SqliteRuntime.MinimumVersion);
    }

    [Fact]
    public void Seed_applies_after_the_baseline_and_is_idempotent()
    {
        using var db = SqlScript.CreateBaselineDatabase();
        var seed = EmbeddedSql.Seed();
        SqlScript.Execute(db, seed);
        SqlScript.Execute(db, seed);

        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM sites").ShouldBe(1);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM business_settings").ShouldBe(1);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM expense_categories").ShouldBe(8);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM users WHERE password_hash IS NULL").ShouldBe(1);
    }

    [Fact]
    public void Seed_is_byte_identical_to_native_conversion_seed_sql()
    {
        var source = TestPaths.NativeConversion("sqlite", "seed.sql");
        if (source is null)
        {
            return;
        }

        EmbeddedSql.Seed().ShouldBe(File.ReadAllText(source));
    }
}
