using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace ShadowBoost.App;
internal static class Glyph
{
    internal static FrameworkElement View(bool on,string brush)
    {
        var canvas = new Canvas { Width=18,Height=18,VerticalAlignment=VerticalAlignment.Center };
        var ring = new Ellipse { Width=14.0625,Height=14.0625,StrokeThickness=1.6875 }; ring.SetResourceReference(Shape.StrokeProperty,brush); Canvas.SetLeft(ring,1.96875); Canvas.SetTop(ring,1.96875); canvas.Children.Add(ring);
        if (on)
        {
            var half = new System.Windows.Shapes.Path { Data=Geometry.Parse("M9,1.96875 A7.03125,7.03125 0 0 0 9,16.03125 Z") }; half.SetResourceReference(Shape.FillProperty,brush); canvas.Children.Add(half);
            var dot = new Ellipse { Width=5.4,Height=5.4 }; dot.SetResourceReference(Shape.FillProperty,brush); Canvas.SetLeft(dot,6.3); Canvas.SetTop(dot,6.3); canvas.Children.Add(dot);
        }
        return canvas;
    }
    internal static Drawing.Bitmap Bitmap(bool on,bool dark,int size)
    {
        var image=new Drawing.Bitmap(size,size); using var g=Drawing.Graphics.FromImage(image); g.SmoothingMode=Drawing.Drawing2D.SmoothingMode.AntiAlias; g.ScaleTransform(size/16f,size/16f);
        var color=dark ? Drawing.Color.White : Drawing.Color.FromArgb(27,27,27); using var pen=new Drawing.Pen(color,1.5f); using var fill=new Drawing.SolidBrush(color);
        if (on) { g.FillPie(fill,1.75f,1.75f,12.5f,12.5f,90,180); g.FillEllipse(fill,5.6f,5.6f,4.8f,4.8f); }
        g.DrawEllipse(pen,1.75f,1.75f,12.5f,12.5f); return image;
    }
    internal static Drawing.Icon Icon(bool on,bool dark,int size)
    {
        using var bitmap=Bitmap(on,dark,size); var handle=bitmap.GetHicon(); try { using var borrowed=Drawing.Icon.FromHandle(handle); return (Drawing.Icon)borrowed.Clone(); } finally { Native.DestroyIcon(handle); }
    }
}
// A fixed LG ULTRAGEAR+ (WK95U) for screenshots taken where no monitor answers DDC/CI.
internal static class Sample
{
    const string Caps = "(prot(monitor)type(lcd)model(WK95U)cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B ) 16 18 1A 52 60(11 12 0F 10 ) AC AE B2 B6 C0 C6 C8 C9 D6(01 04) DF 62 8D F4 F5(01 02 03 04) F6(00 01 02) 4D 4E 4F 15(01 06 11 13 14 15 18 19 20 22 23 24 28 29 32 48) F7(00 01 02 03) F8(00 01) F9 E4 E5 E6 E7 E8 E9 EA EB EF FA(00 01) FD(00 01) FE(00 01 02) FF)mccs_ver(2.1)mswhql(1))";
    internal static readonly IReadOnlyList<MonitorInfo> Monitors = Build();
    static IReadOnlyList<MonitorInfo> Build()
    {
        var caps = VcpCaps.Parse(Caps); VcpValue Value(int current, int max) => new(current, max, null, null, null);
        var values = new Dictionary<byte, VcpValue> { [0x10] = Value(40, 100), [0x12] = Value(70, 100), [0x14] = Value(0x0B, 0), [0x16] = Value(50, 100), [0x18] = Value(50, 100), [0x1A] = Value(50, 100) };
        return new[] { new MonitorInfo("sample", "Generic PnP Monitor", caps.Model, caps.Mccs, caps, values) };
    }
}
internal static class Screenshots
{
    // Renders hidden views to PNG without opening a window: 7 views per theme plus 2 tray glyphs per theme (18 files).
    internal static void Save(App app,string directory)
    {
        Directory.CreateDirectory(directory); bool original=app.Dark;
        try
        {
            var now=DateTimeOffset.Now; var tz=TimeZoneInfo.Local; var s=app.State; var look=app.Look;
            var window=Night.CurrentOrNext(now,app.Settings,NightKind.Warmth,tz);
            // Fixed night and day states, so the warm and daylight flyouts render at any hour.
            var warm=look with { Warmth=new NightState(1,false,window,window?.End ?? now.AddHours(8)), Kelvin=app.Settings.WarmKelvin };
            var day=look with { Warmth=new NightState(0,false,window,window?.Start ?? now.AddHours(8)), Kelvin=Night.NeutralKelvin };
            var off=s with { Enabled=false,IsOverride=false,AppliedCurve=null };
            var boosted=s with { Enabled=true,IsOverride=false,ActiveLevel=s.ScheduledLevel,AppliedCurve=app.Settings.Curves[s.ScheduledLevel] };
            foreach (bool dark in new[] { false,true })
            {
                app.ApplyTheme(dark); string theme=dark ? "dark" : "light";
                string Path(string name)=>System.IO.Path.Combine(directory,$"{name}-{theme}.png");
                Render(new FlyoutView(app,new Snapshot(s,look)),Path("flyout"));
                Render(new FlyoutView(app,new Snapshot(off,warm)),Path("flyout-warm"));
                Render(new FlyoutView(app,new Snapshot(off,day)),Path("flyout-day"));
                Render(new FlyoutView(app,new Snapshot(boosted,day)),Path("flyout-boost"));
                Render(new MainView(app,new Snapshot(s,look)),Path("main"));
                Render(new BoostView(app,false),Path("boost"));
                Render(new FineTuneView(app),Path("finetune"));
                foreach (bool on in new[] { false,true }) { using var image=Glyph.Bitmap(on,dark,32); image.Save(Path($"tray-{(on ? "on" : "off")}"),Drawing.Imaging.ImageFormat.Png); }
            }
        }
        finally { app.ApplyTheme(original); }
    }
    static void Render(FrameworkElement view,string path)
    {
        view.Measure(new Size(view.Width,double.PositiveInfinity)); view.Arrange(new Rect(0,0,view.Width,view.DesiredSize.Height)); view.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth*2),(int)Math.Ceiling(view.ActualHeight*2),192,192,PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output=File.Create(path); encoder.Save(output);
    }
}
