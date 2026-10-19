using System.Text;

namespace Shabana.Core.Formatting;

/// <summary>
/// The fixed number tables of 04 section 4.6. .NET formats <c>ar-EG</c> numbers with Latin digits, so nothing here consults the culture:
/// digits U+0660..0669, thousands U+066C, decimal U+066B, percent U+066A, minus U+2212. (Built from code points, not literals: SHB0001.)
/// </summary>
public static class ArabicNumber
{
    public const char ZeroDigit = (char)0x0660;
    public const char ThousandsSeparator = (char)0x066C;
    public const char DecimalSeparator = (char)0x066B;
    public const char PercentSign = (char)0x066A;
    public const char MinusSign = (char)0x2212;

    public static string FormatInteger(long value) => FormatScaled(value, 0, 0);

    /// <summary>
    /// Formats <c>value / 10^scale</c>: grouped integer part, then between <paramref name="minFraction"/> and <paramref name="scale"/> fraction
    /// digits (trailing zeros trimmed down to the minimum). Negative values get U+2212 in front. Exact for every <c>long</c>.
    /// </summary>
    public static string FormatScaled(long value, int scale, int minFraction)
    {
        if (scale < 0 || scale > 9 || minFraction < 0 || minFraction > scale)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var negative = value < 0;
        var magnitude = negative ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
        var divisor = 1UL;
        for (var i = 0; i < scale; i++)
        {
            divisor *= 10;
        }

        var whole = magnitude / divisor;
        var fraction = magnitude % divisor;

        var fractionDigits = new StringBuilder();
        if (scale > 0)
        {
            var text = fraction.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(scale, '0');
            var keep = scale;
            while (keep > minFraction && text[keep - 1] == '0')
            {
                keep--;
            }

            fractionDigits.Append(text, 0, keep);
        }

        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append(MinusSign);
        }

        AppendGrouped(sb, whole);
        if (fractionDigits.Length > 0)
        {
            sb.Append(DecimalSeparator);
            for (var i = 0; i < fractionDigits.Length; i++)
            {
                sb.Append(ToArabicDigit(fractionDigits[i]));
            }
        }

        return sb.ToString();
    }

    /// <summary>A year or other number that is never grouped (2026, not 2,026).</summary>
    public static string FormatUngrouped(long value)
    {
        var ascii = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder(ascii.Length);
        foreach (var c in ascii)
        {
            sb.Append(c == '-' ? MinusSign : ToArabicDigit(c));
        }

        return sb.ToString();
    }

    public static char ToArabicDigit(char asciiDigit) => (char)(ZeroDigit + (asciiDigit - '0'));

    private static void AppendGrouped(StringBuilder sb, ulong whole)
    {
        var ascii = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < ascii.Length; i++)
        {
            if (i > 0 && (ascii.Length - i) % 3 == 0)
            {
                sb.Append(ThousandsSeparator);
            }

            sb.Append(ToArabicDigit(ascii[i]));
        }
    }
}
