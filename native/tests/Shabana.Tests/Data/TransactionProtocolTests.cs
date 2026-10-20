using Microsoft.Data.Sqlite;
using Shabana.Core.Errors;
using Shabana.Data.Maintenance;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>
/// P2 acceptance (1): the transaction protocol of 03 section 9 / 04 section 7.2 — BEGIN IMMEDIATE, app_context written INSIDE the
/// transaction, rolled back with it. "An error inside a tx leaves app_context and data unchanged."
/// </summary>
public class TransactionProtocolTests
{
    private const string Admin = "u-admin";

    private static async Task<TestDatabase> NewDatabaseAsync(TempDir dir)
    {
        var db = TestDatabase.Create(dir);
        await db.Uow.RunAsync(null, tx =>
        {
            tx.Execute("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES (@id, 'admin.x', 'x', 'admin', '$argon2id$v=19$m=1,t=1,p=1$c$h', 0)", new { id = Admin });
            tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('s1', 'S1')");
            tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i1', 'I1', 'unit')");
            return Task.CompletedTask;
        });
        return db;
    }

    private static (string Data, string Schema, long Version, string? Actor, long Editing) State(TestDatabase db)
    {
        using var c = db.Factory.OpenExisting();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT actor_user_id, editing FROM app_context";
        using var r = cmd.ExecuteReader();
        r.Read();
        var actor = r.IsDBNull(0) ? null : r.GetString(0);
        var editing = r.GetInt64(1);
        r.Close();
        return (DatabaseFingerprint.Data(c), DatabaseFingerprint.Schema(c), DatabaseChecks.UserVersion(c), actor, editing);
    }

    [Fact]
    public async Task A_schema_rule_violation_inside_a_transaction_leaves_app_context_and_data_unchanged()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        var before = State(db);

        var ex = await Should.ThrowAsync<DomainException>(() => db.Uow.RunAsync(Admin, tx =>
        {
            // Valid writes first (an audited item, a stock movement) ...
            tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i2', 'I2', 'kg')");
            tx.Execute("INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s1', 'i1', 5000, 'adjustment')");
            // ... the actor is visible to triggers while the transaction is open ...
            tx.ExecuteScalar<string>("SELECT actor_user_id FROM app_context WHERE id = 1").ShouldBe(Admin);
            // ... then a rule violation: stock cannot go negative.
            tx.Execute("INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s1', 'i1', -9000, 'adjustment')");
            return Task.CompletedTask;
        }));

