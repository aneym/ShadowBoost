using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace ShadowBoost.App;

internal sealed record Snapshot(EngineState Boost, Look Look);

internal static class UI
{
    internal static TextBlock Text(string text, double size = 14, string brush = "Text", FontWeight? weight = null, double width = double.NaN)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Width = width };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush); return t;
    }
    internal static Button Link(string text, Action click) { var b = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left }; b.Click += (_, _) => click(); return b; }
    internal static CheckBox Switch(bool value, Action click, string? id = null) { var b = new CheckBox { IsChecked = value, Style = (Style)Application.Current.FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center }; if (id != null) AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => click(); return b; }
    internal static Border Choice<T>(IReadOnlyList<T> values, T? selected, Func<T, string> label, Action<T> click, double width = double.NaN, bool small = false, string? ids = null) where T : struct
    {
        var grid = new Grid(); var group = Guid.NewGuid().ToString();
        for (int i = 0; i < values.Count; i++)
        {
            var value = values[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var b = new RadioButton { Content = label(value), IsChecked = selected.HasValue && selected.Value.Equals(value), GroupName = group, FontSize = small ? 12 : 13, Padding = new Thickness(0, small ? 4 : 6, 0, small ? 4 : 6) };
            Grid.SetColumn(b, i); if (ids != null) AutomationProperties.SetAutomationId(b, ids + label(value).Replace(" ", ""));
            b.Click += (_, _) => click(value); grid.Children.Add(b);
        }
        var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(3), Child = grid, Width = width };
        border.SetResourceReference(Border.BackgroundProperty, "SegBg"); border.SetResourceReference(Border.BorderBrushProperty, "Stroke"); return border;
    }
    internal static Border Segments(LevelId? selected, Action<LevelId> click, bool small = false, bool ids = false) =>
        Choice(Enum.GetValues<LevelId>(), selected, level => level.ToString(), click, small ? 240 : double.NaN, small, ids ? "Level" : null);
    internal static Grid Row(UIElement left, UIElement? right, bool first = false)
    {
        var grid = new Grid { Margin = new Thickness(0, first ? 0 : 1, 0, 0) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var border = new Border { BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) }; border.SetResourceReference(Border.BorderBrushProperty, "Stroke"); Grid.SetColumnSpan(border, 2); grid.Children.Add(border);
        var a = new Border { Padding = new Thickness(0,12,right == null ? 0 : 12,12), Child = left, VerticalAlignment = VerticalAlignment.Center }; if (right == null) Grid.SetColumnSpan(a, 2); grid.Children.Add(a);
        if (right != null) { var b = new Border { Padding = new Thickness(0,12,0,12), Child = right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(b,1); grid.Children.Add(b); }
        return grid;
    }
    internal static StackPanel Caption(string title, string sub) { var p = new StackPanel(); p.Children.Add(Text(title)); p.Children.Add(Text(sub,12,"Text3")); return p; }
    internal static void Section(StackPanel panel, string title) { panel.Children.Add(new Border { Margin = new Thickness(0,26,0,10), Child = Text(title,13,weight:FontWeights.SemiBold) }); }
    internal static string Clock(DateTimeOffset time) => time.LocalDateTime.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"));
    internal static string Clock(TimeOnly time) => time.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"));
    internal static TextBox Field(string value, Action<string> save, double width = 100)
    {
        var box = new TextBox { Text = value, Width = width, FontSize = 12 };
        box.LostKeyboardFocus += (_, _) => save(box.Text);
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { save(box.Text); System.Windows.Input.Keyboard.ClearFocus(); } }; return box;
    }
    internal static TextBox ClockField(TimeOnly value, Action<TimeOnly> set, double width = 78)
    {
        TextBox? box = null;
        box = Field(Clock(value), text => { if (ParseTime(text, out var t)) set(t); else box!.Text = Clock(value); }, width);
        box.Padding = new Thickness(3,0,3,0); box.VerticalAlignment = VerticalAlignment.Center; return box;
    }
    internal static bool ParseTime(string text, out TimeOnly value) => TimeOnly.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out value);
    /// <summary>Label, value and slider; returns the panel. <paramref name="format"/> maps the slider value to the shown text.</summary>
    internal static StackPanel Slider(string title, double min, double max, double value, Func<int, string> format, Action<int> changed, string? id = null, bool warm = false, string? left = null, string? right = null, bool large = false)
    {
        var panel = new StackPanel();
        var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(Text(title, large ? 14 : 13, weight: large ? FontWeights.Normal : FontWeights.SemiBold));
        var shown = Text(format((int)Math.Round(value)), 13, "Text2"); Grid.SetColumn(shown, 1); heading.Children.Add(shown); panel.Children.Add(heading);
        var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 0) };
        if (warm) slider.Style = (Style)Application.Current.FindResource("WarmSlider");
        if (id != null) AutomationProperties.SetAutomationId(slider, id);
        slider.ValueChanged += (_, e) => { int n = (int)Math.Round(e.NewValue); shown.Text = format(n); changed(n); };
        panel.Children.Add(slider);
        if (left != null)
        {
            var ends = new Grid(); ends.ColumnDefinitions.Add(new()); ends.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            ends.Children.Add(Text(left, 11, "Text3")); var r = Text(right ?? "", 11, "Text3"); Grid.SetColumn(r, 1); ends.Children.Add(r); panel.Children.Add(ends);
        }
        return panel;
    }
    // The warmth slider runs from neutral on the left to warmest on the right, in 100 K steps.
    internal static StackPanel WarmSlider(App app, string title, bool large = false, string? left = "Less warm", string? right = "More warm") =>
        Slider(title, 0, (Night.NeutralKelvin - Night.MinKelvin) / 100, (Night.NeutralKelvin - app.Settings.WarmKelvin) / 100.0,
            n => $"{Night.NeutralKelvin - n * 100} K", n => app.SetWarmth(Night.NeutralKelvin - n * 100), "WarmthSlider", true, left, right, large);
    internal static string WarmCopy(Look look, Settings s)
    {
        var w = look.Warmth; bool sun = s.WarmSchedule == NightSchedule.Sunset;
        string dim = look.Dimming.On ? $" Dimmed to {s.DimPercent}%." : "";
        if (w.Held) return (w.On ? $"On until {Clock(w.NextChange)}." : $"Off until {Clock(w.NextChange)}.") + dim;
        if (w.Window == null) return "No schedule. The switch warms until morning." + dim;
        if (w.On) return (sun ? $"On until sunrise, {Clock(w.NextChange)}." : $"On until {Clock(w.NextChange)}.") + dim;
        return (sun ? $"Warm from sunset, {Clock(w.NextChange)}." : $"Warm from {Clock(w.NextChange)}.") + dim;
    }
    internal static string BoostCopy(EngineState s, Settings settings)
    {
        var next = Schedule.At(s.NextChange, settings, TimeZoneInfo.Local);
        return s.IsOverride ? $"Held until {Clock(s.NextChange)}, then {next.Period} uses {next.Level}." : next.Level == s.ActiveLevel ? $"{s.Period} level until {Clock(s.NextChange)}." : $"{s.Period} level. {next.Level} from {Clock(s.NextChange)}.";
    }
}

