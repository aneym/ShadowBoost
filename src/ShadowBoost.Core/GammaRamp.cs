namespace ShadowBoost.Core;

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

    public static ushort[] Compose(Curve? boost, int userBrightness)
    {
        var ramp = boost == null ? Identity() : Build(boost);
        if (userBrightness == 100) return ramp;
        return ramp.Select(value => (ushort)Math.Clamp(value * Math.Clamp(userBrightness, 50, 130) / 100.0, 0, 65535)).ToArray();
    }

    public static ushort[] Identity() => Enumerable.Range(0, 256).Select(i => (ushort)(i * 257)).ToArray();
}
