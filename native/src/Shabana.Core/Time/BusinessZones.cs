using Shabana.Core.Resources;

namespace Shabana.Core.Time;

public sealed record BusinessZone(string Id, string Name);

/// <summary>The 18 Arab-region IANA zones a business may choose (02 section 3, <c>lib/locale.ts</c>). Anything else is refused on save.</summary>
public static class BusinessZones
{
    public const string DefaultId = "Africa/Cairo";

    public static IReadOnlyList<BusinessZone> All { get; } = new[]
    {
        "Africa/Cairo", "Asia/Riyadh", "Asia/Dubai", "Asia/Qatar", "Asia/Kuwait", "Asia/Bahrain", "Asia/Muscat", "Asia/Amman", "Asia/Beirut",
        "Asia/Damascus", "Asia/Baghdad", "Asia/Jerusalem", "Africa/Khartoum", "Africa/Tripoli", "Africa/Tunis", "Africa/Algiers",
        "Africa/Casablanca", "Europe/Istanbul",
    }.Select(id => new BusinessZone(id, Labels.TimezoneName.ByKey[id])).ToArray();

    public static bool IsSupported(string? id) => All.Any(z => string.Equals(z.Id, id, StringComparison.Ordinal));
}
