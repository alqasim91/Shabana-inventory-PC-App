using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Shabana.Core.Errors;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>Runs one business operation as ONE write transaction with the app_context protocol. Rule violations arrive as <see cref="DomainException"/>.</summary>
internal interface IStressExecutor
{
    Task<bool> RunAsync(string? actor, Func<DbTx, bool> work);
}

/// <summary>The real stack: file database, pooled WAL connections, <see cref="UnitOfWork"/>.</summary>
internal sealed class UowExecutor(UnitOfWork uow) : IStressExecutor
{
    public Task<bool> RunAsync(string? actor, Func<DbTx, bool> work) => uow.RunAsync(actor, tx => Task.FromResult(work(tx)));
}

/// <summary>
/// The same protocol (BEGIN IMMEDIATE, app_context actor, COMMIT or ROLLBACK, error mapping) on ONE in-memory connection holding the EMBEDDED
/// baseline: no file-system calls, so the 48,000 operations of the full run take seconds. The schema logic under test is identical.
/// </summary>
internal sealed class MemoryExecutor : IStressExecutor, IDisposable
{
    public MemoryExecutor(bool recursiveTriggers)
    {
        Connection = SqlScript.CreateBaselineDatabase();
        SqlScript.Execute(Connection, "PRAGMA recursive_triggers = " + (recursiveTriggers ? "ON" : "OFF"));
    }

    public SqliteConnection Connection { get; }

    public Task<bool> RunAsync(string? actor, Func<DbTx, bool> work)
    {
        using var transaction = Connection.BeginTransaction(deferred: false);
        try
        {
            var tx = new DbTx(Connection, transaction);
            tx.Execute("UPDATE app_context SET actor_user_id = @a, editing = 0 WHERE id = 1", new { a = actor });
            var result = work(tx);
            tx.Execute("UPDATE app_context SET actor_user_id = NULL WHERE id = 1");
            transaction.Commit();
            return Task.FromResult(result);
        }
        catch (SqliteException ex)
        {
            transaction.Rollback();
            throw SqliteErrorMapper.ToException(ex);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void Dispose() => Connection.Dispose();
}

/// <summary>Counts of a stress run.</summary>
internal sealed class StressStats
{
    public int Attempted { get; set; }

    public int Applied { get; set; }

    public int Rejected { get; set; }

    public SortedDictionary<string, int> RejectedByCode { get; } = [];

    public SortedDictionary<string, int> AppliedByOp { get; } = [];

    public SortedDictionary<string, long> TicksByOp { get; } = [];

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"attempted={Attempted} applied={Applied} rejected={Rejected} [");
        sb.Append(string.Join(", ", RejectedByCode.Select(p => $"{p.Key}={p.Value}")));
        sb.Append(']');
        return sb.ToString();
    }
}

/// <summary>
/// Seeded random business operations against a real file database through the real <see cref="UnitOfWork"/> (the stress test of 03 section 0,
/// re-created: the original script is not in the repository). Operations: PO / conversion / reversal / SO / line / discount / invoice /
/// place / cancel / pay / delete payment / transfer / adjust / manual cash, plus itemized POs, wallets and expenses. About one in seven is
/// built to be REJECTED by a schema rule; the harness accepts only domain rejections from <see cref="AllowedRejections"/> — any other failure
/// (a CHECK, a foreign key, an unexpected code) fails the run. Ids are chosen by the generator (not by SQLite), so a seed is reproducible.
/// </summary>
internal sealed class LedgerStress
{
    public const string Actor = "u-admin";

