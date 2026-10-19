namespace Shabana.Core.Numerics;

/// <summary>Range limits of 03 section 2 / 04 section 7.3, validated before insert (SQLite would silently turn an overflowing product into REAL).</summary>
public static class Limits
{
    /// <summary>Largest stored money total / PO price / discount value: 999,999,999,999 piasters = NUMERIC(12,2) max.</summary>
    public const long MaxStoredPiasters = 999_999_999_999;

    /// <summary>Largest stored quantity (PO total kg): 999,999,999,999 milli.</summary>
    public const long MaxStoredMilli = 999_999_999_999;

    /// <summary>Largest line quantity: 1,000,000.000 (1e9 milli), so qty x price stays below 1e18 &lt; 9.22e18.</summary>
    public const long MaxLineQtyMilli = 1_000_000_000;

    /// <summary>Largest unit price: 10,000,000.00 EGP (1e9 piasters).</summary>
    public const long MaxUnitPricePiasters = 1_000_000_000;

    public static bool IsValidLineQty(Qty qty) => qty.Milli is > 0 and <= MaxLineQtyMilli;

    public static bool IsValidUnitPrice(Money price) => price.Piasters is >= 0 and <= MaxUnitPricePiasters;

    public static bool IsValidStoredMoney(Money amount) => amount.Piasters is >= 0 and <= MaxStoredPiasters;

    public static bool IsValidStoredQty(Qty qty) => qty.Milli is >= 0 and <= MaxStoredMilli;
}
