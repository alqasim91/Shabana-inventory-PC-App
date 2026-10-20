using Shabana.Data.Migrations;

namespace Shabana.Tests.Data;

/// <summary>Migration chains for tests. v1 is the EMBEDDED baseline; the later scripts are written here, the way a real one would be.</summary>
internal static class TestMigrations
{
    public static string Baseline => EmbeddedSql.Baseline();

    /// <summary>One statement of the baseline, by what it starts with (leading comments ignored).</summary>
    public static string BaselineStatement(string startsWith) =>
        SqlStatementSplitter.Split(Baseline).First(s => StripComments(s).StartsWith(startsWith, StringComparison.OrdinalIgnoreCase));

    private static string StripComments(string statement)
    {
        var lines = statement.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        return string.Join('\n', lines).TrimStart();
    }

    /// <summary>0002: additive change (new column, table, index, rows) and the schema_version mirror.</summary>
    public const string V2Additive = """
        ALTER TABLE sites ADD COLUMN note TEXT;
        CREATE TABLE feature_flags (key TEXT NOT NULL PRIMARY KEY, enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1))) STRICT, WITHOUT ROWID;
        INSERT INTO feature_flags (key, enabled) VALUES ('beta', 0);
        CREATE INDEX contacts_name_idx ON contacts(name);
        UPDATE app_meta SET value = '2' WHERE key = 'schema_version';
        """;

    /// <summary>
    /// 0003: the 12-step rebuild of 03 section 6 row 9 on a table that other tables, triggers and views refer to
    /// (expense_categories gets a column). Needs foreign_keys=OFF (the runner's) and legacy_alter_table=ON (this script's own).
    /// </summary>
    public static string V3Rebuild()
    {
        var create = BaselineStatement("CREATE TABLE expense_categories (")
            .Replace("CREATE TABLE expense_categories (", "CREATE TABLE expense_categories_new (", StringComparison.Ordinal);
        var close = create.LastIndexOf(") STRICT", StringComparison.Ordinal);
        create = string.Concat(create.AsSpan(0, close), ", note TEXT\n) STRICT;");
        var triggers = string.Join('\n', BaselineSplitter.SplitEmbedded().Phase2Objects
            .Where(o => o.Name.StartsWith("trg_audit_expense_categories", StringComparison.Ordinal)).Select(o => o.Sql));

        return $"""
            PRAGMA legacy_alter_table = ON;
            {create}
            INSERT INTO expense_categories_new (id, name_ar, sort, active, created_by, created_at)
              SELECT id, name_ar, sort, active, created_by, created_at FROM expense_categories;
            DROP TABLE expense_categories;
            ALTER TABLE expense_categories_new RENAME TO expense_categories;
            CREATE UNIQUE INDEX expense_categories_name_idx ON expense_categories(name_ar);
            {triggers}
            UPDATE app_meta SET value = '3' WHERE key = 'schema_version';
            """;
    }

    public static IReadOnlyList<Migration> Chain(params (string Name, string Sql)[] scripts) => MigrationSet.Create(scripts);

    public static IReadOnlyList<Migration> V1() => Chain(("0001_baseline.sql", Baseline));

    public static IReadOnlyList<Migration> V1To2() => Chain(("0001_baseline.sql", Baseline), ("0002_additive.sql", V2Additive));

    public static IReadOnlyList<Migration> V1To3() => Chain(("0001_baseline.sql", Baseline), ("0002_additive.sql", V2Additive), ("0003_rebuild.sql", V3Rebuild()));

    /// <summary>A third script that does real work and then fails; <paramref name="tail"/> is the failing part.</summary>
    public static IReadOnlyList<Migration> WithFailing(string tail, string name = "0003_failing.sql") =>
        Chain(("0001_baseline.sql", Baseline), ("0002_additive.sql", V2Additive), (name, tail));
}