    public static readonly HashSet<ErrorCode> AllowedRejections =
    [
        ErrorCode.CashFloor, ErrorCode.CashSiteRequired, ErrorCode.ClientCreditNegative, ErrorCode.ConversionReverseInsufficient,
        ErrorCode.DiscountBelowPaid, ErrorCode.DiscountExceedsSubtotal, ErrorCode.DiscountPercentOver100, ErrorCode.OrderDeleteBlocked,
        ErrorCode.PaymentOverpay, ErrorCode.PlcExceedsRemaining, ErrorCode.PoConversionExceedsRemaining, ErrorCode.SoDraftNoPayment,
        ErrorCode.SoInvoiceNoLines, ErrorCode.SoLinesLocked, ErrorCode.SoStockInsufficient, ErrorCode.SoTransitionIllegal,
        ErrorCode.StockFloor, ErrorCode.TransferStockInsufficient, ErrorCode.VendorCreditNegative, ErrorCode.ExpenseAlreadyVoided,
    ];

    private static readonly string[] Sites = ["site-a", "site-b"];
    private static readonly string[] Items = ["item-1", "item-2", "item-3", "item-4", "item-5", "item-6"];
    private static readonly string[] Clients = ["cl-1", "cl-2", "cl-3", "cl-4", "cl-5", "cl-6"];
    private static readonly string[] Vendors = ["vd-1", "vd-2", "vd-3", "vd-4"];
    private static readonly string[] Methods = ["cash", "instapay", "bank_transfer"];

    private readonly IStressExecutor _exec;
    private readonly Random _rng;
    private int _seq;

    public LedgerStress(IStressExecutor exec, int seed)
    {
        _exec = exec;
        _rng = new Random(seed);
    }

    public StressStats Stats { get; } = new();

    /// <summary>Sites, items, contacts, one user, an expense category and opening cash. Itself one unit of work.</summary>
    public Task SetupAsync() => _exec.RunAsync(null, tx =>
    {
        tx.Execute("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES (@id, 'stress.admin', 'stress', 'admin', '$argon2id$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaA', 0)", new { id = Actor });
        tx.Execute("INSERT INTO sites (id, name_ar) VALUES ('site-a', 'A'), ('site-b', 'B')");
        for (var i = 0; i < Items.Length; i++)
        {
            tx.Execute("INSERT INTO items (id, name_ar, unit_type, sale_price_piasters) VALUES (@id, @n, @u, @p)",
                new { id = Items[i], n = "item " + (i + 1), u = i % 2 == 0 ? "unit" : "kg", p = 1000 + (i * 250) });
        }

        foreach (var c in Clients)
        {
            tx.Execute("INSERT INTO contacts (id, type, name) VALUES (@id, 'client', @id)", new { id = c });
        }

        foreach (var v in Vendors)
        {
            tx.Execute("INSERT INTO contacts (id, type, name) VALUES (@id, 'vendor', @id)", new { id = v });
        }

        tx.Execute("INSERT INTO expense_categories (id, name_ar) VALUES ('cat-1', 'cat')");
        tx.Execute("UPDATE app_context SET actor_user_id = @a WHERE id = 1", new { a = Actor });
        foreach (var s in Sites)
        {
            tx.Execute("INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason, created_by) VALUES (@s, 150000, 'manual', 'opening', @a)", new { s, a = Actor });
        }

        return true;
    });

    public async Task RunAsync(int operations, int checkEvery = 0, Func<Task>? check = null)
    {
        for (var i = 1; i <= operations; i++)
        {
            await OneAsync().ConfigureAwait(false);
            if (checkEvery > 0 && check is not null && i % checkEvery == 0)
            {
                await check().ConfigureAwait(false);
            }
        }
    }

