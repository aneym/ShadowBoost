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
    ushort[] expected = GammaRamp.Identity();
    string rampCheck = "identity";
    DateTimeOffset pinDeadline;
    bool quitting;
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
        mutex = new Mutex(true, "Local\\" + pipeName, out bool first);
        if (!first)
        {
            try { using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out); client.Connect(5000); using var writer = new StreamWriter(client); writer.WriteLine(JsonSerializer.Serialize(e.Args)); }
            catch (Exception ex) { Trace.WriteLine(ex); Environment.ExitCode = 1; }
            Shutdown(); return;
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
        Native.Apply(expected, Dry); rampCheck = Dry ? "dry" : "identity";
        ApplyTheme();
        hotkey = new HwndSource(new HwndSourceParameters(pipeName) { Width = 0, Height = 0, WindowStyle = 0 });
        hotkey.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) => { if (msg == 0x312) { Toggle(); handled = true; } return IntPtr.Zero; });
        // Isolated checks must not steal the installed app's hotkey.
        if (!Isolated && !Dry) Native.RegisterHotKey(hotkey.Handle, 1, 0x4006, 0x44);
        tray = new Forms.NotifyIcon { Visible = true };
        tray.MouseClick += (_, a) => Dispatcher.Invoke(() => { if (a.Button == Forms.MouseButtons.Left) ToggleFlyout(); else if (a.Button == Forms.MouseButtons.Right) Menu(); });
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += (_, _) => { debounce.Stop(); try { var text = File.ReadAllText(settingsPath); if (text != lastSaved) { Settings = Settings.Load(settingsPath); lastSaved = text; Engine.UpdateSettings(Settings); Refresh(true); } } catch (IOException ex) { Trace.WriteLine(ex); debounce.Start(); } };
        watcher = new FileSystemWatcher(dataDir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        FileSystemEventHandler reload = (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        watcher.Changed += reload; watcher.Created += reload; watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => { bool change = Engine.Tick(DateTimeOffset.Now); Refresh(change); PinTray(); };
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
        Refresh(true);
        if (args.Contains("--show")) ShowMain();
        if (args.Contains("--flyout")) ToggleFlyout();
        var shots = Value(args, "--screenshot"); if (shots != null) Screenshots.Save(this, shots);
    }
    internal void Toggle() { if (State.Enabled) Engine.TurnOff(DateTimeOffset.Now); else Engine.TurnOn(DateTimeOffset.Now); Refresh(true); }
    internal void Select(LevelId level) { Engine.SetLevel(level, DateTimeOffset.Now); Refresh(true); }
    internal void ScheduleAgain() { Engine.TurnOn(DateTimeOffset.Now); Refresh(true); }
    internal void Save() { Settings.Save(settingsPath); lastSaved = File.ReadAllText(settingsPath); Engine.UpdateSettings(Settings); SyncRun(); Refresh(true); }
    internal void SetBrightness(int value) { if (!Dry) Native.Brightness(value); Settings.MonitorBrightness = value; Save(); }
    void Refresh(bool apply)
    {
        var state = State;
        var ramp = state.AppliedCurve == null ? GammaRamp.Identity() : GammaRamp.Build(state.AppliedCurve);
        bool mismatch = !Dry && !Native.Matches(expected);
        if (apply || mismatch || !expected.SequenceEqual(ramp)) Native.Apply(ramp, Dry);
        expected = ramp;
        rampCheck = Dry ? "dry" : (!Native.Matches(expected) ? "mismatch" : state.Enabled ? "boost" : "identity");
        UpdateTray(); Changed?.Invoke(); WriteStatus();
    }
    void WriteStatus()
    {
        var state = State;
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        string tmp = statusPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { pid = Environment.ProcessId, exePath = exe, dataDir, updatedAt = DateTimeOffset.Now, dryGamma = Dry, state.Enabled, state.Period, state.ScheduledLevel, state.ActiveLevel, state.IsOverride, state.NextChange, state.AppliedCurve, rampCheck, runKey = RunValue(), trayPinned = PinValue() }, Json));
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
        var icon = Glyph.Icon(State.Enabled, !light, size); tray.Icon = icon; trayImage?.Dispose(); trayImage = icon;
        var s = State;
        tray.Text = !s.Enabled ? "ShadowBoost: Off" : s.IsOverride ? $"ShadowBoost: {s.ActiveLevel} (held until {s.NextChange.LocalDateTime:h:mm tt})" : $"ShadowBoost: {s.ActiveLevel} ({s.Period})";
    }
    void Menu()
    {
        var menu = new ContextMenu { Width = 220, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Item(string text, Action action, bool selected = false) { var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(UI.Text(selected ? "•" : "", 13, "Accent", width: 18)); row.Children.Add(UI.Text(text, 13)); var item = new MenuItem { Header = row }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item(State.Enabled ? "Turn off" : "Turn on", Toggle); menu.Items.Add(new Separator());
        foreach (var level in Enum.GetValues<LevelId>()) Item(level.ToString(), () => Select(level), State.Enabled && State.ActiveLevel == level);
        menu.Items.Add(new Separator()); Item("Schedule and settings", ShowMain); Item("Quit ShadowBoost", Quit); menu.IsOpen = true;
    }
    internal void ShowMain()
    {
        if (main == null)
        {
            main = new Window { Title = "ShadowBoost", Width = 460, Height = 690, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new MainView(this) };
            main.SetResourceReference(Window.BackgroundProperty, "Bg"); main.SourceInitialized += (_, _) => Native.Decorate(main, Dark);
            main.Closing += (_, e) => { if (!quitting) { e.Cancel = true; main.Hide(); } };
        }
        main.Content = new MainView(this); main.Show(); main.Activate();
    }
    internal void FineTune() { if (main != null) main.Content = new FineTuneView(this); }
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
    void OnSessionEnding(object s, SessionEndingEventArgs e) => Native.Apply(GammaRamp.Identity(), Dry);
    void ProcessExit(object? s, EventArgs e) => Native.Apply(GammaRamp.Identity(), Dry);
    internal void Quit()
    {
        quitting = true; timer?.Stop(); stopping.Cancel(); watcher?.Dispose(); debounce?.Stop();
        Native.Apply(GammaRamp.Identity(), Dry);
        Engine.TurnOff(DateTimeOffset.Now); rampCheck = Dry ? "dry" : "identity"; WriteStatus();
        if (hotkey != null) { if (!Isolated && !Dry) Native.UnregisterHotKey(hotkey.Handle, 1); hotkey.Dispose(); }
        tray?.Dispose(); trayImage?.Dispose();
        SystemEvents.TimeChanged -= TimeChanged; SystemEvents.DisplaySettingsChanged -= TimeChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.UserPreferenceChanged -= ThemeChanged; SystemEvents.SessionEnding -= OnSessionEnding;
        mutex?.ReleaseMutex(); mutex?.Dispose(); Shutdown();
    }
}
