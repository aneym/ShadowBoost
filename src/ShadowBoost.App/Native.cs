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
        public Ramp(ushort[] values) { Red = values; Green = values.ToArray(); Blue = values.ToArray(); }
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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr rect, MonitorCallback callback, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr monitor);

    public static void Apply(ushort[] values, bool dry)
    {
        if (dry) return;
        Displays((dc, _) => { var ramp = new Ramp(values); SetDeviceGammaRamp(dc, ref ramp); });
    }
    public static bool Matches(ushort[] values)
    {
        bool matches = true;
        Displays((dc, primary) => { if (!primary) return; var ramp = new Ramp(new ushort[256]); matches = GetDeviceGammaRamp(dc, ref ramp) && WithinTolerance(ramp.Red, values) && WithinTolerance(ramp.Green, values) && WithinTolerance(ramp.Blue, values); });
        return matches;
    }
    public static int? RampMid()
    {
        int? value = null;
        Displays((dc, primary) => { if (!primary) return; var ramp = new Ramp(new ushort[256]); if (GetDeviceGammaRamp(dc, ref ramp)) value = ramp.Red[128]; });
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
    public static int? Brightness(int? set = null)
    {
        int? result = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return true;
            var monitors = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return true;
            foreach (var item in monitors)
            {
                try
                {
                    if (GetMonitorBrightness(item.Handle, out var min, out var current, out var max) && max > min)
                    {
                        result ??= (int)((current - min) * 100 / (max - min));
                        if (set.HasValue) SetMonitorBrightness(item.Handle, min + (uint)(Math.Clamp(set.Value, 0, 100) * (max - min) / 100));
                    }
                }
                finally { DestroyPhysicalMonitor(item.Handle); }
            }
            return true;
        }, IntPtr.Zero);
        return result;
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
