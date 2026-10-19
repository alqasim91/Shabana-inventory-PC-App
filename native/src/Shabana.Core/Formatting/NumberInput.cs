using System.Globalization;
using System.Text;

namespace Shabana.Core.Formatting;

/// <summary>
/// Typed-number handling of 02 section 3 ("Numeric input", <c>lib/digits.ts</c>): Arabic-Indic and Persian digits are accepted,
/// U+066B, comma, U+060C and dot all mean the decimal point, U+066C and spaces are dropped, a leading minus is allowed where the caller says so.
/// Parsing is from text to scaled integers; no <c>double</c> is involved.
/// </summary>
public static class NumberInput
{
    private const int ArabicIndicZero = 0x0660;
    private const int ExtendedArabicIndicZero = 0x06F0;
    private const int ArabicDecimal = 0x066B;
    private const int ArabicThousands = 0x066C;
    private const int ArabicComma = 0x060C;
    private const int MinusSignCode = 0x2212;

    /// <summary>Folds Arabic/Persian digits to ASCII and the three decimal marks to '.'; drops thousands separators and spaces (web <c>toLatinDigits</c>).</summary>
    public static string ToLatinDigits(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (TryDigit(ch, out var d))
            {
                sb.Append((char)('0' + d));
            }
            else if (ch == ',' || ch == (char)ArabicDecimal || ch == (char)ArabicComma)
            {
                sb.Append('.');
            }
            else if (ch == (char)ArabicThousands || char.IsWhiteSpace(ch))
            {
                // dropped
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Live-typing filter of a numeric field (web <c>normalizeNumericInput</c>): digits folded, everything that is not part of a number removed,
    /// one dot kept, a minus kept only in first position. Half-finished input (<c>12.</c>, <c>-</c>, empty) passes through unchanged.
    /// </summary>
    public static string NormalizeForTyping(string? raw)
    {
        var latin = ToLatinDigits(raw);
        var sb = new StringBuilder(latin.Length);
        var seenDot = false;
        foreach (var ch in latin)
        {
            if (ch is >= '0' and <= '9')
            {
                sb.Append(ch);
            }
            else if (ch == '.' && !seenDot)
            {
                seenDot = true;
                sb.Append(ch);
            }
            else if ((ch == '-' || ch == (char)MinusSignCode) && sb.Length == 0)
            {
                sb.Append('-');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Text -&gt; integer scaled by 10^<paramref name="scale"/>, rounding half away from zero when more decimals were typed
    /// (<paramref name="rounded"/> is true if any dropped digit was non-zero). Stricter than the typing filter: any foreign character,
    /// a second decimal mark or no digit at all makes it fail instead of silently repairing the text.
    /// </summary>
    public static bool TryParseScaled(string? text, int scale, bool allowNegative, out long value, out bool rounded)
    {
        value = 0;
        rounded = false;
        if (string.IsNullOrWhiteSpace(text) || scale is < 0 or > 9)
        {
            return false;
        }

        var s = ToLatinDigits(text.Trim());
        var negative = false;
        if (s.StartsWith('-') || (s.Length > 0 && s[0] == (char)MinusSignCode))
        {
            if (!allowNegative)
            {
                return false;
            }

            negative = true;
            s = s[1..];
        }

        var whole = new StringBuilder();
        var frac = new StringBuilder();
        var seenDot = false;
        foreach (var ch in s)
        {
            if (ch is >= '0' and <= '9')
            {
                (seenDot ? frac : whole).Append(ch);
            }
            else if (ch == '.' && !seenDot)
            {
                seenDot = true;
            }
            else
            {
                return false;
            }
        }

        if (whole.Length + frac.Length == 0 || whole.Length > 19)
        {
            return false;
        }

        var kept = frac.Length >= scale ? frac.ToString(0, scale) : frac.ToString().PadRight(scale, '0');
        var dropped = frac.Length > scale ? frac.ToString(scale, frac.Length - scale) : string.Empty;

        Int128 magnitude;
        try
        {
            magnitude = checked(Int128.Parse("0" + whole + kept, CultureInfo.InvariantCulture));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (dropped.Length > 0)
        {
            rounded = dropped.Any(c => c != '0');
            if (dropped[0] >= '5')
            {
                magnitude += 1; // half away from zero: the sign is applied after
            }
        }

        var signed = negative ? -magnitude : magnitude;
        if (signed < long.MinValue || signed > long.MaxValue)
        {
            return false;
        }

        value = (long)signed;
        return true;
    }

    /// <summary>ASCII text of a scaled integer (<c>1234.56</c>); with <paramref name="trimZeros"/> the trailing zeros of the fraction are cut.</summary>
    public static string ToInvariantScaled(long value, int scale, bool trimZeros = false)
    {
        var negative = value < 0;
        var magnitude = negative ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
        var digits = magnitude.ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        var whole = scale == 0 ? digits : digits[..^scale];
        var frac = scale == 0 ? string.Empty : digits[^scale..];
        if (trimZeros)
        {
            frac = frac.TrimEnd('0');
        }

        return (negative ? "-" : string.Empty) + whole + (frac.Length > 0 ? "." + frac : string.Empty);
    }

    private static bool TryDigit(char ch, out int digit)
    {
        if (ch >= (char)ArabicIndicZero && ch <= (char)(ArabicIndicZero + 9))
        {
            digit = ch - ArabicIndicZero;
            return true;
        }

        if (ch >= (char)ExtendedArabicIndicZero && ch <= (char)(ExtendedArabicIndicZero + 9))
        {
            digit = ch - ExtendedArabicIndicZero;
            return true;
        }

        digit = 0;
        return false;
    }
}
