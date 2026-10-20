using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Shabana.Data.Maintenance;

/// <param name="Id">Stable identifier (also the test name).</param>
/// <param name="Description">Developer-facing statement of the rule; the Arabic wording belongs to the integrity screen (later phase).</param>
/// <param name="ViolationSql">A query returning ONE number: how many rows violate the rule. 0 = holds.</param>
public sealed record LedgerInvariant(string Id, string Description, string ViolationSql);

/// <summary>
/// The 14 ledger invariants of 03 section 0 / 04 section 9.4, as SQL over the schema. They are the oracle of the randomised stress run
/// and the checks of the owner's "full integrity check" button, so the shipped checks are the tested checks. Every query is a single
/// pass over each ledger (GROUP BY + join, no correlated scans), because the full check must finish in seconds on years of data.
/// </summary>
public static class LedgerInvariants
{
    public static IReadOnlyList<LedgerInvariant> All { get; } =
    [
        new("stock_non_negative", "No branch holds negative stock of any item.",
            "SELECT COUNT(*) FROM (SELECT 1 FROM stock_movements GROUP BY site_id, item_id HAVING SUM(qty_delta_milli) < 0)"),

        new("cash_drawer_non_negative", "No branch cash drawer is negative.",
            "SELECT COUNT(*) FROM (SELECT 1 FROM cash_movements GROUP BY site_id HAVING SUM(amount_delta_piasters) < 0)"),

        new("no_overpaid_order", "No sales order or purchase order has more paid than its total.",
            "SELECT (SELECT COUNT(*) FROM v_so_balance WHERE paid_piasters > total_piasters) + (SELECT COUNT(*) FROM v_po_balance WHERE paid_piasters > total_piasters)"),

        new("converted_not_over_total", "A purchase order never converts more than it bought (general: kg; itemized: per line and in total).",
            """
            SELECT (SELECT COUNT(*) FROM v_po_kg_progress WHERE converted_kg_milli > total_kg_milli)
                 + (SELECT COUNT(*) FROM v_po_itemized_progress WHERE received_milli > ordered_milli)
                 + (SELECT COUNT(*) FROM po_lines l
                      JOIN (SELECT po_line_id, SUM(qty_milli) q FROM po_line_conversions GROUP BY po_line_id) c ON c.po_line_id = l.id
                     WHERE c.q > l.qty_milli)
            """),

        new("closed_is_fully_collected", "A closed sales order has collected at least its total.",
            "SELECT COUNT(*) FROM v_so_balance WHERE status = 'closed' AND paid_piasters < total_piasters"),

        new("so_total_is_subtotal_minus_discount", "total = subtotal - discount, and the discount equals what its type and value give (clamped to the subtotal).",
            """
            SELECT COUNT(*) FROM sales_orders
             WHERE total_amount_piasters <> subtotal_piasters - discount_amount_piasters
                OR discount_amount_piasters < 0 OR discount_amount_piasters > subtotal_piasters
                OR discount_amount_piasters <> min(subtotal_piasters,
                     CASE discount_type WHEN 'percent' THEN (subtotal_piasters * discount_value_x100 + 5000) / 10000
                                        WHEN 'amount'  THEN discount_value_x100 ELSE 0 END)
            """),

        new("so_subtotal_is_sum_of_lines", "A sales order's subtotal is the sum of its lines.",
            """
            SELECT COUNT(*) FROM sales_orders so
              LEFT JOIN (SELECT so_id, SUM(line_total_piasters) s FROM sales_order_lines GROUP BY so_id) l ON l.so_id = so.id
             WHERE so.subtotal_piasters <> COALESCE(l.s, 0)
            """),

        new("cash_ledger_matches_cash_payments", "Every payment's drawer rows add up to its amount (signed) when paid in cash, and to nothing otherwise.",
            """
            WITH c AS (SELECT source_id, SUM(amount_delta_piasters) s FROM cash_movements WHERE source_type = 'payment' GROUP BY source_id)
            SELECT COUNT(*) FROM payments p LEFT JOIN c ON c.source_id = p.id
             WHERE COALESCE(c.s, 0) <> CASE WHEN p.method = 'cash'
                                            THEN CASE p.parent_type WHEN 'so' THEN p.amount_piasters ELSE -p.amount_piasters END
                                            ELSE 0 END
            """),

        new("sale_movements_match_placed_orders", "Net stock movements of a sales-order line equal -qty while the order is placed or closed, else 0 (also for deleted lines).",
            """
            WITH m AS (SELECT source_id, SUM(qty_delta_milli) s FROM stock_movements WHERE source_type IN ('sale', 'adjustment') AND source_id IS NOT NULL GROUP BY source_id)
            SELECT (SELECT COUNT(*) FROM sales_order_lines l
                      JOIN sales_orders so ON so.id = l.so_id
                      LEFT JOIN m ON m.source_id = l.id
                     WHERE COALESCE(m.s, 0) <> CASE WHEN so.status IN ('placed', 'closed') THEN -l.qty_milli ELSE 0 END)
                 + (SELECT COUNT(*) FROM m
                     WHERE m.s <> 0
                       AND m.source_id IN (SELECT source_id FROM stock_movements WHERE source_type = 'sale')
                       AND m.source_id NOT IN (SELECT id FROM sales_order_lines))
            """),

        new("numbering_assigned", "Every order has its running number; every invoiced-or-later sales order has an invoice number.",
            """
            SELECT (SELECT COUNT(*) FROM purchase_orders WHERE order_seq <= 0 OR order_code IS NULL)
                 + (SELECT COUNT(*) FROM sales_orders WHERE order_seq <= 0)
                 + (SELECT COUNT(*) FROM sales_orders WHERE status <> 'draft' AND invoice_number IS NULL)
            """),

        new("foreign_keys_clean", "PRAGMA foreign_key_check finds nothing.",
            "SELECT COUNT(*) FROM pragma_foreign_key_check"),

        new("credit_wallets_non_negative", "No client credit and no vendor advance wallet is negative.",
            "SELECT (SELECT COUNT(*) FROM v_client_credit_balance WHERE balance_piasters < 0) + (SELECT COUNT(*) FROM v_vendor_credit_balance WHERE balance_piasters < 0)"),

        new("transfers_balanced", "Every stock transfer has exactly one outgoing and one incoming movement of its quantity.",
            """
            WITH m AS (SELECT source_id, SUM(qty_delta_milli) s, COUNT(*) n,
                              SUM(CASE WHEN qty_delta_milli > 0 THEN qty_delta_milli ELSE 0 END) pos
                         FROM stock_movements WHERE source_type = 'transfer' GROUP BY source_id)
            SELECT COUNT(*) FROM stock_transfers t LEFT JOIN m ON m.source_id = t.id
             WHERE m.source_id IS NULL OR m.s <> 0 OR m.n <> 2 OR m.pos <> t.qty_milli
            """),

        new("conversions_posted_to_stock", "Every purchase conversion (kg and itemized) has put exactly its output quantity into stock.",
            """
            WITH m AS (SELECT source_id, SUM(qty_delta_milli) s FROM stock_movements WHERE source_type IN ('po_conversion', 'adjustment') AND source_id IS NOT NULL GROUP BY source_id)
            SELECT (SELECT COUNT(*) FROM po_conversions c LEFT JOIN m ON m.source_id = c.id WHERE COALESCE(m.s, 0) <> c.output_qty_milli)
                 + (SELECT COUNT(*) FROM po_line_conversions c LEFT JOIN m ON m.source_id = c.id WHERE COALESCE(m.s, 0) <> c.qty_milli)
            """),
    ];

