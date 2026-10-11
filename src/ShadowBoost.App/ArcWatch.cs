using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShadowBoost.App;

/// <summary>
/// Finds the game in the foreground and samples its screen. Anti-cheat safe by construction: the process name comes from
/// the system process list (no handle to the game), and pixels come from DXGI desktop duplication of the monitor (what OBS
/// and Discord read), opened only for one frame per sample. Nothing is injected and nothing reads game memory.
/// </summary>
internal static class ArcWatch
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    static (IntPtr Window, uint Pid, string? Name) last;

    /// <summary>The foreground window and its process name when that name is listed, else null.
    /// The name comes from the system process snapshot (NtQuerySystemInformation), never from a handle to the process,
    /// and is looked up again only when the foreground window or its process changes.</summary>
    internal static (IntPtr Window, string Process)? Foreground(IReadOnlyCollection<string> names)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return null;
        if (last.Window != hwnd || last.Pid != pid)
        {
            string? name = null;
            foreach (var process in Process.GetProcesses())
                using (process) if (process.Id == pid) name = process.ProcessName;
            last = (hwnd, pid, name);
        }
        return last.Name is { } n && names.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)) ? (hwnd, n) : null;
    }

    static ID3D11Device? device;
    static ID3D11DeviceContext? context;
    static ID3D11Texture2D? staging;
    static readonly object gate = new();

    /// <summary>Mean centre luma (0..255) of the monitor showing <paramref name="window"/>, or NaN with a reason.</summary>
    internal static (double Luma, string? Error) Sample(IntPtr window)
    {
        lock (gate)
        {
            try
            {
                var monitor = MonitorFromWindow(window, 2);
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
                    using (adapter)
                        for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                            using (output)
                            {
                                if (output.Description.Monitor != monitor) continue;
                                return Grab(adapter, output);
                            }
                return (double.NaN, "no output for the game's monitor");
            }
            catch (Exception ex) { Reset(); return (double.NaN, ex.GetType().Name + ": " + ex.Message); }
        }
    }

    static (double, string?) Grab(IDXGIAdapter1 adapter, IDXGIOutput output)
    {
        if (device == null)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device, out context).CheckError();
        }
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        using var duplication = output1.DuplicateOutput(device);
        var desc = duplication.Description;
        if (desc.ModeDescription.Format != Format.B8G8R8A8_UNorm) return (double.NaN, $"format {desc.ModeDescription.Format} (HDR or 10-bit desktop)");
        IDXGIResource? resource = null;
        // The first frame after DuplicateOutput carries the whole desktop image.
        for (int attempt = 0; attempt < 3 && resource == null; attempt++)
        {
            var result = duplication.AcquireNextFrame(100, out var info, out resource);
            if (result.Failure) { resource = null; if (result.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code) return (double.NaN, "acquire " + result.Code.ToString("X8")); continue; }
            if (info.LastPresentTime == 0 && attempt < 2) { resource.Dispose(); resource = null; duplication.ReleaseFrame(); }
        }
        if (resource == null) return (double.NaN, "no frame");
        try
        {
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var td = texture.Description;
            if (staging == null || staging.Description.Width != td.Width || staging.Description.Height != td.Height)
            {
                staging?.Dispose();
                staging = device!.CreateTexture2D(new Texture2DDescription { Width = td.Width, Height = td.Height, MipLevels = 1, ArraySize = 1, Format = td.Format, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });
            }
            context!.CopyResource(staging, texture);
        }
        finally { resource.Dispose(); duplication.ReleaseFrame(); }
        var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)staging.Description.Width, height = (int)staging.Description.Height, stride = (int)map.RowPitch;
            unsafe { return (Luma.Mean(new ReadOnlySpan<byte>((void*)map.DataPointer, stride * height), width, height, stride), null); }
        }
        finally { context.Unmap(staging, 0); }
    }

    internal static void Reset()
    {
        lock (gate) { staging?.Dispose(); staging = null; context?.Dispose(); context = null; device?.Dispose(); device = null; }
    }
}
