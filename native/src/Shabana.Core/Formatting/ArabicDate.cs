using System.Globalization;
using Shabana.Core.Resources;

namespace Shabana.Core.Formatting;

/// <summary>
/// Calendar dates (<c>YYYY-MM-DD</c> strings / <see cref="DateOnly"/>) with the app's own month table; never time-zone shifted (02 section 3).
/// Instants are converted to the business date by <c>BusinessCalendar.DateOf</c> first.
/// </summary>
public static class ArabicDate
{
    /// <summary>Month name, 1..12.</summary>
    public static string MonthName(int month) =>
        month is >= 1 and <= 12 ? Labels.Month.ByKey[month.ToString(CultureInfo.InvariantCulture)] : throw new ArgumentOutOfRangeException(nameof(month));

    /// <summary>Day + month, e.g. 5 September in Arabic digits and month name.</summary>
    public static string Short(DateOnly date) => ArabicNumber.FormatUngrouped(date.Day) + " " + MonthName(date.Month);

    /// <summary>Day + month + year, e.g. 5 September 2026 in Arabic digits and month name.</summary>
    public static string Long(DateOnly date) => Short(date) + " " + ArabicNumber.FormatUngrouped(date.Year);

    /// <summary>ISO weekday label: 1 = Monday ... 7 = Sunday.</summary>
    public static string WeekdayName(DateOnly date) => Labels.WeekdayLabel.ByKey[IsoWeekday(date).ToString(CultureInfo.InvariantCulture)];

    public static int IsoWeekday(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    /// <summary>The stored form, <c>YYYY-MM-DD</c>.</summary>
    public static string ToIso(DateOnly date) => date.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

    /// <summary>Strict <c>YYYY-MM-DD</c> (also accepts the date part of a longer ISO timestamp, like the web's <c>toDate</c>).</summary>
    public static bool TryParseIso(string? text, out DateOnly date)
    {
        date = default;
        if (text is null)
        {
            return false;
        }

        var t = text.Length > 10 && text[10] == 'T' ? text[..10] : text;
        return DateOnly.TryParseExact(t, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static string Short(string iso) => TryParseIso(iso, out var d) ? Short(d) : throw new FormatException("Not an ISO date.");

    public static string Long(string iso) => TryParseIso(iso, out var d) ? Long(d) : throw new FormatException("Not an ISO date.");
}
