namespace ShadowBoost.Core;

public sealed class Rgb
{
    public Rgb(ushort[] red, ushort[] green, ushort[] blue) { Red = red; Green = green; Blue = blue; }
    public ushort[] Red { get; }
    public ushort[] Green { get; }
    public ushort[] Blue { get; }
    public bool SameAs(Rgb other) => Red.SequenceEqual(other.Red) && Green.SequenceEqual(other.Green) && Blue.SequenceEqual(other.Blue);
    public bool IsIdentity => SameAs(GammaRamp.Compose(null, 100, Night.NeutralKelvin));
}

public static class GammaRamp
{
    public static ushort[] Build(Curve c)
    {
        var ramp = new ushort[256];
        for (int i = 0; i < ramp.Length; i++)
        {
            double normalized = i / 255.0;
            double adjusted = normalized + c.BlackLift / 100.0 * (1.0 - normalized);
            adjusted = Math.Pow(adjusted, c.Gamma / 100.0);
            adjusted = (adjusted - 0.5) * (c.Contrast / 100.0) + 0.5;
            adjusted *= c.Brightness / 100.0;
            adjusted = Math.Max(0.0, Math.Min(1.0, adjusted));
            ramp[i] = (ushort)(adjusted * 65535);
        }
        return ramp;
    }

    /// <summary>Boost curve (or identity), times brightness percent, times the white point of <paramref name="kelvin"/> per channel.</summary>
    public static Rgb Compose(Curve? boost, double brightness, int kelvin)
    {
        var baseRamp = boost == null ? Identity() : Build(boost);
        var (r, g, b) = WhitePoint(kelvin);
        double scale = Math.Clamp(brightness, 15, 130) / 100.0;
        ushort[] Channel(double gain) => gain * scale == 1 ? baseRamp.ToArray()
            : baseRamp.Select(value => (ushort)Math.Clamp(Math.Round(value * gain * scale), 0, 65535)).ToArray();
        return new(Channel(r), Channel(g), Channel(b));
    }

    /// <summary>Channel gains for a blackbody colour temperature, normalised so 6500 K is (1, 1, 1).</summary>
    public static (double R, double G, double B) WhitePoint(int kelvin)
    {
        kelvin = Math.Clamp(kelvin, 1000, Night.NeutralKelvin);
        if (kelvin == Night.NeutralKelvin) return (1, 1, 1);
        var raw = Blackbody(kelvin);
        var white = Blackbody(Night.NeutralKelvin);
        return (Math.Min(1, raw.R / white.R), Math.Min(1, raw.G / white.G), Math.Min(1, raw.B / white.B));
    }

    // Tanner Helland's fit to the CIE 1964 blackbody colours, valid from 1000 K to 40000 K.
    static (double R, double G, double B) Blackbody(int kelvin)
    {
        double t = kelvin / 100.0;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    public static ushort[] Identity() => Enumerable.Range(0, 256).Select(i => (ushort)(i * 257)).ToArray();
}