public sealed class FlyoutView : UserControl
{
    readonly App app;
    readonly Snapshot? snapshot;
    public FlyoutView(App app) : this(app, null) { }
    internal FlyoutView(App app, Snapshot? snapshot)
    {
        this.app = app; this.snapshot = snapshot; Width = 340; Build();
        if (snapshot == null) { app.Changed += Build; Unloaded += (_, _) => app.Changed -= Build; }
    }
    void Build()
    {
        if (IsMouseCaptureWithin) return;
        var s = snapshot?.Boost ?? app.State; var look = snapshot?.Look ?? app.Look; var settings = app.Settings;
        var body = new StackPanel();
        var head = new Grid { Margin = new Thickness(16,16,16,0) }; head.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        head.Children.Add(Glyph.View(look.Warmth.On, look.Warmth.On ? "Warm" : "Text2"));
        var name = UI.Text("ShadowBoost",14,weight:FontWeights.SemiBold); Grid.SetColumn(name,1); head.Children.Add(name);
        var toggle = UI.Switch(look.Warmth.On, app.ToggleWarm, "WarmToggle"); Grid.SetColumn(toggle,2); head.Children.Add(toggle); body.Children.Add(head);
        var big = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        big.Inlines.Add(new System.Windows.Documents.Run(look.Warmth.On ? "Warm" : "Daylight"));
        if (look.Warmth.On) { var k = new System.Windows.Documents.Run($"  {look.Kelvin} K") { FontSize = 20, FontWeight = FontWeights.Normal }; k.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text2"); big.Inlines.Add(k); }
        big.SetResourceReference(TextBlock.ForegroundProperty, look.Warmth.On ? "Text" : "Text2");
        body.Children.Add(new Border { Margin = new Thickness(16,14,16,0), Child = big });
        string copy = UI.WarmCopy(look, settings);
        // Windows bounds gamma ramps unless the expanded range is enabled; say what the display really shows.
        if (snapshot == null && look.Warmth.On && app.AppliedKelvin > look.Kelvin) copy += $" Windows allows {app.AppliedKelvin} K on this display.";
        body.Children.Add(new Border { Margin = new Thickness(16,2,16,0), Child = UI.Text(copy,13,"Text2") });
        body.Children.Add(new Border { Margin = new Thickness(16,16,16,0), Child = UI.WarmSlider(app, "Warmth") });
        body.Children.Add(new Border { Margin = new Thickness(16,16,16,0), Child = UI.Slider("Brightness", 50, 130, settings.UserBrightness, n => n + "%", app.SetUserBrightness, "BrightnessSlider") });
        var boost = new Grid { Margin = new Thickness(16,14,16,0) }; boost.ColumnDefinitions.Add(new()); boost.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var boostTitle = new StackPanel(); boostTitle.Children.Add(UI.Text("Shadow boost",13,weight:FontWeights.SemiBold)); if (s.Enabled) boostTitle.Children.Add(UI.Text(UI.BoostCopy(s, settings),12,"Text3"));
        boost.Children.Add(boostTitle); var boostSwitch = UI.Switch(s.Enabled, app.Toggle, "FlyoutToggle"); Grid.SetColumn(boostSwitch,1); boost.Children.Add(boostSwitch); body.Children.Add(boost);
        if (s.Enabled)
        {
            body.Children.Add(new Border { Margin = new Thickness(16,10,16,0), Child = UI.Segments(s.ActiveLevel, app.Select, ids:true) });
            if (s.IsOverride) { var back = UI.Link("Back to schedule", app.ScheduleAgain); back.HorizontalAlignment = HorizontalAlignment.Right; AutomationProperties.SetAutomationId(back,"BackToSchedule"); body.Children.Add(new Border { Margin = new Thickness(16,8,16,0), Child = back }); }
        }
        var foot = new Grid(); foot.ColumnDefinitions.Add(new()); foot.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); foot.Children.Add(UI.Link("Schedule and settings",app.ShowMain)); var key = UI.Text("Ctrl+Shift+N",12,"Text3"); Grid.SetColumn(key,1); foot.Children.Add(key);
        var footer = new Border { Margin = new Thickness(0,16,0,0), Padding = new Thickness(16,12,16,12), BorderThickness = new Thickness(0,1,0,0), CornerRadius = new CornerRadius(0,0,8,8), Child = foot }; footer.SetResourceReference(Border.BackgroundProperty,"Surface2"); footer.SetResourceReference(Border.BorderBrushProperty,"Stroke"); body.Children.Add(footer);
        var shell = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = body }; shell.SetResourceReference(Border.BackgroundProperty,"Surface"); shell.SetResourceReference(Border.BorderBrushProperty,"Stroke"); Content = shell;
    }
}

