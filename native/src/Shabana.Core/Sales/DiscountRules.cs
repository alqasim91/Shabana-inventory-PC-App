using Shabana.Core.Errors;
using Shabana.Core.Numerics;

namespace Shabana.Core.Sales;

public enum DiscountType
{
    None,
    Amount,
    Percent,
}

public enum DiscountCheck
{
    Ok,

    /// <summary>A type was chosen but the value is zero (<c>SO_FORM.discountRequired</c>; UI-level, no error code).</summary>
    ValueRequired,

    PercentOver100,
    ExceedsSubtotal,

    /// <summary>Collected money would exceed the new total.</summary>
    BelowPaid,
}

/// <summary>
/// Sales-order discount (rule 7 of 02 section 8.2, 03 section 2): <c>amount</c> is taken as typed, <c>percent</c> is
/// <c>round(subtotal x pct / 100, 2)</c> computed once; the result is clamped to [0, subtotal] so the total can never go negative.
/// The stored value is piasters for <c>amount</c> and basis points for <c>percent</c> (<c>discount_value_x100</c>).
/// </summary>
public static class DiscountRules
{
    /// <summary>Discount in money for the given type/value, clamped to [0, subtotal]. <c>None</c> gives 0.</summary>
    public static Money Amount(DiscountType type, long valueX100, Money subtotal)
    {
        var raw = type switch
        {
            DiscountType.None => 0,
            DiscountType.Amount => valueX100,
            DiscountType.Percent => MoneyMath.PercentDiscount(subtotal, new Bp(checked((int)Math.Min(valueX100, int.MaxValue)))).Piasters,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        return new Money(Math.Clamp(raw, 0, Math.Max(subtotal.Piasters, 0)));
    }

    /// <summary>Order total after discount.</summary>
    public static Money Total(DiscountType type, long valueX100, Money subtotal) => subtotal - Amount(type, valueX100, subtotal);

    /// <summary>
    /// The live checks of the form and the triggers (the value of a <c>None</c> discount is ignored; storage keeps it 0): percent &gt; 100 %, amount &gt; subtotal, type chosen without a value, and (when
    /// <paramref name="collected"/> is given) collected &gt; new total. Pass <paramref name="editing"/> = true for the admin rewind-and-replay, where the
    /// over-discount checks stand down (lines are rewritten first, B3).
    /// </summary>
    public static DiscountCheck Check(DiscountType type, long valueX100, Money subtotal, Money? collected = null, bool editing = false)
    {
        if (type != DiscountType.None)
        {
            if (valueX100 <= 0)
            {
                return DiscountCheck.ValueRequired;
            }

            if (type == DiscountType.Percent && valueX100 > Bp.Full.Value)
            {
                return DiscountCheck.PercentOver100;
            }

            if (!editing && type == DiscountType.Amount && valueX100 > subtotal.Piasters)
            {
                return DiscountCheck.ExceedsSubtotal;
            }
        }

        if (collected is { } paid && Total(type, valueX100, subtotal) < paid)
        {
            return DiscountCheck.BelowPaid;
        }

        return DiscountCheck.Ok;
    }

    /// <summary>The schema error code behind a failed check; null for <see cref="DiscountCheck.Ok"/> and the UI-only <see cref="DiscountCheck.ValueRequired"/>.</summary>
    public static ErrorCode? ToErrorCode(this DiscountCheck check) => check switch
    {
        DiscountCheck.PercentOver100 => ErrorCode.DiscountPercentOver100,
        DiscountCheck.ExceedsSubtotal => ErrorCode.DiscountExceedsSubtotal,
        DiscountCheck.BelowPaid => ErrorCode.DiscountBelowPaid,
        _ => null,
    };
}
