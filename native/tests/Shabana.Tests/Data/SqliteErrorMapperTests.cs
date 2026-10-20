using Microsoft.Data.Sqlite;
using Shabana.Core.Errors;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>P2: RAISE(ABORT, 'CODE|...') becomes a <see cref="DomainException"/> carrying the <see cref="ErrorCode"/> of 03 section 9.</summary>
public class SqliteErrorMapperTests
{
    private const int ConstraintTrigger = 1811;

    [Fact]
    public void Every_error_code_of_the_schema_maps_back_to_its_enum_member()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var ex = new SqliteException($"SQLite Error 19: '{code.Code()}|نص عربي'.", 19, ConstraintTrigger);
            var mapped = SqliteErrorMapper.Classify(ex);
            mapped.Kind.ShouldBe(DbErrorKind.Domain, code.ToString());
            mapped.Code.ShouldBe(code);
            var thrown = SqliteErrorMapper.ToException(ex).ShouldBeOfType<DomainException>();
            thrown.Code.ShouldBe(code);
            thrown.Message.ShouldBe(code.Message());
        }
    }

    [Fact]
    public void Real_schema_rules_arrive_as_domain_errors_with_their_code()
    {
        using var db = SqlScript.CreateBaselineDatabase();
        SqlScript.Execute(db, "INSERT INTO sites (id, name_ar) VALUES ('s', 'S'); INSERT INTO items (id, name_ar, unit_type) VALUES ('i', 'I', 'kg')");

        var ex = Should.Throw<SqliteException>(() => SqlScript.Execute(db, "INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s', 'i', -1, 'adjustment')"));
        var mapped = SqliteErrorMapper.Classify(ex);
        mapped.Kind.ShouldBe(DbErrorKind.Domain);
        mapped.Code.ShouldBe(ErrorCode.StockFloor);
        mapped.Detail.ShouldNotBeNullOrWhiteSpace();   // the text after '|'
    }

    [Fact]
    public void Constraint_failures_that_are_not_schema_rules_are_classified_but_not_turned_into_domain_errors()
    {
        using var db = SqlScript.CreateBaselineDatabase();
        SqlScript.Execute(db, "INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('u1', 'dup', 'x', 'staff', '$argon2id$x', 0)");

        DbErrorKind Kind(string sql)
        {
            var ex = Should.Throw<SqliteException>(() => SqlScript.Execute(db, sql));
            var mapped = SqliteErrorMapper.Classify(ex);
            mapped.Code.ShouldBeNull();
            SqliteErrorMapper.ToException(ex).ShouldBeSameAs(ex);
            return mapped.Kind;
        }

        Kind("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('u2', 'dup', 'x', 'staff', '$argon2id$x', 0)").ShouldBe(DbErrorKind.Unique);
        Kind("INSERT INTO sites (id, name_ar) VALUES ('s', '   ')").ShouldBe(DbErrorKind.Check);
        Kind("INSERT INTO contact_phones (contact_id, phone) VALUES ('nobody', '1')").ShouldBe(DbErrorKind.ForeignKey);
        Kind("INSERT INTO items (id, name_ar, unit_type) VALUES ('i', NULL, 'kg')").ShouldBe(DbErrorKind.NotNull);
    }

    [Fact]
    public void Text_that_merely_looks_like_a_code_is_not_mistaken_for_one()
    {
        // A note typed by a user: 'STOCK_FLOOR|x' inside a CHECK failure message is still just a CHECK failure.
        var check = new SqliteException("SQLite Error 19: 'CHECK constraint failed: note = 'STOCK_FLOOR|x''.", 19, 275);
        SqliteErrorMapper.Classify(check).Kind.ShouldBe(DbErrorKind.Check);
        SqliteErrorMapper.Classify(check).Code.ShouldBeNull();

        // A trigger message with a code the enum does not know is a generic constraint failure, never a wrong Arabic message.
        var unknown = new SqliteException("SQLite Error 19: 'SOMETHING_NEW|text'.", 19, ConstraintTrigger);
        SqliteErrorMapper.Classify(unknown).Kind.ShouldBe(DbErrorKind.OtherConstraint);
    }

    [Theory]
    [InlineData(5, DbErrorKind.Busy)]
    [InlineData(6, DbErrorKind.Locked)]
    [InlineData(8, DbErrorKind.ReadOnly)]
    [InlineData(10, DbErrorKind.Io)]
    [InlineData(11, DbErrorKind.Corrupt)]
    [InlineData(13, DbErrorKind.DiskFull)]
    [InlineData(26, DbErrorKind.NotADatabase)]
    [InlineData(1, DbErrorKind.Other)]
    public void Environmental_failures_are_classified_for_the_start_up_and_backup_screens(int result, DbErrorKind expected)
    {
        SqliteErrorMapper.Classify(new SqliteException("x", result)).Kind.ShouldBe(expected);
    }

    [Fact]
    public void Foreign_keys_and_checks_are_not_domain_errors_through_the_unit_of_work_either()
    {
        using var dir = new TempDir();
        var db = TestDatabase.Create(dir);
        var ex = Should.Throw<AggregateException>(() => db.Uow.RunAsync(null, tx =>
        {
            tx.Execute("INSERT INTO contact_phones (contact_id, phone) VALUES ('nobody', '1')");
            return Task.CompletedTask;
        }).Wait());
        ex.InnerException.ShouldBeOfType<SqliteException>();
    }
}
