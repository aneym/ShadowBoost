using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadowBoost.Core;

public enum LevelId { Gentle, Balanced, Strong }
public enum Period { Day, Evening, Night }
public sealed record Curve(int BlackLift, int Gamma, int Contrast, int Brightness);

/// <summary>Saved hardware (DDC/CI) facts about one monitor.</summary>
public sealed class MonitorSettings
{
    public string? Model { get; set; }
    /// <summary>Percent of the contrast (0x12) range.</summary>
    public int? Contrast { get; set; }
    /// <summary>Neutral red, green, blue video gains (0x16, 0x18, 0x1A) as raw values; warmth is applied on top of them.</summary>
    public int[]? Gains { get; set; }
    /// <summary>The gains read when the monitor was first seen, for Reset.</summary>
    public int[]? DefaultGains { get; set; }
    /// <summary>Raw colour preset (0x14) value.</summary>
    public int? Preset { get; set; }
}

public sealed class Settings
{
    public double Latitude { get; set; } = 40.7128;
    public double Longitude { get; set; } = -74.0060;
    public string PlaceName { get; set; } = "New York";
    public bool FollowSun { get; set; } = true;
    public TimeOnly DayStart { get; set; } = new(7, 0);
    public TimeOnly EveningStart { get; set; } = new(19, 0);
    public TimeOnly NightStart { get; set; } = new(22, 30);
    public Dictionary<Period, LevelId> PeriodLevels { get; set; } = new()
    {
        [Period.Day] = LevelId.Strong, [Period.Evening] = LevelId.Balanced, [Period.Night] = LevelId.Gentle
    };
    public Dictionary<LevelId, Curve> Curves { get; set; } = new()
    {
        [LevelId.Gentle] = new(4, 82, 100, 100),
        [LevelId.Balanced] = new(10, 70, 105, 100),
        [LevelId.Strong] = new(18, 55, 115, 105)
    };
    public bool StartAtSignIn { get; set; } = true;
    public int UserBrightness { get; set; } = 100;
    // Legacy: still loaded and saved, no longer used by the app.
    public int? MonitorBrightness { get; set; }
    /// <summary>The one brightness slider, 0..180 (see <see cref="Level"/>); null until first adopted from the monitor.</summary>
    public double? BrightnessLevel { get; set; }
    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new();
    public bool TrayPinApplied { get; set; }
    public NightSchedule WarmSchedule { get; set; } = NightSchedule.Sunset;
    public TimeOnly WarmFrom { get; set; } = new(21, 0);
    public TimeOnly WarmTo { get; set; } = new(7, 0);
    public int WarmKelvin { get; set; } = 3400;
    public WarmHold? WarmHold { get; set; }
    public NightSchedule DimSchedule { get; set; } = NightSchedule.Off;
    public TimeOnly DimFrom { get; set; } = new(23, 0);
    public TimeOnly DimTo { get; set; } = new(7, 0);
    public int DimPercent { get; set; } = 80;
    public int FadeMinutes { get; set; } = 20;

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ClockConverter());
        return options;
    }

    public static Settings Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("Settings must be an object.");
            settings.Normalize();
            return settings;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bad", overwrite: true);
            return new();
        }
    }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static Settings FromLegacy(string legacyText)
    {
        var settings = new Settings();
        var fields = legacyText.Split(',');
        if (fields.Length is not (2 or 3 or 5)) return settings;
        var values = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            if (!int.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                return settings;
        settings.Curves[LevelId.Balanced] = fields.Length == 5
            ? new(values[0], values[1], values[2], values[3])
            : settings.Curves[LevelId.Balanced] with { Gamma = values[0], Brightness = values[1] };
        if (fields.Length is 3 or 5) settings.MonitorBrightness = values[^1];
        settings.Normalize();
        return settings;
    }

    private static int[]? Gains(int[]? gains) => gains is { Length: 3 } ? gains.Select(g => Math.Clamp(g, 0, 65535)).ToArray() : null;

    public void Normalize()
    {
        var defaults = new Settings();
        PeriodLevels ??= new();
        Curves ??= new();
        foreach (var (period, level) in defaults.PeriodLevels)
            if (!PeriodLevels.TryGetValue(period, out var saved) || !Enum.IsDefined(saved))
                PeriodLevels[period] = level;
        foreach (var (level, curve) in defaults.Curves)
        {
            var saved = Curves.GetValueOrDefault(level) ?? curve;
            Curves[level] = new(Math.Clamp(saved.BlackLift, 0, 30), Math.Clamp(saved.Gamma, 30, 100),
                Math.Clamp(saved.Contrast, 50, 150), Math.Clamp(saved.Brightness, 50, 130));
        }
        UserBrightness = Math.Clamp(UserBrightness, 50, 130);
        WarmKelvin = (int)Math.Round(Math.Clamp(WarmKelvin, Night.MinKelvin, Night.NeutralKelvin) / 100.0) * 100;
        DimPercent = Math.Clamp(DimPercent, 30, 100);
        FadeMinutes = Math.Clamp(FadeMinutes, 0, 60);
        if (!Enum.IsDefined(WarmSchedule)) WarmSchedule = NightSchedule.Sunset;
        if (!Enum.IsDefined(DimSchedule)) DimSchedule = NightSchedule.Off;
        if (MonitorBrightness.HasValue) MonitorBrightness = Math.Clamp(MonitorBrightness.Value, 0, 100);
        if (BrightnessLevel.HasValue) BrightnessLevel = double.IsFinite(BrightnessLevel.Value) ? Math.Clamp(BrightnessLevel.Value, 0, Level.Max) : null;
        Monitors ??= new();
        foreach (var (key, monitor) in Monitors.ToList())
            if (monitor == null) Monitors[key] = new();
            else
            {
                if (monitor.Contrast.HasValue) monitor.Contrast = Math.Clamp(monitor.Contrast.Value, 0, 100);
                if (monitor.Preset.HasValue) monitor.Preset = Math.Clamp(monitor.Preset.Value, 0, 255);
                monitor.Gains = Gains(monitor.Gains); monitor.DefaultGains = Gains(monitor.DefaultGains);
            }
    }

    private sealed class ClockConverter : JsonConverter<TimeOnly>
    {
        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String ||
                !TimeOnly.TryParseExact(reader.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new JsonException("Expected time in HH:mm format.");
            return time;
        }
        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("HH:mm", CultureInfo.InvariantCulture));
    }
}
