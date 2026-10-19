using System.Numerics;
using Shabana.Core.Numerics;

namespace Shabana.Tests.Core;

/// <summary>
/// P1 acceptance (2): <c>LineTotal</c> and <c>PercentDiscount</c> equal an independently computed round-half-up reference for >= 1,000,000 random
/// values each and never overflow silently (an unrepresentable result throws; a representable one is always exact). Two independent references:
/// BigInteger (floor-based formulation, a different algorithm from the Int128 remainder test used in production) and System.Decimal
/// (the textual definition <c>round(qty * price, 2)</c>, MidpointRounding.AwayFromZero).
/// </summary>
public class MoneyMathPropertyTests
{
    private const int Samples = 1_000_000;

    /// <summary>round-half-away-from-zero of n/d (d &gt; 0) using floor division only: floor((2|n| + d) / 2d) with the sign re-applied.</summary>
    private static BigInteger RefDivRound(BigInteger n, BigInteger d)
    {
        var sign = n.Sign < 0 ? -1 : 1;
        var a = BigInteger.Abs(n);
        return sign * BigInteger.Divide(2 * a + d, 2 * d);
    }

    private static long NextLong(Random r, long maxAbs, bool allowNegative)
    {
        var v = (long)(r.NextDouble() * maxAbs);
        if (r.Next(50) == 0)
        {
            v = maxAbs; // boundary values
        }

        return allowNegative && r.Next(8) == 0 ? -v : v;
    }

