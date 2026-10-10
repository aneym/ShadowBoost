using ShadowBoost.Core;
using Xunit;

namespace ShadowBoost.Core.Tests;

// These unit tests guard pure algorithms with date, boundary, and curve edge cases; persistence uses real files.
public sealed class CoreTests
{
    private static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static DateTimeOffset June(int hour, int minute = 0) => new(2026, 6, 21, hour, minute, 0, TimeSpan.FromHours(-4));

    // Guards solstice solar accuracy against independent NYC sunrise/sunset reference times.
    [Theory]
    [InlineData(6, 21, 5, 25, 20, 31, -4)]
    [InlineData(12, 21, 7, 17, 16, 32, -5)]
    public void SolarSolstices(int month, int day, int riseHour, int riseMinute, int setHour, int setMinute, int offset)
    {
        var (rise, set) = SunTimes.For(new(2026, month, day), 40.7128, -74.0060, Ny);
        Assert.NotNull(rise);
        Assert.NotNull(set);
        var expectedRise = new DateTimeOffset(2026, month, day, riseHour, riseMinute, 0, TimeSpan.FromHours(offset));
        var expectedSet = new DateTimeOffset(2026, month, day, setHour, setMinute, 0, TimeSpan.FromHours(offset));
        Assert.InRange(Math.Abs((rise.Value - expectedRise).TotalMinutes), 0, 3);
        Assert.InRange(Math.Abs((set.Value - expectedSet).TotalMinutes), 0, 3);
        Assert.Equal(expectedRise.Offset, rise.Value.Offset);
        Assert.Equal(expectedSet.Offset, set.Value.Offset);
    }

    // Guards conversion using the event's DST offset rather than midnight's offset.
    [Theory]
    [InlineData(3, 8, -4)]
    [InlineData(11, 1, -5)]
    public void SolarDstDays(int month, int day, int offset)
    {
        var date = new DateOnly(2026, month, day);
        var (rise, set) = SunTimes.For(date, 40.7128, -74.0060, Ny);
        Assert.NotNull(rise);
        Assert.NotNull(set);
        Assert.Equal(TimeSpan.FromHours(offset), rise.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(offset), set.Value.Offset);
        Assert.Equal(date, DateOnly.FromDateTime(rise.Value.DateTime));
        Assert.Equal(date, DateOnly.FromDateTime(set.Value.DateTime));
    }

