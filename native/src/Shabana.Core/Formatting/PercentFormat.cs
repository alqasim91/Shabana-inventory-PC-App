using Shabana.Core.Numerics;

namespace Shabana.Core.Formatting;

/// <summary>02 section 3 percent: the number in Arabic digits followed by U+066A (10 and 12.5 percent in Arabic digits).</summary>
public static class PercentFormat
{
    public static string Format(Bp percent) =>
        ArabicNumber.FormatScaled(percent.Value, Bp.Scale, 0) + ArabicNumber.PercentSign;
}
