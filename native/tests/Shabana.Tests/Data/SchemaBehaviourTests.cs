using Dapper;
using Shabana.Core.Errors;
using Shabana.Data.Sqlite;

namespace Shabana.Tests.Data;

/// <summary>
/// P2 acceptance (4a): the ported schema behavioural suite -- 65 assertions, run with <c>recursive_triggers</c> OFF and ON, against the EMBEDDED
/// baseline (04 section 12.1). The original script of 03 section 0 is not in the repository; these are re-created from its description:
/// PO conversion limits, stock/cash floors, lifecycle, discount rounding, overpayment, auto-close/re-open, transfers, credits, expenses,
/// itemized POs, cascade deletes, audit contents, STRICT/CHECK rejections (plus append-only ledgers and the user rules).
/// </summary>
public class SchemaBehaviourTests
{
    internal sealed record Case(string Name, Action<Fx> Body);

    internal static readonly IReadOnlyList<Case> Cases =
    [
        // ===== A. Purchase-order conversion limits (9) ==================================================================================
        new("A1 conversion within the remaining kg posts stock and shows progress", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Conv("c1", "po1", "s1", "i1", 40_000, 25_000);
            f.Stock("s1", "i1").ShouldBe(25_000);
            f.Q<(long Converted, long Remaining)>("SELECT converted_kg_milli AS Converted, remaining_kg_milli AS Remaining FROM v_po_kg_progress WHERE po_id = 'po1'").ShouldBe((40_000, 60_000));
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'po1'").ShouldBe("open");
        }),
        new("A2 converting exactly the remaining kg marks the order fully converted", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Conv("c1", "po1", "s1", "i1", 100_000, 1);
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'po1'").ShouldBe("fully_converted");
        }),
        new("A3 converting one milli-kg more than the remaining is refused", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Rejects(ErrorCode.PoConversionExceedsRemaining, "INSERT INTO po_conversions (po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit) VALUES ('po1', 's1', 'i1', 100001, 5, 'unit')");
            f.Count("po_conversions").ShouldBe(0);
            f.Count("stock_movements").ShouldBe(0);
        }),
        new("A4 the limit is cumulative across conversions", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Conv("c1", "po1", "s1", "i1", 60_000, 10);
            f.Rejects(ErrorCode.PoConversionExceedsRemaining, "INSERT INTO po_conversions (po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit) VALUES ('po1', 's1', 'i1', 40001, 5, 'unit')");
            f.Conv("c2", "po1", "s2", "i2", 40_000, 10, "kg");
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'po1'").ShouldBe("fully_converted");
        }),
        new("A5 the 100 kg example: 50 kg to 40 units at A, 25 kg to 25 kg at A, 25 kg to 50 units at B", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Conv("c1", "po1", "s1", "i1", 50_000, 40_000, "unit");
            f.Conv("c2", "po1", "s1", "i2", 25_000, 25_000, "kg");
            f.Conv("c3", "po1", "s2", "i3", 25_000, 50_000, "unit");
            f.Stock("s1", "i1").ShouldBe(40_000);
            f.Stock("s1", "i2").ShouldBe(25_000);
            f.Stock("s2", "i3").ShouldBe(50_000);
            f.Count("v_stock_balance").ShouldBe(3);
            f.Q<(long Total, long Converted, long Remaining)>("SELECT total_kg_milli AS Total, converted_kg_milli AS Converted, remaining_kg_milli AS Remaining FROM v_po_kg_progress WHERE po_id = 'po1'").ShouldBe((100_000, 100_000, 0));
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'po1'").ShouldBe("fully_converted");
        }),
        new("A6 a kg conversion on an itemized or missing order is refused", f =>
        {
            f.ItemizedPo("ipo");
            f.Rejects(ErrorCode.PoNotGeneral, "INSERT INTO po_conversions (po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit) VALUES ('ipo', 's1', 'i1', 1000, 5, 'unit')");
            f.Rejects(ErrorCode.PoNotFound, "INSERT INTO po_conversions (po_id, site_id, item_id, kg_consumed_milli, output_qty_milli, output_unit) VALUES ('nope', 's1', 'i1', 1000, 5, 'unit')");
        }),
        new("A7 total kg is frozen once a conversion exists, unless the edit window is open", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Po("po2", 100_000, 1000);
            f.Exec("UPDATE purchase_orders SET total_kg_milli = 120000 WHERE id = 'po1'");           // no conversion yet: allowed
            f.Conv("c1", "po1", "s1", "i1", 10_000, 10);
            f.Rejects(ErrorCode.PoTotalKgFrozen, "UPDATE purchase_orders SET total_kg_milli = 130000 WHERE id = 'po1'");
            f.Actor("adm", editing: true);
            f.Exec("UPDATE purchase_orders SET total_kg_milli = 130000 WHERE id = 'po1'");           // R6 admin edit
            f.Q<long>("SELECT total_kg_milli FROM purchase_orders WHERE id = 'po1'").ShouldBe(130_000);
        }),
        new("A8 reversing a conversion takes its stock back out and re-opens the order", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Conv("c1", "po1", "s1", "i1", 100_000, 25_000);
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'po1'").ShouldBe("fully_converted");
            f.Exec("DELETE FROM po_conversions WHERE id = 'c1'");
            f.Stock("s1", "i1").ShouldBe(0);
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'po1'").ShouldBe("open");
            f.Count("stock_movements", "source_type = 'adjustment' AND qty_delta_milli = -25000").ShouldBe(1);   // a compensating row, nothing deleted
        }),
        new("A9 reversing is refused once the stock was sold, and a conversion cannot be edited", f =>
        {
            f.Po("po1", 100_000, 1000);
            f.Conv("c1", "po1", "s1", "i1", 50_000, 25_000);
            f.Exec("INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s1', 'i1', -20000, 'adjustment')");
            f.Rejects(ErrorCode.ConversionReverseInsufficient, "DELETE FROM po_conversions WHERE id = 'c1'");
            f.Count("po_conversions").ShouldBe(1);
            f.Rejects(ErrorCode.RowImmutable, "UPDATE po_conversions SET output_qty_milli = 99 WHERE id = 'c1'");
        }),

        // ===== B. Stock and cash floors (5) ===================================================================================================
        new("B1 stock cannot go below zero but may reach exactly zero", f =>
        {
            f.AddStock("s1", "i1", 5000);
            f.Rejects(ErrorCode.StockFloor, "INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s1', 'i1', -5001, 'adjustment')");
            f.AddStock("s1", "i1", -5000);
            f.Stock("s1", "i1").ShouldBe(0);
        }),
        new("B2 the stock floor is per branch and per item", f =>
        {
            f.AddStock("s1", "i1", 5000);
            f.Rejects(ErrorCode.StockFloor, "INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s2', 'i1', -1, 'adjustment')");
            f.Rejects(ErrorCode.StockFloor, "INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s1', 'i2', -1, 'adjustment')");
        }),
        new("B3 the cash drawer cannot be overdrawn but may reach exactly zero", f =>
        {
            f.Fund("s1", 1000);
            f.Rejects(ErrorCode.CashFloor, "INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason) VALUES ('s1', -1001, 'manual', 'x')");
            f.Fund("s1", -1000, "withdraw");
            f.Cash("s1").ShouldBe(0);
        }),
        new("B4 the cash floor is per drawer", f =>
        {
            f.Fund("s1", 100_000);
            f.Rejects(ErrorCode.CashFloor, "INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason) VALUES ('s2', -1, 'manual', 'x')");
        }),
        new("B5 a manual drawer movement needs a note", f =>
        {
            f.Fund("s1", 1000);
            f.RejectedKind("INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason) VALUES ('s1', 5, 'manual', '   ')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO cash_movements (site_id, amount_delta_piasters, source_type, reason) VALUES ('s1', 5, 'manual', NULL)").ShouldBe(DbErrorKind.NotNull);
        }),

        // ===== C. Append-only ledgers (3) =====================================================================================================
        new("C1 stock and cash ledgers refuse UPDATE and DELETE", f =>
        {
            f.AddStock("s1", "i1", 100);
            f.Fund("s1", 100);
            f.Rejects(ErrorCode.LedgerReadonly, "UPDATE stock_movements SET qty_delta_milli = 999");
            f.Rejects(ErrorCode.LedgerReadonly, "DELETE FROM stock_movements");
            f.Rejects(ErrorCode.LedgerReadonly, "UPDATE cash_movements SET amount_delta_piasters = 999");
            f.Rejects(ErrorCode.LedgerReadonly, "DELETE FROM cash_movements");
            (f.Stock("s1", "i1"), f.Cash("s1")).ShouldBe((100, 100));
        }),
        new("C2 the audit log and auth events are append-only", f =>
        {
            f.Exec("INSERT INTO items (id, name_ar, unit_type) VALUES ('x', 'x', 'kg')");       // writes an audit row
            f.Count("audit_log").ShouldBeGreaterThan(0);
            f.Rejects(ErrorCode.LedgerReadonly, "UPDATE audit_log SET entity = 'tampered'");
            f.Rejects(ErrorCode.LedgerReadonly, "DELETE FROM audit_log");
            f.Exec("INSERT INTO auth_events (user_id, event) VALUES ('adm', 'login_ok')");
            f.Rejects(ErrorCode.LedgerReadonly, "DELETE FROM auth_events");
        }),
        new("C3 credit wallets, transfers and opening balances are append-only", f =>
        {
            f.Exec("INSERT INTO client_credits (id, contact_id, amount_delta_piasters, source_type) VALUES ('cc', 'cl', 500, 'deposit')");
            f.Exec("INSERT INTO vendor_credits (id, contact_id, amount_delta_piasters, source_type) VALUES ('vc', 'vd', 500, 'deposit')");
            f.Exec("INSERT INTO contact_opening_balances (id, contact_id, kind, amount_delta_piasters) VALUES ('ob', 'cl', 'receivable', 700)");
            f.AddStock("s1", "i1", 1000);
            f.Exec("INSERT INTO stock_transfers (id, from_site, to_site, item_id, qty_milli) VALUES ('tr', 's1', 's2', 'i1', 100)");
            f.Rejects(ErrorCode.LedgerReadonly, "UPDATE client_credits SET amount_delta_piasters = 1");
            f.Rejects(ErrorCode.LedgerReadonly, "DELETE FROM vendor_credits");
            f.Rejects(ErrorCode.LedgerReadonly, "UPDATE contact_opening_balances SET amount_delta_piasters = 1");
            f.Rejects(ErrorCode.LedgerReadonly, "DELETE FROM stock_transfers");
        }),

        // ===== D. Sales-order lifecycle (10) ==================================================================================================
        new("D1 invoicing issues SO-YYYY-#### numbers per order year (web defect B1 fixed)", f =>
        {
            foreach (var (id, date) in new[] { ("a", "2026-01-05"), ("b", "2026-12-31"), ("c", "2027-01-01") })
            {
                f.So(id, date: date);
                f.Line(id, "i1", 1000, 100);
                f.Status(id, "invoiced");
            }

            f.Q<string>("SELECT invoice_number FROM sales_orders WHERE id = 'a'").ShouldBe("SO-2026-0001");
            f.Q<string>("SELECT invoice_number FROM sales_orders WHERE id = 'b'").ShouldBe("SO-2026-0002");
            f.Q<string>("SELECT invoice_number FROM sales_orders WHERE id = 'c'").ShouldBe("SO-2027-0001");
        }),
        new("D2 an order without lines cannot be invoiced", f =>
        {
            f.So("a");
            f.Rejects(ErrorCode.SoInvoiceNoLines, "UPDATE sales_orders SET status = 'invoiced' WHERE id = 'a'");
            f.StatusOf("a").ShouldBe("draft");
        }),
        new("D3 status changes outside the lifecycle are refused", f =>
        {
            f.AddStock("s1", "i1", 100_000);
            f.So("a");
            f.Line("a", "i1", 1000, 100);
            f.Rejects(ErrorCode.SoTransitionIllegal, "UPDATE sales_orders SET status = 'placed' WHERE id = 'a'");      // draft -> placed
            f.Rejects(ErrorCode.SoTransitionIllegal, "UPDATE sales_orders SET status = 'closed' WHERE id = 'a'");      // draft -> closed
            f.Status("a", "invoiced");
            f.Rejects(ErrorCode.SoTransitionIllegal, "UPDATE sales_orders SET status = 'closed' WHERE id = 'a'");      // invoiced -> closed
            f.Rejects(ErrorCode.SoTransitionIllegal, "UPDATE sales_orders SET status = 'draft' WHERE id = 'a'");       // invoiced -> draft
            f.Status("a", "placed");
            f.Rejects(ErrorCode.SoTransitionIllegal, "UPDATE sales_orders SET status = 'draft' WHERE id = 'a'");       // placed -> draft
            f.StatusOf("a").ShouldBe("placed");
        }),
        new("D4 lines are locked from invoicing on", f =>
        {
            f.InvoicedSo("a");
            f.Rejects(ErrorCode.SoLinesLocked, "INSERT INTO sales_order_lines (so_id, item_id, qty_milli, unit_price_piasters) VALUES ('a', 'i1', 1, 1)");
            f.Rejects(ErrorCode.SoLinesLocked, "UPDATE sales_order_lines SET qty_milli = 1 WHERE so_id = 'a'");
            f.Rejects(ErrorCode.SoLinesLocked, "DELETE FROM sales_order_lines WHERE so_id = 'a'");
        }),
        new("D5 placing deducts stock at the order's branch only", f =>
        {
            f.AddStock("s1", "i1", 10_000);
            f.AddStock("s2", "i1", 10_000);
            f.InvoicedSo("a", qtyMilli: 3000, site: "s1");
            f.Status("a", "placed");
            (f.Stock("s1", "i1"), f.Stock("s2", "i1")).ShouldBe((7000, 10_000));
        }),
        new("D6 placing needs stock for every line, all or nothing", f =>
        {
            f.AddStock("s1", "i1", 10_000);
            f.So("a");
            f.Line("a", "i1", 5000, 100);
            f.Line("a", "i2", 5000, 100);          // no i2 in stock
            f.Status("a", "invoiced");
            f.Rejects(ErrorCode.SoStockInsufficient, "UPDATE sales_orders SET status = 'placed' WHERE id = 'a'");
            f.StatusOf("a").ShouldBe("invoiced");
            f.Stock("s1", "i1").ShouldBe(10_000);
            f.Count("stock_movements", "source_type = 'sale'").ShouldBe(0);
        }),
        new("D7 cancelling a placement returns the stock", f =>
        {
            f.AddStock("s1", "i1", 10_000);
            f.InvoicedSo("a", qtyMilli: 4000);
            f.Status("a", "placed");
            f.Stock("s1", "i1").ShouldBe(6000);
            f.Status("a", "invoiced");
            f.Stock("s1", "i1").ShouldBe(10_000);
        }),
        new("D8 order numbers are gap-free: a rolled-back insert does not use one", f =>
        {
            f.So("a");
            f.Exec("SAVEPOINT s");
            f.So("lost");
            f.Exec("ROLLBACK TO s");
            f.Exec("RELEASE s");
            f.So("b");
            f.Q<long>("SELECT order_seq FROM sales_orders WHERE id = 'a'").ShouldBe(1);
            f.Q<long>("SELECT order_seq FROM sales_orders WHERE id = 'b'").ShouldBe(2);
            f.Po("p1", 1000, 100);
            f.Q<long>("SELECT order_seq FROM purchase_orders WHERE id = 'p1'").ShouldBe(1);
            f.Q<string>("SELECT order_code FROM purchase_orders WHERE id = 'p1'").ShouldBe("PO-2026-0001");
        }),
        new("D9 an admin edit of a CLOSED order does not deduct its stock twice (web defect B2 fixed)", f =>
        {
            f.AddStock("s1", "i1", 10_000);
            f.InvoicedSo("a", qtyMilli: 4000, price: 1000);        // total 4000 piasters
            f.Status("a", "placed");
            f.Pay("so", "a", 4000, "instapay");
            f.StatusOf("a").ShouldBe("closed");
            f.Stock("s1", "i1").ShouldBe(6000);

            f.Actor("adm", editing: true);                           // R5
            f.Status("a", "invoiced");                               // closed -> invoiced returns the stock
            f.Stock("s1", "i1").ShouldBe(10_000);
            f.Exec("DELETE FROM sales_order_lines WHERE so_id = 'a'");
            f.Line("a", "i1", 5000, 1000);
            f.Status("a", "placed");                                 // replay: deducts the NEW quantity once
            f.Actor(null);

            f.Stock("s1", "i1").ShouldBe(5000);
            f.Q<long>("SELECT paid_piasters FROM v_so_balance WHERE so_id = 'a'").ShouldBe(4000);
        }),
        new("D10 an admin can rewrite the lines of an order with a fixed discount (web defect B3 fixed)", f =>
        {
            f.So("a");
            f.Line("a", "i1", 10_000, 1000);                         // subtotal 10000
            f.Exec("UPDATE sales_orders SET discount_type = 'amount', discount_value_x100 = 2000 WHERE id = 'a'");
            f.Q<long>("SELECT total_amount_piasters FROM sales_orders WHERE id = 'a'").ShouldBe(8000);
            // Normal rules: deleting the lines would leave a discount bigger than the subtotal.
            f.Rejects(ErrorCode.DiscountExceedsSubtotal, "DELETE FROM sales_order_lines WHERE so_id = 'a'");
            // With the edit window open the rewrite goes through; the generated columns clamp the discount.
            f.Actor("adm", editing: true);
            f.Exec("DELETE FROM sales_order_lines WHERE so_id = 'a'");
            f.Q<(long Sub, long Disc, long Tot)>("SELECT subtotal_piasters AS Sub, discount_amount_piasters AS Disc, total_amount_piasters AS Tot FROM sales_orders WHERE id = 'a'").ShouldBe((0, 0, 0));
            f.Line("a", "i1", 5000, 1000);
            f.Actor(null);
            f.Q<(long Sub, long Disc, long Tot)>("SELECT subtotal_piasters AS Sub, discount_amount_piasters AS Disc, total_amount_piasters AS Tot FROM sales_orders WHERE id = 'a'").ShouldBe((5000, 2000, 3000));
        }),

        // ===== E. Discount and line rounding (6) ==============================================================================================
        new("E1 a percent discount rounds half up in integers", f =>
        {
            f.So("a");
            f.Line("a", "i1", 1000, 1005);                           // subtotal 1005
            f.Exec("UPDATE sales_orders SET discount_type = 'percent', discount_value_x100 = 1250 WHERE id = 'a'");   // 12.5 %
            f.Q<long>("SELECT discount_amount_piasters FROM sales_orders WHERE id = 'a'").ShouldBe(126);            // 125.625
            f.So("b");
            f.Line("b", "i1", 1000, 1010);
            f.Exec("UPDATE sales_orders SET discount_type = 'percent', discount_value_x100 = 500 WHERE id = 'b'");    // 5 % of 1010 = 50.5
            f.Q<long>("SELECT discount_amount_piasters FROM sales_orders WHERE id = 'b'").ShouldBe(51);
            f.Total("b").ShouldBe(959);
        }),
        new("E2 a line total is qty x price rounded half up in integers", f =>
        {
            f.So("a");
            f.Line("a", "i1", 2500, 333, "l1");       // 832.5
            f.Line("a", "i1", 1, 100, "l2");          // 0.1
            f.Line("a", "i1", 1, 500, "l3");          // 0.5
            f.Line("a", "i1", 3, 333, "l4");          // 0.999
            f.Q<long>("SELECT line_total_piasters FROM sales_order_lines WHERE id = 'l1'").ShouldBe(833);
            f.Q<long>("SELECT line_total_piasters FROM sales_order_lines WHERE id = 'l2'").ShouldBe(0);
            f.Q<long>("SELECT line_total_piasters FROM sales_order_lines WHERE id = 'l3'").ShouldBe(1);
            f.Q<long>("SELECT line_total_piasters FROM sales_order_lines WHERE id = 'l4'").ShouldBe(1);
            f.Q<long>("SELECT subtotal_piasters FROM sales_orders WHERE id = 'a'").ShouldBe(833 + 0 + 1 + 1);
        }),
        new("E3 an amount discount above the subtotal is refused, equal to it is fine", f =>
        {
            f.So("a");
            f.Line("a", "i1", 1000, 1000);
            f.Rejects(ErrorCode.DiscountExceedsSubtotal, "UPDATE sales_orders SET discount_type = 'amount', discount_value_x100 = 1001 WHERE id = 'a'");
            f.Rejects(ErrorCode.DiscountExceedsSubtotal, "INSERT INTO sales_orders (id, site_id, client_id, discount_type, discount_value_x100) VALUES ('b', 's1', 'cl', 'amount', 5)");
            f.Exec("UPDATE sales_orders SET discount_type = 'amount', discount_value_x100 = 1000 WHERE id = 'a'");
            f.Total("a").ShouldBe(0);
        }),
        new("E4 a percent discount above 100 percent is refused, exactly 100 is fine", f =>
        {
            f.So("a");
            f.Line("a", "i1", 1000, 1000);
            f.Rejects(ErrorCode.DiscountPercentOver100, "UPDATE sales_orders SET discount_type = 'percent', discount_value_x100 = 10001 WHERE id = 'a'");
            f.Rejects(ErrorCode.DiscountPercentOver100, "INSERT INTO sales_orders (id, site_id, client_id, discount_type, discount_value_x100) VALUES ('b', 's1', 'cl', 'percent', 10001)");
            f.Exec("UPDATE sales_orders SET discount_type = 'percent', discount_value_x100 = 10000 WHERE id = 'a'");
            f.Total("a").ShouldBe(0);
        }),
        new("E5 a discount cannot push the total below what was already collected", f =>
        {
            f.InvoicedSo("a", qtyMilli: 10_000, price: 1000);          // total 10000
            f.Pay("so", "a", 8000, "instapay");
            f.Rejects(ErrorCode.DiscountBelowPaid, "UPDATE sales_orders SET discount_type = 'amount', discount_value_x100 = 2001 WHERE id = 'a'");
            f.Exec("UPDATE sales_orders SET discount_type = 'amount', discount_value_x100 = 2000 WHERE id = 'a'");   // total 8000 = paid: allowed
            f.Total("a").ShouldBe(8000);
        }),
        new("E6 a percent discount follows the lines, and a zero total never auto-closes", f =>
        {
            f.So("a");
            f.Line("a", "i1", 10_000, 1000);
            f.Exec("UPDATE sales_orders SET discount_type = 'percent', discount_value_x100 = 1000 WHERE id = 'a'");
            f.Total("a").ShouldBe(9000);
            f.Line("a", "i1", 5000, 1000);
            f.Q<(long Sub, long Disc, long Tot)>("SELECT subtotal_piasters AS Sub, discount_amount_piasters AS Disc, total_amount_piasters AS Tot FROM sales_orders WHERE id = 'a'").ShouldBe((15_000, 1500, 13_500));
            f.Exec("UPDATE sales_orders SET discount_value_x100 = 10000 WHERE id = 'a'");
            f.Total("a").ShouldBe(0);
            f.AddStock("s1", "i1", 100_000);
            f.Status("a", "invoiced");
            f.Status("a", "placed");
            f.StatusOf("a").ShouldBe("placed");                     // collected (0) >= total (0), yet not closed: closing needs total > 0
        }),

        // ===== F. Payments and overpayment (5) ================================================================================================
        new("F1 a payment may not exceed what is left on the order", f =>
        {
            f.AddStock("s1", "i1", 100_000);
            f.InvoicedSo("a", qtyMilli: 10_000, price: 1000);       // 10000
            f.Pay("so", "a", 6000, "instapay");
            f.Rejects(ErrorCode.PaymentOverpay, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('so', 'a', 4001, 'instapay')");
            f.Pay("so", "a", 4000, "instapay");
            f.Count("payments").ShouldBe(2);
            f.Po("p1", 5000, 1000);                                  // 5000
            f.Rejects(ErrorCode.PaymentOverpay, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('po', 'p1', 5001, 'bank_transfer')");
        }),
        new("F2 a draft order takes no payment; a missing order neither", f =>
        {
            f.So("a");
            f.Line("a", "i1", 1000, 1000);
            f.Rejects(ErrorCode.SoDraftNoPayment, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('so', 'a', 1, 'instapay')");
            f.Rejects(ErrorCode.SoNotFound, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('so', 'ghost', 1, 'instapay')");
            f.Rejects(ErrorCode.PoNotFound, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('po', 'ghost', 1, 'instapay')");
        }),
        new("F3 a cash payment on a purchase order must name the drawer", f =>
        {
            f.Po("p1", 5000, 1000);
            f.Rejects(ErrorCode.CashSiteRequired, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method) VALUES ('po', 'p1', 100, 'cash')");
            f.Pay("po", "p1", 100, "instapay");                      // other methods need no drawer
            f.Fund("s1", 1000);
            f.Pay("po", "p1", 100, "cash", "s1");
            f.Cash("s1").ShouldBe(900);
        }),
        new("F4 the payment method must belong to the order's contact and match the payment's method", f =>
        {
            f.Exec("INSERT INTO contact_payment_methods (id, contact_id, method, instapay_number) VALUES ('pm-v', 'vd', 'instapay', '0100')");
            f.InvoicedSo("a");
            f.Po("p1", 5000, 1000);
            f.Rejects(ErrorCode.PmNotOwned, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method, contact_payment_method_id) VALUES ('so', 'a', 1, 'instapay', 'pm-v')");   // the client's order, the vendor's account
            f.Rejects(ErrorCode.PmNotOwned, "INSERT INTO payments (parent_type, parent_id, amount_piasters, method, site_id, contact_payment_method_id) VALUES ('po', 'p1', 1, 'cash', 's1', 'pm-v')");   // wrong method
            f.Exec("INSERT INTO payments (parent_type, parent_id, amount_piasters, method, contact_payment_method_id) VALUES ('po', 'p1', 1, 'instapay', 'pm-v')");
            f.Count("payments").ShouldBe(1);
        }),
        new("F5 a recorded payment cannot be edited", f =>
        {
            f.InvoicedSo("a");
            f.Pay("so", "a", 1000, "instapay", id: "pay1");
            f.Rejects(ErrorCode.PaymentImmutable, "UPDATE payments SET amount_piasters = 1 WHERE id = 'pay1'");
            f.Rejects(ErrorCode.PaymentImmutable, "UPDATE payments SET note = 'edited' WHERE id = 'pay1'");
            f.Rejects(ErrorCode.PaymentImmutable, "UPDATE payments SET paid_at = '2020-01-01T00:00:00.000Z' WHERE id = 'pay1'");
        }),

        // ===== G. Auto-close and re-open (4) ==================================================================================================
        new("G1 collecting the whole total closes a placed order", f =>
        {
            f.AddStock("s1", "i1", 100_000);
            f.InvoicedSo("a", qtyMilli: 10_000, price: 1000);
            f.Status("a", "placed");
            f.Pay("so", "a", 9999, "instapay");
            f.StatusOf("a").ShouldBe("placed");
            f.Pay("so", "a", 1, "instapay");
            f.StatusOf("a").ShouldBe("closed");
        }),
        new("G2 deleting a payment re-opens a closed order and compensates the drawer", f =>
        {
            f.AddStock("s1", "i1", 100_000);
            f.InvoicedSo("a", qtyMilli: 10_000, price: 1000);
            f.Status("a", "placed");
            f.Pay("so", "a", 10_000, "cash", id: "pay1");
            f.StatusOf("a").ShouldBe("closed");
            f.Cash("s1").ShouldBe(10_000);
            f.Exec("DELETE FROM payments WHERE id = 'pay1'");
            f.StatusOf("a").ShouldBe("placed");
            f.Cash("s1").ShouldBe(0);
            f.Count("cash_movements", "source_id = 'pay1'").ShouldBe(2);        // the original row and its reversal: nothing deleted
        }),
        new("G3 an order paid in full before placement closes the moment it is placed", f =>
        {
            f.AddStock("s1", "i1", 100_000);
            f.InvoicedSo("a", qtyMilli: 5000, price: 1000);
            f.Pay("so", "a", 5000, "instapay");
            f.StatusOf("a").ShouldBe("invoiced");
            f.Status("a", "placed");
            f.StatusOf("a").ShouldBe("closed");
            f.Stock("s1", "i1").ShouldBe(95_000);
        }),
        new("G4 drawer rows follow cash payments only, in the right direction, dated by paid_at (amendment S-2)", f =>
        {
            f.Fund("s1", 5000);
            f.AddStock("s1", "i1", 100_000);
            f.InvoicedSo("a", qtyMilli: 10_000, price: 1000);
            f.Status("a", "placed");
            f.Pay("so", "a", 3000, "cash", id: "in", paidAt: "2026-01-05T09:00:00.000Z");
            f.Pay("so", "a", 1000, "instapay", id: "none");
            f.Po("p1", 5000, 1000);
            f.Pay("po", "p1", 2000, "cash", "s1", id: "out");
            f.Cash("s1").ShouldBe(5000 + 3000 - 2000);
            f.Count("cash_movements", "source_id = 'none'").ShouldBe(0);
            f.Q<string>("SELECT created_at FROM cash_movements WHERE source_id = 'in'").ShouldBe("2026-01-05T09:00:00.000Z");
            f.Q<long>("SELECT amount_delta_piasters FROM cash_movements WHERE source_id = 'out'").ShouldBe(-2000);
        }),

        // ===== H. Transfers (2) ===============================================================================================================
        new("H1 a transfer moves stock between branches with two movements", f =>
        {
            f.AddStock("s1", "i1", 10_000);
            f.Exec("INSERT INTO stock_transfers (id, from_site, to_site, item_id, qty_milli) VALUES ('t1', 's1', 's2', 'i1', 4000)");
            (f.Stock("s1", "i1"), f.Stock("s2", "i1")).ShouldBe((6000, 4000));
            f.Count("stock_movements", "source_type = 'transfer' AND source_id = 't1'").ShouldBe(2);
            f.RejectedKind("INSERT INTO stock_transfers (from_site, to_site, item_id, qty_milli) VALUES ('s1', 's1', 'i1', 1)").ShouldBe(DbErrorKind.Check);   // same branch
        }),
        new("H2 a transfer larger than the source stock is refused and posts nothing", f =>
        {
            f.AddStock("s1", "i1", 10_000);
            f.Rejects(ErrorCode.TransferStockInsufficient, "INSERT INTO stock_transfers (from_site, to_site, item_id, qty_milli) VALUES ('s1', 's2', 'i1', 10001)");
            f.Count("stock_transfers").ShouldBe(0);
            (f.Stock("s1", "i1"), f.Stock("s2", "i1")).ShouldBe((10_000, 0));
        }),

        // ===== I. Credit wallets (2) ==========================================================================================================
        new("I1 a client credit never goes negative", f =>
        {
            f.Exec("INSERT INTO client_credits (contact_id, amount_delta_piasters, source_type) VALUES ('cl', 5000, 'deposit')");
            f.Rejects(ErrorCode.ClientCreditNegative, "INSERT INTO client_credits (contact_id, amount_delta_piasters, source_type) VALUES ('cl', -5001, 'refund')");
            f.Exec("INSERT INTO client_credits (contact_id, amount_delta_piasters, source_type) VALUES ('cl', -5000, 'refund')");
            f.Q<long>("SELECT COALESCE(SUM(amount_delta_piasters), 0) FROM client_credits WHERE contact_id = 'cl'").ShouldBe(0);
        }),
        new("I2 a vendor advance never goes negative", f =>
        {
            f.Exec("INSERT INTO vendor_credits (contact_id, amount_delta_piasters, source_type) VALUES ('vd', 5000, 'deposit')");
            f.Rejects(ErrorCode.VendorCreditNegative, "INSERT INTO vendor_credits (contact_id, amount_delta_piasters, source_type) VALUES ('vd', -5001, 'applied')");
            f.Q<long>("SELECT balance_piasters FROM v_vendor_credit_balance WHERE contact_id = 'vd'").ShouldBe(5000);
        }),

        // ===== J. Expenses (4) ================================================================================================================
        new("J1 a cash expense withdraws from its drawer and cannot overdraw it", f =>
        {
            f.Exec("INSERT INTO expense_categories (id, name_ar) VALUES ('cat', 'Meals')");
            f.Fund("s1", 10_000);
            f.Exec("INSERT INTO expenses (id, category_id, site_id, amount_piasters, method, description) VALUES ('e1', 'cat', 's1', 3000, 'cash', 'lunch')");
            f.Cash("s1").ShouldBe(7000);
            f.Q<string>("SELECT reason FROM cash_movements WHERE source_id = 'e1'").ShouldContain("Meals");
            f.Exec("INSERT INTO expenses (id, category_id, site_id, amount_piasters, method) VALUES ('e2', 'cat', 's1', 3000, 'instapay')");    // no drawer row
            f.Cash("s1").ShouldBe(7000);
            f.Rejects(ErrorCode.CashFloor, "INSERT INTO expenses (id, category_id, site_id, amount_piasters, method) VALUES ('e3', 'cat', 's1', 7001, 'cash')");
            f.Count("expenses", "id = 'e3'").ShouldBe(0);
        }),
        new("J2 a recorded expense cannot be changed except for its text", f =>
        {
            f.Exec("INSERT INTO expense_categories (id, name_ar) VALUES ('cat', 'Meals')");
            f.Fund("s1", 10_000);
            f.Exec("INSERT INTO expenses (id, category_id, site_id, amount_piasters, method) VALUES ('e1', 'cat', 's1', 3000, 'cash')");
            f.Rejects(ErrorCode.ExpenseImmutable, "UPDATE expenses SET amount_piasters = 1 WHERE id = 'e1'");
            f.Rejects(ErrorCode.ExpenseImmutable, "UPDATE expenses SET method = 'instapay' WHERE id = 'e1'");
            f.Rejects(ErrorCode.ExpenseImmutable, "UPDATE expenses SET site_id = 's2' WHERE id = 'e1'");
            f.Exec("UPDATE expenses SET description = 'fixed typo' WHERE id = 'e1'");
        }),
        new("J3 voiding an expense returns the money once and needs a reason", f =>
        {
            f.Exec("INSERT INTO expense_categories (id, name_ar) VALUES ('cat', 'Meals')");
            f.Fund("s1", 10_000);
            f.Exec("INSERT INTO expenses (id, category_id, site_id, amount_piasters, method) VALUES ('e1', 'cat', 's1', 3000, 'cash')");
            f.RejectedKind("UPDATE expenses SET voided_at = strftime('%Y-%m-%dT%H:%M:%fZ','now'), void_reason = '  ' WHERE id = 'e1'").ShouldBe(DbErrorKind.Check);
            f.Exec("UPDATE expenses SET voided_at = strftime('%Y-%m-%dT%H:%M:%fZ','now'), void_reason = 'entered twice' WHERE id = 'e1'");
            f.Cash("s1").ShouldBe(10_000);
            f.Count("expenses").ShouldBe(1);                                   // voided, not deleted
            f.Rejects(ErrorCode.ExpenseAlreadyVoided, "UPDATE expenses SET voided_at = strftime('%Y-%m-%dT%H:%M:%fZ','now', '+1 second'), void_reason = 'again' WHERE id = 'e1'");
            f.Cash("s1").ShouldBe(10_000);
        }),
        new("J4 an inactive category takes no new expenses", f =>
        {
            f.Exec("INSERT INTO expense_categories (id, name_ar, active) VALUES ('old', 'Retired', 0)");
            f.Rejects(ErrorCode.ExpenseCategoryInactive, "INSERT INTO expenses (category_id, site_id, amount_piasters, method) VALUES ('old', 's1', 100, 'instapay')");
        }),

        // ===== K. Itemized purchase orders (3) ================================================================================================
        new("K1 an itemized order's total is the sum of its lines, kept in step", f =>
        {
            f.ItemizedPo("ipo");
            f.PoLine("l1", "ipo", "i1", 2500, 333);                  // 833
            f.PoLine("l2", "ipo", "i2", 1000, 1000);                 // 1000
            f.Q<long>("SELECT total_amount_piasters FROM purchase_orders WHERE id = 'ipo'").ShouldBe(1833);
            f.Exec("UPDATE po_lines SET qty_milli = 5000, line_total_piasters = 1665 WHERE id = 'l1'");
            f.Q<long>("SELECT total_amount_piasters FROM purchase_orders WHERE id = 'ipo'").ShouldBe(2665);
            f.Exec("DELETE FROM po_lines WHERE id = 'l2'");
            f.Q<long>("SELECT total_amount_piasters FROM purchase_orders WHERE id = 'ipo'").ShouldBe(1665);
        }),
        new("K2 line conversions are limited to the line quantity, post stock and complete the order", f =>
        {
            f.ItemizedPo("ipo");
            f.PoLine("l1", "ipo", "i1", 10_000, 100);
            f.Exec("INSERT INTO po_line_conversions (id, po_line_id, site_id, qty_milli) VALUES ('pc1', 'l1', 's1', 6000)");
            f.Rejects(ErrorCode.PlcExceedsRemaining, "INSERT INTO po_line_conversions (po_line_id, site_id, qty_milli) VALUES ('l1', 's1', 4001)");
            f.Rejects(ErrorCode.PoLineNotFound, "INSERT INTO po_line_conversions (po_line_id, site_id, qty_milli) VALUES ('ghost', 's1', 1)");
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'ipo'").ShouldBe("open");
            f.Exec("INSERT INTO po_line_conversions (id, po_line_id, site_id, qty_milli) VALUES ('pc2', 'l1', 's2', 4000)");
            (f.Stock("s1", "i1"), f.Stock("s2", "i1")).ShouldBe((6000, 4000));
            f.Q<string>("SELECT status FROM purchase_orders WHERE id = 'ipo'").ShouldBe("fully_converted");
            f.Q<(string Item, string Po)>("SELECT item_id AS Item, po_id AS Po FROM po_line_conversions WHERE id = 'pc1'").ShouldBe(("i1", "ipo"));   // filled by the trigger
        }),
        new("K3 removing a converted line reverses its stock, unless that stock was sold", f =>
        {
            f.ItemizedPo("ipo");
            f.PoLine("l1", "ipo", "i1", 10_000, 100);
            f.Exec("INSERT INTO po_line_conversions (id, po_line_id, site_id, qty_milli) VALUES ('pc1', 'l1', 's1', 6000)");
            f.Exec("INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s1', 'i1', -2000, 'adjustment')");
            f.Rejects(ErrorCode.ConversionReverseInsufficient, "DELETE FROM po_lines WHERE id = 'l1'");            // the cascade hits the guard
            f.Count("po_lines").ShouldBe(1);
            f.AddStock("s1", "i1", 2000);
            f.Exec("DELETE FROM po_lines WHERE id = 'l1'");
            f.Stock("s1", "i1").ShouldBe(0);
            f.Count("po_line_conversions").ShouldBe(0);
            f.Q<long>("SELECT total_amount_piasters FROM purchase_orders WHERE id = 'ipo'").ShouldBe(0);
        }),

        // ===== L. Deletes and cascades (3) ====================================================================================================
        new("L1 deleting a draft order removes its lines, attachments and attachment bytes", f =>
        {
            f.So("a");
            f.Line("a", "i1", 1000, 100);
            f.Exec("INSERT INTO order_attachments (id, order_type, order_id, storage_path, file_name, mime_type, byte_size) VALUES ('at', 'sale', 'a', 'sale/a/1.pdf', 'x.pdf', 'application/pdf', 3)");
            f.Exec("INSERT INTO attachment_blobs (storage_path, bytes) VALUES ('sale/a/1.pdf', x'010203')");
            f.Rejects(ErrorCode.AttachmentParentNotFound, "INSERT INTO order_attachments (order_type, order_id, storage_path, file_name, mime_type, byte_size) VALUES ('sale', 'ghost', 'sale/g/1.pdf', 'x.pdf', 'application/pdf', 3)");
            f.Exec("DELETE FROM sales_orders WHERE id = 'a'");
            (f.Count("sales_order_lines"), f.Count("order_attachments"), f.Count("attachment_blobs")).ShouldBe((0, 0, 0));
        }),
        new("L2 orders that were executed or paid cannot be deleted", f =>
        {
            f.AddStock("s1", "i1", 100_000);
            f.InvoicedSo("placed");
            f.Status("placed", "placed");
            f.Rejects(ErrorCode.OrderDeleteBlocked, "DELETE FROM sales_orders WHERE id = 'placed'");
            f.InvoicedSo("paid");
            f.Pay("so", "paid", 100, "instapay");
            f.Rejects(ErrorCode.OrderDeleteBlocked, "DELETE FROM sales_orders WHERE id = 'paid'");
            f.Po("p1", 5000, 1000);
            f.Pay("po", "p1", 100, "instapay");
            f.Rejects(ErrorCode.OrderDeleteBlocked, "DELETE FROM purchase_orders WHERE id = 'p1'");
            f.InvoicedSo("free");
            f.Exec("DELETE FROM sales_orders WHERE id = 'free'");                              // invoiced but untouched: deletable
            f.Count("sales_orders", "id = 'free'").ShouldBe(0);
        }),
        new("L3 deleting a purchase order reverses its conversions, unless the stock was sold", f =>
        {
            f.Po("p1", 100_000, 1000);
            f.Conv("c1", "p1", "s1", "i1", 50_000, 25_000);
            f.Exec("INSERT INTO stock_movements (site_id, item_id, qty_delta_milli, source_type) VALUES ('s1', 'i1', -10000, 'adjustment')");
            f.Rejects(ErrorCode.ConversionReverseInsufficient, "DELETE FROM purchase_orders WHERE id = 'p1'");
            f.Count("purchase_orders").ShouldBe(1);
            f.AddStock("s1", "i1", 10_000);
            f.Exec("DELETE FROM purchase_orders WHERE id = 'p1'");
            f.Stock("s1", "i1").ShouldBe(0);
            f.Count("po_conversions").ShouldBe(0);
        }),

        // ===== M. Audit contents (3) ==========================================================================================================
        new("M1 payments are audited with the actor on insert and on delete", f =>
        {
            f.InvoicedSo("a");
            f.Actor("adm");
            f.Pay("so", "a", 700, "instapay", id: "pay1");
            f.Exec("DELETE FROM payments WHERE id = 'pay1'");
            f.Actor(null);
            f.Q<(string Actor, string Action, long Amount, string Method)>("SELECT actor AS Actor, action AS Action, json_extract(data, '$.amount_piasters') AS Amount, json_extract(data, '$.method') AS Method FROM audit_log WHERE entity = 'payments' AND action = 'insert'")
                .ShouldBe(("adm", "insert", 700, "instapay"));
            f.Q<(string Actor, long Amount)>("SELECT actor AS Actor, json_extract(data, '$.amount_piasters') AS Amount FROM audit_log WHERE entity = 'payments' AND action = 'delete' AND entity_id = 'pay1'").ShouldBe(("adm", 700));
        }),
        new("M2 status changes, discounts, stock adjustments and manual cash are audited; ordinary sales movements are not", f =>
        {
            f.Actor("adm");
            f.AddStock("s1", "i1", 10_000);                          // 'adjustment' -> audited
            f.Fund("s1", 100, "float");                              // 'manual' -> audited
            f.InvoicedSo("a", qtyMilli: 1000);                       // status update -> audited
            f.Exec("UPDATE sales_orders SET discount_type = 'percent', discount_value_x100 = 1000 WHERE id = 'a'");
            f.Status("a", "placed");                                 // writes a 'sale' movement (not audited)
            f.Actor(null);
            f.Count("audit_log", "entity = 'stock_movements'").ShouldBe(1);
            f.Q<string>("SELECT json_extract(data, '$.reason') FROM audit_log WHERE entity = 'cash_movements'").ShouldBe("float");
            f.Count("audit_log", "entity = 'sales_orders' AND action = 'update' AND json_extract(data, '$.status') = 'invoiced'").ShouldBeGreaterThan(0);
            f.Count("audit_log", "entity = 'sales_orders' AND json_extract(data, '$.discount_type') = 'percent'").ShouldBeGreaterThan(0);
            f.Count("audit_log", "entity = 'sales_orders' AND json_extract(data, '$.status') = 'placed'").ShouldBe(1);
        }),
        new("M3 password hashes are never copied into the audit log", f =>
        {
            f.Actor("adm");
            f.Exec("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('n', 'newuser', 'New', 'staff', '$argon2id$v=19$m=1,t=1,p=1$SECRETSALT$SECRETHASH', 0)");
            f.Exec("UPDATE users SET full_name = 'Renamed', must_change_password = 1 WHERE id = 'n'");
            f.Actor(null);
            f.Count("audit_log", "entity = 'users' AND entity_id = 'n'").ShouldBe(2);
            f.Count("audit_log", "entity = 'users' AND (data LIKE '%SECRET%' OR data LIKE '%password_hash%' OR data LIKE '%argon2%')").ShouldBe(0);
        }),

        // ===== N. STRICT and CHECK rejections (4) ===============================================================================================
        new("N1 STRICT tables refuse a fraction or text in an integer column, and ranges are enforced", f =>
        {
            f.RejectedKind("INSERT INTO items (id, name_ar, unit_type, low_stock_threshold_milli) VALUES ('x', 'x', 'kg', 1.5)").ShouldBe(DbErrorKind.DataType);
            f.RejectedKind("INSERT INTO items (id, name_ar, unit_type, sale_price_piasters) VALUES ('x', 'x', 'kg', 'abc')").ShouldBe(DbErrorKind.DataType);
            f.RejectedKind("INSERT INTO items (id, name_ar, unit_type, sale_price_piasters) VALUES ('x', 'x', 'kg', 1000000001)").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO items (id, name_ar, unit_type) VALUES ('x', 'x', 'litre')").ShouldBe(DbErrorKind.Check);
            f.Count("items").ShouldBe(3);
        }),
        new("N2 instants must be UTC with milliseconds and dates must be real dates", f =>
        {
            f.RejectedKind("INSERT INTO sites (id, name_ar, created_at) VALUES ('x', 'x', 'yesterday')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO sites (id, name_ar, created_at) VALUES ('x', 'x', '2026-01-01T00:00:00+02:00')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO sites (id, name_ar, created_at) VALUES ('x', 'x', '2026-01-01 00:00:00')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO sales_orders (id, site_id, client_id, order_date) VALUES ('x', 's1', 'cl', '2026-02-30')").ShouldBe(DbErrorKind.Check);
            f.Exec("INSERT INTO sites (id, name_ar, created_at) VALUES ('ok', 'x', '2026-01-01T00:00:00.000Z')");
        }),
        new("N3 blank names, bad user names and unsupported password hashes are refused", f =>
        {
            f.RejectedKind("INSERT INTO sites (id, name_ar) VALUES ('x', '   ')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO contacts (id, type, name) VALUES ('x', 'client', '')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('x', 'Upper Case', 'x', 'staff', '$argon2id$x', 0)").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('x', 'plain', 'x', 'staff', 'hunter2', 0)").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('x', 'legacy', 'x', 'staff', '$2a$10$abcdefghijklmnopqrstuv', 0)").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('x', 'nohash', 'x', 'staff', NULL, 0)").ShouldBe(DbErrorKind.Check);
            f.Exec("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('x', 'pbkdf', 'x', 'staff', '$pbkdf2-sha256$i=600000$c2FsdA$aGFzaA', 0)");
        }),
        new("N4 one primary phone per contact, and a payment method must carry the details its kind needs", f =>
        {
            f.Exec("INSERT INTO contact_phones (contact_id, phone, is_primary) VALUES ('cl', '0100', 1)");
            f.Exec("INSERT INTO contact_phones (contact_id, phone, is_primary) VALUES ('cl', '0101', 0), ('cl', '0102', 0)");
            f.RejectedKind("INSERT INTO contact_phones (contact_id, phone, is_primary) VALUES ('cl', '0103', 1)").ShouldBe(DbErrorKind.Unique);
            f.RejectedKind("INSERT INTO contact_payment_methods (contact_id, method) VALUES ('cl', 'instapay')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO contact_payment_methods (contact_id, method, bank_name) VALUES ('cl', 'bank_transfer', 'NBE')").ShouldBe(DbErrorKind.Check);
            f.RejectedKind("INSERT INTO contact_payment_methods (contact_id, method) VALUES ('cl', 'credit')").ShouldBe(DbErrorKind.Check);
            f.Exec("INSERT INTO contact_payment_methods (contact_id, method, bank_name, account_number) VALUES ('cl', 'bank_transfer', 'NBE', '123')");
        }),

        // ===== O. Users (2) ===================================================================================================================
        new("O1 the last active administrator cannot be demoted, deactivated or deleted", f =>
        {
            f.Rejects(ErrorCode.LastAdmin, "UPDATE users SET active = 0 WHERE id = 'adm'");
            f.Rejects(ErrorCode.LastAdmin, "UPDATE users SET role = 'manager' WHERE id = 'adm'");
            f.Rejects(ErrorCode.LastAdmin, "DELETE FROM users WHERE id = 'adm'");
            f.Exec("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('adm2', 'adm2', 'Second', 'admin', '$argon2id$x', 0)");
            f.Exec("UPDATE users SET active = 0 WHERE id = 'adm'");        // a second admin exists now
            f.Q<long>("SELECT active FROM users WHERE id = 'adm'").ShouldBe(0);
        }),
        new("O2 an administrator cannot change their own role, and new users get the role's permission preset", f =>
        {
            f.Exec("INSERT INTO users (id, username, full_name, role, password_hash, must_change_password) VALUES ('adm2', 'adm2', 'Second', 'admin', '$argon2id$x', 0)");
            f.Actor("adm");
            f.Rejects(ErrorCode.SelfRoleChange, "UPDATE users SET role = 'staff' WHERE id = 'adm'");
            f.Rejects(ErrorCode.SelfRoleChange, "UPDATE users SET active = 0 WHERE id = 'adm'");
            f.Exec("UPDATE users SET role = 'staff' WHERE id = 'adm2'");    // someone else: fine
            f.Actor(null);
            f.Count("user_permissions", "user_id = 'stf' AND perm = 'payments.record'").ShouldBe(1);
            f.Count("user_permissions", "user_id = 'stf' AND perm = 'users.manage'").ShouldBe(0);
            f.Count("role_presets", "role = 'manager' AND perm = 'purchases.edit'").ShouldBe(0);      // L-22: PO editing is admin-only
        }),
    ];

    public static TheoryData<string, bool> Rows
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var recursive in new[] { false, true })
            {
                foreach (var c in Cases)
                {
                    data.Add(c.Name, recursive);
                }
            }

            return data;
        }
    }

    [Fact]
    public void The_suite_has_exactly_65_uniquely_named_assertions()
    {
        Cases.Count.ShouldBe(65);
        Cases.Select(c => c.Name).Distinct().Count().ShouldBe(65);
        Cases.Select(c => c.Name[..c.Name.IndexOf(' ', StringComparison.Ordinal)]).Distinct().Count().ShouldBe(65);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Assertion(string name, bool recursiveTriggers)
    {
        using var fx = new Fx(recursiveTriggers);
        Cases.Single(c => c.Name == name).Body(fx);
    }
}