public sealed class MainView : UserControl
{
    const double PageWidth = 500;
    readonly App app;
    readonly Snapshot? snapshot;
    bool locationOpen;
    string locateNote = "";
    public MainView(App app) : this(app, null) { }
    internal MainView(App app, Snapshot? snapshot) { this.app = app; this.snapshot = snapshot; Width = PageWidth; Build(); if (snapshot == null) { app.Changed += Build; Unloaded += (_, _) => app.Changed -= Build; } }
    void Build()
    {
        if (IsKeyboardFocusWithin || IsMouseCaptureWithin) return;
        var look = snapshot?.Look ?? app.Look; var settings = app.Settings;
        var page = new StackPanel { Margin = new Thickness(28,4,28,24) };
        var head = new Grid(); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titles = new StackPanel(); titles.Children.Add(UI.Text("ShadowBoost",28,weight:FontWeights.SemiBold));
        string status = (look.Warmth.On ? $"Warm {look.Kelvin} K" + (look.Warmth.NextChange == DateTimeOffset.MaxValue ? "" : $" until {UI.Clock(look.Warmth.NextChange)}") : "Daylight") + $" · Brightness {Math.Round(look.Brightness)}%";
        titles.Children.Add(UI.Text(status,13,"Text2")); head.Children.Add(titles);
        var toggle = UI.Switch(look.Warmth.On,app.ToggleWarm,"MainWarmToggle"); Grid.SetColumn(toggle,1); head.Children.Add(toggle); page.Children.Add(head);
        bool anySun = settings.WarmSchedule == NightSchedule.Sunset || settings.DimSchedule == NightSchedule.Sunset;
        UI.Section(page, anySun ? "Today in " + settings.PlaceName : "Today"); page.Children.Add(Timeline());

        UI.Section(page,"Warmth"); Plan(page, NightKind.Warmth);
        page.Children.Add(UI.Row(UI.WarmSlider(app, "Night warmth", true, $"{Night.NeutralKelvin} K", $"{Night.MinKelvin} K"), null));
        UI.Section(page,"Dimming"); Plan(page, NightKind.Dimming);
        page.Children.Add(UI.Row(UI.Slider("Dim to", 30, 100, settings.DimPercent, n => $"{n}% of your brightness", app.SetDim, "DimSlider", large: true), null));

        UI.Section(page,"General");
        page.Children.Add(UI.Row(UI.Slider("Brightness", 50, 130, settings.UserBrightness, n => n + "%", app.SetUserBrightness, "MainBrightnessSlider", large: true), null, true));
        string coordinates = settings.Longitude.ToString("0.00",CultureInfo.InvariantCulture).Replace("-","−");
        page.Children.Add(UI.Row(UI.Caption("Location",$"{settings.PlaceName} · {settings.Latitude.ToString("0.00",CultureInfo.InvariantCulture)}, {coordinates}"), UI.Link(locationOpen ? "Done" : "Change", () => { locationOpen = !locationOpen; Build(); })));
        if (locationOpen) Location(page);
        int[] fades = { 0, 20, 60 };
        page.Children.Add(UI.Row(UI.Caption("Fade","How long changes take"), UI.Choice(fades, settings.FadeMinutes, n => n == 0 ? "Off" : $"{n} min", n => { settings.FadeMinutes = n; app.Save(); }, 180, true, "Fade")));
        page.Children.Add(UI.Row(UI.Caption("Start at sign-in","Warmth resumes; shadow boost stays off"), UI.Switch(settings.StartAtSignIn, () => { settings.StartAtSignIn = !settings.StartAtSignIn; app.Save(); })));
        string levels = string.Join(" · ", Enum.GetValues<Period>().Select(p => $"{p} {settings.PeriodLevels[p]}"));
        page.Children.Add(UI.Row(UI.Caption("Shadow boost", (app.State.Enabled ? "On · " : "Off · ") + levels), UI.Link("›",app.BoostPage)));
        var bg = new Border { Child = page }; bg.SetResourceReference(Border.BackgroundProperty,"Bg");
        Content = snapshot != null ? bg : new ScrollViewer { Content = bg, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    void Plan(StackPanel page, NightKind kind)
    {
        var settings = app.Settings; var (schedule, from, to) = Night.Plan(settings, kind);
        var window = Night.CurrentOrNext(DateTimeOffset.Now, settings, kind, TimeZoneInfo.Local);
        string caption = schedule switch
        {
            NightSchedule.Off => kind == NightKind.Warmth ? "Only when you turn it on" : "Never",
            NightSchedule.Sunset when window != null => $"{UI.Clock(window.Start)} to {UI.Clock(window.End)}, sunset to sunrise",
            NightSchedule.Sunset => "Sunset to sunrise",
            _ => "Fixed hours"
        };
        NightSchedule[] options = { NightSchedule.Off, NightSchedule.Sunset, NightSchedule.Custom };
        page.Children.Add(UI.Row(UI.Caption("Schedule", caption), UI.Choice(options, schedule, o => o.ToString(), o =>
        {
            if (kind == NightKind.Warmth) { settings.WarmSchedule = o; settings.WarmHold = null; } else settings.DimSchedule = o;
            app.Save();
        }, 220, true, kind + "Schedule"), true));
        if (schedule != NightSchedule.Custom) return;
        var hours = new StackPanel { Orientation = Orientation.Horizontal };
        hours.Children.Add(UI.Text("From ",13,"Text2"));
        hours.Children.Add(UI.ClockField(from, t => { if (kind == NightKind.Warmth) settings.WarmFrom = t; else settings.DimFrom = t; app.Save(); }));
        hours.Children.Add(UI.Text(" to ",13,"Text2"));
        hours.Children.Add(UI.ClockField(to, t => { if (kind == NightKind.Warmth) settings.WarmTo = t; else settings.DimTo = t; app.Save(); }));
        page.Children.Add(UI.Row(UI.Text("Hours"), hours));
    }
    UIElement Timeline()
    {
        const double lane = 64, gap = 10, width = PageWidth - 56 - lane - gap;
        var tz = TimeZoneInfo.Local; var settings = app.Settings;
        var date = DateOnly.FromDateTime(DateTime.Now);
        DateTimeOffset start = new(date.ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)));
        var endLocal = date.AddDays(1).ToDateTime(TimeOnly.MinValue); DateTimeOffset end = new(endLocal, tz.GetUtcOffset(endLocal));
        double duration = (end - start).TotalSeconds;
        double X(DateTimeOffset value) => Math.Clamp((value - start).TotalSeconds / duration * width, 0, width);
        var panel = new StackPanel();
        foreach (var kind in new[] { NightKind.Warmth, NightKind.Dimming })
        {
            var row = new Grid { Margin = new Thickness(0,0,0,6) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(lane + gap) }); row.ColumnDefinitions.Add(new());
            row.Children.Add(UI.Text(kind.ToString(),12,"Text3"));
            var canvas = new Canvas { Width = width, Height = 18 };
            var track = new Rectangle { Width = width, Height = 8, RadiusX = 4, RadiusY = 4 }; track.SetResourceReference(Shape.FillProperty, "Stroke"); Canvas.SetTop(track, 5); canvas.Children.Add(track);
            foreach (var w in Night.Windows(date, settings, kind, tz).Where(w => w.End > start && w.Start < end))
            {
                var band = new Rectangle { Width = Math.Max(2, X(w.End) - X(w.Start)), Height = 8, RadiusX = 4, RadiusY = 4 };
                band.SetResourceReference(Shape.FillProperty, kind == NightKind.Warmth ? "WarmBand" : "DimBand"); Canvas.SetLeft(band, X(w.Start)); Canvas.SetTop(band, 5); canvas.Children.Add(band);
            }
            var marker = new Border { Width = 2, Height = 18, CornerRadius = new CornerRadius(1) }; marker.SetResourceReference(Border.BackgroundProperty,"Text"); Canvas.SetLeft(marker, X(DateTimeOffset.Now)); canvas.Children.Add(marker);
            Grid.SetColumn(canvas, 1); row.Children.Add(canvas); panel.Children.Add(row);
        }
        var entries = new List<(double X, string Full, string Short)> { (0, "12 AM", "12 AM") };
        if (settings.WarmSchedule == NightSchedule.Sunset || settings.DimSchedule == NightSchedule.Sunset)
        {
            var (rise, set) = SunTimes.For(date, settings.Latitude, settings.Longitude, tz);
            if (rise != null) entries.Add((X(rise.Value), "Sunrise " + UI.Clock(rise.Value), UI.Clock(rise.Value)));
            if (set != null) entries.Add((X(set.Value), "Sunset " + UI.Clock(set.Value), UI.Clock(set.Value)));
        }
        foreach (var kind in new[] { NightKind.Warmth, NightKind.Dimming })
        {
            var (schedule, from, to) = Night.Plan(settings, kind);
            if (schedule != NightSchedule.Custom) continue;
            foreach (var t in new[] { from, to }) { var at = new DateTimeOffset(date.ToDateTime(t), tz.GetUtcOffset(date.ToDateTime(t))); entries.Add((X(at), UI.Clock(t), UI.Clock(t))); }
        }
        entries = entries.GroupBy(e => e.Short).Select(g => g.First()).OrderBy(e => e.X).ToList();
        var labels = new Canvas { Width = width, Height = 18, Margin = new Thickness(lane + gap, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        bool Layout(bool shorten)
        {
            labels.Children.Clear();
            var measured = new List<(TextBlock Label, double Width)>();
            foreach (var entry in entries)
            {
                var label = UI.Text(shorten ? entry.Short : entry.Full,12,"Text3"); label.TextWrapping = TextWrapping.NoWrap;
                label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity)); measured.Add((label,label.DesiredSize.Width));
            }
            if (measured.Sum(item => item.Width) + (measured.Count - 1) * 8 > width) return false;
            var positions = new double[entries.Count]; double right = -8;
            for (int i = 0; i < entries.Count; i++) { positions[i] = Math.Max(Math.Clamp(entries[i].X,0,width-measured[i].Width),right+8); right = positions[i] + measured[i].Width; }
            // Late boundaries can overflow even when the row fits; back-pack from the right edge.
            double next = width + 8;
            for (int i = entries.Count - 1; i >= 0; i--) { positions[i] = Math.Min(positions[i],next-8-measured[i].Width); next = positions[i]; }
            for (int i = 0; i < entries.Count; i++) { Canvas.SetLeft(measured[i].Label,positions[i]); labels.Children.Add(measured[i].Label); }
            return true;
        }
        if (!Layout(false)) { entries.RemoveAt(0); if (!Layout(false)) Layout(true); }
        panel.Children.Add(labels); return panel;
    }
    void Location(StackPanel page)
    {
        var settings = app.Settings;
        var locate = UI.Link("Use this PC's location", async () => { locateNote = "Asking Windows…"; Build(); locateNote = await app.LocatePc(); Build(); });
        page.Children.Add(UI.Row(locateNote == "" ? locate : new StackPanel { Children = { locate, UI.Text(locateNote,12,"Text3") } }, null));
        page.Children.Add(UI.Row(UI.Text("Place",13),UI.Field(settings.PlaceName,text => { settings.PlaceName = text; app.Save(); },220)));
        void Coordinate(string title,double value,double limit,Action<double> set) { TextBox? box = null; box = UI.Field(value.ToString(CultureInfo.InvariantCulture),text => { if (double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number) && double.IsFinite(number) && Math.Abs(number) <= limit) { set(number); app.Save(); } else box!.Text = value.ToString(CultureInfo.InvariantCulture); },220); page.Children.Add(UI.Row(UI.Text(title,13),box)); }
        Coordinate("Latitude",settings.Latitude,90,n => settings.Latitude=n); Coordinate("Longitude",settings.Longitude,180,n => settings.Longitude=n);
    }
}

