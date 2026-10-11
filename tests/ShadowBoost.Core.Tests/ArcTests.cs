using ShadowBoost.Core;
using Xunit;

namespace ShadowBoost.Core.Tests;

// Unit tests by the test-audit bar: SceneTracker, the luma-to-curve mapping and Luma.Mean are pure algorithms with
// timing and boundary edge cases (flash rejection, agreement delay, fades, HUD exclusion) that only show on a live game otherwise.
public sealed class ArcTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 11, 21, 0, 0, TimeSpan.FromHours(-4));

    // Guards the scene-to-look table: day/dusk/night anchors, clamping outside the luma range, and linear blending between anchors.
    [Theory]
    [InlineData(255, 3, 90, 100, 100)]
    [InlineData(90, 3, 90, 100, 100)]
    [InlineData(75, 6, 84, 102, 100)]
    [InlineData(60, 8, 78, 104, 100)]
    [InlineData(45, 11, 70, 106, 102)]
    [InlineData(30, 14, 62, 108, 104)]
    [InlineData(0, 14, 62, 108, 104)]
    public void LumaPicksTheCurve(double luma, int lift, int gamma, int contrast, int brightness)
    {
        var arc = new ArcSettings();
        Assert.Equal(new Curve(lift, gamma, contrast, brightness), arc.CurveAt(arc.Darkness(luma)));
    }

    // Guards the hardware-only look: pins move only the monitor brightness, from the room level (Auto, Day) to the panel maximum (Night).
    [Theory]
    [InlineData(Period.Day, ArcPin.Night, 100)]
    [InlineData(Period.Night, ArcPin.Day, 80)]
    [InlineData(Period.Night, ArcPin.Dusk, 90)]
    [InlineData(Period.Night, ArcPin.Night, 100)]
    [InlineData(Period.Evening, ArcPin.Dusk, 95)]
    public void PinsMoveMonitorBrightness(Period period, ArcPin pin, int percent)
    {
        Assert.Equal(percent, new ArcSettings().HardwareBrightness(period, ArcSettings.PinDarkness(pin)!.Value));
    }

    // Guards flash rejection: one bright frame (muzzle flash, explosion, a menu) inside a dark scene never moves the look.
    [Fact]
    public void OneBrightSampleDoesNotMoveTheLook()
    {
        var scene = new SceneTracker(1);
        for (int i = 0; i < 10; i++) scene.Add(T0.AddSeconds(i * 2), i == 4 ? 0 : 1);
        Assert.Equal(1, scene.Target);
        Assert.Equal(1, scene.Current(T0.AddSeconds(20)));
    }

    // Guards the agreement delay and the fade: a sustained change is adopted only after the window median moves
    // and holds for 3 s, then the shown darkness fades over 1.5 s from where it was.
    [Fact]
    public void SustainedChangeSettlesThenFades()
    {
        var scene = new SceneTracker(1);
        for (int i = 0; i <= 4; i++) scene.Add(T0.AddSeconds(i * 2), 1);           // night until t=8
        scene.Add(T0.AddSeconds(10), 0);                                           // window holds 1,1,1,1,0: median 1
        scene.Add(T0.AddSeconds(12), 0);                                           // 1,1,1,0,0: still 1
        scene.Add(T0.AddSeconds(14), 0);                                           // 1,1,0,0,0: median 0, candidate starts
        Assert.Equal(1, scene.Target);
        scene.Add(T0.AddSeconds(16), 0);                                           // held 2 s: not yet
        Assert.Equal(1, scene.Target);
        scene.Add(T0.AddSeconds(18), 0);                                           // held 4 s: settles
        Assert.Equal(0, scene.Target);
        Assert.Equal(1, scene.Current(T0.AddSeconds(18)), 3);
        Assert.Equal(0.5, scene.Current(T0.AddSeconds(18.75)), 3);
        Assert.Equal(0, scene.Current(T0.AddSeconds(19.5)), 3);
    }

    // Guards step quantisation: drifting a few percent inside one 0.1 step never starts a change.
    [Fact]
    public void SmallDriftStaysOnItsStep()
    {
        var scene = new SceneTracker(0.5);
        for (int i = 0; i < 20; i++) scene.Add(T0.AddSeconds(i), 0.5 + (i % 2 == 0 ? 0.04 : -0.04));
        Assert.Equal(0.5, scene.Target);
    }

    // Guards HUD exclusion: a bright frame border (HUD, compass, minimap) does not count, only the centre 70 %.
    [Fact]
    public void LumaReadsTheCentreOnly()
    {
        int w = 100, h = 60, stride = w * 4;
        var frame = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool centre = x >= 15 && x < 85 && y >= 9 && y < 51;
                byte v = centre ? (byte)40 : (byte)255;
                frame[y * stride + x * 4] = v; frame[y * stride + x * 4 + 1] = v; frame[y * stride + x * 4 + 2] = v; frame[y * stride + x * 4 + 3] = 255;
            }
        Assert.Equal(40, Luma.Mean(frame, w, h, stride), 1);
    }

    // Guards persistence: the manual pick sticks across restarts, and a settings file from before the ARC profile loads with its defaults.
    [Fact]
    public void PinSurvivesARestartAndOldFilesGetDefaults()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var saved = new Settings(); saved.Arc.Pin = ArcPin.Night; saved.Arc.Processes = new() { "PioneerGame.exe" }; saved.Save(path);
            var loaded = Settings.Load(path);
            Assert.Equal(ArcPin.Night, loaded.Arc.Pin);
            Assert.Equal(new[] { "PioneerGame" }, loaded.Arc.Processes);

            File.WriteAllText(path, "{\"userBrightness\": 108, \"brightnessLevel\": 131}");
            var old = Settings.Load(path);
            Assert.True(old.Arc.Enabled);
            Assert.False(old.Arc.SoftwareLift);
            Assert.Equal(ArcPin.Auto, old.Arc.Pin);
            Assert.Equal(new[] { "PioneerGame" }, old.Arc.Processes);
            Assert.Equal(80, old.Arc.RoomBrightness[Period.Night]);
        }
        finally { dir.Delete(true); }
    }
}
