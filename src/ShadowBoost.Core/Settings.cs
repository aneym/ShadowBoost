using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadowBoost.Core;

public enum LevelId { Gentle, Balanced, Strong }
public enum Period { Day, Evening, Night }
public sealed record Curve(int BlackLift, int Gamma, int Contrast, int Brightness);

public sealed class Settings
{
    public double Latitude { get; set; } = 40.7128;
    public double Longitude { get; set; } = -74.0060;
    public string PlaceName { get; set; } = "New York";
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
    public int? MonitorBrightness { get; set; }
    public bool TrayPinApplied { get; set; }

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

    private void Normalize()
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
        if (MonitorBrightness.HasValue) MonitorBrightness = Math.Clamp(MonitorBrightness.Value, 0, 100);
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
