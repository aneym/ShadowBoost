namespace ShadowBoost.Core;

public sealed record ScheduleSlot(Period Period, LevelId Level, DateTimeOffset Start, DateTimeOffset End);

public static class Schedule
{
    public static ScheduleSlot At(DateTimeOffset now, Settings s, TimeZoneInfo tz)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
        return Around(date, s, tz).First(slot => slot.Start <= now && now < slot.End);
    }

    public static IReadOnlyList<ScheduleSlot> Day(DateOnly date, Settings s, TimeZoneInfo tz)
    {
        var start = Local(date, TimeOnly.MinValue, tz);
        var end = Local(date.AddDays(1), TimeOnly.MinValue, tz);
        return Around(date, s, tz).Where(slot => slot.Start < end && slot.End > start)
            .Select(slot => slot with
            {
                Start = slot.Start < start ? start : slot.Start,
                End = slot.End > end ? end : slot.End
            }).ToArray();
    }

    private static IEnumerable<ScheduleSlot> Around(DateOnly date, Settings s, TimeZoneInfo tz)
    {
        for (int delta = -2; delta <= 1; delta++)
        {
            var day = date.AddDays(delta);
            var (sunrise, sunset) = Solar(day, s, tz);
            var (nextSunrise, _) = Solar(day.AddDays(1), s, tz);
            var nightStart = Local(day, s.NightStart, tz);
            if (nightStart <= sunrise) nightStart = Local(day.AddDays(1), s.NightStart, tz);
            else if (nightStart < sunset) nightStart = sunset;
            if (nightStart > nextSunrise) nightStart = nextSunrise;
            yield return new(Period.Day, s.PeriodLevels[Period.Day], sunrise, sunset);
            if (sunset < nightStart)
                yield return new(Period.Evening, s.PeriodLevels[Period.Evening], sunset, nightStart);
            if (nightStart < nextSunrise)
                yield return new(Period.Night, s.PeriodLevels[Period.Night], nightStart, nextSunrise);
        }
    }

    private static (DateTimeOffset Sunrise, DateTimeOffset Sunset) Solar(DateOnly date, Settings s, TimeZoneInfo tz)
    {
        if (!s.FollowSun) return (Local(date, s.DayStart, tz), Local(date, s.EveningStart, tz));
        var (rise, set) = SunTimes.For(date, s.Latitude, s.Longitude, tz);
        return (rise ?? Local(date, new(7, 0), tz), set ?? Local(date, new(19, 0), tz));
    }

    internal static DateTimeOffset Local(DateOnly date, TimeOnly time, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // A skipped wall time starts at the next valid minute; a repeated wall time uses standard time.
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new(local, tz.GetUtcOffset(local));
    }
}
