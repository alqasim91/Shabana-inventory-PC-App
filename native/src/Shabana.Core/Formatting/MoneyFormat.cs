using Shabana.Core.Numerics;

namespace Shabana.Core.Formatting;

/// <summary>02 section 3 <c>formatMoney</c>: integer amounts without fraction, otherwise exactly two digits, then a space and the currency symbol.</summary>
public static class MoneyFormat
{
    /// <summary>12,345.50 and 500 in Arabic digits and separators, then the currency symbol; negative amounts carry U+2212 in front.</summary>
    public static string Format(Money amount, string? currencyCode = null) =>
        Number(amount) + " " + Currencies.Get(currencyCode).Symbol;

    /// <summary>The number part alone (no symbol).</summary>
    public static string Number(Money amount) =>
        ArabicNumber.FormatScaled(amount.Piasters, Money.Scale, amount.Piasters % 100 == 0 ? 0 : Money.Scale);

    /// <summary>Signed amount of the cash report / credit ledgers: <c>+</c> or U+2212, then the absolute amount. Zero is "+" (Reports.tsx uses <c>&gt;= 0</c>).</summary>
    public static string FormatSigned(Money amount, string? currencyCode = null)
    {
        var sign = amount.Piasters >= 0 ? '+' : ArabicNumber.MinusSign;
        var magnitude = amount.Piasters == long.MinValue ? throw new OverflowException() : amount.Abs();
        return sign + Format(magnitude, currencyCode);
    }
}
