using System.Numerics;
using Shabana.Core.Numerics;

namespace Shabana.Tests.Core;

/// <summary>P1 acceptance (3): bidirectional PO pricing (02 section 7.6, rule 3): any two of (price per kg, total kg, total) determine the third, with the recency rule.</summary>
public class PoPricingTests
{
    private static readonly PriceField[] All = [PriceField.Kg, PriceField.PricePerKg, PriceField.Total];

    private static Qty K(string s) => Qty.Parse(s);

    private static Money M(string s) => Money.Parse(s);

    private static PoPricing Edit(PoPricing p, PriceField f, string? text) => f switch
    {
        PriceField.Kg => p.EditKg(text is null ? null : K(text)),
        PriceField.PricePerKg => p.EditPricePerKg(text is null ? null : M(text)),
        _ => p.EditTotal(text is null ? null : M(text)),
    };

    private static BigInteger Ref(BigInteger n, BigInteger d) => (2 * n + d) / (2 * d); // non-negative half-up

    // ---- the worked examples ----

    [Fact]
    public void Initial_state_derives_the_total_first()
    {
        PoPricing.Initial.Derived.ShouldBe(PriceField.Total);
        PoPricing.Initial.Middle.ShouldBe(PriceField.PricePerKg);
        PoPricing.Initial.Newest.ShouldBe(PriceField.Kg);
        PoPricing.Initial.Kg.ShouldBeNull();
        PoPricing.Initial.Validate().ShouldBe(PoPricingIssue.KgRequired);
    }

    [Fact]
    public void Kg_and_price_give_the_total()
    {
        var p = PoPricing.Initial.EditKg(K("100")).EditPricePerKg(M("12.50"));
        p.Total.ShouldBe(M("1250.00"));
        p.Derived.ShouldBe(PriceField.Total);
        p.Validate().ShouldBe(PoPricingIssue.None);
    }

    [Fact]
    public void Kg_and_total_give_the_price_per_kg()
    {
        // user types kg, then total: the price (oldest of the three touches) is the derived one
        var p = PoPricing.Initial.EditKg(K("3")).EditTotal(M("10.00"));
        p.Derived.ShouldBe(PriceField.PricePerKg);
        p.PricePerKg.ShouldBe(M("3.33")); // 10.00 / 3 = 3.3333 -> 2 dp
        p.Total.ShouldBe(M("10.00"));
    }

    [Fact]
    public void Price_and_total_give_the_kg_to_three_decimals()
    {
        var p = PoPricing.Initial.EditPricePerKg(M("3.33")).EditTotal(M("10.00"));
        p.Derived.ShouldBe(PriceField.Kg);
        p.Kg.ShouldBe(K("3.003")); // 10.00 / 3.33 = 3.003003 -> 3 dp
    }

    [Fact]
    public void Editing_the_derived_total_makes_the_middle_field_derived_not_the_kg()
    {
        // recency kg, ppk, total.  Type kg and ppk (total computed), then overwrite the total by hand:
        var p = PoPricing.Initial.EditKg(K("100")).EditPricePerKg(M("12.50"));
        p.Total.ShouldBe(M("1250.00"));
        p = p.EditTotal(M("1300"));
        p.Newest.ShouldBe(PriceField.Total);
        p.Middle.ShouldBe(PriceField.PricePerKg);
        p.Derived.ShouldBe(PriceField.Kg); // ppk was touched later than kg: kg is the least recent
        p.Kg.ShouldBe(K("104.000")); // 1300 / 12.50
        p.PricePerKg.ShouldBe(M("12.50"));
    }

