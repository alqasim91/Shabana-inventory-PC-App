using Shabana.Core.Numerics;
using Shabana.Core.Resources;

namespace Shabana.Core.Formatting;

/// <summary>02 section 3 <c>formatQty</c>: whole numbers as is, otherwise up to three decimals with trailing zeros trimmed, then an optional unit label.</summary>
public static class QtyFormat
{
    public static string Number(Qty qty) => ArabicNumber.FormatScaled(qty.Milli, Qty.Scale, 0);

    public static string Format(Qty qty, string? unitLabel = null) =>
        string.IsNullOrEmpty(unitLabel) ? Number(qty) : Number(qty) + " " + unitLabel;

    public static string Format(Qty qty, UnitType unit) => Format(qty, UnitLabel(unit));

    /// <summary>kg / unit labels from the labels file.</summary>
    public static string UnitLabel(UnitType unit) => unit switch
    {
        UnitType.Kg => Labels.UnitLabel.Kg,
        UnitType.Unit => Labels.UnitLabel.Unit,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };
}
