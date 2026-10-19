using Shabana.Core.Formatting;

namespace Shabana.Core.Numerics;

/// <summary>An amount in piasters (EGP x 100). Exact integer arithmetic; never a <c>double</c> or <c>decimal</c> (DIV-12). All arithmetic is checked.</summary>
public readonly record struct Money(long Piasters) : IComparable<Money>
{
    public const int Scale = 2;

    public static Money Zero => new(0);

    public static Money FromPounds(long pounds) => new(checked(pounds * 100));

    public bool IsZero => Piasters == 0;

    public bool IsNegative => Piasters < 0;

    public int Sign => Math.Sign(Piasters);

    public Money Abs() => new(checked(Math.Abs(Piasters)));

    public static Money Min(Money a, Money b) => a.Piasters <= b.Piasters ? a : b;

    public static Money Max(Money a, Money b) => a.Piasters >= b.Piasters ? a : b;

    public static Money operator +(Money a, Money b) => new(checked(a.Piasters + b.Piasters));

    public static Money operator -(Money a, Money b) => new(checked(a.Piasters - b.Piasters));

    public static Money operator -(Money a) => new(checked(-a.Piasters));

    public static bool operator <(Money a, Money b) => a.Piasters < b.Piasters;

    public static bool operator >(Money a, Money b) => a.Piasters > b.Piasters;

    public static bool operator <=(Money a, Money b) => a.Piasters <= b.Piasters;

    public static bool operator >=(Money a, Money b) => a.Piasters >= b.Piasters;

    public int CompareTo(Money other) => Piasters.CompareTo(other.Piasters);

    public static Money Sum(IEnumerable<Money> values)
    {
        long total = 0;
        foreach (var v in values)
        {
            total = checked(total + v.Piasters);
        }

        return new Money(total);
    }

    /// <summary>
    /// Parses typed text (Arabic-Indic / Persian / ASCII digits; U+066B, comma, U+060C or dot as the decimal point; U+066C and spaces ignored) into piasters.
    /// More than two decimals are rounded half away from zero and <paramref name="rounded"/> tells the form to show its rounding hint.
    /// </summary>
    public static bool TryParse(string? text, bool allowNegative, out Money value, out bool rounded)
    {
        var ok = NumberInput.TryParseScaled(text, Scale, allowNegative, out var raw, out rounded);
        value = new Money(raw);
        return ok;
    }

    public static bool TryParse(string? text, out Money value) => TryParse(text, false, out value, out _);

    public static bool TryParse(string? text, out Money value, out bool rounded) => TryParse(text, false, out value, out rounded);

    public static Money Parse(string? text, bool allowNegative = false) =>
        TryParse(text, allowNegative, out var value, out _) ? value : throw new FormatException("Not a valid amount.");

    /// <summary>04 section 7.3 name for <see cref="Parse"/>.</summary>
    public static Money FromDecimalText(string? text, bool allowNegative = false) => Parse(text, allowNegative);

    /// <summary>ASCII decimal text with two decimals (<c>1234.56</c>, <c>-0.05</c>): for logs, tests and storage-neutral exchange. Not for display (use <see cref="MoneyFormat"/>).</summary>
    public override string ToString() => NumberInput.ToInvariantScaled(Piasters, Scale);
}