    [Fact]
    public void Recency_rule_the_least_recently_touched_field_is_the_one_recomputed()
    {
        var p = PoPricing.Initial.EditTotal(M("1000")).EditKg(K("50")); // total, then kg: ppk derived
        p.Derived.ShouldBe(PriceField.PricePerKg);
        p.PricePerKg.ShouldBe(M("20.00"));
        p = p.EditPricePerKg(M("25")); // recency now [ppk, kg, total]: the total is the oldest
        p.Derived.ShouldBe(PriceField.Total);
        p.Total.ShouldBe(M("1250.00"));
        p = p.EditTotal(M("2000")); // [total, ppk, kg] -> kg = 2000 / 25
        p.Derived.ShouldBe(PriceField.Kg);
        p.Kg.ShouldBe(K("80"));
        p = p.EditKg(K("40")); // [kg, total, ppk] -> ppk = 2000 / 40
        p.Derived.ShouldBe(PriceField.PricePerKg);
        p.PricePerKg.ShouldBe(M("50.00"));
    }

    [Fact]
    public void Derived_field_is_cleared_when_its_precondition_fails()
    {
        // kg must be > 0 for total and price; price > 0 for kg; empty input clears
        var p = PoPricing.Initial.EditKg(K("0")).EditPricePerKg(M("10"));
        p.Total.ShouldBeNull();
        PoPricing.Initial.EditKg(K("5")).EditPricePerKg(M("0")).Total.ShouldBe(M("0.00")); // price >= 0 is enough for the total
        PoPricing.Initial.EditKg(K("5")).EditPricePerKg(null).Total.ShouldBeNull();
        PoPricing.Initial.EditKg(null).EditPricePerKg(M("10")).Total.ShouldBeNull();

        var byTotal = PoPricing.Initial.EditKg(K("0")).EditTotal(M("10")); // ppk derived, kg = 0 -> cleared
        byTotal.PricePerKg.ShouldBeNull();

        var byPrice = PoPricing.Initial.EditPricePerKg(M("0")).EditTotal(M("10")); // kg derived, ppk = 0 -> cleared
        byPrice.Kg.ShouldBeNull();
    }

    [Fact]
    public void Derived_value_outside_the_stored_range_is_cleared_not_wrapped()
    {
        var p = PoPricing.Initial.EditKg(Qty.FromWhole(999_999_999)).EditPricePerKg(M("9999999999.99")); // total would be ~1e19 piasters
        p.Total.ShouldBeNull();
        PoPricing.Initial.EditPricePerKg(M("0.01")).EditTotal(M("9999999999.99")).Kg.ShouldBeNull(); // kg > 999,999,999,999 milli
        PoPricing.Initial.EditKg(K("0.001")).EditTotal(M("9999999999.99")).PricePerKg.ShouldBeNull(); // ppk > stored max
    }

    [Fact]
    public void Validate_follows_the_form_rules()
    {
        PoPricing.Initial.EditKg(K("1")).Validate().ShouldBe(PoPricingIssue.AmountsRequired);
        PoPricing.Initial.EditKg(K("1")).EditPricePerKg(M("2")).Validate().ShouldBe(PoPricingIssue.None);
        PoPricing.Initial.EditKg(K("0")).EditPricePerKg(M("2")).Validate().ShouldBe(PoPricingIssue.KgRequired);
        PoPricing.Initial.EditPricePerKg(M("2")).EditTotal(M("10")).Validate().ShouldBe(PoPricingIssue.None); // kg derived = 5
    }

    [Fact]
    public void Opening_a_stored_order_restores_values_and_resets_recency()
    {
        var p = PoPricing.FromStored(K("100"), M("12.5"), M("1250"));
        p.Derived.ShouldBe(PriceField.Total);
        p.Validate().ShouldBe(PoPricingIssue.None);
        p.EditKg(K("200")).Total.ShouldBe(M("2500")); // editing kg recomputes the total from the unchanged price
    }

    // ---- exhaustive / property tests ----

