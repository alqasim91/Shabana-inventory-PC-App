using Shabana.Data.Maintenance;

namespace Shabana.Tests.Data;

/// <summary>
/// The 14 invariants are the oracle of the stress run, so they must not be vacuous: on a clean database every one holds, and for each one
/// there is a way to break exactly that rule (by switching off the trigger that normally guards it) that the check reports.
/// </summary>
public class LedgerInvariantTests
{
    public static TheoryData<string> Ids => new(LedgerInvariants.All.Select(i => i.Id));

    [Fact]
    public void There_are_fourteen_uniquely_named_invariants()
    {
        LedgerInvariants.All.Count.ShouldBe(14);
        LedgerInvariants.All.Select(i => i.Id).Distinct().Count().ShouldBe(14);
        LedgerInvariants.All.ShouldAllBe(i => i.ViolationSql.StartsWith("SELECT", StringComparison.Ordinal) || i.ViolationSql.TrimStart().StartsWith("WITH", StringComparison.Ordinal));
    }

    [Fact]
    public void A_freshly_built_and_a_busy_database_satisfy_all_of_them()
    {
        using var fx = new Fx(false);
        LedgerInvariants.Run(fx.Db).ShouldAllBe(r => r.Holds);
        fx.Po("p1", 100_000, 1000);
        fx.Conv("c1", "p1", "s1", "i1", 50_000, 25_000);
        fx.Fund("s1", 50_000);
        fx.AddStock("s1", "i1", 5000);
        fx.InvoicedSo("a", qtyMilli: 4000);
        fx.Status("a", "placed");
        fx.Pay("so", "a", 4000, "cash");
        fx.Exec("INSERT INTO stock_transfers (from_site, to_site, item_id, qty_milli) VALUES ('s1', 's2', 'i1', 100)");
        LedgerInvariants.Run(fx.Db).ShouldAllBe(r => r.Holds);
        IntegrityChecker.Full(fx.Db).IsClean.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Each_invariant_reports_the_violation_it_is_named_for(string id)
    {
        using var fx = new Fx(false);
        Break(fx, id);
        var results = LedgerInvariants.Run(fx.Db).ToDictionary(r => r.Id);
        results[id].Violations.ShouldBeGreaterThan(0, id);
        IntegrityChecker.Full(fx.Db).IsClean.ShouldBeFalse();
    }

    private static void Break(Fx f, string id)
    {
        switch (id)
        {
            case "stock_non_negative":
                f.Exec("DROP TRIGGER trg_stock_movements_floor");
                f.AddStock("s1", "i1", -5);
                break;
            case "cash_drawer_non_negative":
                f.Exec("DROP TRIGGER trg_cash_movements_floor");
                f.Fund("s1", -5, "overdraw");
                break;
            case "no_overpaid_order":
                f.InvoicedSo("a");
                f.Exec("DROP TRIGGER trg_payment_validate");
                f.Pay("so", "a", 10_001, "instapay");
                break;
            case "converted_not_over_total":
                f.Po("p1", 1000, 100);
                f.Exec("DROP TRIGGER trg_po_conversion_check");
                f.Conv("c1", "p1", "s1", "i1", 2000, 1);
                break;
            case "closed_is_fully_collected":
                f.So("a");
                f.Line("a", "i1", 1000, 1000);
                f.Actor("adm", editing: true);
                f.Status("a", "closed");
                break;
            case "so_total_is_subtotal_minus_discount":
                // The columns are generated, so the only way to break the rule is a different table shape (a hand-edited or damaged file):
                // replace the table by a plain copy of itself (legacy_alter_table keeps the views pointing at the NAME) and corrupt a total.
                f.So("x");
                f.Line("x", "i1", 1000, 1000);
                f.Exec("PRAGMA legacy_alter_table = ON");
                f.Exec("ALTER TABLE sales_orders RENAME TO sales_orders_generated");
                f.Exec("CREATE TABLE sales_orders AS SELECT * FROM sales_orders_generated");
                f.Exec("PRAGMA legacy_alter_table = OFF");
                f.Exec("UPDATE sales_orders SET total_amount_piasters = 999 WHERE id = 'x'");
                break;
            case "so_subtotal_is_sum_of_lines":
                f.So("a");
                f.Line("a", "i1", 1000, 1000);
                f.Exec("UPDATE sales_orders SET subtotal_piasters = 5 WHERE id = 'a'");
                break;
            case "cash_ledger_matches_cash_payments":
                f.InvoicedSo("a");
                f.Pay("so", "a", 500, "cash", "s1");
                f.Exec("DROP TRIGGER trg_cash_movements_append_only_del");
                f.Exec("DELETE FROM cash_movements WHERE source_type = 'payment'");
                break;
            case "sale_movements_match_placed_orders":
                f.AddStock("s1", "i1", 10_000);
                f.InvoicedSo("a", qtyMilli: 1000);
                f.Status("a", "placed");
                f.Exec("DROP TRIGGER trg_stock_movements_append_only_del");
                f.Exec("DELETE FROM stock_movements WHERE source_type = 'sale'");
                break;
            case "numbering_assigned":
                f.Po("p1", 1000, 100);
                f.Exec("UPDATE purchase_orders SET order_seq = 0 WHERE id = 'p1'");
                break;
            case "foreign_keys_clean":
                f.Exec("PRAGMA foreign_keys = OFF");
                f.Exec("INSERT INTO contact_phones (contact_id, phone) VALUES ('ghost', '0100')");
                break;
            case "credit_wallets_non_negative":
                f.Exec("DROP TRIGGER trg_client_credit_nonneg");
                f.Exec("INSERT INTO client_credits (contact_id, amount_delta_piasters, source_type) VALUES ('cl', -5, 'refund')");
                break;
            case "transfers_balanced":
                f.AddStock("s1", "i1", 1000);
                f.Exec("INSERT INTO stock_transfers (id, from_site, to_site, item_id, qty_milli) VALUES ('t', 's1', 's2', 'i1', 100)");
                f.Exec("DROP TRIGGER trg_stock_movements_append_only_del");
                f.Exec("DELETE FROM stock_movements WHERE source_type = 'transfer' AND qty_delta_milli > 0");
                break;
            case "conversions_posted_to_stock":
                f.Po("p1", 1000, 100);
                f.Conv("c1", "p1", "s1", "i1", 500, 7);
                f.Exec("DROP TRIGGER trg_stock_movements_append_only_del");
                f.Exec("DELETE FROM stock_movements WHERE source_type = 'po_conversion'");
                break;
            default:
                throw new InvalidOperationException("No way to break '" + id + "' is defined: add one.");
        }
    }
}
