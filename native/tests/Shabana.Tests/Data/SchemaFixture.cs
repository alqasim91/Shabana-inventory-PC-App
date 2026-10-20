using Dapper;
using Microsoft.Data.Sqlite;
using Shabana.Core.Errors;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>An in-memory database built from the EMBEDDED baseline, with a small shop in it and helpers to say what a test means.</summary>
internal sealed class Fx : IDisposable
{
    public Fx(bool recursiveTriggers)
    {
        Db = SqlScript.CreateBaselineDatabase();
        SqlScript.Execute(Db, "PRAGMA recursive_triggers = " + (recursiveTriggers ? "ON" : "OFF"));
        Exec("INSERT INTO sites (id, name_ar) VALUES ('s1', 'S1'), ('s2', 'S2')");
        Exec("INSERT INTO items (id, name_ar, unit_type, sale_price_piasters) VALUES ('i1', 'I1', 'unit', 1000), ('i2', 'I2', 'kg', 2000), ('i3', 'I3', 'unit', 500)");
        Exec("INSERT INTO contacts (id, type, name) VALUES ('cl', 'client', 'Client'), ('vd', 'vendor', 'Vendor'), ('bo', 'both', 'Both')");
        Exec("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('adm', 'adm', 'Admin', 'admin', '$argon2id$v=19$m=1,t=1,p=1$c2FsdA$aGFzaA', 0), ('stf', 'stf', 'Staff', 'staff', '$argon2id$v=19$m=1,t=1,p=1$c2FsdA$aGFzaA', 0)");
    }

    public SqliteConnection Db { get; }

    public void Exec(string sql, object? p = null) => Db.Execute(sql, p);

    public T Q<T>(string sql, object? p = null) => Db.QuerySingle<T>(sql, p);

    public long Count(string table, string where = "1 = 1") => Q<long>($"SELECT COUNT(*) FROM {table} WHERE {where}");

    /// <summary>The statement must be refused by the schema; returns the error code it carries.</summary>
    public ErrorCode Rejected(string sql, object? p = null)
    {
        try
        {
            Db.Execute(sql, p);
        }
        catch (SqliteException ex)
        {
            var mapped = SqliteErrorMapper.Classify(ex);
            mapped.Kind.ShouldBe(DbErrorKind.Domain, "not a schema rule: " + ex.Message);
            return mapped.Code!.Value;
        }

        throw new Shouldly.ShouldAssertException("The statement was expected to be rejected but succeeded: " + sql);
    }

    public void Rejects(ErrorCode expected, string sql, object? p = null) => Rejected(sql, p).ShouldBe(expected, sql);

    /// <summary>The statement must fail with a plain constraint (CHECK, UNIQUE, NOT NULL, ...) or type error rather than a rule code.</summary>
    public DbErrorKind RejectedKind(string sql, object? p = null)
    {
        try
        {
            Db.Execute(sql, p);
        }
        catch (SqliteException ex)
        {
            var mapped = SqliteErrorMapper.Classify(ex);
            mapped.Code.ShouldBeNull(ex.Message);
            return mapped.Kind;
        }

        throw new Shouldly.ShouldAssertException("The statement was expected to be rejected but succeeded: " + sql);
    }

    // ---- shorthand for building situations -------------------------------------------------------------------------------------------

    public void Po(string id, long kgMilli, long pricePerKgPiasters, string date = "2026-03-01", string vendor = "vd") =>
        Exec("INSERT INTO purchase_orders (id, vendor_id, po_type, order_date, total_kg_milli, price_per_kg_piasters, total_amount_piasters) VALUES (@id, @vendor, 'general', @date, @kg, @price, @total)",
            new { id, vendor, date, kg = kgMilli, price = pricePerKgPiasters, total = (kgMilli * pricePerKgPiasters + 500) / 1000 });

    public void ItemizedPo(string id, string date = "2026-03-01") =>
        Exec("INSERT INTO purchase_orders (id, vendor_id, po_type, order_date, total_amount_piasters) VALUES (@id, 'vd', 'itemized', @date, 0)", new { id, date });

    public void PoLine(string id, string po, string item, long qtyMilli, long price) =>
        Exec("INSERT INTO po_lines (id, po_id, item_id, qty_milli, unit_price_piasters, line_total_piasters) VALUES (@id, @po, @item, @q, @p, @t)",
            new { id, po, item, q = qtyMilli, p = price, t = (qtyMilli * price + 500) / 1000 });

    public void Conv(string id, string po, string site, string item, long kgMilli, long qtyMilli, string unit = "unit") =>
        Exec("INSERT INTO po_conversions (id, po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit) VALUES (@id, @po, @site, @item, @kg, @q, @unit)",
            new { id, po, site, item, kg = kgMilli, q = qtyMilli, unit });

    public long Stock(string site, string item) =>
        Q<long>("SELECT COALESCE(SUM(qty_delta_milli), 0) FROM stock_movements WHERE site_id = @site AND item_id = @item", new { site, item });

    public void AddStock(string site, string item, long qtyMilli) =>
        Exec("INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, note) VALUES (@site, @item, @q, 'adjustment', 'test')", new { site, item, q = qtyMilli });

    public long Cash(string site) => Q<long>("SELECT COALESCE(SUM(amount_delta_piasters), 0) FROM cash_movements WHERE site_id = @site", new { site });

    public void Fund(string site, long piasters, string reason = "deposit") =>
        Exec("INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason) VALUES (@site, @a, 'manual', @reason)", new { site, a = piasters, reason });

    public void So(string id, string site = "s1", string client = "cl", string date = "2026-03-01") =>
        Exec("INSERT INTO sales_orders (id, site_id, client_id, order_date) VALUES (@id, @site, @client, @date)", new { id, site, client, date });

    public void Line(string so, string item, long qtyMilli, long price, string? id = null) =>
        Exec("INSERT INTO sales_order_lines (id, so_id, item_id, qty_milli, unit_price_piasters) VALUES (@id, @so, @item, @q, @p)",
            new { id = id ?? Guid.NewGuid().ToString("N"), so, item, q = qtyMilli, p = price });

    public void Status(string so, string status) => Exec("UPDATE sales_orders SET status = @status WHERE id = @so", new { so, status });

    public string StatusOf(string so) => Q<string>("SELECT status FROM sales_orders WHERE id = @so", new { so });

    public long Total(string so) => Q<long>("SELECT total_amount_piasters FROM sales_orders WHERE id = @so", new { so });

    /// <summary>An invoiced order with one line (qty x price piasters) at the given branch.</summary>
    public void InvoicedSo(string id, long qtyMilli = 10000, long price = 1000, string item = "i1", string site = "s1")
    {
        So(id, site);
        Line(id, item, qtyMilli, price, id + "-line");
        Status(id, "invoiced");
    }

    public void Pay(string type, string parent, long piasters, string method = "cash", string? site = null, string? id = null, string? paidAt = null) =>
        Exec("INSERT INTO payments (id, parent_type, parent_id, amount_piasters, method, site_id, paid_at) VALUES (@id, @type, @parent, @a, @method, @site, COALESCE(@paidAt, strftime('%Y-%m-%dT%H:%M:%fZ','now')))",
            new { id = id ?? Guid.NewGuid().ToString("N"), type, parent, a = piasters, method, site, paidAt });

    public void Actor(string? userId, bool editing = false) =>
        Exec("UPDATE app_context SET actor_user_id = @userId, editing = @e WHERE id = 1", new { userId, e = editing ? 1 : 0 });

    public void Dispose() => Db.Dispose();
}