    [Fact]
    public void Any_two_fields_determine_the_third_in_every_order_of_entry()
    {
        var rnd = new Random(1234);
        for (var i = 0; i < 20_000; i++)
        {
            var kg = new Qty(1 + rnd.NextInt64(0, 100_000_000)); // up to 100,000.000 kg
            var ppk = new Money(rnd.NextInt64(1, 10_000_000)); // up to 100,000.00 per kg
            var total = new Money(rnd.NextInt64(0, 1_000_000_000));

            foreach (var (a, b) in Pairs())
            {
                var third = All.Single(f => f != a && f != b);
                var p = PoPricing.Initial;
                p = Apply(p, a, kg, ppk, total);
                p = Apply(p, b, kg, ppk, total);
                p.Derived.ShouldBe(third, $"order {a},{b}");

                switch (third)
                {
                    case PriceField.Total:
                        p.Total!.Value.Piasters.ShouldBe((long)Ref(new BigInteger(kg.Milli) * ppk.Piasters, 1000));
                        break;
                    case PriceField.PricePerKg:
                        p.PricePerKg!.Value.Piasters.ShouldBe((long)Ref(new BigInteger(total.Piasters) * 1000, kg.Milli));
                        break;
                    default:
                        p.Kg!.Value.Milli.ShouldBe((long)Ref(new BigInteger(total.Piasters) * 1000, ppk.Piasters));
                        break;
                }
            }
        }
    }

    private static IEnumerable<(PriceField A, PriceField B)> Pairs() =>
        from a in All from b in All where a != b select (a, b);

    private static PoPricing Apply(PoPricing p, PriceField f, Qty kg, Money ppk, Money total) => f switch
    {
        PriceField.Kg => p.EditKg(kg),
        PriceField.PricePerKg => p.EditPricePerKg(ppk),
        _ => p.EditTotal(total),
    };

    [Fact]
    public void Recency_list_matches_a_model_implementation_for_random_edit_sequences()
    {
        var rnd = new Random(99);
        for (var run = 0; run < 3000; run++)
        {
            var p = PoPricing.Initial;
            var model = new List<PriceField> { PriceField.Kg, PriceField.PricePerKg, PriceField.Total }; // most recent first
            for (var step = 0; step < 12; step++)
            {
                var f = All[rnd.Next(3)];
                var text = rnd.Next(10) == 0 ? null : (rnd.Next(1, 100_000) / 100.0m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                p = Edit(p, f, text);
                model.Remove(f);
                model.Insert(0, f);
                p.Newest.ShouldBe(model[0]);
                p.Middle.ShouldBe(model[1]);
                p.Derived.ShouldBe(model[2]);

                // the two fields that are NOT derived hold exactly what the user typed last
                if (f == PriceField.Kg && text is not null)
                {
                    p.Kg.ShouldBe(K(text));
                }

                if (f == PriceField.PricePerKg && text is not null)
                {
                    p.PricePerKg.ShouldBe(M(text));
                }

                if (f == PriceField.Total && text is not null)
                {
                    p.Total.ShouldBe(M(text));
                }
            }
        }
    }

    [Fact]
    public void Solve_is_the_stateless_form_of_the_same_rule()
    {
        var (kg, ppk, total) = PoPricing.Solve(K("100"), M("12.5"), null, PriceField.Total);
        total.ShouldBe(M("1250"));
        kg.ShouldBe(K("100"));
        ppk.ShouldBe(M("12.5"));
        PoPricing.Solve(null, M("12.5"), M("1250"), PriceField.Kg).Kg.ShouldBe(K("100"));
        PoPricing.Solve(K("100"), null, M("1250"), PriceField.PricePerKg).PricePerKg.ShouldBe(M("12.5"));
    }

    [Fact]
    public void Itemized_line_helpers_use_the_same_rounding()
    {
        // 02 section 7.6: unit mode total = qty x price (2 dp); total mode price = total / qty (2 dp)
        MoneyMath.LineTotal(K("2.5"), M("19.99")).ShouldBe(M("49.98")); // 49.975 -> 49.98
        MoneyMath.UnitPriceFromLineTotal(M("100"), K("3")).ShouldBe(M("33.33"));
        MoneyMath.UnitPriceFromLineTotal(M("100"), K("6")).ShouldBe(M("16.67"));
    }
}
