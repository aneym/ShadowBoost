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

/// <summary>Maps between percent and a monitor's raw VCP range.</summary>
public static class Hardware
{
    public static int ToRaw(int percent, int max) => (int)Math.Round(Math.Clamp(percent, 0, 100) * max / 100.0, MidpointRounding.AwayFromZero);
    public static int ToPercent(int raw, int max) => max <= 0 ? 0 : Math.Clamp((int)Math.Round(raw * 100.0 / max, MidpointRounding.AwayFromZero), 0, 100);
    public static int Dimmed(int percent, double dimScale) => Math.Clamp((int)Math.Round(percent * dimScale, MidpointRounding.AwayFromZero), 0, 100);
}