    // Guards polar day where no horizon crossing exists.
    [Fact]
    public void PolarDayHasNoSunEvents()
    {
        var times = SunTimes.For(new(2026, 6, 21), 69.65, 18.96, TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo"));
        Assert.Null(times.Sunrise);
        Assert.Null(times.Sunset);
    }

    // Guards day, evening, overnight, post-midnight night-start, and an empty evening.
    [Theory]
    [InlineData(12, 0, 22, 30, Period.Day, 21, 20, 31)]
    [InlineData(21, 0, 22, 30, Period.Evening, 21, 22, 30)]
    [InlineData(23, 0, 22, 30, Period.Night, 22, 5, 25)]
    [InlineData(3, 0, 22, 30, Period.Night, 21, 5, 25)]
    [InlineData(23, 30, 1, 0, Period.Evening, 22, 1, 0)]
    [InlineData(20, 45, 18, 0, Period.Night, 22, 5, 25)]
    public void ScheduleSelectsSlot(int hour, int minute, int nightHour, int nightMinute,
        Period period, int endDay, int endHour, int endMinute)
    {
        var settings = new Settings { NightStart = new(nightHour, nightMinute) };
        var now = June(hour, minute);
        var slot = Schedule.At(now, settings, Ny);
        Assert.Equal(period, slot.Period);
        Assert.Equal(settings.PeriodLevels[period], slot.Level);
        Assert.True(slot.Start <= now && now < slot.End);
        var expected = new DateTimeOffset(2026, 6, endDay, endHour, endMinute, 0, TimeSpan.FromHours(-4));
        Assert.InRange(Math.Abs((slot.End - expected).TotalMinutes), 0, period == Period.Evening ? 0 : 3);
    }

    // Guards a clipped, gap-free timeline including 23/25-hour DST days and polar fallback.
    [Theory]
    [InlineData(6, 21, 22, 30, false, 24)]
    [InlineData(6, 21, 1, 0, false, 24)]
    [InlineData(6, 21, 18, 0, false, 24)]
    [InlineData(3, 8, 22, 30, false, 23)]
    [InlineData(11, 1, 22, 30, false, 25)]
    [InlineData(6, 21, 22, 30, true, 24)]
    public void DailyTimelineCoversLocalDay(int month, int day, int nightHour, int nightMinute, bool polar, int hours)
    {
        var settings = new Settings { NightStart = new(nightHour, nightMinute) };
        var tz = Ny;
        if (polar)
        {
            settings.Latitude = 69.65;
            settings.Longitude = 18.96;
            tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
        }
        var date = new DateOnly(2026, month, day);
        var slots = Schedule.Day(date, settings, tz);
        Assert.NotEmpty(slots);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue), slots[0].Start.DateTime);
        Assert.Equal(date.AddDays(1).ToDateTime(TimeOnly.MinValue), slots[^1].End.DateTime);
        Assert.Equal(hours, (slots[^1].End - slots[0].Start).TotalHours);
        for (int i = 0; i < slots.Count; i++)
        {
            Assert.True(slots[i].Start < slots[i].End);
            if (i > 0) Assert.Equal(slots[i - 1].End, slots[i].Start);
            Assert.Equal(slots[i].Period, Schedule.At(slots[i].Start, settings, tz).Period);
        }
        if (polar)
        {
            var daylight = Assert.Single(slots, slot => slot.Period == Period.Day);
            Assert.Equal(7, daylight.Start.Hour);
            Assert.Equal(19, daylight.End.Hour);
        }
        if (nightHour == 18) Assert.DoesNotContain(slots, slot => slot.Period == Period.Evening);
    }

    // Guards disabled startup, scheduled activation, override expiry, and one-shot boundary reporting.
    [Fact]
    public void EngineLifecycle()
    {
        var settings = new Settings();
        var engine = new BoostEngine(settings, Ny);
        var noon = June(12);
        Assert.False(engine.State(noon).Enabled);
        Assert.Null(engine.State(noon).AppliedCurve);
        engine.TurnOn(noon);
        Assert.Equal(LevelId.Strong, engine.State(noon).ActiveLevel);
        Assert.Equal(settings.Curves[LevelId.Strong], engine.State(noon).AppliedCurve);
        Assert.False(engine.Tick(noon));
        engine.SetLevel(LevelId.Gentle, noon);
        var end = engine.State(noon).NextChange;
        Assert.Equal(Schedule.At(noon, settings, Ny).End, end);
        Assert.True(engine.State(end.AddMinutes(-1)).IsOverride);
        Assert.Equal(LevelId.Gentle, engine.State(end.AddMinutes(-1)).ActiveLevel);
        Assert.False(engine.State(end).IsOverride); // State alone does not expose expired overrides.
        Assert.True(engine.Tick(end));
        Assert.Equal(LevelId.Balanced, engine.State(end).ActiveLevel);
        Assert.Equal(settings.Curves[LevelId.Balanced], engine.State(end).AppliedCurve);
        Assert.False(engine.Tick(end));
        engine.SetLevel(LevelId.Strong, end);
        engine.TurnOff(end);
        Assert.False(engine.State(end).IsOverride);
        Assert.Null(engine.State(end).AppliedCurve);
        engine.SetLevel(LevelId.Gentle, noon);
        Assert.True(engine.State(noon).Enabled);
        Assert.True(engine.State(noon).IsOverride);
        engine.TurnOn(noon);
        Assert.False(engine.State(noon).IsOverride);
        Assert.True(engine.Tick(end));
        Assert.False(engine.Tick(end));
    }

    // Guards an override's recorded expiry when settings move the scheduled boundary.
    [Fact]
    public void SettingsUpdatePreservesOverrideEnd()
    {
        var engine = new BoostEngine(new Settings(), Ny);
        engine.SetLevel(LevelId.Strong, June(21));
        engine.UpdateSettings(new Settings { NightStart = new(23, 30) });
        Assert.Equal(June(22, 30), engine.State(June(21)).NextChange);
        Assert.True(engine.State(June(22, 29)).IsOverride);
        Assert.False(engine.State(June(22, 30)).IsOverride);
    }

    // Guards legacy neutral gamma rounding and monotonic default curves.
    [Theory]
    [InlineData(0, 100, 100, 100, true)]
    [InlineData(4, 82, 100, 100, false)]
    [InlineData(10, 70, 105, 100, false)]
    [InlineData(18, 55, 115, 105, false)]
    public void GammaCurveInvariants(int lift, int gamma, int contrast, int brightness, bool neutral)
    {
        var ramp = GammaRamp.Build(new(lift, gamma, contrast, brightness));
        var identity = GammaRamp.Identity();
        Assert.Equal(256, ramp.Length);
        for (int i = 0; i < ramp.Length; i++)
        {
            if (i > 0) Assert.True(ramp[i] >= ramp[i - 1]);
            if (neutral)
            {
                Assert.Equal(i * 257, (int)identity[i]);
                Assert.InRange(Math.Abs((int)ramp[i] - identity[i]), 0, 1);
            }
        }
    }

    // Guards both legacy formats, range clamping, and all-or-nothing rejection of garbage.
    [Theory]
    [InlineData("9,80,117,100,84", 9, 80, 117, 100, 84)]
    [InlineData("80,110", 10, 80, 105, 110, null)]
    [InlineData("80,110,84", 10, 80, 105, 110, 84)]
    [InlineData("-4,200,0,300,200", 0, 100, 50, 130, 100)]
    [InlineData("9,no,117,100,84", 10, 70, 105, 100, null)]
    [InlineData("garbage", 10, 70, 105, 100, null)]
    public void LegacyMigration(string text, int lift, int gamma, int contrast, int brightness, int? monitor)
    {
        var settings = Settings.FromLegacy(text);
        Assert.Equal(new(lift, gamma, contrast, brightness), settings.Curves[LevelId.Balanced]);
        Assert.Equal(monitor, settings.MonitorBrightness);
    }

    // Guards actual JSON persistence, format, missing-key repair, and corrupt-file preservation.
    [Fact]
    public void SettingsFileLifecycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ShadowBoost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Assert.Equal("New York", Settings.Load(path).PlaceName);
            var settings = new Settings
            {
                Latitude = 51.5, Longitude = -0.12, PlaceName = "London", NightStart = new(1, 15),
                StartAtSignIn = false, MonitorBrightness = 84, TrayPinApplied = true
            };
            settings.PeriodLevels[Period.Day] = LevelId.Gentle;
            settings.Curves[LevelId.Balanced] = new(9, 80, 117, 100);
            settings.Save(path);
            var json = File.ReadAllText(path);
            Assert.Contains("\"nightStart\": \"01:15\"", json);
            Assert.Contains("\"Day\": \"Gentle\"", json);
            var loaded = Settings.Load(path);
            Assert.Equal(settings.Latitude, loaded.Latitude);
            Assert.Equal(settings.Longitude, loaded.Longitude);
            Assert.Equal(settings.PlaceName, loaded.PlaceName);
            Assert.Equal(settings.NightStart, loaded.NightStart);
            Assert.Equal(settings.StartAtSignIn, loaded.StartAtSignIn);
            Assert.Equal(settings.MonitorBrightness, loaded.MonitorBrightness);
            Assert.Equal(settings.TrayPinApplied, loaded.TrayPinApplied);
            Assert.Equal(settings.PeriodLevels.OrderBy(p => p.Key), loaded.PeriodLevels.OrderBy(p => p.Key));
            Assert.Equal(settings.Curves.OrderBy(p => p.Key), loaded.Curves.OrderBy(p => p.Key));
            File.WriteAllText(path, "{\"periodLevels\":{},\"curves\":{\"Balanced\":{\"blackLift\":99,\"gamma\":0,\"contrast\":200,\"brightness\":0}}}");
            loaded = Settings.Load(path);
            Assert.Equal(LevelId.Strong, loaded.PeriodLevels[Period.Day]);
            Assert.Equal(new(4, 82, 100, 100), loaded.Curves[LevelId.Gentle]);
            Assert.Equal(new(30, 30, 150, 50), loaded.Curves[LevelId.Balanced]);
            File.WriteAllText(path, "{broken");
            loaded = Settings.Load(path);
            Assert.Equal("New York", loaded.PlaceName);
            Assert.Equal(new(10, 70, 105, 100), loaded.Curves[LevelId.Balanced]);
            Assert.Equal("{broken", File.ReadAllText(path + ".bad"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
