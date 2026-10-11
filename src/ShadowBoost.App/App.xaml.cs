using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ShadowBoost.App;

public partial class App : Application
{
    internal Settings Settings = new();
    internal BoostEngine Engine = null!;
    internal EngineState State => Engine.State(DateTimeOffset.Now);
    // With hardware brightness the one level splits into the monitor's percent and the ramp's brightness.
    // While ARC owns the foreground: neutral white point, ramp brightness 100 and the room-proxy monitor brightness (EffectiveLevel).
    internal Look Look { get { var look = Night.Look(DateTimeOffset.Now, Settings, TimeZoneInfo.Local); if (ArcOn) return look with { Kelvin = Night.NeutralKelvin, Brightness = 100 }; return HardwareBrightness ? look with { Brightness = Level.Split(EffectiveLevel(look)).Ramp } : look; } }
    // ARC competitive profile state (see ArcWatch and Core/Arc.cs).
    internal bool ArcOn;
    string? arcProcess; IntPtr arcWindow; SceneTracker arcScene = new();
    DateTimeOffset arcLost = DateTimeOffset.MaxValue, arcNextSample, arcSince;
    double arcLuma = double.NaN; string? arcError; bool arcSampling; int arcSamples;
    DispatcherTimer? arcTimer;
    // Hardware only (default): Auto is the room level and pins move the monitor brightness. With the software lift: the scene drives the ramp.
    internal double ArcDarkness(DateTimeOffset now) => ArcSettings.PinDarkness(Settings.Arc.Pin) ?? (!Settings.Arc.SoftwareLift ? 0 : Settings.Arc.SceneAdaptive ? arcScene.Current(now) : 0.5);
    internal Curve? ArcCurve => Settings.Arc.SoftwareLift ? Settings.Arc.CurveAt(ArcDarkness(DateTimeOffset.Now)) : null;
    // With the software lift the scene moves only the ramp; the monitor stays at the room level (or the pin) so DDC never pumps.
    int ArcHardwarePercent => Settings.Arc.HardwareBrightness(State.Period, Settings.Arc.SoftwareLift && Settings.Arc.Pin == ArcPin.Auto ? 0 : ArcDarkness(DateTimeOffset.Now));
    Curve? CurrentCurve => ArcOn ? ArcCurve : State.AppliedCurve;
    internal bool Dry, Isolated, Dark;
    MonitorBus? bus;
    // The last raw value we sent (or adopted) per monitor and VCP code.
    readonly Dictionary<(string Key, byte Code), int> sentRaw = new();
    int ticks;
    bool gainsReset;
    internal IReadOnlyList<MonitorInfo> Monitors => bus?.Monitors ?? Array.Empty<MonitorInfo>();
    // Hardware mode: every display answers DDC/CI brightness. Otherwise the ramp alone scales brightness, as before.
    internal bool HardwareBrightness => Monitors.Count > 0 && Monitors.All(m => m.HasBrightness);
    // Before the first adoption the level comes from the primary monitor's current value and the saved ramp brightness.
    internal double SavedLevel => Settings.BrightnessLevel ?? Level.Join(Monitors.Count > 0 && Monitors[0].Brightness is { Current: int c } b ? Hardware.ToPercent(c, b.Max) : 0, Math.Clamp(Settings.UserBrightness, 50, 130));
    MonitorSettings? Saved(MonitorInfo m) => Settings.Monitors.GetValueOrDefault(m.Key);
    // A monitor can carry warmth in its RGB gains when it lists them and its preset is a user preset (or it has no presets).
    bool GainsActive(MonitorInfo m) => m.HasGains && Saved(m) is { Gains: { Length: 3 } } s && (!m.Caps.Has(0x14) || (s.Preset is int p && Hardware.IsUserPreset(p)));
    // Warmth goes to the hardware only when every display can take it; otherwise one shared ramp carries it for all.
    internal bool HardwareWarmth => Monitors.Count > 0 && Monitors.All(GainsActive);
    int RampKelvin(Look look) => HardwareWarmth ? Night.NeutralKelvin : look.Kelvin;
    internal int ContrastPercent(MonitorInfo m) => Saved(m)?.Contrast ?? Hardware.ToPercent(m.V(0x12)?.Current ?? 0, m.V(0x12)?.Max ?? 0);
    internal int PresetOf(MonitorInfo m) => Saved(m)?.Preset ?? m.V(0x14)?.Current ?? 0;
    internal int[] NeutralGains(MonitorInfo m) => Saved(m)?.Gains is { Length: 3 } g ? g : new[] { m.V(0x16)?.Current ?? 0, m.V(0x18)?.Current ?? 0, m.V(0x1A)?.Current ?? 0 };
    internal bool ShowGains(MonitorInfo m) => m.HasGains && (!m.Caps.Has(0x14) || Hardware.IsUserPreset(PresetOf(m)));
    double EffectiveLevel(Look look) => ArcOn ? Level.Below + ArcHardwarePercent : Level.Dim(SavedLevel, Night.DimScale(look.Dimming, Settings));
    internal string BrightnessShown(Look look) => HardwareBrightness ? Level.Label(EffectiveLevel(look)) : $"{Math.Round(look.Brightness)}%";
    string lastSaved = "";
    internal event Action? Changed;
    string dataDir = "", settingsPath = "", statusPath = "", pipeName = "";
    readonly string exe = Environment.ProcessPath!;
    Mutex? mutex;
    readonly CancellationTokenSource stopping = new();
    FileSystemWatcher? watcher;
    DispatcherTimer? debounce, timer;
    HwndSource? hotkey;
    Forms.NotifyIcon? tray;
    System.Drawing.Icon? trayImage;
    Window? main, flyout;
    Rgb expected = GammaRamp.Compose(null, 100, Night.NeutralKelvin);
    Rgb? lastTarget;
    internal int AppliedKelvin = Night.NeutralKelvin;
    string rampCheck = "identity";
    DateTimeOffset pinDeadline;
    bool quitting;
    DispatcherTimer? saveDebounce;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var home = Value(e.Args, "--home");
        Dry = e.Args.Contains("--dry-gamma"); Isolated = home != null;
        dataDir = Path.GetFullPath(home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShadowBoost"));
        settingsPath = Path.Combine(dataDir, "settings.json");
        statusPath = Path.Combine(home == null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadowBoost") : dataDir, "status.json");
        pipeName = "ShadowBoost-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToUpperInvariant() + Environment.UserName)))[..16];
        mutex = new Mutex(true, "Global\\" + pipeName, out bool first);
        if (!first)
        {
            try { using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out); client.Connect(5000); using var writer = new StreamWriter(client); writer.WriteLine(JsonSerializer.Serialize(e.Args)); }
            catch (Exception ex) { Trace.WriteLine(ex); Environment.ExitCode = 1; }
            Shutdown(); return;
        }
        string[] commands = { "--arc", "--on", "--off", "--level", "--schedule", "--show", "--flyout", "--quit", "--screenshot", "--night", "--warmth", "--brightness", "--brightness-level", "--contrast", "--gains", "--preset" };
        if (e.Args.Any(commands.Contains))
        {
            Native.AttachConsole(uint.MaxValue);
            Console.WriteLine("ShadowBoost is not running");
            mutex.ReleaseMutex(); mutex.Dispose(); Shutdown(3); return;
        }
        Directory.CreateDirectory(dataDir);
        // A tray app that vanishes leaves the display as it was; log a UI-thread failure to errors.log and keep running.
        DispatcherUnhandledException += (_, a) => { try { File.AppendAllText(Path.Combine(dataDir, "errors.log"), $"{DateTimeOffset.Now:O} {a.Exception}\n"); } catch (IOException) { } a.Handled = true; };
        if (!File.Exists(settingsPath))
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShadowBoost.txt");
            Settings = !Isolated && File.Exists(legacy) ? Settings.FromLegacy(File.ReadAllText(legacy)) : new();
            Settings.Save(settingsPath);
        }
        else Settings = Settings.Load(settingsPath);
        lastSaved = File.ReadAllText(settingsPath);
        Engine = new BoostEngine(Settings, TimeZoneInfo.Local);
        if (!Dry) { bus = new MonitorBus(); bus.Changed += () => Dispatcher.BeginInvoke(() => { Changed?.Invoke(); if (!quitting) WriteStatus(); }); RefreshBus(); }
        // Boost is off at every start; warmth, dimming and brightness resume from the saved settings.
        var look = Look; Push(null, look, true); rampCheck = Dry ? "dry" : expected.IsIdentity ? "identity" : "applied";
        ApplyTheme();
        hotkey = new HwndSource(new HwndSourceParameters(pipeName) { Width = 0, Height = 0, WindowStyle = 0 });
        hotkey.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) => { if (msg == 0x312) { if (w == (IntPtr)3) CycleArc(); else if (w == (IntPtr)2) ToggleWarm(); else Toggle(); handled = true; } return IntPtr.Zero; });
        // Isolated checks must not steal the installed app's hotkeys: Ctrl+Shift+D boost, Ctrl+Shift+N warmth.
        // Ctrl+Alt+F10 cycles the ARC look (Auto, Day, Dusk, Night); it stays registered in game because no game binds it.
        if (!Isolated && !Dry) { Native.RegisterHotKey(hotkey.Handle, 1, 0x4006, 0x44); Native.RegisterHotKey(hotkey.Handle, 2, 0x4006, 0x4E); Native.RegisterHotKey(hotkey.Handle, 3, 0x4003, 0x79); }
        tray = new Forms.NotifyIcon { Visible = !Isolated };
        tray.MouseClick += (_, a) => Dispatcher.Invoke(() => { if (a.Button == Forms.MouseButtons.Left) ToggleFlyout(); else if (a.Button == Forms.MouseButtons.Right) Menu(); });
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += (_, _) => { debounce.Stop(); try { var text = File.ReadAllText(settingsPath); if (text != lastSaved) { Settings = Settings.Load(settingsPath); lastSaved = text; Engine.UpdateSettings(Settings); Refresh(true); } } catch (IOException ex) { Trace.WriteLine(ex); debounce.Start(); } };
        watcher = new FileSystemWatcher(dataDir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        FileSystemEventHandler reload = (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        watcher.Changed += reload; watcher.Created += reload; watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => { bool change = Engine.Tick(DateTimeOffset.Now); ExpireHold(); Refresh(change); PinTray(); if (++ticks % 3 == 0) PollMonitors(); };
        timer.Start(); pinDeadline = DateTimeOffset.Now.AddMinutes(2);
        arcTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        arcTimer.Tick += (_, _) => ArcTick(); arcTimer.Start();
        SyncRun();
        SystemEvents.TimeChanged += TimeChanged;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        AppDomain.CurrentDomain.ProcessExit += ProcessExit;
        _ = Task.Run(PipeLoop);
        Refresh(true); Commands(e.Args);
    }
    internal static string? Value(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    async Task PipeLoop()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stopping.Token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(stopping.Token);
                var args = JsonSerializer.Deserialize<string[]>(line ?? "[]") ?? Array.Empty<string>();
                await Dispatcher.InvokeAsync(() => Commands(args));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Trace.WriteLine(ex); }
        }
    }
    void Commands(string[] args)
    {
        if (args.Contains("--quit")) { Quit(); return; }
        if (args.Contains("--on") || (args.Contains("--schedule") && State.Enabled)) Engine.TurnOn(DateTimeOffset.Now);
        if (args.Contains("--off")) Engine.TurnOff(DateTimeOffset.Now);
        if (Enum.TryParse<LevelId>(Value(args, "--level"), true, out var level)) Engine.SetLevel(level, DateTimeOffset.Now);
        if (int.TryParse(Value(args, "--warmth"), out var kelvin)) { Settings.WarmKelvin = kelvin; Settings.Normalize(); Save(); }
        MonitorCommands(args);
        switch (Value(args, "--arc")?.ToLowerInvariant())
        {
            case "on": Settings.Arc.Enabled = true; Save(); break;
            case "off": Settings.Arc.Enabled = false; Save(); break;
            case "auto": PinArc(ArcPin.Auto); break;
            case "day": PinArc(ArcPin.Day); break;
            case "dusk": PinArc(ArcPin.Dusk); break;
            case "night": PinArc(ArcPin.Night); break;
            case "lift-on": Settings.Arc.SoftwareLift = true; Save(); break;
            case "lift-off": Settings.Arc.SoftwareLift = false; Save(); break;
        }
        if (int.TryParse(Value(args, "--brightness"), out var percent)) { if (HardwareBrightness) Settings.BrightnessLevel = Level.Below + Math.Clamp(percent, 0, Level.Span); else Settings.UserBrightness = percent; Settings.Normalize(); Save(); }
        if (double.TryParse(Value(args, "--brightness-level"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var levelValue) && double.IsFinite(levelValue)) { Settings.BrightnessLevel = levelValue; Settings.Normalize(); Save(); }
        switch (Value(args, "--night"))
        {
            case "on": if (!Look.Warmth.On) ToggleWarm(); break;
            case "off": if (Look.Warmth.On) ToggleWarm(); break;
            case "toggle": ToggleWarm(); break;
            case "schedule": Settings.WarmHold = null; Save(); break;
        }
        Refresh(true);
        if (args.Contains("--show")) ShowMain();
        if (args.Contains("--flyout")) ToggleFlyout();
        var shots = Value(args, "--screenshot"); if (shots != null) Screenshots.Save(this, shots);
    }
    internal void Toggle() { if (State.Enabled) Engine.TurnOff(DateTimeOffset.Now); else Engine.TurnOn(DateTimeOffset.Now); Refresh(true); }
    internal void Select(LevelId level) { Engine.SetLevel(level, DateTimeOffset.Now); Refresh(true); }
    internal void ScheduleAgain() { Engine.TurnOn(DateTimeOffset.Now); Refresh(true); }
    internal void Save() { Settings.Save(settingsPath); lastSaved = File.ReadAllText(settingsPath); Engine.UpdateSettings(Settings); SyncRun(); Refresh(true); }
    internal void ToggleWarm() { Settings.WarmHold = Night.Toggle(DateTimeOffset.Now, Settings, TimeZoneInfo.Local); Save(); }
    void ExpireHold() { if (Settings.WarmHold is { } hold && DateTimeOffset.Now >= hold.Until) { Settings.WarmHold = null; Save(); } }
    // Slider drags apply the ramp at once and write settings once the drag settles.
    internal void SetUserBrightness(int value) { Settings.UserBrightness = Math.Clamp(value,50,130); Live(); }
    internal void SetBrightnessLevel(int level) { Settings.BrightnessLevel = Math.Clamp(level, 0, Level.Max); Live(); }
    MonitorSettings Entry(MonitorInfo m)
    {
        if (!Settings.Monitors.TryGetValue(m.Key, out var entry)) Settings.Monitors[m.Key] = entry = new();
        entry.Model = m.Model; return entry;
    }
    internal void SetWarmth(int kelvin) { Settings.WarmKelvin = kelvin; Settings.Normalize(); Live(); }
    internal void SetDim(int percent) { Settings.DimPercent = percent; Settings.Normalize(); Live(); }
    void Live()
    {
        Apply();
        saveDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        saveDebounce.Stop();
        saveDebounce.Tick -= SaveSettled;
        saveDebounce.Tick += SaveSettled;
        saveDebounce.Start();
    }
    void SaveSettled(object? sender, EventArgs e) { saveDebounce!.Stop(); Save(); }
    // Applies the composed ramp and the monitor brightness without rebuilding views, for slider drags.
    void Apply() { Push(CurrentCurve, Look, false); ApplyHardware(); }
    void RefreshBus() => bus?.Refresh(() => Dispatcher.BeginInvoke(() => BusRefreshed()));
    // After every bus refresh: the first time a monitor is seen, adopt its current values as the saved ones (no write, so installing never changes the display); then apply the saved values.
    void BusRefreshed()
    {
        if (quitting) return;
        sentRaw.Clear(); bool adopted = false;
        foreach (var m in Monitors)
        {
            var entry = Entry(m);
            if (m.Works(0x12) && entry.Contrast == null) { entry.Contrast = Hardware.ToPercent(m.V(0x12)!.Current!.Value, m.V(0x12)!.Max); adopted = true; }
            if (m.Works(0x14) && entry.Preset == null) { entry.Preset = m.V(0x14)!.Current!.Value; adopted = true; }
            if (m.HasGains && entry.Gains == null) { entry.Gains = new[] { m.V(0x16)!.Current!.Value, m.V(0x18)!.Current!.Value, m.V(0x1A)!.Current!.Value }; entry.DefaultGains ??= entry.Gains.ToArray(); adopted = true; }
        }
        if (HardwareBrightness && Settings.BrightnessLevel == null) { Settings.BrightnessLevel = SavedLevel; adopted = true; }
        if (adopted) Save(); else Refresh(true);
    }
    // Contrast and brightness compare by percent so a 0..255 monitor's rounding never causes a write; presets and gains compare exactly.
    static bool SameRaw(int a, int b, int max) => a == b || (max > 0 && Hardware.ToPercent(a, max) == Hardware.ToPercent(b, max));
    void Send(MonitorInfo m, byte code, int raw, bool percent)
    {
        int max = m.V(code)?.Max ?? 0, baseline = sentRaw.TryGetValue((m.Key, code), out var last) ? last : m.V(code)?.Current ?? -1;
        if (baseline >= 0 && (percent ? SameRaw(baseline, raw, max) : baseline == raw)) { sentRaw[(m.Key, code)] = baseline; return; }
        sentRaw[(m.Key, code)] = raw; bus!.Set(m.Key, code, raw);
    }
    // Writes the saved values to each monitor whose target changed: brightness, contrast, preset, then gains (the neutral gains with the schedule's warmth on top).
    void ApplyHardware()
    {
        if (bus == null || gainsReset) return;
        var look = Look;
        int percent = HardwareBrightness ? Level.Split(EffectiveLevel(look)).Hardware : -1;
        foreach (var m in Monitors)
        {
            if (percent >= 0) Send(m, 0x10, Hardware.ToRaw(percent, m.Brightness!.Max), true);
            if (Saved(m) is not { } s) continue;
            if ((ArcOn && Settings.Arc.Contrast is int ac ? ac : s.Contrast) is int c && m.Works(0x12)) Send(m, 0x12, Hardware.ToRaw(c, m.V(0x12)!.Max), true);
            if (s.Preset is int p && m.Works(0x14)) Send(m, 0x14, p, false);
            if (!GainsActive(m)) continue;
            var neutral = (s.Gains![0], s.Gains[1], s.Gains[2]);
            var (r, g, b) = HardwareWarmth ? Hardware.WarmGains(neutral, look.Kelvin, new[] { 0x16, 0x18, 0x1A }.Min(code => m.V((byte)code)!.Max)) : neutral;
            Send(m, 0x16, r, false); Send(m, 0x18, g, false); Send(m, 0x1A, b, false);
        }
    }
    // Warmth is the app's effect and the neutral gains are the user's setting, so exit puts the neutral gains back; brightness and contrast stay as set.
    void RestoreNeutralGains()
    {
        if (bus == null || gainsReset) return;
        gainsReset = true;
        try
        {
            foreach (var m in Monitors)
                if (GainsActive(m))
                    for (int channel = 0; channel < 3; channel++)
                    {
                        byte code = (byte)(0x16 + 2 * channel); int neutral = Saved(m)!.Gains![channel];
                        if (sentRaw.TryGetValue((m.Key, code), out var sent) && sent != neutral) bus.Set(m.Key, code, neutral);
                    }
            bus.Drain(2500);
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }
    MonitorInfo? Find(string key) => Monitors.FirstOrDefault(m => m.Key == key);
    internal void SetContrast(string key, int percent) { if (Find(key) is { } m && m.Works(0x12)) { Entry(m).Contrast = Math.Clamp(percent, 0, 100); Live(); } }
    internal void SetPreset(string key, int value) { if (Find(key) is { } m && m.Works(0x14)) { Entry(m).Preset = value; Save(); } }
    internal void SetGain(string key, int channel, int percent)
    {
        if (Find(key) is not { } m || !m.HasGains || Entry(m).Gains is not { Length: 3 } gains) return;
        gains[channel] = Hardware.ToRaw(percent, m.V((byte)(0x16 + 2 * channel))!.Max); Live();
    }
    internal void ResetGains(string key) { if (Find(key) is { } m && Entry(m) is { DefaultGains: { Length: 3 } d } entry) { entry.Gains = d.ToArray(); Save(); } }
    // --contrast <0-100>, --gains <r,g,b> (percent of max) and --preset <hex> apply to every monitor that supports them.
    void MonitorCommands(string[] args)
    {
        bool changed = false;
        if (int.TryParse(Value(args, "--contrast"), out var contrast))
            foreach (var m in Monitors.Where(x => x.Works(0x12))) { Entry(m).Contrast = Math.Clamp(contrast, 0, 100); changed = true; }
        var parts = Value(args, "--gains")?.Split(',');
        if (parts is { Length: 3 } && parts.Select(x => int.TryParse(x, out _)).All(ok => ok))
            foreach (var m in Monitors.Where(x => x.HasGains)) { Entry(m).Gains = parts.Select((x, i) => Hardware.ToRaw(int.Parse(x), m.V((byte)(0x16 + 2 * i))!.Max)).ToArray(); changed = true; }
        if (int.TryParse(Value(args, "--preset"), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var preset))
            foreach (var m in Monitors.Where(x => x.Works(0x14) && x.Caps.Codes[0x14] is { } list && list.Contains(preset))) { Entry(m).Preset = preset; changed = true; }
        if (changed) Save();
    }
    // Every 30 s: retry the bus while any display lacks DDC/CI; otherwise a monitor that no longer holds what we sent was changed with its own buttons.
    void PollMonitors()
    {
        if (bus == null) return;
        if (Monitors.Count == 0 || !HardwareBrightness) RefreshBus();
        foreach (var m in Monitors)
            foreach (byte code in MonitorBus.Driven)
            {
                if (code == 0x10 ? !HardwareBrightness : !m.Works(code)) continue;
                string key = m.Key; int? sent = sentRaw.TryGetValue((key, code), out var s) ? s : null;
                bus.Read(key, code, v => Dispatcher.BeginInvoke(() => Adopt(key, code, sent, v)));
            }
    }
    void Adopt(string key, byte code, int? sentAtRead, VcpValue v)
    {
        // In game the monitor holds the ARC values, which are not the user's desktop settings: never adopt them.
        if (bus == null || quitting || ArcOn || v.Error != null || v.Current is not int now || Find(key) is not { } m) return;
        if (code == 0x10 ? !HardwareBrightness || v.Max <= 0 : !m.Works(code)) return;
        bool percent = code is 0x10 or 0x12;
        if (!sentRaw.TryGetValue((key, code), out var sent) || sentAtRead != sent || bus.Busy(key) || (percent ? SameRaw(sent, now, v.Max) : sent == now)) return;
        var look = Look; var entry = Entry(m);
        switch (code)
        {
            case 0x10:
                double scale = Night.DimScale(look.Dimming, Settings), ramp = Level.Split(Level.Dim(SavedLevel, scale)).Ramp;
                Settings.BrightnessLevel = Math.Clamp(Level.Join(Hardware.ToPercent(now, v.Max), ramp) / (scale > 0 ? scale : 1), 0, Level.Max); break;
            case 0x12: entry.Contrast = Hardware.ToPercent(now, v.Max); break;
            case 0x14: entry.Preset = now; break;
            default:
                // Gains read back carry the schedule's warmth; undo it to get the neutral gain the user set.
                int channel = (code - 0x16) / 2; var (wr, wg, wb) = HardwareWarmth ? GammaRamp.WhitePoint(look.Kelvin) : (1.0, 1.0, 1.0);
                double white = channel == 0 ? wr : channel == 1 ? wg : wb;
                if (entry.Gains is { Length: 3 } gains) gains[channel] = (int)Math.Round(now / (white > 0 ? white : 1)); break;
        }
        sentRaw[(key, code)] = now; Save();
    }
    // Sets the ramp for this look. When Windows refuses it, keep brightness and boost and search for the
    // warmest white point it accepts, so a refused warm ramp never leaves the display at identity.
    void Push(Curve? curve, Look look, bool force)
    {
        int warm = RampKelvin(look);
        var target = GammaRamp.Compose(curve, look.Brightness, warm);
        if (!force && lastTarget != null && lastTarget.SameAs(target)) return;
        lastTarget = target;
        if (Native.Apply(target, Dry)) { expected = target; AppliedKelvin = warm; return; }
        int Kelvin(double strength) => (int)Math.Round(Night.NeutralKelvin - strength * (Night.NeutralKelvin - warm));
        var neutral = GammaRamp.Compose(curve, look.Brightness, Night.NeutralKelvin);
        double low = 0, high = 1;
        if (!Native.Apply(neutral, Dry)) { Trace.WriteLine("gamma ramp refused even without warmth"); return; }
        expected = neutral; AppliedKelvin = Night.NeutralKelvin;
        for (int i = 0; i < 7; i++)
        {
            double mid = (low + high) / 2; var ramp = GammaRamp.Compose(curve, look.Brightness, Kelvin(mid));
            if (Native.Apply(ramp, Dry)) { low = mid; expected = ramp; AppliedKelvin = Kelvin(mid); } else high = mid;
        }
        if (!Native.Apply(expected, Dry)) Trace.WriteLine("accepted ramp refused on reapply");
    }
    void Refresh(bool apply)
    {
        ApplyHardware();
        var state = State; var look = Look;
        // A game or driver may reset ramps; reapply whenever the readback drifts from what we set.
        bool mismatch = !Dry && !Native.Matches(expected);
        var curve = CurrentCurve;
        var target = GammaRamp.Compose(curve, look.Brightness, RampKelvin(look));
        Push(curve, look, mismatch || (apply && !target.IsIdentity));
        rampCheck = Dry ? "dry" : !Native.Matches(expected) ? "mismatch" : expected.IsIdentity ? "identity" : AppliedKelvin != RampKelvin(look) ? "limited" : "applied";
        UpdateTray(); Changed?.Invoke(); WriteStatus();
    }
    static int?[] GainValues(MonitorInfo m, Func<VcpValue?, int?> pick) => new byte[] { 0x16, 0x18, 0x1A }.Select(code => pick(m.V(code))).ToArray();
    void WriteStatus()
    {
        var state = State;
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        string tmp = statusPath + ".tmp";
        var look = Look;
        var json = JsonSerializer.Serialize(new { pid = Environment.ProcessId, exePath = exe, dataDir, updatedAt = DateTimeOffset.Now, dryGamma = Dry, state.Enabled, state.Period, state.ScheduledLevel, state.ActiveLevel, state.IsOverride, state.NextChange, state.AppliedCurve, userBrightness = Settings.UserBrightness,
            warm = new { on = look.Warmth.On, factor = Math.Round(look.Warmth.Factor, 3), kelvin = look.Kelvin, appliedKelvin = AppliedKelvin, target = Settings.WarmKelvin, held = look.Warmth.Held, nextChange = look.Warmth.NextChange, schedule = Settings.WarmSchedule },
            dim = new { on = look.Dimming.On, factor = Math.Round(look.Dimming.Factor, 3), percent = Settings.DimPercent, schedule = Settings.DimSchedule, nextChange = look.Dimming.NextChange },
            brightness = Math.Round(look.Brightness, 2), brightnessSource = HardwareBrightness ? "monitor" : "gamma", brightnessLevel = Settings.BrightnessLevel, effectiveLevel = HardwareBrightness ? EffectiveLevel(look) : (double?)null, rampBrightness = Math.Round(look.Brightness, 2),
            monitors = Monitors.Select(m => new { m.Key, m.Description, m.Model, m.Mccs, codes = string.Join(" ", m.Caps.Codes.Keys.Order().Select(c => c.ToString("X2"))),
                brightness = new { target = m.Brightness?.Target, current = m.Brightness?.Current, max = m.Brightness?.Max, readback = m.Brightness?.Readback, error = m.Brightness?.Error },
                contrast = new { percent = Saved(m)?.Contrast, target = m.V(0x12)?.Target, current = m.V(0x12)?.Current, max = m.V(0x12)?.Max, readback = m.V(0x12)?.Readback, error = m.V(0x12)?.Error },
                preset = m.Caps.Has(0x14) ? new { value = (int?)PresetOf(m), name = Hardware.PresetName(PresetOf(m)) } : null,
                gains = m.Caps.Has(0x16) ? new { neutral = Saved(m)?.Gains, target = GainValues(m, v => v?.Target), current = GainValues(m, v => v?.Current), readback = GainValues(m, v => v?.Readback) } : null,
                warmth = HardwareWarmth ? "monitor" : "gamma", warmthOnMonitor = GainsActive(m) }).ToList(),
            expectedMid = new[] { expected.Red[128], expected.Green[128], expected.Blue[128] },
            rampMid = Dry ? null : Native.RampMid(), rampCheck,
            arc = new { enabled = Settings.Arc.Enabled, active = ArcOn, process = arcProcess, since = ArcOn ? arcSince : (DateTimeOffset?)null, pin = Settings.Arc.Pin, sceneAdaptive = Settings.Arc.SceneAdaptive,
                luma = double.IsFinite(arcLuma) ? Math.Round(arcLuma, 1) : (double?)null, samples = arcSamples, sampleError = arcError, darkness = Math.Round(ArcDarkness(DateTimeOffset.Now), 2), target = arcScene.Target,
                softwareLift = Settings.Arc.SoftwareLift, curve = ArcOn ? ArcCurve : null, monitorBrightness = ArcOn ? ArcHardwarePercent : (int?)null }, runKey = RunValue(), trayPinned = PinValue() }, Json);
        // A reader holding status.json open (a check, an editor, antivirus) makes the replace fail; retry briefly, never crash.
        for (int attempt = 0; ; attempt++)
        {
            try { File.WriteAllText(tmp, json); File.Move(tmp, statusPath, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (attempt >= 4) { Trace.WriteLine(ex); return; } Thread.Sleep(40); }
        }
    }
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string PinPath = @"Control Panel\NotifyIconSettings";
    string? RunValue() { if (Isolated || Dry) return null; using var key = Registry.CurrentUser.OpenSubKey(RunPath); return key?.GetValue("ShadowBoost") as string; }
    int? PinValue() { if (Isolated || Dry) return null; using var parent = Registry.CurrentUser.OpenSubKey(PinPath); if (parent == null) return null; foreach (var name in parent.GetSubKeyNames()) { using var key = parent.OpenSubKey(name); if (string.Equals(key?.GetValue("ExecutablePath") as string, exe, StringComparison.OrdinalIgnoreCase)) return key?.GetValue("IsPromoted") as int?; } return null; }
    void SyncRun() { if (Isolated || Dry) return; using var key = Registry.CurrentUser.CreateSubKey(RunPath); if (Settings.StartAtSignIn) key.SetValue("ShadowBoost", $"\"{exe}\" --tray"); else key.DeleteValue("ShadowBoost", false); }
    void PinTray()
    {
        if (Isolated || Dry || Settings.TrayPinApplied || DateTimeOffset.Now > pinDeadline) return;
        using var parent = Registry.CurrentUser.OpenSubKey(PinPath);
        if (parent == null) return;
        foreach (var name in parent.GetSubKeyNames())
        {
            using var key = parent.OpenSubKey(name, true);
            if (!string.Equals(key?.GetValue("ExecutablePath") as string, exe, StringComparison.OrdinalIgnoreCase)) continue;
            key!.SetValue("IsPromoted", 1, RegistryValueKind.DWord); Settings.TrayPinApplied = true; Save(); break;
        }
    }
    internal void ApplyTheme(bool? dark = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        Dark = dark ?? (key?.GetValue("AppsUseLightTheme") is int value && value == 0);
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{(Dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (main != null) Native.Decorate(main, Dark); if (flyout != null) Native.Decorate(flyout, Dark);
    }
    void UpdateTray()
    {
        if (tray == null) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        int size = (int)Math.Round(16 * (hotkey == null ? 1 : Math.Max(96, Native.GetDpiForWindow(hotkey.Handle)) / 96.0));
        var s = State; var look = Look;
        var icon = Glyph.Icon(s.Enabled || look.Warmth.On, !light, size); tray.Icon = icon; trayImage?.Dispose(); trayImage = icon;
        string warm = look.Warmth.On ? $"Warm {look.Kelvin} K" : "Daylight";
        string boost = s.Enabled ? $", boost {s.ActiveLevel}" : "";
        // NotifyIcon text is limited to 127 characters.
        var text = ArcOn ? $"ShadowBoost: ARC {ArcLabel}, brightness {BrightnessShown(look)}" : $"ShadowBoost: {warm}{boost}, brightness {BrightnessShown(look)}"; tray.Text = text.Length > 127 ? text[..127] : text;
    }
    void Menu()
    {
        var menu = new ContextMenu { Width = 220, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Item(string text, Action action, bool selected = false) { var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(UI.Text(selected ? "•" : "", 13, "Accent", width: 18)); row.Children.Add(UI.Text(text, 13)); var item = new MenuItem { Header = row }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        var warm = Look.Warmth;
        Item("Open ShadowBoost", ShowMain); menu.Items.Add(new Separator());
        Item(warm.On ? $"Warm until {UI.Clock(warm.NextChange)}" : "Warm now", ToggleWarm, warm.On);
        Item("Shadow boost", Toggle, State.Enabled); menu.Items.Add(new Separator());
        Item(ArcOn ? $"ARC profile: {ArcLabel}" : "ARC profile", () => { Settings.Arc.Enabled = !Settings.Arc.Enabled; Save(); }, Settings.Arc.Enabled);
        foreach (var pin in Enum.GetValues<ArcPin>()) { var p = pin; Item(p == ArcPin.Auto ? (Settings.Arc.SoftwareLift ? "ARC look: Auto (follows the scene)" : "ARC look: Auto (room light)") : $"ARC look: {p}", () => PinArc(p), Settings.Arc.Pin == p); }
        Item("Software lift (not used in ARC)", () => { Settings.Arc.SoftwareLift = !Settings.Arc.SoftwareLift; Save(); }, Settings.Arc.SoftwareLift);
        menu.Items.Add(new Separator());
        Item("Quit ShadowBoost", Quit); menu.IsOpen = true;
    }
    internal string ArcLabel => Settings.Arc.Pin != ArcPin.Auto ? $"{Settings.Arc.Pin} (pinned)" : Settings.Arc.SoftwareLift ? $"Auto {ArcDarkness(DateTimeOffset.Now):0.0}" : "Auto (room light)";
    internal void PinArc(ArcPin pin) { Settings.Arc.Pin = pin; Save(); }
    void CycleArc() => PinArc((ArcPin)(((int)Settings.Arc.Pin + 1) % 4));
    // Every 250 ms: is the game in front? Enter when it is, leave 3 s after it is not; sample the scene every SampleSeconds and fade the ramp.
    void ArcTick()
    {
        if (quitting) return;
        var now = DateTimeOffset.Now;
        var front = Settings.Arc.Enabled ? ArcWatch.Foreground(Settings.Arc.Processes) : null;
        if (front is { } f) { arcLost = DateTimeOffset.MaxValue; arcWindow = f.Window; arcProcess = f.Process; if (!ArcOn) EnterArc(now); }
        else if (ArcOn)
        {
            if (arcLost == DateTimeOffset.MaxValue) arcLost = now;
            if (!Settings.Arc.Enabled || now - arcLost >= TimeSpan.FromSeconds(3)) { LeaveArc(); return; }
        }
        if (!ArcOn) return;
        // The screen is read only with the software lift on; the hardware-only profile never opens desktop duplication.
        if (front != null && Settings.Arc.SoftwareLift && Settings.Arc.Pin == ArcPin.Auto && Settings.Arc.SceneAdaptive && !arcSampling && now >= arcNextSample)
        {
            arcSampling = true; arcNextSample = now.AddSeconds(Settings.Arc.SampleSeconds); var window = arcWindow;
            Task.Run(() => ArcWatch.Sample(window)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                arcSampling = false;
                var (luma, error) = t.IsCompletedSuccessfully ? t.Result : (double.NaN, t.Exception?.GetBaseException().Message);
                arcLuma = luma; arcError = error;
                if (double.IsFinite(luma)) { arcSamples++; arcScene.Add(DateTimeOffset.Now, Settings.Arc.Darkness(luma)); }
                if (ArcOn) { UpdateTray(); WriteStatus(); }
            }));
        }
        Push(ArcCurve, Look, false);
        ApplyHardware();
    }
    void EnterArc(DateTimeOffset now)
    {
        ArcOn = true; arcSince = now; arcSamples = 0; arcLuma = double.NaN; arcError = null; arcNextSample = now;
        arcScene = new SceneTracker(ArcSettings.PinDarkness(Settings.Arc.Pin) ?? 0.5);
        // Ctrl+Shift+D and Ctrl+Shift+N would swallow D and N while crouch-sprinting; release them in game.
        if (hotkey != null && !Isolated && !Dry) { Native.UnregisterHotKey(hotkey.Handle, 1); Native.UnregisterHotKey(hotkey.Handle, 2); }
        Refresh(true);
    }
    void LeaveArc()
    {
        ArcOn = false; arcLost = DateTimeOffset.MaxValue; ArcWatch.Reset();
        if (hotkey != null && !Isolated && !Dry) { Native.RegisterHotKey(hotkey.Handle, 1, 0x4006, 0x44); Native.RegisterHotKey(hotkey.Handle, 2, 0x4006, 0x4E); }
        Refresh(true);
    }
    internal void ShowMain()
    {
        if (main == null)
        {
            main = new Window { Title = "ShadowBoost", Width = 500, Height = 780, ResizeMode = ResizeMode.CanMinimize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new MainView(this) };
            main.SetResourceReference(Window.BackgroundProperty, "Bg"); main.SourceInitialized += (_, _) => Native.Decorate(main, Dark);
            main.Closing += (_, e) => { if (!quitting) { e.Cancel = true; main.Hide(); } };
        }
        main.Content = new MainView(this); main.Show(); main.Activate();
    }
    // Reads the position Windows already knows (location services or the default location in Settings); no web lookup.
    internal async Task<string> LocatePc()
    {
        try
        {
            if (await global::Windows.Devices.Geolocation.Geolocator.RequestAccessAsync() != global::Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
                return "Windows location is off for desktop apps. Type the coordinates instead.";
            var position = await new global::Windows.Devices.Geolocation.Geolocator().GetGeopositionAsync(TimeSpan.FromHours(12), TimeSpan.FromSeconds(10));
            var point = position.Coordinate.Point.Position;
            Settings.Latitude = Math.Round(point.Latitude, 4); Settings.Longitude = Math.Round(point.Longitude, 4); Settings.PlaceName = "This PC"; Save();
            return "Updated from this PC's location.";
        }
        catch (Exception ex) { Trace.WriteLine(ex); return "Windows did not return a location. Type the coordinates instead."; }
    }
    internal void FineTune() { if (main != null) main.Content = new FineTuneView(this); }
    internal void BoostPage() { if (main != null) main.Content = new BoostView(this); }
    internal void ShowMainPage() { if (main != null) main.Content = new MainView(this); }
    void ToggleFlyout()
    {
        if (flyout?.IsVisible == true) { flyout.Hide(); return; }
        if (flyout == null)
        {
            flyout = new Window { Width = 340, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Content = new FlyoutView(this) };
            flyout.SetResourceReference(Window.BackgroundProperty, "Surface"); flyout.Deactivated += (_, _) => flyout.Hide(); flyout.SourceInitialized += (_, _) => Native.Decorate(flyout, Dark);
        }
        flyout.Content = new FlyoutView(this); flyout.Show();
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position); var work = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(flyout); flyout.Left = work.Right / dpi.DpiScaleX - 340 - 12; flyout.Top = work.Bottom / dpi.DpiScaleY - flyout.ActualHeight - 12; flyout.Activate();
    }
    void TimeChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(() => { Engine.Tick(DateTimeOffset.Now); Refresh(true); });
    void DisplayChanged(object? s, EventArgs e) { RefreshBus(); TimeChanged(s, e); }
    // Monitors can take a few seconds to answer DDC/CI after waking.
    void PowerChanged(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) { _ = Task.Delay(3000).ContinueWith(_ => RefreshBus()); TimeChanged(s, e); } }
    void ThemeChanged(object s, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { ApplyTheme(); Refresh(false); });
    static Rgb Neutral => GammaRamp.Compose(null, 100, Night.NeutralKelvin);
    void OnSessionEnding(object s, SessionEndingEventArgs e) { RestoreNeutralGains(); Native.Apply(Neutral, Dry); }
    void ProcessExit(object? s, EventArgs e) { RestoreNeutralGains(); Native.Apply(Neutral, Dry); }
    internal void Quit()
    {
        if (saveDebounce?.IsEnabled == true) { saveDebounce.Stop(); Save(); }
        quitting = true; timer?.Stop(); stopping.Cancel(); watcher?.Dispose(); debounce?.Stop();
        RestoreNeutralGains(); Native.Apply(Neutral, Dry);
        Engine.TurnOff(DateTimeOffset.Now); rampCheck = Dry ? "dry" : "identity"; WriteStatus();
        arcTimer?.Stop();
        if (hotkey != null) { if (!Isolated && !Dry) { Native.UnregisterHotKey(hotkey.Handle, 1); Native.UnregisterHotKey(hotkey.Handle, 2); Native.UnregisterHotKey(hotkey.Handle, 3); } hotkey.Dispose(); }
        tray?.Dispose(); trayImage?.Dispose(); bus?.Dispose();
        SystemEvents.TimeChanged -= TimeChanged; SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.UserPreferenceChanged -= ThemeChanged; SystemEvents.SessionEnding -= OnSessionEnding;
        mutex?.ReleaseMutex(); mutex?.Dispose(); Shutdown();
    }
}
