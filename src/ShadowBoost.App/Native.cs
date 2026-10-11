using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ShadowBoost.App;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Ramp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
        public Ramp(Rgb values) { Red = values.Red.ToArray(); Green = values.Green.ToArray(); Blue = values.Blue.ToArray(); }
        public Ramp(int size) { Red = new ushort[size]; Green = new ushort[size]; Blue = new ushort[size]; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice data, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ref Ramp ramp);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr dc, ref Ramp ramp);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);

    // Every attached display gets the same ramp, the way f.lux sets each monitor's DC.
    // Returns false when any display refuses the ramp (Windows bounds ramps unless GdiICMGammaRange is raised).
    public static bool Apply(Rgb values, bool dry)
    {
        if (dry) return true;
        bool ok = true;
        Displays((dc, _) => { var ramp = new Ramp(values); ok &= SetDeviceGammaRamp(dc, ref ramp); });
        return ok;
    }
    public static bool Matches(Rgb values)
    {
        bool matches = true;
        Displays((dc, primary) => { if (!primary) return; var ramp = new Ramp(256); matches = GetDeviceGammaRamp(dc, ref ramp) && WithinTolerance(ramp.Red, values.Red) && WithinTolerance(ramp.Green, values.Green) && WithinTolerance(ramp.Blue, values.Blue); });
        return matches;
    }
    public static int[]? RampMid()
    {
        int[]? value = null;
        Displays((dc, primary) => { if (!primary) return; var ramp = new Ramp(256); if (GetDeviceGammaRamp(dc, ref ramp)) value = new int[] { ramp.Red[128], ramp.Green[128], ramp.Blue[128] }; });
        return value;
    }
    [DllImport("kernel32.dll")] internal static extern bool AttachConsole(uint processId);
    static bool WithinTolerance(ushort[] actual, ushort[] expected) =>
        actual.Length == expected.Length && actual.Zip(expected, (a, b) => Math.Abs((int)a - b) <= 512).All(value => value);

    static void Displays(Action<IntPtr, bool> action)
    {
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref device, 0)) break;
            if ((device.Flags & 1) == 0) continue;
            var dc = CreateDC("DISPLAY", device.Name, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try { action(dc, (device.Flags & 4) != 0); } finally { DeleteDC(dc); }
        }
    }
    public static void Decorate(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int rounded = 2, immersive = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 33, ref rounded, 4);
        DwmSetWindowAttribute(hwnd, 20, ref immersive, 4);
    }
}
