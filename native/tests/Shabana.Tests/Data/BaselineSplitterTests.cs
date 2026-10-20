using Shabana.Data.Maintenance;
using Shabana.Data.Migrations;

namespace Shabana.Tests.Data;

/// <summary>04 section 8.1: Phase 1 = tables and reference data, Phase 2 = triggers and views.</summary>
public class BaselineSplitterTests
{
    [Fact]
    public void The_split_is_complete_and_phase_two_holds_exactly_the_108_triggers_and_13_views()
    {
        var split = BaselineSplitter.SplitEmbedded();
        split.Phase2Objects.Count(o => o.Kind == "TRIGGER").ShouldBe(108);
        split.Phase2Objects.Count(o => o.Kind == "VIEW").ShouldBe(13);
        split.Phase2Objects.Select(o => o.Name).Distinct().Count().ShouldBe(121);

        // Phase 1 alone: 36 tables, all the indexes and the reference data -- and not one trigger or view.
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        db.Open();
        SqlScript.Execute(db, split.Phase1);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").ShouldBe(36);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL").ShouldBe(61);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('trigger', 'view')").ShouldBe(0);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM permission_keys").ShouldBe(38);
        SqlScript.Scalar<int>(db, "SELECT COUNT(*) FROM app_meta WHERE key = 'schema_version'").ShouldBe(1);
        split.Phase2.ShouldNotContain("CREATE TABLE");
    }

    [Fact]
    public void Phase_one_then_phase_two_builds_exactly_the_same_schema_as_the_whole_baseline()
    {
        using var whole = SqlScript.CreateBaselineDatabase();
        using var parts = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        parts.Open();
        var split = BaselineSplitter.SplitEmbedded();
        SqlScript.Execute(parts, split.Phase1);
        SqlScript.Execute(parts, split.Phase2);
        DatabaseFingerprint.Schema(parts).ShouldBe(DatabaseFingerprint.Schema(whole));
        DatabaseFingerprint.Data(parts).ShouldBe(DatabaseFingerprint.Data(whole));
    }

    [Fact]
    public void The_idempotent_phase_two_repairs_missing_triggers_and_views_without_touching_a_single_row()
    {
        using var db = SqlScript.CreateBaselineDatabase();
        SqlScript.Execute(db, EmbeddedSql.Seed());
        SqlScript.Execute(db, "INSERT INTO contacts (id, type, name) VALUES ('c', 'client', 'C')");
        var schema = DatabaseFingerprint.Schema(db);
        var data = DatabaseFingerprint.Data(db);

        SqlScript.Execute(db, "DROP TRIGGER trg_cash_movements_floor; DROP VIEW v_stock_balance; DROP TRIGGER trg_audit_contacts_ins");
        DatabaseFingerprint.Schema(db).ShouldNotBe(schema);

        var repair = BaselineSplitter.SplitEmbedded().Phase2Idempotent();
        SqlScript.Execute(db, repair);
        SqlScript.Execute(db, repair);      // idempotent

        DatabaseFingerprint.Schema(db).ShouldBe(schema);
        DatabaseFingerprint.Data(db).ShouldBe(data);
    }

    [Fact]
    public void A_crlf_checkout_splits_the_same_way()
    {
        var lf = EmbeddedSql.Baseline();
        var a = BaselineSplitter.Split(lf);
        var b = BaselineSplitter.Split(lf.Replace("\n", "\r\n", StringComparison.Ordinal));
        b.Phase1.ShouldBe(a.Phase1);
        b.Phase2.ShouldBe(a.Phase2);
    }

    [Fact]
    public void A_baseline_without_exactly_one_marker_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => BaselineSplitter.Split("CREATE TABLE a (x);"));
        Should.Throw<InvalidOperationException>(() => BaselineSplitter.Split("-- >>> PHASE 1 ENDS HERE\n-- >>> PHASE 1 ENDS HERE\n"));
    }

    [Fact]
    public void The_statement_splitter_understands_strings_comments_and_trigger_bodies()
    {
        var statements = SqlStatementSplitter.Split("""
            -- a; comment
            CREATE TABLE t (a TEXT DEFAULT 'x;y', b TEXT); /* c; d */
            CREATE TRIGGER trg AFTER INSERT ON t
            BEGIN
              UPDATE t SET b = CASE WHEN new.a = 'q' THEN 'w' ELSE 'e' END;
              UPDATE t SET a = "col;umn" WHERE 1;
            END;
            SELECT 1
            """);
        statements.Count.ShouldBe(3);
        statements[1].ShouldContain("ELSE 'e' END;");
        statements[1].ShouldEndWith("END;");
        statements[2].ShouldBe("SELECT 1");
    }
}
