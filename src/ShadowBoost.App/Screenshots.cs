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
internal static class Screenshots
{
    internal static void Save(App app,string directory)
    {
        Directory.CreateDirectory(directory); bool original=app.Dark;
        try
        {
            foreach (bool dark in new[] { false,true })
            {
                app.ApplyTheme(dark); string theme=dark ? "dark" : "light";
                Render(new FlyoutView(app, app.State),System.IO.Path.Combine(directory,$"flyout-{theme}.png"));
                Render(new MainView(app, false),System.IO.Path.Combine(directory,$"main-{theme}.png"));
                Render(new FineTuneView(app),System.IO.Path.Combine(directory,$"finetune-{theme}.png"));
                // The brief requests 14 PNGs although its six view/four glyph names total ten.
                // Include the two other flyout states per theme without changing the real engine.
                var s=app.State;
                Render(new FlyoutView(app,s with { Enabled=false,IsOverride=false,AppliedCurve=null }),System.IO.Path.Combine(directory,$"flyout-off-{theme}.png"));
                Render(new FlyoutView(app,s with { Enabled=true,IsOverride=false,ActiveLevel=s.ScheduledLevel,AppliedCurve=app.Settings.Curves[s.ScheduledLevel] }),System.IO.Path.Combine(directory,$"flyout-schedule-{theme}.png"));
                foreach (bool on in new[] { false,true }) { using var image=Glyph.Bitmap(on,dark,32); image.Save(System.IO.Path.Combine(directory,$"tray-{(on ? "on" : "off")}-{theme}.png"),Drawing.Imaging.ImageFormat.Png); }
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
