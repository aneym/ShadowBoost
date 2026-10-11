namespace ShadowBoost.Core;

public enum NightSchedule { Off, Sunset, Custom }
public enum NightKind { Warmth, Dimming }
public sealed record NightWindow(DateTimeOffset Start, DateTimeOffset End);
public sealed record WarmHold(bool On, DateTimeOffset Until);

/// <summary>Strength of one night effect: 0 is daylight, 1 is the full night setting.</summary>
public sealed record NightState(double Factor, bool Held, NightWindow? Window, DateTimeOffset NextChange)
{
    public bool On => Factor > 0;
}

public sealed record Look(NightState Warmth, int Kelvin, NightState Dimming, double Brightness);

public static class Night
{
    public const int NeutralKelvin = 6500, MinKelvin = 2700;

    public static (NightSchedule Schedule, TimeOnly From, TimeOnly To) Plan(Settings s, NightKind kind) =>
        kind == NightKind.Warmth ? (s.WarmSchedule, s.WarmFrom, s.WarmTo) : (s.DimSchedule, s.DimFrom, s.DimTo);

    /// <summary>Night windows that start on the local days around <paramref name="date"/>, oldest first.</summary>
    public static IReadOnlyList<NightWindow> Windows(DateOnly date, Settings s, NightKind kind, TimeZoneInfo tz)
    {
        var (schedule, from, to) = Plan(s, kind);
        var list = new List<NightWindow>();
        if (schedule == NightSchedule.Off || (schedule == NightSchedule.Custom && from == to)) return list;
        for (int delta = -1; delta <= 1; delta++)
        {
            var day = date.AddDays(delta);
            if (schedule == NightSchedule.Sunset)
            {
                var (_, set) = Sun(day, s, tz);
                var (rise, _) = Sun(day.AddDays(1), s, tz);
                if (set < rise) list.Add(new(set, rise));
            }
            else
                list.Add(new(Schedule.Local(day, from, tz), Schedule.Local(to > from ? day : day.AddDays(1), to, tz)));
        }
        return list;
    }

    /// <summary>The window holding <paramref name="now"/>, else the next one to start; null when the schedule is off.</summary>
    public static NightWindow? CurrentOrNext(DateTimeOffset now, Settings s, NightKind kind, TimeZoneInfo tz)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
        return Windows(date, s, kind, tz).Concat(Windows(date.AddDays(2), s, kind, tz)).FirstOrDefault(w => now < w.End);
    }

    public static NightState State(DateTimeOffset now, Settings s, NightKind kind, TimeZoneInfo tz)
    {
        var window = CurrentOrNext(now, s, kind, tz);
        if (kind == NightKind.Warmth && s.WarmHold is { } hold && now < hold.Until)
            return new(hold.On ? 1 : 0, true, window, hold.Until);
        if (window == null) return new(0, false, null, DateTimeOffset.MaxValue);
        bool inside = window.Start <= now;
        return new(inside ? Fade(now, window, s.FadeMinutes) : 0, false, window, inside ? window.End : window.Start);
    }

    /// <summary>Linear fade in after the start and out before the end; a short window fades over half its length.</summary>
    public static double Fade(DateTimeOffset now, NightWindow w, int minutes)
    {
        if (now < w.Start || now >= w.End) return 0;
        double fade = Math.Min(minutes * 60.0, (w.End - w.Start).TotalSeconds / 2);
        if (fade <= 0) return 1;
        double inFactor = (now - w.Start).TotalSeconds / fade, outFactor = (w.End - now).TotalSeconds / fade;
        return Math.Clamp(Math.Min(inFactor, outFactor), 0, 1);
    }

    /// <summary>The quick toggle: flips warmth now and holds that until the schedule's next change.</summary>
    public static WarmHold? Toggle(DateTimeOffset now, Settings s, TimeZoneInfo tz)
    {
        var state = State(now, s, NightKind.Warmth, tz);
        var window = state.Window;
        bool insideWindow = window != null && window.Start <= now;
        if (state.On)
            return insideWindow ? new WarmHold(false, window!.End) : null;
        if (insideWindow) return null;
        if (window != null) return new WarmHold(true, window.End);
        // No schedule: stay warm until the next morning at the custom end time.
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var morning = Schedule.Local(DateOnly.FromDateTime(local.DateTime), s.WarmTo, tz);
        return new WarmHold(true, morning > now ? morning : Schedule.Local(DateOnly.FromDateTime(local.DateTime).AddDays(1), s.WarmTo, tz));
    }

    /// <summary>The share of the saved brightness left while dimming: 1 in daylight, DimPercent/100 at full night.</summary>
    public static double DimScale(NightState dim, Settings s) => 1 - dim.Factor * (100 - s.DimPercent) / 100.0;

    public static Look Look(DateTimeOffset now, Settings s, TimeZoneInfo tz)
    {
        var warm = State(now, s, NightKind.Warmth, tz);
        var dim = State(now, s, NightKind.Dimming, tz);
        int kelvin = (int)Math.Round(NeutralKelvin - warm.Factor * (NeutralKelvin - s.WarmKelvin));
        double brightness = s.UserBrightness * DimScale(dim, s);
        return new(warm, kelvin, dim, brightness);
    }

    static (DateTimeOffset Rise, DateTimeOffset Set) Sun(DateOnly day, Settings s, TimeZoneInfo tz)
    {
        var (rise, set) = SunTimes.For(day, s.Latitude, s.Longitude, tz);
        return (rise ?? Schedule.Local(day, new(7, 0), tz), set ?? Schedule.Local(day, new(19, 0), tz));
    }
}