        ex.Code.ShouldBe(ErrorCode.StockFloor);
        ex.Message.ShouldBe(ErrorCode.StockFloor.Message());   // the Arabic text, not a stack trace
        State(db).ShouldBe(before);
        db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 'i2'").ShouldBe(0);
        db.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity_id = 'i2'").ShouldBe(0);
    }

    [Fact]
    public async Task A_non_schema_exception_inside_a_transaction_also_leaves_everything_unchanged()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        var before = State(db);

        await Should.ThrowAsync<InvalidOperationException>(() => db.Uow.RunAsync(Admin, tx =>
        {
            tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i3', 'I3', 'kg')");
            tx.SetEditing(true);
            throw new InvalidOperationException("the recipe blew up while the edit window was open");
        }));

        State(db).ShouldBe(before);
    }

    [Fact]
    public async Task A_cancelled_operation_rolls_back()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        var before = State(db);
        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => db.Uow.RunAsync(Admin, async tx =>
        {
            tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i4', 'I4', 'kg')");
            await cts.CancelAsync();
        }, cts.Token));

        State(db).ShouldBe(before);
        await Should.ThrowAsync<OperationCanceledException>(() => db.Uow.RunAsync(Admin, _ => Task.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task A_successful_transaction_attributes_its_audit_rows_to_the_actor_and_leaves_app_context_neutral()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);

        await db.Uow.RunAsync(Admin, tx =>
        {
            tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i5', 'I5', 'kg')");
            return Task.CompletedTask;
        });

        db.Scalar<string>("SELECT actor FROM audit_log WHERE entity = 'items' AND entity_id = 'i5'").ShouldBe(Admin);
        var (_, _, _, actor, editing) = State(db);
        actor.ShouldBeNull();      // nothing outside a unit of work is attributed to the last actor
        editing.ShouldBe(0);
    }

    [Fact]
    public async Task The_actor_of_one_operation_never_leaks_into_the_next()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        await db.Uow.RunAsync(Admin, tx => { tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i6', 'I6', 'kg')"); return Task.CompletedTask; });
        await db.Uow.RunAsync(null, tx => { tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i7', 'I7', 'kg')"); return Task.CompletedTask; });
        db.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity_id = 'i7' AND actor IS NULL").ShouldBe(1);
    }

    [Fact]
    public async Task Committing_with_the_edit_window_still_open_is_refused()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        var before = State(db);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => db.Uow.RunAsync(Admin, tx =>
        {
            tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('i8', 'I8', 'kg')");
            tx.SetEditing(true);      // forgot to close the window
            return Task.CompletedTask;
        }));

        ex.Message.ShouldContain("editing");
        State(db).ShouldBe(before);   // and the guard triggers are still armed
    }

    [Fact]
    public async Task The_edit_window_disarms_the_guard_triggers_only_while_it_is_open()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        await db.Uow.RunAsync(Admin, tx =>
        {
            tx.Execute("INSERT INTO contacts (id, type, name) VALUES ('c1', 'client', 'C1')");
            tx.Execute("INSERT INTO sales_orders (id, site_id, client_id) VALUES ('so1', 's1', 'c1')");
            tx.Execute("INSERT INTO sales_order_lines (so_id, item_id, qty_milli, unit_price_piasters) VALUES ('so1', 'i1', 1000, 100)");
            tx.Execute("UPDATE sales_orders SET status = 'invoiced' WHERE id = 'so1'");
            return Task.CompletedTask;
        });

        // Closed window: lines of an invoiced order are locked.
        var locked = await Should.ThrowAsync<DomainException>(() => db.Uow.RunAsync(Admin, tx =>
        {
            tx.Execute("INSERT INTO sales_order_lines (so_id, item_id, qty_milli, unit_price_piasters) VALUES ('so1', 'i1', 1000, 100)");
            return Task.CompletedTask;
        }));
        locked.Code.ShouldBe(ErrorCode.SoLinesLocked);

        // Open window (R5-R7): allowed, and the unit of work commits because the recipe closed it again.
        await db.Uow.RunAsync(Admin, tx =>
        {
            tx.SetEditing(true);
            tx.Execute("INSERT INTO sales_order_lines (so_id, item_id, qty_milli, unit_price_piasters) VALUES ('so1', 'i1', 1000, 100)");
            tx.SetEditing(false);
            return Task.CompletedTask;
        });
        db.Scalar<long>("SELECT COUNT(*) FROM sales_order_lines WHERE so_id = 'so1'").ShouldBe(2);
        State(db).Editing.ShouldBe(0);
    }

    [Fact]
    public async Task The_transaction_is_BEGIN_IMMEDIATE_so_a_second_writer_is_turned_away_before_it_writes()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        var other = new ConnectionFactory(db.Path, new ConnectionOptions { Pooling = false, BusyTimeoutMilliseconds = 0, Synchronous = SynchronousMode.Off });

        await db.Uow.RunAsync(Admin, tx =>
        {
            // The unit of work has done NO write yet beyond the app_context row, but IMMEDIATE already holds the write lock.
            using var second = other.OpenExisting();
            var busy = Should.Throw<SqliteException>(() =>
            {
                using var cmd = second.CreateCommand();
                cmd.CommandText = "BEGIN IMMEDIATE";
                cmd.ExecuteNonQuery();
            });
            busy.SqliteErrorCode.ShouldBe(5);   // SQLITE_BUSY
            SqliteErrorMapper.Classify(busy).Kind.ShouldBe(DbErrorKind.Busy);

            // A reader is not blocked (WAL).
            using var reader = other.OpenExisting();
            using var read = reader.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM sites";
            Convert.ToInt32(read.ExecuteScalar()).ShouldBe(1);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Concurrent_units_of_work_are_serialised_and_never_interleave()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        var log = new List<string>();

        async Task Writer(string name)
        {
            await db.Uow.RunAsync(Admin, async tx =>
            {
                lock (log) { log.Add(name + ":begin"); }
                tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES (@id, 'x', 'kg')", new { id = name });
                await Task.Delay(80);
                lock (log) { log.Add(name + ":end"); }
            });
        }

        await Task.WhenAll(Writer("w1"), Writer("w2"), Writer("w3"));
        log.Count.ShouldBe(6);
        for (var i = 0; i < log.Count; i += 2)
        {
            log[i].Replace(":begin", "", StringComparison.Ordinal).ShouldBe(log[i + 1].Replace(":end", "", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_unit_of_work_started_from_inside_another_is_refused_instead_of_deadlocking()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        await Should.ThrowAsync<InvalidOperationException>(() => db.Uow.RunAsync(Admin, tx => db.Uow.RunAsync(Admin, _ => Task.CompletedTask)));
    }

    [Fact]
    public async Task Reads_see_one_snapshot_cannot_write_and_leave_the_pooled_connection_writable()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);

        var counts = await db.Uow.ReadAsync(async read =>
        {
            var first = read.QuerySingle<long>("SELECT COUNT(*) FROM items");
            // A write commits on another connection while this read transaction is open ...
            await db.Uow.RunAsync(Admin, tx => { tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('late', 'x', 'kg')"); return Task.CompletedTask; });
            var second = read.QuerySingle<long>("SELECT COUNT(*) FROM items");
            return (first, second);
        });
        counts.first.ShouldBe(counts.second);   // ... but this reader still sees its snapshot

        var write = await Should.ThrowAsync<SqliteException>(() => db.Uow.ReadAsync(read =>
        {
            read.Connection.CreateCommand().ExecuteNonQuery();
            using var cmd = read.Connection.CreateCommand();
            cmd.Transaction = read.Transaction;
            cmd.CommandText = "INSERT INTO items (id, name_ar, unit_type) VALUES ('nope', 'x', 'kg')";
            cmd.ExecuteNonQuery();
            return Task.FromResult(0);
        }));
        write.SqliteErrorCode.ShouldBe(8);   // SQLITE_READONLY

        // The (pooled) connection is writable again for the next unit of work.
        await db.Uow.RunAsync(Admin, tx => { tx.Execute("INSERT INTO items (id, name_ar, unit_type) VALUES ('after', 'x', 'kg')"); return Task.CompletedTask; });
        db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 'after'").ShouldBe(1);
        db.Scalar<long>("SELECT COUNT(*) FROM items WHERE id = 'nope'").ShouldBe(0);
    }

    [Fact]
    public async Task A_database_file_that_disappears_is_an_error_not_a_reason_to_write_into_a_new_blank_one()
    {
        using var dir = new TempDir();
        var db = await NewDatabaseAsync(dir);
        ConnectionFactory.ReleaseFileHandles();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(db.Path + suffix);
        }

        await Should.ThrowAsync<SqliteException>(() => db.Uow.RunAsync(Admin, tx => { tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('x', 'x')"); return Task.CompletedTask; }));
        File.Exists(db.Path).ShouldBeFalse();
    }
}
