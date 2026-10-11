using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShadowBoost.App;

internal sealed record VcpValue(int? Current, int Max, int? Target, int? Readback, string? Error);
internal sealed record MonitorInfo(string Key, string Description, string? Model, string? Mccs, VcpCaps Caps, IReadOnlyDictionary<byte, VcpValue> Values)
{
    internal VcpValue? Brightness => Values.GetValueOrDefault((byte)0x10);
    internal bool HasBrightness => Brightness is { Current: not null, Max: > 0, Error: null };
}

/// <summary>The only place that talks DDC/CI. Every call runs on one background thread, so the UI never blocks on a monitor.</summary>
internal sealed class MonitorBus : IDisposable
{
    const int MinGapMs = 60, SettleMs = 100;
    sealed class Entry
    {
        public string Key = "", Description = ""; public IntPtr Handle; public VcpCaps Caps = VcpCaps.Parse(null);
        public Dictionary<byte, VcpValue> Values = new(); public long LastCommand;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfoEx
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr rect, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint index, ref Native.DisplayDevice data, uint flags);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
    [DllImport("dxva2.dll")] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, [Out] byte[] caps, uint length);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr monitor);

    readonly object gate = new();
    readonly Queue<Action> jobs = new();
    readonly Dictionary<(string Key, byte Code), int> pending = new();
    readonly List<Entry> entries = new();
    List<Action>? refreshDone;
    (string Key, byte Code)? writing;
    readonly Thread thread;
    volatile IReadOnlyList<MonitorInfo> snapshot = Array.Empty<MonitorInfo>();
    bool disposed;

    /// <summary>Raised on the bus thread after a refresh and after each readback.</summary>
    internal event Action? Changed;
    internal IReadOnlyList<MonitorInfo> Monitors => snapshot;

    internal MonitorBus() { thread = new Thread(Loop) { IsBackground = true, Name = "ShadowBoost DDC" }; thread.Start(); }

    /// <summary>Re-enumerates monitors and reads their capabilities; calls <paramref name="done"/> on the bus thread. Refreshes queued behind a waiting one are merged.</summary>
    internal void Refresh(Action? done = null)
    {
        lock (gate)
        {
            if (disposed) return;
            if (refreshDone != null) { if (done != null) refreshDone.Add(done); return; }
            refreshDone = new(); if (done != null) refreshDone.Add(done);
            jobs.Enqueue(DoRefresh); Monitor.PulseAll(gate);
        }
    }
    /// <summary>Queues a write; only the latest value per monitor and code is sent.</summary>
    internal void Set(string key, byte code, int raw) { lock (gate) { if (disposed) return; pending[(key, code)] = raw; Monitor.PulseAll(gate); } }
    /// <summary>Queues a read that updates Current and Max, then calls <paramref name="done"/> on the bus thread.</summary>
    internal void Read(string key, byte code, Action<VcpValue>? done = null)
    {
        lock (gate) { if (disposed) return; jobs.Enqueue(() => { var entry = entries.Find(e => e.Key == key); if (entry == null) return; var value = ReadValue(entry, code, 2); done?.Invoke(value); Publish(); }); Monitor.PulseAll(gate); }
    }
    /// <summary>True while a write for this monitor is queued or being sent.</summary>
    internal bool Busy(string key) { lock (gate) return pending.Keys.Any(k => k.Key == key) || writing?.Key == key; }

    void Loop()
    {
        while (true)
        {
            Action? job = null; (string Key, byte Code, int Raw)? write = null;
            lock (gate)
            {
                while (!disposed && jobs.Count == 0 && pending.Count == 0) Monitor.Wait(gate);
                if (disposed) break;
                if (jobs.Count > 0) job = jobs.Dequeue();
                else { var item = pending.First(); pending.Remove(item.Key); writing = item.Key; write = (item.Key.Key, item.Key.Code, item.Value); }
            }
            try { if (job != null) job(); else Write(write!.Value.Key, write.Value.Code, write.Value.Raw); }
            catch (Exception ex) { Trace.WriteLine(ex); }
            finally { lock (gate) writing = null; }
        }
        foreach (var entry in entries) DestroyPhysicalMonitor(entry.Handle);
        entries.Clear();
    }

    void DoRefresh()
    {
        List<Action> done;
        lock (gate) { done = refreshDone ?? new(); refreshDone = null; }
        foreach (var entry in entries) DestroyPhysicalMonitor(entry.Handle);
        entries.Clear();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                try { Enumerate(monitor); } catch (Exception ex) { Trace.WriteLine(ex); }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
        Publish();
        foreach (var action in done) { try { action(); } catch (Exception ex) { Trace.WriteLine(ex); } }
    }

    void Enumerate(IntPtr monitor)
    {
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) info.Device = "";
        bool primary = (info.Flags & 1) != 0;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return;
        var physical = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return;
        for (int index = 0; index < count; index++)
        {
            string key = index == 0 ? InterfaceKey(info.Device) ?? $"{info.Device}/{index}" : $"{info.Device}/{index}";
            for (int n = 2; entries.Any(e => e.Key == key); n++) key = $"{key.Split('~')[0]}~{n}";
            var entry = new Entry { Key = key, Description = physical[index].Description, Handle = physical[index].Handle };
            entries.Insert(primary && index == 0 ? 0 : entries.Count, entry);
            string? caps = null;
            for (int attempt = 0; attempt < 3 && caps == null; attempt++)
            {
                if (attempt > 0) Thread.Sleep(100);
                if (GetCapabilitiesStringLength(entry.Handle, out var length) && length > 0)
                {
                    var buffer = new byte[length];
                    if (CapabilitiesRequestAndCapabilitiesReply(entry.Handle, buffer, length)) caps = Encoding.ASCII.GetString(buffer).TrimEnd('\0');
                }
            }
            entry.Caps = VcpCaps.Parse(caps);
            ReadValue(entry, 0x10, 2);
        }
    }

    // The interface path looks like \\?\DISPLAY#GSM780E#5&20e287fe&0&UID4352#{guid}; the middle part names the monitor and its port.
    static string? InterfaceKey(string device)
    {
        if (device.Length == 0) return null;
        var dd = new Native.DisplayDevice { Size = Marshal.SizeOf<Native.DisplayDevice>() };
        if (!EnumDisplayDevices(device, 0, ref dd, 1)) return null;
        int start = dd.Id.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase), end = dd.Id.IndexOf("#{", StringComparison.Ordinal);
        if (start < 0 || end <= start + 8) return null;
        return dd.Id[(start + 8)..end];
    }

    VcpValue ReadValue(Entry entry, byte code, int tries)
    {
        var previous = entry.Values.GetValueOrDefault(code);
        VcpValue value = new(previous?.Current, previous?.Max ?? 0, previous?.Target, previous?.Readback, "read failed");
        for (int attempt = 0; attempt < tries; attempt++)
        {
            if (attempt > 0) Thread.Sleep(MinGapMs);
            Gap(entry);
            bool ok = GetVCPFeatureAndVCPFeatureReply(entry.Handle, code, IntPtr.Zero, out var current, out var max);
            int error = Marshal.GetLastWin32Error(); entry.LastCommand = Stopwatch.GetTimestamp();
            if (ok) { value = value with { Current = (int)current, Max = (int)max, Error = null }; break; }
            value = value with { Error = $"read failed (error {error})" };
        }
        entry.Values[code] = value;
        return value;
    }

    // At least MinGapMs between commands to one monitor.
    static void Gap(Entry entry)
    {
        if (entry.LastCommand == 0) return;
        double wait = MinGapMs - Stopwatch.GetElapsedTime(entry.LastCommand).TotalMilliseconds;
        if (wait > 0) Thread.Sleep((int)Math.Ceiling(wait));
    }

    void Write(string key, byte code, int raw)
    {
        var entry = entries.Find(e => e.Key == key);
        if (entry == null) return;
        var known = entry.Values.GetValueOrDefault(code);
        if (known is { Max: > 0 }) raw = Math.Clamp(raw, 0, known.Max);
        Gap(entry);
        bool ok = SetVCPFeature(entry.Handle, code, (uint)Math.Max(0, raw));
        int error = Marshal.GetLastWin32Error(); entry.LastCommand = Stopwatch.GetTimestamp();
        entry.Values[code] = (known ?? new VcpValue(null, 0, null, null, null)) with { Target = raw, Error = ok ? null : $"write failed (error {error})" };
        if (ok)
        {
            Thread.Sleep(SettleMs);
            var value = ReadValue(entry, code, 1);
            entry.Values[code] = value with { Readback = value.Current };
        }
        Publish();
    }

    void Publish()
    {
        snapshot = entries.Select(e => new MonitorInfo(e.Key, e.Description, e.Caps.Model, e.Caps.Mccs, e.Caps, new Dictionary<byte, VcpValue>(e.Values))).ToList();
        try { Changed?.Invoke(); } catch (Exception ex) { Trace.WriteLine(ex); }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; Monitor.PulseAll(gate); }
        thread.Join(1500);
    }
}
