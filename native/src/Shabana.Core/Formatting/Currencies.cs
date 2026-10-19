using Shabana.Core.Resources;

namespace Shabana.Core.Formatting;

public sealed record CurrencyInfo(string Code, string Symbol, string Name);

/// <summary>The 12 two-decimal currencies of 02 section 3 (display only, no conversion). 3-decimal dinars are deliberately absent.</summary>
public static class Currencies
{
    public const string DefaultCode = "EGP";

    public static IReadOnlyList<CurrencyInfo> All { get; } =
    [
        new("EGP", Labels.CurrencySymbol.Egp, Labels.CurrencyName.Egp),
        new("SAR", Labels.CurrencySymbol.Sar, Labels.CurrencyName.Sar),
        new("AED", Labels.CurrencySymbol.Aed, Labels.CurrencyName.Aed),
        new("QAR", Labels.CurrencySymbol.Qar, Labels.CurrencyName.Qar),
        new("USD", Labels.CurrencySymbol.Usd, Labels.CurrencyName.Usd),
        new("EUR", Labels.CurrencySymbol.Eur, Labels.CurrencyName.Eur),
        new("TRY", Labels.CurrencySymbol.Try, Labels.CurrencyName.Try),
        new("MAD", Labels.CurrencySymbol.Mad, Labels.CurrencyName.Mad),
        new("LBP", Labels.CurrencySymbol.Lbp, Labels.CurrencyName.Lbp),
        new("ILS", Labels.CurrencySymbol.Ils, Labels.CurrencyName.Ils),
        new("DZD", Labels.CurrencySymbol.Dzd, Labels.CurrencyName.Dzd),
        new("SYP", Labels.CurrencySymbol.Syp, Labels.CurrencyName.Syp),
    ];

    public static bool IsSupported(string? code) => All.Any(c => c.Code == code);

    /// <summary>The currency for a code; an unknown or missing code falls back to EGP (as the web does: a bad setting must not blank every price).</summary>
    public static CurrencyInfo Get(string? code) => All.FirstOrDefault(c => c.Code == code) ?? All[0];
}
