using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace ShadowBoost.App;

internal static class UI
{
    internal static TextBlock Text(string text, double size = 14, string brush = "Text", FontWeight? weight = null, double width = double.NaN)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Width = width };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush); return t;
    }
    internal static Button Link(string text, Action click) { var b = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left }; b.Click += (_, _) => click(); return b; }
    internal static CheckBox Switch(bool value, Action click, string? id = null) { var b = new CheckBox { IsChecked = value, Style = (Style)Application.Current.FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center }; if (id != null) AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => click(); return b; }
    internal static Border Segments(LevelId? selected, Action<LevelId> click, bool small = false, bool ids = false)
    {
        var grid = new Grid();
        foreach (var level in Enum.GetValues<LevelId>())
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var b = new RadioButton { Content = level.ToString(), IsChecked = selected == level, GroupName = Guid.NewGuid().ToString(), FontSize = small ? 12 : 13, Padding = new Thickness(0, small ? 4 : 6, 0, small ? 4 : 6) };
            Grid.SetColumn(b, (int)level); if (ids) AutomationProperties.SetAutomationId(b, "Level" + level);
            b.Click += (_, _) => click(level); grid.Children.Add(b);
        }
        var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(3), Child = grid, Width = small ? 240 : double.NaN };
        border.SetResourceReference(Border.BackgroundProperty, "SegBg"); border.SetResourceReference(Border.BorderBrushProperty, "Stroke"); return border;
    }
    internal static Grid Row(UIElement left, UIElement right, bool first = false)
    {
        var grid = new Grid { Margin = new Thickness(0, first ? 0 : 1, 0, 0) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var border = new Border { BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) }; border.SetResourceReference(Border.BorderBrushProperty, "Stroke"); Grid.SetColumnSpan(border, 2); grid.Children.Add(border);
        var a = new Border { Padding = new Thickness(0,12,12,12), Child = left }; grid.Children.Add(a);
        var b = new Border { Padding = new Thickness(0,12,0,12), Child = right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(b,1); grid.Children.Add(b); return grid;
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
    internal static bool ParseTime(string text, out TimeOnly value) => TimeOnly.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out value);
}

public sealed class FlyoutView : UserControl
{
    readonly App app;
    readonly EngineState? snapshot;
    public FlyoutView(App app) : this(app, null) { }
    internal FlyoutView(App app, EngineState? snapshot)
    {
        this.app = app; this.snapshot = snapshot; Width = 340; Build();
        if (snapshot == null) { app.Changed += Build; Unloaded += (_, _) => app.Changed -= Build; }
    }
    void Build()
    {
        var s = snapshot ?? app.State; var next = Schedule.At(s.NextChange, app.Settings, TimeZoneInfo.Local);
        var body = new StackPanel();
        var head = new Grid { Margin = new Thickness(16,16,16,0) }; head.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var glyph = Glyph.View(s.Enabled, s.Enabled ? "Accent" : "Text2"); head.Children.Add(glyph);
        var name = UI.Text("ShadowBoost",14,weight:FontWeights.SemiBold); Grid.SetColumn(name,1); head.Children.Add(name);
        var toggle = UI.Switch(s.Enabled, app.Toggle,"FlyoutToggle"); Grid.SetColumn(toggle,2); head.Children.Add(toggle); body.Children.Add(head);
        body.Children.Add(new Border { Margin = new Thickness(16,14,16,0), Child = UI.Text(s.Enabled ? s.ActiveLevel.ToString() : "Off",28,s.Enabled ? "Text" : "Text2",FontWeights.SemiBold) });
        string copy = !s.Enabled ? "Your screen is normal. Turn it on to follow the schedule, or pick a level." : s.IsOverride ? $"Held until {UI.Clock(s.NextChange)}, then {next.Period} uses {next.Level}." : next.Level == s.ActiveLevel ? $"{s.Period} level until {UI.Clock(s.NextChange)}." : $"{s.Period} level. {next.Level} from {UI.Clock(s.NextChange)}.";
        body.Children.Add(new Border { Margin = new Thickness(16,2,16,0), Child = UI.Text(copy,13,"Text2") });
        body.Children.Add(new Border { Margin = new Thickness(16,16,16,0), Child = UI.Segments(s.Enabled ? s.ActiveLevel : null, app.Select, ids:true) });
        if (s.IsOverride) { var back = UI.Link("Back to schedule", app.ScheduleAgain); back.HorizontalAlignment = HorizontalAlignment.Right; AutomationProperties.SetAutomationId(back,"BackToSchedule"); body.Children.Add(new Border { Margin = new Thickness(16,10,16,0), Child = back }); }
        var foot = new Grid(); foot.ColumnDefinitions.Add(new()); foot.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); foot.Children.Add(UI.Link("Schedule and settings",app.ShowMain)); var key = UI.Text("Ctrl+Shift+D",12,"Text3"); Grid.SetColumn(key,1); foot.Children.Add(key);
        var footer = new Border { Margin = new Thickness(0,16,0,0), Padding = new Thickness(16,12,16,12), BorderThickness = new Thickness(0,1,0,0), CornerRadius = new CornerRadius(0,0,8,8), Child = foot }; footer.SetResourceReference(Border.BackgroundProperty,"Surface2"); footer.SetResourceReference(Border.BorderBrushProperty,"Stroke"); body.Children.Add(footer);
        var shell = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = body }; shell.SetResourceReference(Border.BackgroundProperty,"Surface"); shell.SetResourceReference(Border.BorderBrushProperty,"Stroke"); Content = shell;
    }
}

