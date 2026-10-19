namespace Shabana.Core.Numerics;

/// <summary>
/// Every derived money figure of the app, in exact integers (04 section 7.3, 03 section 2). Intermediates are <see cref="Int128"/>;
/// narrowing back to <c>long</c> is checked, so an out-of-range result throws <see cref="OverflowException"/> and never wraps.
/// Rounding is half away from zero (= round-half-up for the non-negative values of the schema, = PostgreSQL <c>round(numeric,2)</c> for all).
/// </summary>
public static class MoneyMath
{
    /// <summary>Line total = <c>round(qty x unit price, 2)</c> = <c>(qty_milli * price_piasters + 500) / 1000</c>.</summary>
    public static Money LineTotal(Qty qty, Money unitPrice) =>
        new(Rounding.ToLongChecked(Rounding.DivRoundHalfAwayFromZero((Int128)qty.Milli * unitPrice.Piasters, 1000)));

    /// <summary>As <see cref="LineTotal"/> but reports overflow of <c>long</c> as <c>false</c> instead of throwing.</summary>
    public static bool TryLineTotal(Qty qty, Money unitPrice, out Money total)
    {
        var ok = Rounding.TryToLong(Rounding.DivRoundHalfAwayFromZero((Int128)qty.Milli * unitPrice.Piasters, 1000), out var v);
        total = new Money(v);
        return ok;
    }

    /// <summary>Percent discount = <c>round(subtotal x pct / 100, 2)</c> = <c>(subtotal_piasters * bp + 5000) / 10000</c>. Not clamped (see <c>DiscountRules</c>).</summary>
    public static Money PercentDiscount(Money subtotal, Bp percent) =>
        new(Rounding.ToLongChecked(Rounding.DivRoundHalfAwayFromZero((Int128)subtotal.Piasters * percent.Value, 10_000)));

    /// <summary>Stock valuation of ONE row (03 C8): round per row, never <c>SUM(q) * cost</c>.</summary>
    public static Money StockValue(Qty qty, Money unitCost) => LineTotal(qty, unitCost);

    /// <summary>PO total from weight and price per kg.</summary>
    public static Money PoTotalFromKgAndPrice(Qty kg, Money perKg) => LineTotal(kg, perKg);

    /// <summary>Price per kg = total / kg, <c>(total*1000 + kg/2) / kg</c>. <paramref name="kg"/> must be &gt; 0.</summary>
    public static Money PoPricePerKgFromTotal(Money total, Qty kg) =>
        new(Rounding.ToLongChecked(Rounding.DivRoundHalfAwayFromZero((Int128)total.Piasters * 1000, kg.Milli)));

    /// <summary>Weight = total / price per kg, <c>(total*1000 + price/2) / price</c> in milli-kg. <paramref name="perKg"/> must be &gt; 0.</summary>
    public static Qty PoKgFromTotalAndPrice(Money total, Money perKg) =>
        new(Rounding.ToLongChecked(Rounding.DivRoundHalfAwayFromZero((Int128)total.Piasters * 1000, perKg.Piasters)));

    /// <summary>Itemized-PO line: unit price back-computed from a typed line total (qty is the anchor), 2 dp. <paramref name="qty"/> must be &gt; 0.</summary>
    public static Money UnitPriceFromLineTotal(Money lineTotal, Qty qty) => PoPricePerKgFromTotal(lineTotal, qty);
}
