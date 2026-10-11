namespace ShadowBoost.Core;

/// <summary>A pinned ARC look, or Auto (follow the scene).</summary>
public enum ArcPin { Auto, Day, Dusk, Night }

/// <summary>The "ARC competitive" profile: applies while a listed game process owns the foreground window.</summary>
public sealed class ArcSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Process names (no .exe) that switch the profile on.</summary>
    public List<string> Processes { get; set; } = new() { "PioneerGame" };
    /// <summary>A manual pick sticks across scenes and game restarts until Auto is picked again.</summary>
    public ArcPin Pin { get; set; } = ArcPin.Auto;
    public Curve Day { get; set; } = new(3, 90, 100, 100);
    public Curve Dusk { get; set; } = new(8, 78, 104, 100);
    public Curve Night { get; set; } = new(14, 62, 108, 104);
    /// <summary>Monitor brightness percent while the game runs, by the room-light proxy (the sun schedule's period); no light sensor exists.</summary>
    public Dictionary<Period, int> RoomBrightness { get; set; } = new() { [Period.Day] = 100, [Period.Evening] = 90, [Period.Night] = 80 };
    /// <summary>Monitor contrast percent while the game runs; null keeps the saved contrast.</summary>
    public int? Contrast { get; set; } = 70;
    /// <summary>Scene adaptation from screen samples; off means the Dusk curve unless pinned.</summary>
    public bool SceneAdaptive { get; set; } = true;
    /// <summary>Seconds between screen samples (desktop duplication is opened only for the sample).</summary>
    public double SampleSeconds { get; set; } = 2;
    /// <summary>Mean luma (0..255) at or above which a scene counts as day, and at or below which it counts as night.</summary>
    public int DayLuma { get; set; } = 90;
    public int NightLuma { get; set; } = 30;

    public void Normalize()
    {
        var d = new ArcSettings();
        Processes = (Processes ?? d.Processes).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Trim()[..^4] : p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!Enum.IsDefined(Pin)) Pin = ArcPin.Auto;
        Day = Clamp(Day ?? d.Day); Dusk = Clamp(Dusk ?? d.Dusk); Night = Clamp(Night ?? d.Night);
        RoomBrightness ??= new();
        foreach (var (period, value) in d.RoomBrightness) RoomBrightness[period] = Math.Clamp(RoomBrightness.GetValueOrDefault(period, value), 0, 100);
        if (Contrast.HasValue) Contrast = Math.Clamp(Contrast.Value, 0, 100);
        SampleSeconds = double.IsFinite(SampleSeconds) ? Math.Clamp(SampleSeconds, 0.5, 30) : d.SampleSeconds;
        DayLuma = Math.Clamp(DayLuma, 1, 255); NightLuma = Math.Clamp(NightLuma, 0, DayLuma - 1);
    }

    static Curve Clamp(Curve c) => new(Math.Clamp(c.BlackLift, 0, 30), Math.Clamp(c.Gamma, 30, 100), Math.Clamp(c.Contrast, 50, 150), Math.Clamp(c.Brightness, 50, 130));

    /// <summary>Darkness 0 (day) .. 1 (night) for a pinned look, or null for Auto.</summary>
    public static double? PinDarkness(ArcPin pin) => pin switch { ArcPin.Day => 0, ArcPin.Dusk => 0.5, ArcPin.Night => 1, _ => null };

    /// <summary>Scene darkness from mean luma: day at DayLuma and above, night at NightLuma and below, linear between.</summary>
    public double Darkness(double luma) => Math.Clamp((DayLuma - luma) / (double)(DayLuma - NightLuma), 0, 1);

    /// <summary>The curve for a darkness: Day to Dusk over 0..0.5, Dusk to Night over 0.5..1, rounded to whole ramp units.</summary>
    public Curve CurveAt(double darkness)
    {
        darkness = Math.Clamp(darkness, 0, 1);
        var (a, b, t) = darkness <= 0.5 ? (Day, Dusk, darkness * 2) : (Dusk, Night, (darkness - 0.5) * 2);
        int Mix(int x, int y) => (int)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero);
        return new(Mix(a.BlackLift, b.BlackLift), Mix(a.Gamma, b.Gamma), Mix(a.Contrast, b.Contrast), Mix(a.Brightness, b.Brightness));
    }
}

/// <summary>
/// Turns noisy screen samples into a steady darkness: the median of the last <see cref="Window"/> of samples,
/// quantised to 0.1 steps, adopted only after it holds for <see cref="Agree"/>, then faded in over <see cref="Fade"/>.
/// A muzzle flash, an explosion or a menu frame never moves the look.
/// </summary>
public sealed class SceneTracker
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(8), Agree = TimeSpan.FromSeconds(3), Fade = TimeSpan.FromSeconds(1.5);
    readonly Queue<(DateTimeOffset At, double Darkness)> samples = new();
    double settled, from;
    DateTimeOffset fadeStart = DateTimeOffset.MinValue;
    (double Step, DateTimeOffset Since)? candidate;

    public SceneTracker(double start = 0.5) { settled = from = Quantise(start); }

    public static double Quantise(double darkness) => Math.Round(Math.Clamp(darkness, 0, 1) * 10, MidpointRounding.AwayFromZero) / 10;

    /// <summary>The settled step the look is fading to.</summary>
    public double Target => settled;

    public void Add(DateTimeOffset at, double darkness)
    {
        samples.Enqueue((at, Math.Clamp(darkness, 0, 1)));
        while (samples.Count > 0 && at - samples.Peek().At > Window) samples.Dequeue();
        var sorted = samples.Select(s => s.Darkness).Order().ToArray();
        double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        double step = Quantise(median);
        if (step == settled) { candidate = null; return; }
        if (candidate is not { } c || c.Step != step) { candidate = (step, at); c = candidate.Value; }
        if (at - c.Since < Agree) return;
        from = Current(at); settled = step; fadeStart = at; candidate = null;
    }

    /// <summary>The darkness to show now: the fade from the previous look to the settled step.</summary>
    public double Current(DateTimeOffset now)
    {
        double t = Fade <= TimeSpan.Zero ? 1 : Math.Clamp((now - fadeStart) / Fade, 0, 1);
        return from + (settled - from) * t;
    }

    public bool Fading(DateTimeOffset now) => now - fadeStart < Fade;
}

/// <summary>Mean luma of a downsampled frame, centre region only (HUD edges excluded).</summary>
public static class Luma
{
    /// <summary>Mean Rec. 709 luma (0..255) of BGRA pixels in the centre <paramref name="centre"/> fraction, sampling a grid of about <paramref name="grid"/> x <paramref name="grid"/> points.</summary>
    public static double Mean(ReadOnlySpan<byte> bgra, int width, int height, int stride, double centre = 0.7, int grid = 48)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4) return double.NaN;
        int x0 = (int)(width * (1 - centre) / 2), y0 = (int)(height * (1 - centre) / 2);
        int w = Math.Max(1, width - 2 * x0), h = Math.Max(1, height - 2 * y0);
        int nx = Math.Min(grid, w), ny = Math.Min(grid, h);
        double sum = 0;
        for (int j = 0; j < ny; j++)
        {
            int y = y0 + (int)((j + 0.5) * h / ny);
            for (int i = 0; i < nx; i++)
            {
                int x = x0 + (int)((i + 0.5) * w / nx), p = y * stride + x * 4;
                sum += 0.0722 * bgra[p] + 0.7152 * bgra[p + 1] + 0.2126 * bgra[p + 2];
            }
        }
        return sum / (nx * ny);
    }
}
