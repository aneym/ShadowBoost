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
    internal Look Look => Night.Look(DateTimeOffset.Now, Settings, TimeZoneInfo.Local);
    internal bool Dry, Isolated, Dark;
    readonly Lazy<int?> brightness = new(() => Native.Brightness());
    internal int? MonitorBrightness => brightness.Value;
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
        string[] commands = { "--on", "--off", "--level", "--schedule", "--show", "--flyout", "--quit", "--screenshot", "--night", "--warmth", "--brightness" };
        if (e.Args.Any(commands.Contains))
        {
            Native.AttachConsole(uint.MaxValue);
            Console.WriteLine("ShadowBoost is not running");
            mutex.ReleaseMutex(); mutex.Dispose(); Shutdown(3); return;
        }
        Directory.CreateDirectory(dataDir);
        if (!File.Exists(settingsPath))
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShadowBoost.txt");
            Settings = !Isolated && File.Exists(legacy) ? Settings.FromLegacy(File.ReadAllText(legacy)) : new();
            Settings.Save(settingsPath);
        }
        else Settings = Settings.Load(settingsPath);
        lastSaved = File.ReadAllText(settingsPath);
        Engine = new BoostEngine(Settings, TimeZoneInfo.Local);
        // Boost is off at every start; warmth, dimming and brightness resume from the saved settings.
        var look = Look; expected = GammaRamp.Compose(null, look.Brightness, look.Kelvin);
        Native.Apply(expected, Dry); rampCheck = Dry ? "dry" : expected.IsIdentity ? "identity" : "applied";
        ApplyTheme();
        hotkey = new HwndSource(new HwndSourceParameters(pipeName) { Width = 0, Height = 0, WindowStyle = 0 });
        hotkey.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) => { if (msg == 0x312) { if (w == (IntPtr)2) ToggleWarm(); else Toggle(); handled = true; } return IntPtr.Zero; });
        // Isolated checks must not steal the installed app's hotkeys: Ctrl+Shift+D boost, Ctrl+Shift+N warmth.
        if (!Isolated && !Dry) { Native.RegisterHotKey(hotkey.Handle, 1, 0x4006, 0x44); Native.RegisterHotKey(hotkey.Handle, 2, 0x4006, 0x4E); }
        tray = new Forms.NotifyIcon { Visible = !Isolated };
        tray.MouseClick += (_, a) => Dispatcher.Invoke(() => { if (a.Button == Forms.MouseButtons.Left) ToggleFlyout(); else if (a.Button == Forms.MouseButtons.Right) Menu(); });
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += (_, _) => { debounce.Stop(); try { var text = File.ReadAllText(settingsPath); if (text != lastSaved) { Settings = Settings.Load(settingsPath); lastSaved = text; Engine.UpdateSettings(Settings); Refresh(true); } } catch (IOException ex) { Trace.WriteLine(ex); debounce.Start(); } };
        watcher = new FileSystemWatcher(dataDir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        FileSystemEventHandler reload = (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        watcher.Changed += reload; watcher.Created += reload; watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => { bool change = Engine.Tick(DateTimeOffset.Now); ExpireHold(); Refresh(change); PinTray(); };
        timer.Start(); pinDeadline = DateTimeOffset.Now.AddMinutes(2);
        SyncRun();
        SystemEvents.TimeChanged += TimeChanged;
        SystemEvents.DisplaySettingsChanged += TimeChanged;
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
        if (int.TryParse(Value(args, "--brightness"), out var percent)) { Settings.UserBrightness = percent; Settings.Normalize(); Save(); }
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
    internal void SetBrightness(int value) { if (!Dry) Native.Brightness(value); Settings.MonitorBrightness = value; Save(); }
    // Applies the composed ramp without rebuilding views, for slider drags.
    void Apply()
    {
        var look = Look;
        var ramp = GammaRamp.Compose(State.AppliedCurve, look.Brightness, look.Kelvin);
        if (!expected.SameAs(ramp)) Native.Apply(ramp, Dry);
        expected = ramp;
    }
    void Refresh(bool apply)
    {
        var state = State; var look = Look;
        var ramp = GammaRamp.Compose(state.AppliedCurve, look.Brightness, look.Kelvin);
        // A game or driver may reset ramps; reapply whenever the readback drifts from what we set.
        bool mismatch = !Dry && !Native.Matches(expected);
        if (!expected.SameAs(ramp) || (!ramp.IsIdentity && (apply || mismatch))) Native.Apply(ramp, Dry);
        expected = ramp;
        rampCheck = Dry ? "dry" : !Native.Matches(expected) ? "mismatch" : ramp.IsIdentity ? "identity" : "applied";
        UpdateTray(); Changed?.Invoke(); WriteStatus();
    }
    void WriteStatus()
    {
        var state = State;
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        string tmp = statusPath + ".tmp";
        var look = Look;
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { pid = Environment.ProcessId, exePath = exe, dataDir, updatedAt = DateTimeOffset.Now, dryGamma = Dry, state.Enabled, state.Period, state.ScheduledLevel, state.ActiveLevel, state.IsOverride, state.NextChange, state.AppliedCurve, userBrightness = Settings.UserBrightness,
            warm = new { on = look.Warmth.On, factor = Math.Round(look.Warmth.Factor, 3), kelvin = look.Kelvin, target = Settings.WarmKelvin, held = look.Warmth.Held, nextChange = look.Warmth.NextChange, schedule = Settings.WarmSchedule },
            dim = new { on = look.Dimming.On, factor = Math.Round(look.Dimming.Factor, 3), percent = Settings.DimPercent, schedule = Settings.DimSchedule, nextChange = look.Dimming.NextChange },
            brightness = Math.Round(look.Brightness, 2), expectedMid = new[] { expected.Red[128], expected.Green[128], expected.Blue[128] },
            rampMid = Dry ? null : Native.RampMid(), rampCheck, runKey = RunValue(), trayPinned = PinValue() }, Json));
        File.Move(tmp, statusPath, true);
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
        var text = $"ShadowBoost: {warm}{boost}, brightness {Math.Round(look.Brightness)}%"; tray.Text = text.Length > 127 ? text[..127] : text;
    }
    void Menu()
    {
        var menu = new ContextMenu { Width = 220, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Item(string text, Action action, bool selected = false) { var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(UI.Text(selected ? "•" : "", 13, "Accent", width: 18)); row.Children.Add(UI.Text(text, 13)); var item = new MenuItem { Header = row }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        var warm = Look.Warmth;
        Item("Open ShadowBoost", ShowMain); menu.Items.Add(new Separator());
        Item(warm.On ? $"Warm until {UI.Clock(warm.NextChange)}" : "Warm now", ToggleWarm, warm.On);
        Item("Shadow boost", Toggle, State.Enabled); menu.Items.Add(new Separator());
        Item("Quit ShadowBoost", Quit); menu.IsOpen = true;
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
    void PowerChanged(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) TimeChanged(s, e); }
    void ThemeChanged(object s, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { ApplyTheme(); Refresh(false); });
    static Rgb Neutral => GammaRamp.Compose(null, 100, Night.NeutralKelvin);
    void OnSessionEnding(object s, SessionEndingEventArgs e) => Native.Apply(Neutral, Dry);
    void ProcessExit(object? s, EventArgs e) => Native.Apply(Neutral, Dry);
    internal void Quit()
    {
        if (saveDebounce?.IsEnabled == true) { saveDebounce.Stop(); Save(); }
        quitting = true; timer?.Stop(); stopping.Cancel(); watcher?.Dispose(); debounce?.Stop();
        Native.Apply(Neutral, Dry);
        Engine.TurnOff(DateTimeOffset.Now); rampCheck = Dry ? "dry" : "identity"; WriteStatus();
        if (hotkey != null) { if (!Isolated && !Dry) { Native.UnregisterHotKey(hotkey.Handle, 1); Native.UnregisterHotKey(hotkey.Handle, 2); } hotkey.Dispose(); }
        tray?.Dispose(); trayImage?.Dispose();
        SystemEvents.TimeChanged -= TimeChanged; SystemEvents.DisplaySettingsChanged -= TimeChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.UserPreferenceChanged -= ThemeChanged; SystemEvents.SessionEnding -= OnSessionEnding;
        mutex?.ReleaseMutex(); mutex?.Dispose(); Shutdown();
    }
}
