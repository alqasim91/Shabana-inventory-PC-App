using System.Globalization;
using Microsoft.Data.Sqlite;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>04 section 5.2: the pragmas applied on EVERY open.</summary>
public class ConnectionFactoryTests
{
    private static string Pragma(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA " + name;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public void Every_open_applies_the_pragmas_of_the_architecture_document()
    {
        using var dir = new TempDir();
        var factory = new ConnectionFactory(dir.File("db", "x.db"));
        using var c = factory.Open();

        Pragma(c, "foreign_keys").ShouldBe("1");
        Pragma(c, "journal_mode").ShouldBe("wal");
        Pragma(c, "synchronous").ShouldBe("2");                    // FULL
        Pragma(c, "busy_timeout").ShouldBe("5000");
        Pragma(c, "recursive_triggers").ShouldBe("0");
        Pragma(c, "wal_autocheckpoint").ShouldBe("1000");
        Pragma(c, "journal_size_limit").ShouldBe("67108864");
        Pragma(c, "temp_store").ShouldBe("2");                     // MEMORY
        Pragma(c, "trusted_schema").ShouldBe("1");
        Pragma(c, "query_only").ShouldBe("0");
    }

    [Fact]
    public void Options_change_only_what_they_name()
    {
        using var dir = new TempDir();
        var factory = new ConnectionFactory(dir.File("x.db"), new ConnectionOptions { RecursiveTriggers = true, Synchronous = SynchronousMode.Normal });
        using var c = factory.Open();
        Pragma(c, "recursive_triggers").ShouldBe("1");
        Pragma(c, "synchronous").ShouldBe("1");
        Pragma(c, "foreign_keys").ShouldBe("1");
        Pragma(c, "journal_mode").ShouldBe("wal");
    }

    [Fact]
    public void A_pooled_connection_gets_the_pragmas_again_when_it_is_reopened()
    {
        using var dir = new TempDir();
        var factory = new ConnectionFactory(dir.File("x.db"));
        using (var first = factory.Open())
        {
            using var cmd = first.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = OFF; PRAGMA query_only = ON; PRAGMA recursive_triggers = ON";
            cmd.ExecuteNonQuery();
        }

        using var second = factory.Open();   // the pool hands back the same native connection
        Pragma(second, "foreign_keys").ShouldBe("1");
        Pragma(second, "query_only").ShouldBe("0");
        Pragma(second, "recursive_triggers").ShouldBe("0");
    }

    [Fact]
    public void Foreign_keys_are_really_enforced()
    {
        using var dir = new TempDir();
        var db = TestDatabase.Create(dir);
        using var c = db.Factory.OpenExisting();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO contact_phones (contact_id, phone) VALUES ('missing', '0100')";
        var ex = Should.Throw<SqliteException>(() => cmd.ExecuteNonQuery());
        SqliteErrorMapper.Classify(ex).Kind.ShouldBe(DbErrorKind.ForeignKey);
    }

    [Fact]
    public void OpenExisting_never_creates_a_blank_database_for_a_mistyped_path()
    {
        using var dir = new TempDir();
        var factory = new ConnectionFactory(dir.File("typo.db"));
        Should.Throw<SqliteException>(() => factory.OpenExisting());
        File.Exists(dir.File("typo.db")).ShouldBeFalse();
    }

    [Fact]
    public void Inspection_does_not_switch_the_journal_mode_or_otherwise_touch_the_file()
    {
        using var dir = new TempDir();
        var path = dir.File("legacy.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 7; CREATE TABLE t (a INTEGER)";
            cmd.ExecuteNonQuery();
        }

        var before = Shabana.Data.Maintenance.DatabaseChecks.FileSha256(path);
        using (var probe = new ConnectionFactory(path).OpenForInspection())
        {
            Pragma(probe, "user_version").ShouldBe("7");
            Pragma(probe, "journal_mode").ShouldBe("delete");     // not converted to WAL
        }

        Shabana.Data.Maintenance.DatabaseChecks.FileSha256(path).ShouldBe(before);
    }

    [Fact]
    public void The_busy_wait_is_bounded_by_the_configured_timeout()
    {
        // Microsoft.Data.Sqlite retries a locked command for "Default Timeout" seconds (30 by default, 0 = forever) whatever
        // PRAGMA busy_timeout says; the factory therefore puts its own value there.
        ConnectionFactory.ConnectionString("x.db", OpenMode.ReadWrite, pooling: true, busyTimeoutMilliseconds: 5000).ShouldContain("Default Timeout=5");
        ConnectionFactory.ConnectionString("x.db", OpenMode.ReadWrite, pooling: true, busyTimeoutMilliseconds: 0).ShouldContain("Default Timeout=1");
    }
}