public sealed class MainView : UserControl
{
    readonly App app;
    bool locationOpen;
    public MainView(App app) : this(app, true) { }
    internal MainView(App app, bool live) { this.app = app; Width = 460; Build(); if (live) { app.Changed += Build; Unloaded += (_, _) => app.Changed -= Build; } }
    void Build()
    {
        if (IsKeyboardFocusWithin) return;
        var s = app.State; var settings = app.Settings;
        var page = new StackPanel { Margin = new Thickness(28,4,28,24) };
        var head = new Grid(); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titles = new StackPanel(); titles.Children.Add(UI.Text("ShadowBoost",28,weight:FontWeights.SemiBold)); titles.Children.Add(UI.Text(!s.Enabled ? "Off" : s.IsOverride ? $"On · {s.ActiveLevel}, held until {UI.Clock(s.NextChange)}" : $"On · {s.Period} · {s.ActiveLevel}",13,"Text2")); head.Children.Add(titles);
        var toggle = UI.Switch(s.Enabled,app.Toggle); Grid.SetColumn(toggle,1); head.Children.Add(toggle); page.Children.Add(head);
        UI.Section(page, settings.FollowSun ? "Today in " + settings.PlaceName : "Today"); page.Children.Add(Timeline());
        UI.Section(page,"Levels");
        foreach (var period in Enum.GetValues<Period>())
        {
            var left = new StackPanel(); var title = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0,0,8,0), VerticalAlignment = VerticalAlignment.Center }; dot.SetResourceReference(Shape.FillProperty, period.ToString()); title.Children.Add(dot); title.Children.Add(UI.Text(period.ToString())); left.Children.Add(title);
            if (period == Period.Night)
            {
                var inline = new WrapPanel(); inline.Children.Add(UI.Text("From ",12,"Text3"));
                TextBox? clock = null; clock = UI.Field(UI.Clock(settings.NightStart), text => { if (UI.ParseTime(text,out var t)) { settings.NightStart = t; app.Save(); } else clock!.Text = UI.Clock(settings.NightStart); },70); clock.Padding = new Thickness(3,0,3,0); inline.Children.Add(clock); inline.Children.Add(UI.Text(settings.FollowSun ? " to sunrise" : " to " + UI.Clock(settings.DayStart),12,"Text3")); left.Children.Add(inline);
            }
            else left.Children.Add(UI.Text(period == Period.Day ? (settings.FollowSun ? "Sunrise to sunset" : $"{UI.Clock(settings.DayStart)} to {UI.Clock(settings.EveningStart)}") : (settings.FollowSun ? "Sunset to night" : $"{UI.Clock(settings.EveningStart)} to night"),12,"Text3"));
            page.Children.Add(UI.Row(left,UI.Segments(settings.PeriodLevels[period], level => { settings.PeriodLevels[period] = level; app.Save(); },true)));
        }
        UI.Section(page,"General");
        string coordinates = settings.Longitude.ToString("0.00",CultureInfo.InvariantCulture).Replace("-","−");
        page.Children.Add(UI.Row(UI.Caption("Location",$"{settings.PlaceName} · {settings.Latitude:0.00}, {coordinates}"), UI.Link("Change", () => { locationOpen = !locationOpen; Build(); })));
        if (locationOpen) Location(page);
        page.Children.Add(UI.Row(UI.Caption("Start at sign-in","Boost is always off when Windows starts"), UI.Switch(settings.StartAtSignIn, () => { settings.StartAtSignIn = !settings.StartAtSignIn; app.Save(); })));
        page.Children.Add(UI.Row(UI.Caption("Fine-tune levels","Black lift, gamma, contrast, brightness, monitor brightness"), UI.Link("›",app.FineTune)));
        var bg = new Border { Child = page }; bg.SetResourceReference(Border.BackgroundProperty,"Bg"); Content = new ScrollViewer { Content = bg, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    UIElement Timeline()
    {
        const double width = 404;
        var date = DateOnly.FromDateTime(DateTime.Now); var slots = Schedule.Day(date,app.Settings,TimeZoneInfo.Local);
        var start = slots[0].Start; double duration = (slots[^1].End-start).TotalSeconds;
        double X(DateTimeOffset value) => Math.Clamp((value-start).TotalSeconds / duration * width,0,width);
        var panel = new StackPanel(); var canvas = new Canvas { Width = width, Height = 18 };
        foreach (var slot in slots) { var rect = new Rectangle { Width = X(slot.End)-X(slot.Start), Height = 10 }; rect.SetResourceReference(Shape.FillProperty,slot.Period.ToString()); Canvas.SetLeft(rect,X(slot.Start)); Canvas.SetTop(rect,4); canvas.Children.Add(rect); }
        var marker = new Border { Width = 2, Height = 18, CornerRadius = new CornerRadius(1) }; marker.SetResourceReference(Border.BackgroundProperty,"Text"); Canvas.SetLeft(marker,X(DateTimeOffset.Now)); canvas.Children.Add(marker); var barClip = new RectangleGeometry(new Rect(0,4,width,10),5,5);
        // Round the timeline ends without clipping the taller now marker.
        foreach (var child in canvas.Children.OfType<Rectangle>())
            child.Clip = new RectangleGeometry(new Rect(-Canvas.GetLeft(child),0,width,10),5,5);
        panel.Children.Add(canvas);
        var labels = new Canvas { Width = width, Height = 18 };
        var entries = new List<(double X, string Full, string Short)> { (0, "12 AM", "12 AM") };
        foreach (var slot in slots.Skip(1))
        {
            string prefix = slot.Period == Period.Day ? (app.Settings.FollowSun ? "Sunrise " : "Day ") : slot.Period == Period.Evening ? (app.Settings.FollowSun ? "Sunset " : "Evening ") : "";
            entries.Add((X(slot.Start), prefix + UI.Clock(slot.Start), UI.Clock(slot.Start)));
        }
        bool Layout(bool shorten)
        {
            labels.Children.Clear(); double right = -8;
            foreach (var entry in entries)
            {
                var label = UI.Text(shorten ? entry.Short : entry.Full,12,"Text3");
                label.TextWrapping = TextWrapping.NoWrap;
                label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                double x = Math.Max(Math.Clamp(entry.X,0,width-label.DesiredSize.Width),right+8);
                if (x + label.DesiredSize.Width > width) { labels.Children.Clear(); return false; }
                Canvas.SetLeft(label,x); labels.Children.Add(label); right = x + label.DesiredSize.Width;
            }
            return true;
        }
        if (!Layout(false))
        {
            entries.RemoveAt(0);
            if (!Layout(false)) Layout(true);
        }
        panel.Children.Add(labels); return panel;
    }
    void Location(StackPanel page)
    {
        var settings = app.Settings;
        page.Children.Add(UI.Row(UI.Text("Follow sunrise and sunset",13),UI.Switch(settings.FollowSun, () => { settings.FollowSun = !settings.FollowSun; app.Save(); })));
        if (settings.FollowSun)
        {
            page.Children.Add(UI.Row(UI.Text("Place",13),UI.Field(settings.PlaceName,text => settings.PlaceName = text,220)));
            void Coordinate(string title,double value,Action<double> set) { TextBox? box = null; box = UI.Field(value.ToString(CultureInfo.InvariantCulture),text => { if (double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number) && double.IsFinite(number) && Math.Abs(number) <= (title == "Latitude" ? 90 : 180)) set(number); else box!.Text = value.ToString(CultureInfo.InvariantCulture); },220); page.Children.Add(UI.Row(UI.Text(title,13),box)); }
            Coordinate("Latitude",settings.Latitude,n => settings.Latitude=n); Coordinate("Longitude",settings.Longitude,n => settings.Longitude=n);
        }
        else
        {
            void Clock(string title,TimeOnly value,Action<TimeOnly> set) { TextBox? box = null; box = UI.Field(UI.Clock(value),text => { if (UI.ParseTime(text,out var time)) set(time); else box!.Text = UI.Clock(value); },120); page.Children.Add(UI.Row(UI.Text(title,13),box)); }
            Clock("Day starts",settings.DayStart,t => settings.DayStart=t); Clock("Evening starts",settings.EveningStart,t => settings.EveningStart=t);
        }
        page.Children.Add(UI.Link("Save", () => { locationOpen=false; app.Save(); }));
    }
}

public sealed class FineTuneView : UserControl
{
    readonly App app;
    LevelId selected;
    public FineTuneView(App app) { this.app=app; selected=app.State.ActiveLevel; Width=460; Build(); }
    void Build()
    {
        var page = new StackPanel { Margin = new Thickness(28,4,28,24) };
        var header = new StackPanel { Orientation=Orientation.Horizontal }; var back = UI.Link("‹",app.ShowMain); back.FontSize=28; back.Margin=new Thickness(0,0,12,0); header.Children.Add(back); header.Children.Add(UI.Text("Fine-tune levels",28,weight:FontWeights.SemiBold)); page.Children.Add(header);
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
