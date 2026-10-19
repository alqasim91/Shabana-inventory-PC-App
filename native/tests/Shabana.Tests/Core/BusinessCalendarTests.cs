using NodaTime;
using Shabana.Core.Time;
using Clock = Shabana.Core.Time.IClock;

namespace Shabana.Tests.Core;

/// <summary>
/// P1 acceptance (4): business-day bounds across the Cairo DST transitions of 2014-2026. Egypt observed DST until 2014 (with a Ramadan pause),
/// suspended it from 2015 to 2022 and reinstated it in 2023 (last Friday of April 00:00 -> last Thursday of October 24:00). The expectations here are
/// written from those rules, not read back from the tz database, so a stale or wrong tzdb fails the test.
/// </summary>
public class BusinessCalendarTests
{
    private sealed class FixedClock(Instant now) : Clock
    {
        public Instant UtcNow { get; set; } = now;
    }

    private static BusinessCalendar Cairo(Instant? now = null) =>
        BusinessCalendar.Create("Africa/Cairo", new FixedClock(now ?? Instant.FromUtc(2026, 1, 1, 0, 0)));

    private static Instant Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) => Instant.FromUtc(y, m, d, h, min, s);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static DateOnly LastWeekday(int year, int month, DayOfWeek day)
    {
        var date = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (date.DayOfWeek != day)
        {
            date = date.AddDays(-1);
        }

        return date;
    }

    /// <summary>Days that are not 24 hours long, per the Egyptian rules; value = expected length in hours.</summary>
    private static Dictionary<DateOnly, int> ExpectedIrregularDays()
    {
        var days = new Dictionary<DateOnly, int>
        {
            // 2014: DST from the night of 15 May (24:00) to 26 June (24:00), paused for Ramadan, again 31 July (24:00) to the last Thursday of September (24:00)
            [D(2014, 5, 16)] = 23, // clocks jump 00:00 -> 01:00: the local day is one hour short
            [D(2014, 6, 26)] = 25, // clocks fall back at 24:00: the 23:00 hour happens twice
            [D(2014, 8, 1)] = 23,
            [D(2014, 9, 25)] = 25,
        };

        // 2015-2022: DST suspended, no entries. 2023 onward: last Friday of April (23 h day) .. last Thursday of October (25 h day)
        for (var year = 2023; year <= 2026; year++)
        {
            days[LastWeekday(year, 4, DayOfWeek.Friday)] = 23;
            days[LastWeekday(year, 10, DayOfWeek.Thursday)] = 25;
        }

        return days;
    }

    [Fact]
    public void Every_day_2014_to_2026_has_the_length_the_egyptian_dst_rules_give()
    {
        var cal = Cairo();
        var irregular = ExpectedIrregularDays();
        var seen = 0;
        for (var date = D(2014, 1, 1); date <= D(2026, 12, 31); date = date.AddDays(1))
        {
            var hours = cal.DayBounds(date).Length.TotalHours;
            hours.ShouldBe(irregular.TryGetValue(date, out var h) ? h : 24, $"{date:yyyy-MM-dd}");
            seen += irregular.ContainsKey(date) ? 1 : 0;
        }

        seen.ShouldBe(irregular.Count);
        irregular.Count.ShouldBe(4 + 8);
    }

    [Theory]
    [InlineData(2015)]
    [InlineData(2016)]
    [InlineData(2017)]
    [InlineData(2018)]
    [InlineData(2019)]
    [InlineData(2020)]
    [InlineData(2021)]
    [InlineData(2022)]
    public void Years_without_dst_are_utc_plus_two_all_year(int year)
    {
        var cal = Cairo();
        for (var date = D(year, 1, 1); date.Year == year; date = date.AddDays(1))
        {
            var b = cal.DayBounds(date);
            b.Length.ShouldBe(Duration.FromHours(24), date.ToString());
            // local midnight is 22:00 UTC of the previous day, winter or summer
            b.StartUtc.ShouldBe(Utc(date.Year, date.Month, date.Day).Minus(Duration.FromHours(2)), date.ToString());
        }
    }

    [Fact]
    public void Specific_transition_instants()
    {
        var cal = Cairo();

        // 2026: starts Fri 24 April (midnight does not exist: the day starts at the jump, 22:00Z), ends Thu 29 October
        var spring = cal.DayBounds(D(2026, 4, 24));
        spring.StartUtc.ShouldBe(Utc(2026, 4, 23, 22));
        spring.EndUtc.ShouldBe(Utc(2026, 4, 24, 21));
        cal.DayBounds(D(2026, 4, 23)).EndUtc.ShouldBe(Utc(2026, 4, 23, 22)); // the day before ends exactly when the jump happens
        cal.DayBounds(D(2026, 4, 25)).StartUtc.ShouldBe(Utc(2026, 4, 24, 21)); // summer: midnight is 21:00Z

        var fall = cal.DayBounds(D(2026, 10, 29));
        fall.StartUtc.ShouldBe(Utc(2026, 10, 28, 21));
        fall.EndUtc.ShouldBe(Utc(2026, 10, 29, 22));
        cal.DayBounds(D(2026, 10, 30)).StartUtc.ShouldBe(Utc(2026, 10, 29, 22));

        // ordinary winter and summer days
        cal.DayBounds(D(2026, 1, 15)).StartUtc.ShouldBe(Utc(2026, 1, 14, 22));
        cal.DayBounds(D(2026, 7, 15)).StartUtc.ShouldBe(Utc(2026, 7, 14, 21));
        cal.DayBounds(D(2026, 7, 15)).EndUtc.ShouldBe(Utc(2026, 7, 15, 21));

        // 2023, the reinstatement year: Fri 28 April .. Thu 26 October
        cal.DayBounds(D(2023, 4, 27)).EndUtc.ShouldBe(Utc(2023, 4, 27, 22));
        cal.DayBounds(D(2023, 4, 28)).Length.ShouldBe(Duration.FromHours(23));
        cal.DayBounds(D(2023, 4, 29)).StartUtc.ShouldBe(Utc(2023, 4, 28, 21));
        cal.DayBounds(D(2023, 10, 26)).Length.ShouldBe(Duration.FromHours(25));
        cal.DayBounds(D(2023, 10, 27)).StartUtc.ShouldBe(Utc(2023, 10, 26, 22));

        // 2022: no DST on the dates it would have started/ended; 2014: the Ramadan pause
        cal.DayBounds(D(2022, 4, 29)).Length.ShouldBe(Duration.FromHours(24));
        cal.DayBounds(D(2022, 7, 15)).StartUtc.ShouldBe(Utc(2022, 7, 14, 22));
        cal.DayBounds(D(2014, 7, 10)).StartUtc.ShouldBe(Utc(2014, 7, 9, 22)); // paused: +02:00 in July 2014
        cal.DayBounds(D(2014, 6, 10)).StartUtc.ShouldBe(Utc(2014, 6, 9, 21)); // DST in force: +03:00
        cal.DayBounds(D(2014, 8, 10)).StartUtc.ShouldBe(Utc(2014, 8, 9, 21)); // resumed
        cal.DayBounds(D(2014, 10, 1)).StartUtc.ShouldBe(Utc(2014, 9, 30, 22)); // ended 25 September
        cal.DayBounds(D(2014, 1, 15)).StartUtc.ShouldBe(Utc(2014, 1, 14, 22));
    }

    [Fact]
    public void Days_tile_the_timeline_with_no_gap_and_no_overlap_and_dateof_inverts_daybounds()
    {
        var cal = Cairo();
        var one = Duration.FromNanoseconds(1);
        for (var date = D(2013, 12, 31); date <= D(2027, 1, 1); date = date.AddDays(1))
        {
            var b = cal.DayBounds(date);
            var next = cal.DayBounds(date.AddDays(1));
            b.EndUtc.ShouldBe(next.StartUtc, date.ToString()); // contiguous
            b.StartUtc.ShouldBeLessThan(b.EndUtc);
            b.Contains(b.StartUtc).ShouldBeTrue();
            b.Contains(b.EndUtc).ShouldBeFalse(); // half-open
            b.Contains(b.EndUtc - one).ShouldBeTrue();
            cal.DateOf(b.StartUtc).ShouldBe(date);
            cal.DateOf(b.EndUtc - one).ShouldBe(date);
            cal.DateOf(b.EndUtc).ShouldBe(date.AddDays(1));
        }
    }

    [Fact]
    public void Today_is_the_business_date_not_the_utc_date()
    {
        // 22:30Z in January is 00:30 the NEXT day in Cairo (DIV-26: the dashboard day, the drawer day and "today" in every form)
        var clock = new FixedClock(Utc(2026, 1, 15, 22, 30));
        var cal = BusinessCalendar.Create("Africa/Cairo", clock);
        cal.Today().ShouldBe(D(2026, 1, 16));

        clock.UtcNow = Utc(2026, 1, 15, 21, 59, 59);
        cal.Today().ShouldBe(D(2026, 1, 15));

        // summer: the day turns at 21:00Z
        clock.UtcNow = Utc(2026, 7, 14, 20, 59, 59);
        cal.Today().ShouldBe(D(2026, 7, 14));
        clock.UtcNow = Utc(2026, 7, 14, 21, 0, 0);
        cal.Today().ShouldBe(D(2026, 7, 15));

        // across the spring-forward night of 2026: 21:59:59Z is still 23:59:59 on the 23rd, 22:00Z is already 01:00 on the 24th
        clock.UtcNow = Utc(2026, 4, 23, 21, 59, 59);
        cal.Today().ShouldBe(D(2026, 4, 23));
        clock.UtcNow = Utc(2026, 4, 23, 22, 0, 0);
        cal.Today().ShouldBe(D(2026, 4, 24));

        // fall-back night: the repeated hour (21:00Z..21:59Z) still belongs to the 29th, the 30th starts at 22:00Z
        clock.UtcNow = Utc(2026, 10, 29, 21, 30);
        cal.Today().ShouldBe(D(2026, 10, 29));
        clock.UtcNow = Utc(2026, 10, 29, 22, 0);
        cal.Today().ShouldBe(D(2026, 10, 30));
    }

    [Fact]
    public void Utc_bounds_bind_as_the_stored_text_form()
    {
        var (start, end) = Cairo().DayBounds(D(2026, 4, 24)).ToUtcText();
        start.ShouldBe("2026-04-23T22:00:00.000Z");
        end.ShouldBe("2026-04-24T21:00:00.000Z");
        InstantText.Format(Utc(2026, 1, 2, 3, 4, 5)).ShouldBe("2026-01-02T03:04:05.000Z");
        InstantText.Parse("2026-01-02T03:04:05.123Z").ShouldBe(Utc(2026, 1, 2, 3, 4, 5).Plus(Duration.FromMilliseconds(123)));
        InstantText.TryParse("2026-01-02", out _).ShouldBeFalse();
        InstantText.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_eighteen_supported_zones_all_resolve_and_anything_else_is_refused()
    {
        BusinessZones.All.Count.ShouldBe(18);
        BusinessZones.All[0].Id.ShouldBe("Africa/Cairo");
        foreach (var z in BusinessZones.All)
        {
            BusinessCalendar.TryCreate(z.Id, new FixedClock(Utc(2026, 1, 1)), out var cal).ShouldBeTrue(z.Id);
            cal.ZoneId.ShouldBe(z.Id);
            z.Name.ShouldNotBeNullOrWhiteSpace();
        }

        foreach (var bad in new[] { "", "Mars/Olympus", "UTC", "africa/cairo", "Europe/London", null })
        {
            BusinessCalendar.TryCreate(bad, new FixedClock(Utc(2026, 1, 1)), out _).ShouldBeFalse(bad ?? "null");
        }

        Should.Throw<ArgumentException>(() => BusinessCalendar.Create("Europe/London", new FixedClock(Utc(2026, 1, 1))));
    }

    [Fact]
    public void Other_zones_use_their_own_midnight()
    {
        var riyadh = BusinessCalendar.Create("Asia/Riyadh", new FixedClock(Utc(2026, 1, 1)));
        riyadh.DayBounds(D(2026, 7, 15)).StartUtc.ShouldBe(Utc(2026, 7, 14, 21)); // UTC+3, no DST
        riyadh.DayBounds(D(2026, 1, 15)).Length.ShouldBe(Duration.FromHours(24));
        var casablanca = BusinessCalendar.Create("Africa/Casablanca", new FixedClock(Utc(2026, 1, 1)));
        casablanca.DayBounds(D(2026, 1, 15)).StartUtc.ShouldBe(Utc(2026, 1, 14, 23)); // UTC+1 (permanent, with a pause in Ramadan)
    }

    [Fact]
    public void System_clock_returns_a_plausible_now()
    {
        var now = new SystemUtcClock().UtcNow;
        now.ShouldBeGreaterThan(Utc(2025, 1, 1));
        Cairo(now).Today().Year.ShouldBeGreaterThanOrEqualTo(2025);
    }
}
