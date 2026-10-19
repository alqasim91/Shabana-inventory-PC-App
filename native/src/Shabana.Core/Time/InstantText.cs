using NodaTime;
using NodaTime.Text;

namespace Shabana.Core.Time;

/// <summary>The stored form of an instant: UTC ISO text <c>YYYY-MM-DDTHH:MM:SS.sssZ</c> (04 section 7.3 type handlers, 03 section 2).</summary>
public static class InstantText
{
    private static readonly InstantPattern Pattern = InstantPattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'");

    public static string Format(Instant instant) => Pattern.Format(instant);

    public static bool TryParse(string? text, out Instant instant)
    {
        if (text is null)
        {
            instant = default;
            return false;
        }

        var result = Pattern.Parse(text);
        instant = result.Success ? result.Value : default;
        return result.Success;
    }

    public static Instant Parse(string text) => TryParse(text, out var i) ? i : throw new FormatException("Not a UTC ISO instant.");
}
