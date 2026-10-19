using Shabana.Core.Resources;

namespace Shabana.Core.Formatting;

/// <summary>Human-facing order identifier (<c>lib/orderNo.ts</c>): the noun, a space and the running number in Arabic digits; the noun alone when there is no number yet.</summary>
public static class OrderNumber
{
    public static string Sales(long? seq) => Compose(Labels.Sales.OrderNoun, seq);

    public static string Purchase(long? seq) => Compose(Labels.Purchases.OrderNoun, seq);

    private static string Compose(string noun, long? seq) => seq is null ? noun : noun + " " + ArabicNumber.FormatInteger(seq.Value);
}
