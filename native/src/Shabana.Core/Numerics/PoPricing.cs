namespace Shabana.Core.Numerics;

/// <summary>The three linked fields of a general purchase order.</summary>
public enum PriceField
{
    Kg,
    PricePerKg,
    Total,
}

public enum PoPricingIssue
{
    None,

    /// <summary>Total kg is missing or not greater than zero (<c>PO_FORM.kgRequired</c>).</summary>
    KgRequired,

    /// <summary>Price per kg or total is missing (<c>PO_FORM.amountsRequired</c>).</summary>
    AmountsRequired,
}

/// <summary>
/// Bidirectional PO pricing (02 section 7.6, rule 3). The form keeps a recency list of which field the user touched last;
/// the least recently touched of the three is the <i>derived</i> field and is recomputed after every edit from the other two:
/// <list type="bullet">
/// <item>derived <c>Total</c> = kg x price/kg (2 dp), only if kg &gt; 0 and price &gt;= 0;</item>
/// <item>derived <c>PricePerKg</c> = total / kg (2 dp), only if kg &gt; 0 and total &gt;= 0;</item>
/// <item>derived <c>Kg</c> = total / price (3 dp), only if price &gt; 0 and total &gt;= 0.</item>
/// </list>
/// When the precondition fails the derived field is cleared (null), exactly as the web form blanks it. The initial recency
/// (most recent first) is kg, price/kg, total, so <c>Total</c> is derived until the user touches it. Immutable: every edit returns a new state.
/// A derived value that would leave the stored range (<see cref="Limits"/>) or <c>long</c> is also cleared rather than wrapped.
/// </summary>
public sealed record PoPricing
{
    private PoPricing(Qty? kg, Money? pricePerKg, Money? total, PriceField newest, PriceField middle, PriceField derived)
    {
        Kg = kg;
        PricePerKg = pricePerKg;
        Total = total;
        Newest = newest;
        Middle = middle;
        Derived = derived;
    }

    public Qty? Kg { get; }

    public Money? PricePerKg { get; }

    public Money? Total { get; }

    /// <summary>Most recently edited field.</summary>
    public PriceField Newest { get; }

    public PriceField Middle { get; }

    /// <summary>Least recently edited field: the one that is computed (shown with the <c>PO_FORM.computed</c> badge).</summary>
    public PriceField Derived { get; }

    /// <summary>Empty form: recency kg, price/kg, total.</summary>
    public static PoPricing Initial { get; } = new(null, null, null, PriceField.Kg, PriceField.PricePerKg, PriceField.Total);

    /// <summary>A stored order opened for editing: values as stored, recency reset to the initial one.</summary>
    public static PoPricing FromStored(Qty kg, Money pricePerKg, Money total) =>
        new(kg, pricePerKg, total, PriceField.Kg, PriceField.PricePerKg, PriceField.Total);

    public PoPricing EditKg(Qty? value) => Edit(PriceField.Kg, value, PricePerKg, Total);

    public PoPricing EditPricePerKg(Money? value) => Edit(PriceField.PricePerKg, Kg, value, Total);

    public PoPricing EditTotal(Money? value) => Edit(PriceField.Total, Kg, PricePerKg, value);

    public PoPricingIssue Validate()
    {
        if (Kg is not { Milli: > 0 })
        {
            return PoPricingIssue.KgRequired;
        }

        return PricePerKg is null || Total is null ? PoPricingIssue.AmountsRequired : PoPricingIssue.None;
    }

    /// <summary>Stateless form of the rule for a caller that already knows which field is derived.</summary>
    public static (Qty? Kg, Money? PricePerKg, Money? Total) Solve(Qty? kg, Money? pricePerKg, Money? total, PriceField derived)
    {
        switch (derived)
        {
            case PriceField.Total:
                total = kg is { Milli: > 0 } k1 && pricePerKg is { Piasters: >= 0 } p1 && MoneyMath.TryLineTotal(k1, p1, out var t) && InMoneyRange(t) ? t : null;
                break;
            case PriceField.PricePerKg:
                pricePerKg = kg is { Milli: > 0 } k2 && total is { Piasters: >= 0 } t2 && TryDivide(t2.Piasters, k2.Milli, out var ppk) && ppk <= Limits.MaxStoredPiasters
                    ? new Money(ppk)
                    : null;
                break;
            default:
                kg = pricePerKg is { Piasters: > 0 } p3 && total is { Piasters: >= 0 } t3 && TryDivide(t3.Piasters, p3.Piasters, out var milli) && milli <= Limits.MaxStoredMilli
                    ? new Qty(milli)
                    : null;
                break;
        }

        return (kg, pricePerKg, total);
    }

    private PoPricing Edit(PriceField field, Qty? kg, Money? pricePerKg, Money? total)
    {
        // New recency: the edited field first, the other two keep their relative order.
        PriceField[] old = [Newest, Middle, Derived];
        var rest = old.Where(f => f != field).ToArray();
        var derived = rest[1];
        var solved = Solve(kg, pricePerKg, total, derived);
        return new PoPricing(solved.Kg, solved.PricePerKg, solved.Total, field, rest[0], derived);
    }

    private static bool InMoneyRange(Money m) => m.Piasters is >= 0 and <= Limits.MaxStoredPiasters;

    /// <summary><c>(a * 1000) / b</c> rounded half away from zero (a &gt;= 0, b &gt; 0); false when it does not fit in <c>long</c>.</summary>
    private static bool TryDivide(long a, long b, out long result) =>
        Rounding.TryToLong(Rounding.DivRoundHalfAwayFromZero((Int128)a * 1000, b), out result);
}