    /// <summary>Runs every invariant; the result lists the ones that do not hold with their violation counts.</summary>
    public static IReadOnlyList<InvariantResult> Run(SqliteConnection connection)
    {
        var results = new List<InvariantResult>(All.Count);
        foreach (var invariant in All)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = invariant.ViolationSql;
            results.Add(new InvariantResult(invariant.Id, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)));
        }

        return results;
    }
}

public sealed record InvariantResult(string Id, long Violations)
{
    public bool Holds => Violations == 0;
}

/// <summary>The full check of Settings -> data integrity (04 section 9.4), minus the repairs and the Arabic wording.</summary>
public sealed record IntegrityReport(IReadOnlyList<string> IntegrityProblems, IReadOnlyList<string> ForeignKeyProblems, IReadOnlyList<InvariantResult> Invariants)
{
    public bool IsClean => IntegrityProblems.Count == 0 && ForeignKeyProblems.Count == 0 && Invariants.All(i => i.Holds);
}

public static class IntegrityChecker
{
    /// <summary><c>PRAGMA quick_check</c> on a database file, opened read-only and unpooled. Problems are returned, not thrown; a file that is not a database is a problem too.</summary>
    public static IReadOnlyList<string> QuickCheck(string databasePath)
    {
        try
        {
            using var connection = new Sqlite.ConnectionFactory(databasePath).OpenForInspection(Sqlite.OpenMode.ReadWrite);
            return DatabaseChecks.QuickCheck(connection);
        }
        catch (SqliteException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>integrity_check + foreign_key_check + the 14 invariants, in one read transaction (one snapshot).</summary>
    public static IntegrityReport Full(SqliteConnection connection)
    {
        using var snapshot = connection.BeginTransaction(deferred: true);
        var integrity = DatabaseChecks.IntegrityCheck(connection);
        var foreignKeys = DatabaseChecks.ForeignKeyViolations(connection);
        var invariants = LedgerInvariants.Run(connection);
        snapshot.Rollback();
        return new IntegrityReport(integrity, foreignKeys, invariants);
    }
}