public sealed class BoostView : UserControl
{
    readonly App app;
    readonly bool live;
    public BoostView(App app) : this(app, true) { }
    internal BoostView(App app, bool live) { this.app = app; this.live = live; Width = 500; Build(); if (live) { app.Changed += Build; Unloaded += (_, _) => app.Changed -= Build; } }
    void Build()
    {
        if (IsKeyboardFocusWithin) return;
        var s = app.State; var settings = app.Settings;
        var page = new StackPanel { Margin = new Thickness(28,4,28,24) };
        var header = new StackPanel { Orientation = Orientation.Horizontal }; var back = UI.Link("‹",app.ShowMainPage); back.FontSize = 28; back.Margin = new Thickness(0,0,12,0); header.Children.Add(back); header.Children.Add(UI.Text("Shadow boost",28,weight:FontWeights.SemiBold)); page.Children.Add(header);
        page.Children.Add(new Border { Height = 18 });
        page.Children.Add(UI.Row(UI.Caption("Shadow boost", s.Enabled ? (s.IsOverride ? $"On · {s.ActiveLevel}, held until {UI.Clock(s.NextChange)}" : $"On · {s.Period} · {s.ActiveLevel}") : "Lifts dark detail. Off at every start"), UI.Switch(s.Enabled, app.Toggle, "BoostToggle"), true));
        UI.Section(page,"Levels");
        foreach (var period in Enum.GetValues<Period>())
        {
            var left = new StackPanel(); var title = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0,0,8,0), VerticalAlignment = VerticalAlignment.Center }; dot.SetResourceReference(Shape.FillProperty, period.ToString()); title.Children.Add(dot); title.Children.Add(UI.Text(period.ToString())); left.Children.Add(title);
            if (period == Period.Night)
            {
                var inline = new StackPanel { Orientation = Orientation.Horizontal }; inline.Children.Add(UI.Text("From ",12,"Text3"));
                inline.Children.Add(UI.ClockField(settings.NightStart, t => { settings.NightStart = t; app.Save(); })); inline.Children.Add(UI.Text(settings.FollowSun ? " to sunrise" : " to " + UI.Clock(settings.DayStart),12,"Text3")); left.Children.Add(inline);
            }
            else left.Children.Add(UI.Text(period == Period.Day ? (settings.FollowSun ? "Sunrise to sunset" : $"{UI.Clock(settings.DayStart)} to {UI.Clock(settings.EveningStart)}") : (settings.FollowSun ? "Sunset to night" : $"{UI.Clock(settings.EveningStart)} to night"),12,"Text3"));
            var picker = UI.Segments(settings.PeriodLevels[period], level => { settings.PeriodLevels[period] = level; app.Save(); },true);
            if (period == Period.Night) picker.Width = 220;
            page.Children.Add(UI.Row(left,picker,period == Period.Day));
        }
        UI.Section(page,"Timing");
        page.Children.Add(UI.Row(UI.Caption("Follow sunrise and sunset","Uses the location in settings"),UI.Switch(settings.FollowSun, () => { settings.FollowSun = !settings.FollowSun; app.Save(); }),true));
        if (!settings.FollowSun)
        {
            page.Children.Add(UI.Row(UI.Text("Day starts",13),UI.ClockField(settings.DayStart, t => { settings.DayStart = t; app.Save(); }, 100)));
            page.Children.Add(UI.Row(UI.Text("Evening starts",13),UI.ClockField(settings.EveningStart, t => { settings.EveningStart = t; app.Save(); }, 100)));
        }
        page.Children.Add(UI.Row(UI.Caption("Fine-tune levels","Black lift, gamma, contrast, brightness, monitor brightness"), UI.Link("›",app.FineTune)));
        var bg = new Border { Child = page }; bg.SetResourceReference(Border.BackgroundProperty,"Bg");
        Content = live ? new ScrollViewer { Content = bg, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : bg;
    }
}

