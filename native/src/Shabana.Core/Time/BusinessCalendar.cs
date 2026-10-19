using NodaTime;

namespace Shabana.Core.Time;

/// <summary>A business day as a half-open UTC interval <c>[StartUtc, EndUtc)</c>; 23, 24 or 25 hours long across a DST change.</summary>
public readonly record struct DayBounds(Instant StartUtc, Instant EndUtc)
{
    public Duration Length => EndUtc - StartUtc;

    public bool Contains(Instant instant) => instant >= StartUtc && instant < EndUtc;

    /// <summary>Bound parameters for a "day D" query: stored UTC text of the start and of the (exclusive) end.</summary>
    public (string StartUtcText, string EndUtcText) ToUtcText() => (InstantText.Format(StartUtc), InstantText.Format(EndUtc));
}

/// <summary>
/// The business day (rule 10, 04 section 7.4, fixes DIV-26): calendar dates are local to the business time zone, instants are UTC. Zone rules come
/// from NodaTime's embedded tzdb, so Egypt's suspended/reinstated DST (2011-2014 changes, none 2015-2022, back 2023) is right on every PC regardless of Windows updates.
/// </summary>
public interface IBusinessCalendar
{
    string ZoneId { get; }

    /// <summary>Today's date in the business zone (not the device's).</summary>
    DateOnly Today();

    /// <summary>UTC bounds of the local day: from the first instant of the day (the first valid instant if midnight is skipped by DST) to the first instant of the next day.</summary>
    DayBounds DayBounds(DateOnly date);

    /// <summary>The business date an instant falls on.</summary>
    DateOnly DateOf(Instant instant);
}

public sealed class BusinessCalendar : IBusinessCalendar
{
    private readonly DateTimeZone _zone;
    private readonly IClock _clock;

    public BusinessCalendar(DateTimeZone zone, IClock clock)
    {
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ZoneId = zone.Id;
    }

    public string ZoneId { get; }

    /// <summary>Creates the calendar of a supported zone id (<see cref="BusinessZones"/>); an unknown id is refused.</summary>
    public static BusinessCalendar Create(string zoneId, IClock clock) =>
        TryCreate(zoneId, clock, out var calendar) ? calendar : throw new ArgumentException("Unsupported business time zone.", nameof(zoneId));

    public static bool TryCreate(string? zoneId, IClock clock, out BusinessCalendar calendar)
    {
        calendar = null!;
        if (!BusinessZones.IsSupported(zoneId))
        {
            return false;
        }

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId!);
        if (zone is null)
        {
            return false;
        }

        calendar = new BusinessCalendar(zone, clock);
        return true;
    }

    public DateOnly Today() => ToDateOnly(_clock.UtcNow.InZone(_zone).Date);

    public DateOnly DateOf(Instant instant) => ToDateOnly(instant.InZone(_zone).Date);

    public DayBounds DayBounds(DateOnly date)
    {
        var day = new LocalDate(date.Year, date.Month, date.Day);
        // AtStartOfDay never throws: when midnight does not exist (spring-forward at 00:00) it returns the first valid instant of the day.
        var start = _zone.AtStartOfDay(day).ToInstant();
        var end = _zone.AtStartOfDay(day.PlusDays(1)).ToInstant();
        return new DayBounds(start, end);
    }

    private static DateOnly ToDateOnly(LocalDate d) => new(d.Year, d.Month, d.Day);
}
