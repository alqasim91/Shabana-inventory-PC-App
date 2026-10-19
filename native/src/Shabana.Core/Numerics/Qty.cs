using Shabana.Core.Formatting;

namespace Shabana.Core.Numerics;

/// <summary>A quantity in milli-units (kg or units x 1000). Exact integer arithmetic; all arithmetic is checked.</summary>
public readonly record struct Qty(long Milli) : IComparable<Qty>
{
    public const int Scale = 3;

    public static Qty Zero => new(0);

    public static Qty FromWhole(long units) => new(checked(units * 1000));

    public bool IsZero => Milli == 0;

    public bool IsNegative => Milli < 0;

    public int Sign => Math.Sign(Milli);

    public Qty Abs() => new(checked(Math.Abs(Milli)));

    public static Qty Min(Qty a, Qty b) => a.Milli <= b.Milli ? a : b;

    public static Qty Max(Qty a, Qty b) => a.Milli >= b.Milli ? a : b;

    public static Qty operator +(Qty a, Qty b) => new(checked(a.Milli + b.Milli));

    public static Qty operator -(Qty a, Qty b) => new(checked(a.Milli - b.Milli));

    public static Qty operator -(Qty a) => new(checked(-a.Milli));

    public static bool operator <(Qty a, Qty b) => a.Milli < b.Milli;

    public static bool operator >(Qty a, Qty b) => a.Milli > b.Milli;

    public static bool operator <=(Qty a, Qty b) => a.Milli <= b.Milli;

    public static bool operator >=(Qty a, Qty b) => a.Milli >= b.Milli;

    public int CompareTo(Qty other) => Milli.CompareTo(other.Milli);

    public static bool TryParse(string? text, bool allowNegative, out Qty value, out bool rounded)
    {
        var ok = NumberInput.TryParseScaled(text, Scale, allowNegative, out var raw, out rounded);
        value = new Qty(raw);
        return ok;
    }

    public static bool TryParse(string? text, out Qty value) => TryParse(text, false, out value, out _);

    public static bool TryParse(string? text, out Qty value, out bool rounded) => TryParse(text, false, out value, out rounded);

    public static Qty Parse(string? text, bool allowNegative = false) =>
        TryParse(text, allowNegative, out var value, out _) ? value : throw new FormatException("Not a valid quantity.");

    /// <summary>ASCII decimal text with up to three decimals, trailing zeros trimmed (<c>2.5</c>, <c>40</c>). Not for display (use <see cref="QtyFormat"/>).</summary>
    public override string ToString() => NumberInput.ToInvariantScaled(Milli, Scale, trimZeros: true);
}
