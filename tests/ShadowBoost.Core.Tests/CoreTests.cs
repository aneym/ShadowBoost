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
    [InlineData(7, 0, 22, 30, Period.Day, 21, 19, 0, false)]
    [InlineData(18, 59, 22, 30, Period.Day, 21, 19, 0, false)]
    [InlineData(19, 0, 22, 30, Period.Evening, 21, 22, 30, false)]
    [InlineData(22, 30, 22, 30, Period.Night, 22, 7, 0, false)]
    public void ScheduleSelectsSlot(int hour, int minute, int nightHour, int nightMinute,
        Period period, int endDay, int endHour, int endMinute, bool followSun = true)
    {
        var settings = new Settings { NightStart = new(nightHour, nightMinute), FollowSun = followSun };
        var now = June(hour, minute);
        var slot = Schedule.At(now, settings, Ny);
        Assert.Equal(period, slot.Period);
        Assert.Equal(settings.PeriodLevels[period], slot.Level);
        Assert.True(slot.Start <= now && now < slot.End);
        var expected = new DateTimeOffset(2026, 6, endDay, endHour, endMinute, 0, TimeSpan.FromHours(-4));
        Assert.InRange(Math.Abs((slot.End - expected).TotalMinutes), 0, !followSun || period == Period.Evening ? 0 : 3);
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

    // Guards per-channel composition: neutral identity, brightness scaling, and warm white points
    // (red kept, green then blue cut, each channel monotonic) against Helland/redshift reference gains.
    [Theory]
    [InlineData(false, 100, 6500, 1.0, 1.0, 1.0)]
    [InlineData(false, 80, 6500, 0.8, 0.8, 0.8)]
    [InlineData(true, 100, 6500, 1.0, 1.0, 1.0)]
    [InlineData(false, 100, 3400, 1.0, 0.75, 0.54)]
    [InlineData(false, 100, 2700, 1.0, 0.66, 0.35)]
    [InlineData(false, 50, 2700, 0.5, 0.33, 0.175)]
    public void ComposeChannels(bool boosted, int brightness, int kelvin, double red, double green, double blue)
    {
        Curve? curve = boosted ? new Settings().Curves[LevelId.Balanced] : null;
        var ramp = GammaRamp.Compose(curve, brightness, kelvin);
        if (boosted) Assert.Equal(GammaRamp.Build(curve!), ramp.Red);
        else
        {
            Assert.InRange(ramp.Red[255] / 65535.0, red - 0.02, red + 0.02);
            Assert.InRange(ramp.Green[255] / 65535.0, green - 0.02, green + 0.02);
            Assert.InRange(ramp.Blue[255] / 65535.0, blue - 0.02, blue + 0.02);
        }
        Assert.Equal(!boosted && brightness == 100 && kelvin == 6500, ramp.IsIdentity);
        foreach (var channel in new[] { ramp.Red, ramp.Green, ramp.Blue })
            for (int i = 1; i < channel.Length; i++) Assert.True(channel[i] >= channel[i - 1]);
        for (int i = 0; i < 256; i++) Assert.True(ramp.Red[i] >= ramp.Green[i] && ramp.Green[i] >= ramp.Blue[i]);
    }

    // Guards the night windows: sunset to sunrise, custom hours across midnight, empty custom hours,
    // fades at both ends, and the next change reported for the tray copy.
    [Theory]
    [InlineData("Sunset", 21, 0, 23, 0, 1.0, "next-rise")]
    [InlineData("Sunset", 12, 0, 23, 0, 0.0, "today-set")]
    [InlineData("Sunset", 20, 41, 23, 0, 0.5, "next-rise")]
    [InlineData("Sunset", 5, 15, 23, 0, 0.5, "today-rise")]
    [InlineData("Custom", 2, 0, 23, 0, 1.0, "07:00")]
    [InlineData("Custom", 22, 0, 23, 0, 0.0, "23:00")]
    [InlineData("Custom", 23, 10, 23, 0, 0.5, "07:00")]
    [InlineData("Custom", 12, 0, 7, 0, 0.0, "never")]
    [InlineData("Off", 23, 30, 23, 0, 0.0, "never")]
    public void NightWindowsAndFades(string schedule, int hour, int minute, int fromHour, int fromMinute, double factor, string next)
    {
        var settings = new Settings
        {
            WarmSchedule = Enum.Parse<NightSchedule>(schedule), WarmFrom = new(fromHour, fromMinute), WarmTo = new(7, 0), FadeMinutes = 20
        };
        var now = June(hour, minute);
        var state = Night.State(now, settings, NightKind.Warmth, Ny);
        Assert.InRange(state.Factor, factor - 0.05, factor + 0.05);
        Assert.False(state.Held);
        var (rise, set) = SunTimes.For(new(2026, 6, 21), 40.7128, -74.0060, Ny);
        var (nextRise, _) = SunTimes.For(new(2026, 6, 22), 40.7128, -74.0060, Ny);
        DateTimeOffset expected = next switch
        {
            "next-rise" => nextRise!.Value, "today-set" => set!.Value, "today-rise" => rise!.Value,
            "never" => DateTimeOffset.MaxValue,
            _ => June(int.Parse(next[..2]), int.Parse(next[3..])) is var t && t <= now ? t.AddDays(1) : June(int.Parse(next[..2]), int.Parse(next[3..]))
        };
        Assert.Equal(expected, state.NextChange);
    }

    // Guards the quick toggle: on in daylight holds until the next morning, off at night holds until
    // the window ends, a second press clears the hold, and with no schedule it holds to the morning time.
    [Fact]
    public void QuickToggleHoldsUntilNextChange()
    {
        var settings = new Settings();
        var noon = June(12);
        var hold = Night.Toggle(noon, settings, Ny)!;
        Assert.True(hold.On);
        settings.WarmHold = hold;
        var (nextRise, _) = SunTimes.For(new(2026, 6, 22), 40.7128, -74.0060, Ny);
        Assert.Equal(nextRise!.Value, hold.Until);
        Assert.Equal(3400, Night.Look(noon, settings, Ny).Kelvin);
        Assert.Null(Night.Toggle(noon, settings, Ny));

        settings.WarmHold = null;
        var night = June(23);
        hold = Night.Toggle(night, settings, Ny)!;
        Assert.False(hold.On);
        Assert.Equal(nextRise.Value, hold.Until);
        settings.WarmHold = hold;
        Assert.Equal(6500, Night.Look(night, settings, Ny).Kelvin);
        Assert.Equal(6500, Night.Look(hold.Until.AddMinutes(1), settings, Ny).Kelvin);
        Assert.Equal(3400, Night.Look(June(22).AddDays(1), settings, Ny).Kelvin); // expired hold: schedule resumes

        settings.WarmHold = null;
        settings.WarmSchedule = NightSchedule.Off;
        hold = Night.Toggle(night, settings, Ny)!;
        Assert.True(hold.On);
        Assert.Equal(June(7).AddDays(1), hold.Until);
    }

    // Guards that dimming multiplies the saved brightness on its own schedule and never rewrites it.
    [Fact]
    public void LookCombinesWarmthAndDimming()
    {
        var settings = new Settings { UserBrightness = 54, DimSchedule = NightSchedule.Custom, DimPercent = 80, FadeMinutes = 0 };
        var look = Night.Look(June(2), settings, Ny);
        Assert.Equal(3400, look.Kelvin);
        Assert.Equal(54 * 0.8, look.Brightness, 6);
        Assert.Equal(54, settings.UserBrightness);
        look = Night.Look(June(12), settings, Ny);
        Assert.Equal(6500, look.Kelvin);
        Assert.Equal(54, look.Brightness, 6);
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
                StartAtSignIn = false, MonitorBrightness = 84, TrayPinApplied = true,
                WarmSchedule = NightSchedule.Custom, WarmFrom = new(20, 30), WarmKelvin = 2900, DimSchedule = NightSchedule.Sunset,
                DimPercent = 65, FadeMinutes = 60, WarmHold = new(true, June(7).AddDays(1))
            };
            settings.PeriodLevels[Period.Day] = LevelId.Gentle;
            settings.Curves[LevelId.Balanced] = new(9, 80, 117, 100);
            settings.Monitors["GSM780E#5&20e287fe&0&UID4352"] = new() { Model = "WK95U", Brightness = 63 };
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
            Assert.Equal((settings.WarmSchedule, settings.WarmFrom, settings.WarmKelvin, settings.DimSchedule, settings.DimPercent, settings.FadeMinutes),
                (loaded.WarmSchedule, loaded.WarmFrom, loaded.WarmKelvin, loaded.DimSchedule, loaded.DimPercent, loaded.FadeMinutes));
            Assert.Equal(settings.WarmHold, loaded.WarmHold);
            Assert.Equal(settings.PeriodLevels.OrderBy(p => p.Key), loaded.PeriodLevels.OrderBy(p => p.Key));
            Assert.Equal(settings.Curves.OrderBy(p => p.Key), loaded.Curves.OrderBy(p => p.Key));
            var monitor = Assert.Single(loaded.Monitors);
            Assert.Equal(("GSM780E#5&20e287fe&0&UID4352", "WK95U", 63), (monitor.Key, monitor.Value.Model, monitor.Value.Brightness));
            File.WriteAllText(path, "{\"warmKelvin\":9000,\"dimPercent\":5}");
            loaded = Settings.Load(path);
            Assert.Equal((6500, 30, NightSchedule.Sunset), (loaded.WarmKelvin, loaded.DimPercent, loaded.WarmSchedule));
            File.WriteAllText(path, "{\"periodLevels\":{},\"curves\":{\"Balanced\":{\"blackLift\":99,\"gamma\":0,\"contrast\":200,\"brightness\":0}}}");
            loaded = Settings.Load(path);
            Assert.Equal(LevelId.Strong, loaded.PeriodLevels[Period.Day]);
            Assert.Equal(new(4, 82, 100, 100), loaded.Curves[LevelId.Gentle]);
            Assert.Equal(new(30, 30, 150, 50), loaded.Curves[LevelId.Balanced]);
            File.WriteAllText(path, "{\"monitors\":{\"a\":{\"brightness\":140},\"b\":{\"brightness\":-5},\"c\":null,\"d\":{}}}");
            loaded = Settings.Load(path);
            Assert.Equal(new int?[] { 100, 0, null, null }, new[] { "a", "b", "c", "d" }.Select(k => loaded.Monitors[k].Brightness));
            File.WriteAllText(path, "{\"monitors\":null}");
            Assert.Empty(Settings.Load(path).Monitors);
            File.WriteAllText(path, "{broken");
            loaded = Settings.Load(path);
            Assert.Equal("New York", loaded.PlaceName);
            Assert.Equal(new(10, 70, 105, 100), loaded.Curves[LevelId.Balanced]);
            Assert.Equal("{broken", File.ReadAllText(path + ".bad"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    const string LgCaps = "(prot(monitor)type(lcd)model(WK95U)cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B ) 16 18 1A 52 60(11 12 0F 10 ) AC AE B2 B6 C0 C6 C8 C9 D6(01 04) DF 62 8D F4 F5(01 02 03 04) F6(00 01 02) 4D 4E 4F 15(01 06 11 13 14 15 18 19 20 22 23 24 28 29 32 48) F7(00 01 02 03) F8(00 01) F9 E4 E5 E6 E7 E8 E9 EA EB EF FA(00 01) FD(00 01) FE(00 01 02) FF)mccs_ver(2.1)mswhql(1))";

    // Guards the capabilities parser on the real LG ULTRAGEAR+ string, a compact variant and garbage; a parser with many edge cases.
    [Fact]
    public void CapsParseRealString()
    {
        var caps = VcpCaps.Parse(LgCaps);
        Assert.Equal(("WK95U", "2.1"), (caps.Model, caps.Mccs));
        Assert.All(new byte[] { 0x10, 0x12, 0x16, 0x18, 0x1A }, code => Assert.True(caps.Has(code)));
        Assert.False(caps.Has(0x0C));
        Assert.False(caps.Has(0x72));
        Assert.Null(caps.Codes[0x10]);
        Assert.Equal(new[] { 0x05, 0x08, 0x0B }, caps.Codes[0x14]);
        Assert.Equal(new[] { 0x11, 0x12, 0x0F, 0x10 }, caps.Codes[0x60]);
        Assert.Equal(16, caps.Codes[0x15]!.Count);
    }

    [Fact]
    public void CapsParseCompactAndLowerCase()
    {
        var caps = VcpCaps.Parse("(prot(monitor)model(abc)vcp(10 14(05 0b)1a 60(0f))mccs_ver(2.2))");
        Assert.Equal(("abc", "2.2"), (caps.Model, caps.Mccs));
        Assert.Equal(new byte[] { 0x10, 0x14, 0x1A, 0x60 }, caps.Codes.Keys.Order());
        Assert.Equal(new[] { 0x05, 0x0B }, caps.Codes[0x14]);
        Assert.Equal(new[] { 0x0F }, caps.Codes[0x60]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("))((vcp(")]
    [InlineData("(vcp(zz 1ff")]
    public void CapsParseGarbageIsEmpty(string text)
    {
        var caps = VcpCaps.Parse(text);
        Assert.Empty(caps.Codes);
        Assert.Null(caps.Model);
    }

    [Fact]
    public void CapsParseSurvivesDeepNesting() => Assert.Empty(VcpCaps.Parse(new string('(', 5000)).Codes);

    // Guards the percent/raw mapping and dim scaling at the range ends and for 100- and 255-step monitors.
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(100, 100, 100)]
    [InlineData(0, 255, 0)]
    [InlineData(50, 255, 128)]
    [InlineData(100, 255, 255)]
    [InlineData(140, 255, 255)]
    [InlineData(-3, 255, 0)]
    public void HardwareRawMapping(int percent, int max, int raw)
    {
        Assert.Equal(raw, Hardware.ToRaw(percent, max));
        Assert.Equal(Math.Clamp(percent, 0, 100), Hardware.ToPercent(raw, max));
    }

    [Fact]
    public void HardwarePercentEdgesAndDimming()
    {
        Assert.Equal(0, Hardware.ToPercent(40, 0));
        Assert.Equal(100, Hardware.ToPercent(300, 255));
        Assert.Equal(63, Hardware.Dimmed(63, 1));
        Assert.Equal(40, Hardware.Dimmed(50, 0.8));
        Assert.Equal(100, Hardware.Dimmed(100, 1.5));
        Assert.Equal(0, Hardware.Dimmed(50, -1));
    }

    // Guards that the dim scale used for the monitor is the same one Look applies to the ramp brightness.
    [Fact]
    public void DimScaleMatchesLook()
    {
        var s = new Settings { DimSchedule = NightSchedule.Custom, DimFrom = new(23, 0), DimTo = new(7, 0), DimPercent = 60, UserBrightness = 90, FadeMinutes = 0, WarmSchedule = NightSchedule.Off };
        var night = Night.Look(June(23, 30), s, Ny);
        Assert.Equal(0.6, Night.DimScale(night.Dimming, s), 6);
        Assert.Equal(54, night.Brightness, 6);
        var day = Night.Look(June(12), s, Ny);
        Assert.Equal(1, Night.DimScale(day.Dimming, s));
        Assert.Equal(90, day.Brightness, 6);
    }
}
