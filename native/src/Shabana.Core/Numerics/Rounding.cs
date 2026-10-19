namespace Shabana.Core.Numerics;

internal static class Rounding
{
    /// <summary>
    /// <c>numerator / denominator</c> rounded half away from zero (denominator &gt; 0). For non-negative numerators this is the
    /// integer round-half-up <c>(n + d/2) / d</c> of 03 section 2 (identical results, also for odd d), and it equals PostgreSQL
    /// <c>round(numeric, 2)</c> for every sign, which the plain formula with C# truncating division would not for negatives.
    /// </summary>
    public static Int128 DivRoundHalfAwayFromZero(Int128 numerator, Int128 denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "The divisor must be positive.");
        }

        var (quotient, remainder) = Int128.DivRem(numerator, denominator);
        if (Int128.Abs(remainder) * 2 >= denominator)
        {
            quotient += numerator < 0 ? -1 : 1;
        }

        return quotient;
    }

    /// <summary>Narrows to long or throws <see cref="OverflowException"/> (never wraps).</summary>
    public static long ToLongChecked(Int128 value) => checked((long)value);

    public static bool TryToLong(Int128 value, out long result)
    {
        if (value < long.MinValue || value > long.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (long)value;
        return true;
    }
}
