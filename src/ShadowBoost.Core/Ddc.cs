using System.Globalization;

namespace ShadowBoost.Core;

/// <summary>A monitor's MCCS capabilities string: model, MCCS version and the VCP codes it lists (with their value lists, when it gives one).</summary>
public sealed record VcpCaps(string? Model, string? Mccs, IReadOnlyDictionary<byte, IReadOnlyList<int>?> Codes)
{
    public bool Has(byte code) => Codes.ContainsKey(code);

    sealed record Node(string Name, List<Node>? Kids);

    /// <summary>Tolerant of spacing, case and garbage; never throws.</summary>
    public static VcpCaps Parse(string? caps)
    {
        var codes = new Dictionary<byte, IReadOnlyList<int>?>(); string? model = null, mccs = null;
        try
        {
            int i = 0; var root = new List<Node>(); var text = caps ?? "";
            while (i < text.Length) { root.AddRange(Items(text, ref i, 0)); if (i < text.Length) i++; }
            foreach (var node in Flat(root))
            {
                if (node.Name.Equals("model", StringComparison.OrdinalIgnoreCase)) model ??= First(node);
                else if (node.Name.Equals("mccs_ver", StringComparison.OrdinalIgnoreCase)) mccs ??= First(node);
                else if (node.Name.Equals("vcp", StringComparison.OrdinalIgnoreCase) && node.Kids != null)
                    foreach (var code in node.Kids)
                        if (code.Name.Length > 0 && Hex(code.Name) is int c and >= 0 and <= 255)
                            codes[(byte)c] = code.Kids == null ? null : code.Kids.Where(v => v.Name.Length > 0 && Hex(v.Name) != null).Select(v => Hex(v.Name)!.Value).ToList();
            }
        }
        catch (Exception) { codes.Clear(); model = mccs = null; }
        return new(model, mccs, codes);
    }

    static string? First(Node node) => node.Kids?.FirstOrDefault(k => k.Name.Length > 0)?.Name;
    static int? Hex(string text) => int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ? value : null;
    static IEnumerable<Node> Flat(List<Node> list) { foreach (var node in list) if (node.Name.Length > 0) yield return node; else if (node.Kids != null) foreach (var inner in Flat(node.Kids)) yield return inner; }

    // Words are runs outside whitespace and parentheses; a word directly followed by "(" owns that group, a bare group has an empty name.
    static List<Node> Items(string s, ref int i, int depth)
    {
        var list = new List<Node>();
        if (depth > 16) { i = s.Length; return list; }
        while (i < s.Length && s[i] != ')')
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            string name = "";
            if (s[i] != '(') { int start = i; while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '(' && s[i] != ')') i++; name = s[start..i]; }
            List<Node>? kids = null;
            if (i < s.Length && s[i] == '(') { i++; kids = Items(s, ref i, depth + 1); if (i < s.Length) i++; }
            list.Add(new(name, kids));
        }
        return list;
    }
}

/// <summary>One brightness level for the whole display chain: ramp 50..100 % below the monitor's minimum, hardware 0..100 %, then ramp 100..130 % above its maximum.</summary>
public static class Level
{
    public const int Below = 50, Span = 100, Above = 30, Max = Below + Span + Above;

    public static (int Hardware, double Ramp) Split(double level)
    {
        level = Math.Clamp(level, 0, Max);
        if (level < Below) return (0, Below + level);
        if (level <= Below + Span) return ((int)Math.Round(level - Below, MidpointRounding.AwayFromZero), 100);
        return (Span, 100 + level - (Below + Span));
    }

    public static double Join(int hardwarePercent, double ramp)
    {
        if (hardwarePercent <= 0 && ramp < 100) return Math.Max(ramp - Below, 0);
        if (hardwarePercent >= Span && ramp > 100) return Math.Min(Below + Span + (ramp - 100), Max);
        return Below + Math.Clamp(hardwarePercent, 0, Span);
    }

    public static string Label(double level)
    {
        var (hardware, ramp) = Split(level);
        return level < Below ? $"0% · ramp {Math.Round(ramp)}%" : level > Below + Span ? $"100% · ramp {Math.Round(ramp)}%" : $"{hardware}%";
    }

    /// <summary>Dimming scales the one curve.</summary>
    public static double Dim(double level, double dimScale) => level * dimScale;
}

/// <summary>Maps between percent and a monitor's raw VCP range.</summary>
public static class Hardware
{
    public static int ToRaw(int percent, int max) => (int)Math.Round(Math.Clamp(percent, 0, 100) * max / 100.0, MidpointRounding.AwayFromZero);
    public static int ToPercent(int raw, int max) => max <= 0 ? 0 : Math.Clamp((int)Math.Round(raw * 100.0 / max, MidpointRounding.AwayFromZero), 0, 100);
    /// <summary>MCCS 0x14 colour preset names.</summary>
    public static string PresetName(int value) => value switch
    {
        0x01 => "sRGB", 0x02 => "Native", 0x03 => "4000 K", 0x04 => "5000 K", 0x05 => "6500 K", 0x06 => "7500 K", 0x07 => "8200 K",
        0x08 => "9300 K", 0x09 => "10000 K", 0x0A => "11500 K", 0x0B => "User 1", 0x0C => "User 2", 0x0D => "User 3",
        _ => $"Preset 0x{value:X2}"
    };
    public static bool IsUserPreset(int value) => value is >= 0x0B and <= 0x0D;
    /// <summary>The user's neutral RGB gains scaled by the same white point the gamma ramp uses for <paramref name="kelvin"/>.</summary>
    public static (int R, int G, int B) WarmGains((int R, int G, int B) neutral, int kelvin, int max)
    {
        var (r, g, b) = GammaRamp.WhitePoint(kelvin);
        int Scale(int gain, double factor) => Math.Clamp((int)Math.Round(gain * factor, MidpointRounding.AwayFromZero), 0, Math.Max(max, 0));
        return (Scale(neutral.R, r), Scale(neutral.G, g), Scale(neutral.B, b));
    }
    public static int Dimmed(int percent, double dimScale) => Math.Clamp((int)Math.Round(percent * dimScale, MidpointRounding.AwayFromZero), 0, 100);
}