public sealed class FineTuneView : UserControl
{
    readonly App app;
    LevelId selected;
    public FineTuneView(App app) { this.app=app; selected=app.State.ActiveLevel; Width=500; Build(); }
    void Build()
    {
        var page = new StackPanel { Margin = new Thickness(28,4,28,24) };
        var header = new StackPanel { Orientation=Orientation.Horizontal }; var back = UI.Link("‹",app.BoostPage); back.FontSize=28; back.Margin=new Thickness(0,0,12,0); header.Children.Add(back); header.Children.Add(UI.Text("Fine-tune levels",28,weight:FontWeights.SemiBold)); page.Children.Add(header);
        page.Children.Add(new Border { Margin=new Thickness(0,26,0,24), Child=UI.Segments(selected,l => { selected=l; Build(); }) });
        var c = app.Settings.Curves[selected];
        void Slider(string title,int min,int max,int value,Func<Curve,int,Curve> update,Func<int,string> format)
        {
            var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width=GridLength.Auto }); heading.Children.Add(UI.Text(title)); var label=UI.Text(format(value),13,"Text2"); Grid.SetColumn(label,1); heading.Children.Add(label); page.Children.Add(heading);
            var slider = new System.Windows.Controls.Slider { Minimum=min,Maximum=max,Value=value,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,8,0,22) }; slider.ValueChanged += (_,e) => { int n=(int)Math.Round(e.NewValue); label.Text=format(n); app.Settings.Curves[selected]=update(app.Settings.Curves[selected],n); app.Save(); }; page.Children.Add(slider);
        }
        Slider("Black lift",0,30,c.BlackLift,(v,n)=>v with { BlackLift=n },n=>n+"%");
        Slider("Gamma",30,100,c.Gamma,(v,n)=>v with { Gamma=n },n=>(n/100.0).ToString("0.00"));
        Slider("Contrast",50,150,c.Contrast,(v,n)=>v with { Contrast=n },n=>n+"%");
        Slider("Brightness",50,130,c.Brightness,(v,n)=>v with { Brightness=n },n=>n+"%");
        page.Children.Add(UI.Link("Reset to default",()=> { app.Settings.Curves[selected]=new Settings().Curves[selected]; app.Save(); Build(); }));
        UI.Section(page,"Monitor brightness");
        int? brightness=app.MonitorBrightness;
        if (brightness == null) page.Children.Add(UI.Text("This monitor does not accept brightness control.",13,"Text3"));
        else { var label=UI.Text(brightness+"%",13,"Text2"); page.Children.Add(label); var slider=new System.Windows.Controls.Slider { Minimum=0,Maximum=100,Value=app.Settings.MonitorBrightness ?? brightness.Value,TickFrequency=1,IsSnapToTickEnabled=true }; slider.ValueChanged += (_,e)=> { int n=(int)e.NewValue; label.Text=n+"%"; app.SetBrightness(n); }; page.Children.Add(slider); }
        var bg = new Border { Child=page }; bg.SetResourceReference(Border.BackgroundProperty,"Bg"); Content=bg;
    }
}