    private async Task OneAsync()
    {
        var roll = _rng.Next(100);
        Stats.Attempted++;
        var (name, work) = Choose(roll);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var did = await _exec.RunAsync(Actor, work).ConfigureAwait(false);
            if (did)
            {
                Stats.Applied++;
                Stats.AppliedByOp[name] = Stats.AppliedByOp.GetValueOrDefault(name) + 1;
            }
        }
        catch (DomainException ex)
        {
            if (!AllowedRejections.Contains(ex.Code))
            {
                throw new InvalidOperationException($"op '{name}' was rejected with unexpected code {ex.Code}", ex);
            }

            Stats.Rejected++;
            Stats.RejectedByCode[ex.Code.ToString()] = Stats.RejectedByCode.GetValueOrDefault(ex.Code.ToString()) + 1;
        }
        finally
        {
            Stats.TicksByOp[name] = Stats.TicksByOp.GetValueOrDefault(name) + (System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    private (string, Func<DbTx, bool>) Choose(int roll) => roll switch
    {
        < 4 => ("po", NewPo),
        < 6 => ("itemized-po", NewItemizedPo),
        < 15 => ("convert", Convert),
        < 18 => ("convert-line", ConvertLine),
        < 20 => ("reverse-conversion", ReverseConversion),
        < 27 => ("so", NewSo),
        < 41 => ("line", AddLine),
        < 46 => ("discount", SetDiscount),
        < 54 => ("invoice", Invoice),
        < 62 => ("place", Place),
        < 65 => ("cancel", CancelPlacement),
        < 79 => ("pay", Pay),
        < 82 => ("delete-payment", DeletePayment),
        < 86 => ("transfer", Transfer),
        < 89 => ("adjust", Adjust),
        < 92 => ("manual-cash", ManualCash),
        < 93 => ("delete-so", DeleteSo),
        < 96 => ("wallet", Wallet),
        _ => ("expense", Expense),
    };

    private T Pick<T>(IReadOnlyList<T> list) => list[_rng.Next(list.Count)];

    private string NextId(string prefix) => prefix + "-" + (++_seq).ToString(CultureInfo.InvariantCulture);

    private string Date() => $"2026-{_rng.Next(1, 13):00}-{_rng.Next(1, 29):00}";

    private string Stamp() => $"2026-{_rng.Next(1, 10):00}-{_rng.Next(1, 29):00}T{_rng.Next(0, 24):00}:{_rng.Next(0, 60):00}:{_rng.Next(0, 60):00}.000Z";

    /// <summary>An existing id from <paramref name="sql"/> most of the time, otherwise null (the caller then aims at a wrong target on purpose).</summary>
    private string? PickRow(DbTx tx, string sql, object? param = null)
    {
        var ids = tx.Query<string>(sql, param).ToList();
        return ids.Count == 0 ? null : Pick(ids);
    }

    private bool NewPo(DbTx tx)
    {
        var kg = _rng.Next(1000, 200_001);
        var price = _rng.Next(500, 5001);
        tx.Execute("INSERT INTO purchase_orders (id, vendor_id, po_type, order_date, total_kg_milli, price_per_kg_piasters, total_amount_piasters, created_by) VALUES (@id, @v, 'general', @d, @kg, @p, @t, @a)",
            new { id = NextId("po"), v = Pick(Vendors), d = Date(), kg, p = price, t = ((long)kg * price + 500) / 1000, a = Actor });
        return true;
    }

    private bool NewItemizedPo(DbTx tx)
    {
        var id = NextId("ipo");
        tx.Execute("INSERT INTO purchase_orders (id, vendor_id, po_type, order_date, total_amount_piasters, created_by) VALUES (@id, @v, 'itemized', @d, 0, @a)",
            new { id, v = Pick(Vendors), d = Date(), a = Actor });
        for (var i = _rng.Next(1, 4); i > 0; i--)
        {
            var qty = _rng.Next(1000, 50_001);
            var price = _rng.Next(100, 5001);
            tx.Execute("INSERT INTO po_lines (id, po_id, item_id, qty_milli, unit_price_piasters, line_total_piasters, created_by) VALUES (@l, @po, @i, @q, @p, @t, @a)",
                new { l = NextId("pol"), po = id, i = Pick(Items), q = qty, p = price, t = ((long)qty * price + 500) / 1000, a = Actor });
        }

        return true;
    }

    private bool Convert(DbTx tx)
    {
        var po = PickRow(tx, "SELECT id FROM purchase_orders WHERE po_type = 'general' AND status = 'open' ORDER BY id") ?? PickRow(tx, "SELECT id FROM purchase_orders ORDER BY id");
        if (po is null)
        {
            return false;
        }

        var remaining = tx.QuerySingle<long?>("SELECT remaining_kg_milli FROM v_po_kg_progress WHERE po_id = @po", new { po }) ?? 1000;
        // Usually a part of what is left; one time in six more than is left (must be refused).
        var kg = _rng.Next(6) == 0 ? remaining + _rng.Next(1, 5000) : Math.Max(1, (long)(remaining * _rng.NextDouble()));
        tx.Execute("INSERT INTO po_conversions (id, po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit, conversion_date, created_by) VALUES (@id, @po, @s, @i, @kg, @q, @u, @d, @a)",
            new { id = NextId("conv"), po, s = Pick(Sites), i = Pick(Items), kg, q = _rng.Next(1, 200_000), u = _rng.Next(2) == 0 ? "kg" : "unit", d = Date(), a = Actor });
        return true;
    }

    private bool ConvertLine(DbTx tx)
    {
        var line = PickRow(tx, "SELECT id FROM po_lines ORDER BY id");
        if (line is null)
        {
            return false;
        }

        var remaining = tx.QuerySingle<long>("SELECT l.qty_milli - COALESCE((SELECT SUM(qty_milli) FROM po_line_conversions WHERE po_line_id = l.id), 0) FROM po_lines l WHERE l.id = @line", new { line });
        var qty = _rng.Next(6) == 0 ? remaining + _rng.Next(1, 3000) : Math.Max(1, (long)(remaining * _rng.NextDouble()));
        tx.Execute("INSERT INTO po_line_conversions (id, po_line_id, site_id, qty_milli, conversion_date, created_by) VALUES (@id, @l, @s, @q, @d, @a)",
            new { id = NextId("plc"), l = line, s = Pick(Sites), q = qty, d = Date(), a = Actor });
        return true;
    }

    private bool ReverseConversion(DbTx tx)
    {
        if (_rng.Next(2) == 0)
        {
            var conv = PickRow(tx, "SELECT id FROM po_conversions ORDER BY id");
            if (conv is null)
            {
                return false;
            }

            tx.Execute("DELETE FROM po_conversions WHERE id = @conv", new { conv });
            return true;
        }

        var plc = PickRow(tx, "SELECT id FROM po_line_conversions ORDER BY id");
        if (plc is null)
        {
            return false;
        }

        tx.Execute("DELETE FROM po_line_conversions WHERE id = @plc", new { plc });
        return true;
    }

    private bool NewSo(DbTx tx)
    {
        tx.Execute("INSERT INTO sales_orders (id, site_id, client_id, order_date, created_by) VALUES (@id, @s, @c, @d, @a)",
            new { id = NextId("so"), s = Pick(Sites), c = Pick(Clients), d = Date(), a = Actor });
        return true;
    }

    private string? SoWithStatus(DbTx tx, string status, int wrongTargetOneIn = 8) =>
        _rng.Next(wrongTargetOneIn) == 0
            ? PickRow(tx, "SELECT id FROM sales_orders ORDER BY id")
            : PickRow(tx, "SELECT id FROM sales_orders WHERE status = @status ORDER BY id", new { status });

    private bool AddLine(DbTx tx)
    {
        var so = SoWithStatus(tx, "draft", 10);
        if (so is null)
        {
            return false;
        }

        tx.Execute("INSERT INTO sales_order_lines (id, so_id, item_id, qty_milli, unit_price_piasters) VALUES (@id, @so, @i, @q, @p)",
            new { id = NextId("line"), so, i = Pick(Items), q = _rng.Next(100, 20_001), p = _rng.Next(100, 10_001) });
        return true;
    }

    private bool SetDiscount(DbTx tx)
    {
        var so = PickRow(tx, "SELECT id FROM sales_orders ORDER BY id");
        if (so is null)
        {
            return false;
        }

        var subtotal = tx.QuerySingle<long>("SELECT subtotal_piasters FROM sales_orders WHERE id = @so", new { so });
        string type;
        long value;
        switch (_rng.Next(4))
        {
            case 0:
                type = "none";
                value = 0;
                break;
            case 1:
                type = "amount";
                value = _rng.Next(5) == 0 ? subtotal + _rng.Next(1, 500) : (long)(subtotal * _rng.NextDouble());
                break;
            default:
                type = "percent";
                value = _rng.Next(8) == 0 ? _rng.Next(10_001, 12_001) : _rng.Next(0, 10_001);
                break;
        }

        tx.Execute("UPDATE sales_orders SET discount_type = @type, discount_value_x100 = @value WHERE id = @so", new { type, value, so });
        return true;
    }

    private bool Invoice(DbTx tx)
    {
        var so = SoWithStatus(tx, "draft");
        if (so is null)
        {
            return false;
        }

        tx.Execute("UPDATE sales_orders SET status = 'invoiced' WHERE id = @so", new { so });
        return true;
    }

    private bool Place(DbTx tx)
    {
        var so = SoWithStatus(tx, "invoiced");
        if (so is null)
        {
            return false;
        }

        tx.Execute("UPDATE sales_orders SET status = 'placed' WHERE id = @so", new { so });
        return true;
    }

    private bool CancelPlacement(DbTx tx)
    {
        var so = SoWithStatus(tx, "placed");
        if (so is null)
        {
            return false;
        }

        tx.Execute("UPDATE sales_orders SET status = 'invoiced' WHERE id = @so", new { so });
        return true;
    }

    private bool Pay(DbTx tx)
    {
        var onSo = _rng.Next(3) != 0;
        string? id = null;
        long remaining = 0;
        if (onSo)
        {
            // Mostly a placed/invoiced order that still owes something; one time in ten ANY order (draft, closed: must be refused).
            for (var attempt = 0; attempt < 4 && id is null; attempt++)
            {
                var candidate = _rng.Next(10) == 0
                    ? PickRow(tx, "SELECT id FROM sales_orders ORDER BY id")
                    : PickRow(tx, "SELECT id FROM sales_orders WHERE status IN ('placed', 'invoiced') ORDER BY id");
                if (candidate is null)
                {
                    break;
                }

                remaining = tx.QuerySingle<long>("SELECT balance_piasters FROM v_so_balance WHERE so_id = @candidate", new { candidate });
                if (remaining > 0)
                {
                    id = candidate;
                }
            }
        }
        else
        {
            for (var attempt = 0; attempt < 4 && id is null; attempt++)
            {
                var candidate = PickRow(tx, "SELECT id FROM purchase_orders ORDER BY id");
                if (candidate is null)
                {
                    break;
                }

                remaining = tx.QuerySingle<long>("SELECT balance_piasters FROM v_po_balance WHERE po_id = @candidate", new { candidate });
                if (remaining > 0)
                {
                    id = candidate;
                }
            }
        }

        if (id is null)
        {
            return false;
        }

        var amount = _rng.Next(8) == 0 ? remaining + _rng.Next(1, 300) : Math.Max(1, (long)(remaining * _rng.NextDouble()));
        var method = Pick(Methods);
        // A purchase-order cash payment needs its drawer; omitted one time in ten (must be refused).
        var site = method == "cash" && (onSo ? _rng.Next(2) == 0 : _rng.Next(10) != 0) ? Pick(Sites) : null;
        tx.Execute("INSERT INTO payments (id, parent_type, parent_id, amount_piasters, method, site_id, paid_at, created_by) VALUES (@pid, @pt, @id, @amount, @method, @site, @at, @a)",
            new { pid = NextId("pay"), pt = onSo ? "so" : "po", id, amount, method, site, at = _rng.Next(2) == 0 ? Stamp() : "2026-10-20T10:00:00.000Z", a = Actor });
        return true;
    }

    private bool DeletePayment(DbTx tx)
    {
        var pay = PickRow(tx, "SELECT id FROM payments ORDER BY id");
        if (pay is null)
        {
            return false;
        }

        tx.Execute("DELETE FROM payments WHERE id = @pay", new { pay });
        return true;
    }

    private bool Transfer(DbTx tx)
    {
        var from = Pick(Sites);
        var to = from == Sites[0] ? Sites[1] : Sites[0];
        var item = Pick(Items);
        var have = tx.QuerySingle<long>("SELECT COALESCE(SUM(qty_delta_milli), 0) FROM stock_movements WHERE site_id = @from AND item_id = @item", new { from, item });
        var qty = _rng.Next(6) == 0 ? have + _rng.Next(1, 2000) : Math.Max(1, (long)(have * _rng.NextDouble()));
        tx.Execute("INSERT INTO stock_transfers (id, from_site, to_site, item_id, qty_milli, created_by) VALUES (@id, @from, @to, @item, @qty, @a)",
            new { id = NextId("tr"), from, to, item, qty, a = Actor });
        return true;
    }

    private bool Adjust(DbTx tx)
    {
        var delta = _rng.Next(-30_000, 60_001);
        if (delta == 0)
        {
            delta = 1;
        }

        tx.Execute("INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type, note, created_by) VALUES (@s, @i, @d, 'adjustment', 'adjust', @a)",
            new { s = Pick(Sites), i = Pick(Items), d = delta, a = Actor });
        return true;
    }

    private bool ManualCash(DbTx tx)
    {
        var delta = _rng.Next(-300_000, 600_001);
        if (delta == 0)
        {
            delta = 1;
        }

        tx.Execute("INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason, created_by) VALUES (@s, @d, 'manual', 'manual', @a)",
            new { s = Pick(Sites), d = delta, a = Actor });
        return true;
    }

    private bool DeleteSo(DbTx tx)
    {
        var so = _rng.Next(3) == 0
            ? PickRow(tx, "SELECT id FROM sales_orders ORDER BY id")
            : PickRow(tx, "SELECT id FROM sales_orders WHERE status IN ('draft', 'invoiced') ORDER BY id");
        if (so is null)
        {
            return false;
        }

        tx.Execute("DELETE FROM sales_orders WHERE id = @so", new { so });
        return true;
    }

    private bool Wallet(DbTx tx)
    {
        var client = _rng.Next(2) == 0;
        var contact = client ? Pick(Clients) : Pick(Vendors);
        var delta = _rng.Next(-20_000, 40_001);
        if (delta == 0)
        {
            delta = 1;
        }

        var table = client ? "client_credits" : "vendor_credits";
        tx.Execute($"INSERT INTO {table} (contact_id, amount_delta_piasters, source_type, occurred_on, created_by) VALUES (@c, @d, @t, @o, @a)",
            new { c = contact, d = delta, t = delta > 0 ? "deposit" : "refund", o = Date(), a = Actor });
        return true;
    }

    private bool Expense(DbTx tx)
    {
        if (_rng.Next(3) == 0)
        {
            var open = PickRow(tx, "SELECT id FROM expenses WHERE voided_at IS NULL ORDER BY id");
            if (open is null)
            {
                return false;
            }

            tx.Execute("UPDATE expenses SET voided_at = strftime('%Y-%m-%dT%H:%M:%fZ','now'), voided_by = @a, void_reason = 'mistake' WHERE id = @id", new { id = open, a = Actor });
            return true;
        }

        tx.Execute("INSERT INTO expenses (id, category_id, site_id, amount_piasters, method, expense_date, description, created_by) VALUES (@id, 'cat-1', @s, @amt, @m, @d, 'x', @a)",
            new { id = NextId("exp"), s = Pick(Sites), amt = _rng.Next(100, 400_000), m = Pick(Methods), d = Date(), a = Actor });
        return true;
    }
}
