namespace ShadowBoost.Core;

public sealed record EngineState(bool Enabled, Period Period, LevelId ScheduledLevel, LevelId ActiveLevel,
    bool IsOverride, DateTimeOffset NextChange, Curve? AppliedCurve);

public sealed class BoostEngine
{
    private readonly TimeZoneInfo timeZone;
    private bool enabled;
    private (LevelId Level, DateTimeOffset End)? manualOverride;
    private Curve? lastApplied;

    public BoostEngine(Settings settings, TimeZoneInfo tz)
    {
        Settings = settings;
        timeZone = tz;
    }

    public Settings Settings { get; private set; }

    public void UpdateSettings(Settings s) => Settings = s;

    public void TurnOn(DateTimeOffset now)
    {
        enabled = true;
        manualOverride = null;
        lastApplied = State(now).AppliedCurve;
    }

    public void TurnOff(DateTimeOffset now)
    {
        enabled = false;
        manualOverride = null;
        lastApplied = State(now).AppliedCurve;
    }

    public void SetLevel(LevelId level, DateTimeOffset now)
    {
        enabled = true;
        manualOverride = (level, Schedule.At(now, Settings, timeZone).End);
        lastApplied = State(now).AppliedCurve;
    }

    public EngineState State(DateTimeOffset now)
    {
        var slot = Schedule.At(now, Settings, timeZone);
        bool isOverride = enabled && manualOverride is { } selection && now < selection.End;
        var level = isOverride ? manualOverride!.Value.Level : slot.Level;
        var next = isOverride && manualOverride!.Value.End < slot.End ? manualOverride.Value.End : slot.End;
        return new(enabled, slot.Period, slot.Level, level, isOverride, next,
            enabled ? Settings.Curves[level] : null);
    }

    public bool Tick(DateTimeOffset now)
    {
        if (manualOverride is { } selection && now >= selection.End) manualOverride = null;
        var applied = State(now).AppliedCurve;
        bool changed = applied != lastApplied;
        lastApplied = applied;
        return changed;
    }
}
