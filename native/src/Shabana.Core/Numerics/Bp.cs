using Shabana.Core.Formatting;

namespace Shabana.Core.Numerics;

/// <summary>Percent x 100 (basis points): 12.5 % = 1250, 100 % = 10000.</summary>
public readonly record struct Bp(int Value) : IComparable<Bp>
{
    public const int Scale = 2;

    /// <summary>100 %.</summary>
    public static Bp Full => new(10_000);

    public static Bp Zero => new(0);

    public static bool operator <(Bp a, Bp b) => a.Value < b.Value;

    public static bool operator >(Bp a, Bp b) => a.Value > b.Value;

    public static bool operator <=(Bp a, Bp b) => a.Value <= b.Value;

    public static bool operator >=(Bp a, Bp b) => a.Value >= b.Value;

    public int CompareTo(Bp other) => Value.CompareTo(other.Value);

    /// <summary>Parses the typed percentage (<c>12.5</c>, or the same in Arabic-Indic digits): two decimals, half away from zero beyond that.</summary>
    public static bool TryParse(string? text, out Bp value, out bool rounded)
    {
        var ok = NumberInput.TryParseScaled(text, Scale, false, out var raw, out rounded) && raw <= int.MaxValue;
        value = ok ? new Bp((int)raw) : default;
        return ok;
    }

    public static bool TryParse(string? text, out Bp value) => TryParse(text, out value, out _);

    public override string ToString() => NumberInput.ToInvariantScaled(Value, Scale, trimZeros: true) + "%";
}