    private static void CheckLine(long qtyMilli, long price)
    {
        var expected = RefDivRound(new BigInteger(qtyMilli) * price, 1000);
        var fits = expected >= long.MinValue && expected <= long.MaxValue;
        if (fits)
        {
            MoneyMath.LineTotal(new Qty(qtyMilli), new Money(price)).Piasters.ShouldBe((long)expected, $"{qtyMilli} x {price}");
            MoneyMath.TryLineTotal(new Qty(qtyMilli), new Money(price), out var t).ShouldBeTrue();
            t.Piasters.ShouldBe((long)expected);
        }
        else
        {
            Should.Throw<OverflowException>(() => MoneyMath.LineTotal(new Qty(qtyMilli), new Money(price)), $"{qtyMilli} x {price}");
            MoneyMath.TryLineTotal(new Qty(qtyMilli), new Money(price), out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void LineTotal_equals_the_bigint_reference_for_a_million_values_across_the_database_domain()
    {
        var rnd = new Random(20261019);
        for (var i = 0; i < Samples; i++)
        {
            // 03 section 2 domain: qty <= 1e9 milli, unit price <= 1e9 piasters
            CheckLine(NextLong(rnd, Limits.MaxLineQtyMilli, true), NextLong(rnd, Limits.MaxUnitPricePiasters, false));
        }
    }

    [Fact]
    public void LineTotal_equals_the_bigint_reference_for_a_million_values_over_the_whole_long_range_and_throws_instead_of_overflowing()
    {
        var rnd = new Random(77);
        var overflowed = 0;
        for (var i = 0; i < Samples; i++)
        {
            // magnitudes from tiny to the full long range: a random power of two keeps small AND huge operands in the mix
            var q = NextLong(rnd, long.MaxValue >> rnd.Next(0, 62), true);
            var p = NextLong(rnd, long.MaxValue >> rnd.Next(0, 62), true);
            CheckLine(q, p);
            if (new BigInteger(q) * p / 1000 is var big && (big > long.MaxValue || big < long.MinValue))
            {
                overflowed++;
            }
        }

        overflowed.ShouldBeGreaterThan(1000, "the generator must actually exercise the overflow path");
    }

    [Fact]
    public void LineTotal_equals_the_decimal_definition_round_qty_times_price_to_2dp()
    {
        var rnd = new Random(31337);
        for (var i = 0; i < Samples; i++)
        {
            var q = NextLong(rnd, 1_000_000_000_000, true);
            var p = NextLong(rnd, 1_000_000_000_000, false);
            var exact = q / 1000m * (p / 100m); // exact in decimal: 3 + 2 decimals, at most 25 digits
            var expected = Math.Round(exact, 2, MidpointRounding.AwayFromZero) * 100m;
            if (Math.Abs(expected) > long.MaxValue)
            {
                Should.Throw<OverflowException>(() => MoneyMath.LineTotal(new Qty(q), new Money(p)));
                continue;
            }

            MoneyMath.LineTotal(new Qty(q), new Money(p)).Piasters.ShouldBe((long)expected, $"{q} x {p}");
        }
    }

    [Fact]
    public void LineTotal_exact_halves_round_up_never_down_and_never_to_even()
    {
        // price odd x qty 500 = exactly half a piaster; half-even would give 0 for 0.5, 2 for 1.5 ...
        for (long k = 0; k < 2000; k++)
        {
            var price = 2 * k + 1;
            MoneyMath.LineTotal(new Qty(500), new Money(price)).Piasters.ShouldBe(k + 1);
            MoneyMath.LineTotal(new Qty(-500), new Money(price)).Piasters.ShouldBe(-(k + 1)); // away from zero, like PostgreSQL round()
        }
    }

    [Fact]
    public void LineTotal_extremes()
    {
        Should.Throw<OverflowException>(() => MoneyMath.LineTotal(new Qty(long.MaxValue), new Money(long.MaxValue)));
        Should.Throw<OverflowException>(() => MoneyMath.LineTotal(new Qty(long.MinValue), new Money(long.MaxValue)));
        Should.Throw<OverflowException>(() => MoneyMath.LineTotal(new Qty(long.MinValue), new Money(long.MinValue)));
        // the largest legal line (1e9 milli x 1e9 piasters = 1e18/1e3) is exact
        MoneyMath.LineTotal(new Qty(Limits.MaxLineQtyMilli), new Money(Limits.MaxUnitPricePiasters)).Piasters.ShouldBe(1_000_000_000_000_000);
        MoneyMath.LineTotal(new Qty(0), new Money(long.MaxValue)).Piasters.ShouldBe(0);
        MoneyMath.LineTotal(new Qty(long.MaxValue), new Money(0)).Piasters.ShouldBe(0);
        // 9.2e18 x 1000 / 1000 = long.MaxValue: representable and exact
        MoneyMath.LineTotal(new Qty(1000), new Money(long.MaxValue)).Piasters.ShouldBe(long.MaxValue);
    }

    private static void CheckPercent(long subtotal, long bp)
    {
        var expected = RefDivRound(new BigInteger(subtotal) * bp, 10_000);
        var fits = expected >= long.MinValue && expected <= long.MaxValue;
        if (fits)
        {
            MoneyMath.PercentDiscount(new Money(subtotal), new Bp((int)bp)).Piasters.ShouldBe((long)expected, $"{subtotal} x {bp}bp");
        }
        else
        {
            Should.Throw<OverflowException>(() => MoneyMath.PercentDiscount(new Money(subtotal), new Bp((int)bp)));
        }
    }

    [Fact]
    public void PercentDiscount_equals_the_bigint_reference_for_a_million_values_across_the_database_domain()
    {
        var rnd = new Random(424242);
        for (var i = 0; i < Samples; i++)
        {
            CheckPercent(NextLong(rnd, Limits.MaxStoredPiasters, false), rnd.Next(0, 10_001));
        }
    }

    [Fact]
    public void PercentDiscount_equals_the_bigint_reference_for_a_million_values_over_the_whole_range_and_throws_instead_of_overflowing()
    {
        var rnd = new Random(99);
        var overflowed = 0;
        for (var i = 0; i < Samples; i++)
        {
            var s = NextLong(rnd, long.MaxValue >> rnd.Next(0, 62), true);
            var bp = rnd.Next(0, 4) == 0 ? rnd.Next(int.MinValue, int.MaxValue) : rnd.Next(0, 20_001);
            CheckPercent(s, bp);
            if (new BigInteger(s) * bp / 10_000 is var big && (big > long.MaxValue || big < long.MinValue))
            {
                overflowed++;
            }
        }

        overflowed.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public void PercentDiscount_equals_the_decimal_definition_round_subtotal_times_pct_over_100_to_2dp()
    {
        var rnd = new Random(2468);
        for (var i = 0; i < Samples; i++)
        {
            var s = NextLong(rnd, Limits.MaxStoredPiasters, false);
            var bp = rnd.Next(0, 10_001);
            var exact = s / 100m * (bp / 100m) / 100m; // round(subtotal * pct / 100, 2) with pct = bp / 100
            var expected = (long)(Math.Round(exact, 2, MidpointRounding.AwayFromZero) * 100m);
            MoneyMath.PercentDiscount(new Money(s), new Bp(bp)).Piasters.ShouldBe(expected, $"{s} x {bp}bp");
        }
    }

    [Fact]
    public void PercentDiscount_bounds()
    {
        MoneyMath.PercentDiscount(new Money(Limits.MaxStoredPiasters), Bp.Full).Piasters.ShouldBe(Limits.MaxStoredPiasters);
        MoneyMath.PercentDiscount(new Money(Limits.MaxStoredPiasters), Bp.Zero).Piasters.ShouldBe(0);
        Should.Throw<OverflowException>(() => MoneyMath.PercentDiscount(new Money(long.MaxValue), new Bp(int.MaxValue)));
    }

    [Fact]
    public void Division_helpers_match_the_bigint_reference()
    {
        var rnd = new Random(5);
        for (var i = 0; i < 200_000; i++)
        {
            var total = NextLong(rnd, Limits.MaxStoredPiasters, false);
            var kg = Math.Max(1, NextLong(rnd, Limits.MaxStoredMilli, false));
            MoneyMath.PoPricePerKgFromTotal(new Money(total), new Qty(kg)).Piasters.ShouldBe((long)RefDivRound(new BigInteger(total) * 1000, kg), $"{total}/{kg}");
            MoneyMath.PoKgFromTotalAndPrice(new Money(total), new Money(kg)).Milli.ShouldBe((long)RefDivRound(new BigInteger(total) * 1000, kg));
            // 04 section 7.3 spelling of the same formulas
            var oddHalf = (long)((new BigInteger(total) * 1000 + kg / 2) / kg);
            MoneyMath.PoPricePerKgFromTotal(new Money(total), new Qty(kg)).Piasters.ShouldBe(oddHalf);
        }

        Should.Throw<ArgumentOutOfRangeException>(() => MoneyMath.PoPricePerKgFromTotal(new Money(1), new Qty(0)));
        Should.Throw<ArgumentOutOfRangeException>(() => MoneyMath.PoKgFromTotalAndPrice(new Money(1), new Money(0)));
    }
}
