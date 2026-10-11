# ShadowBoost

A Windows 11 tray app for evening screens, in the spirit of Night Shift and
f.lux: warmth (colour temperature), dimming and a brightness slider on every
attached display, through each monitor's gamma ramp. It can also lift dark
detail (shadow boost).

Click the tray ring for the flyout: the warmth quick toggle, the Warmth and
Brightness sliders, and the shadow boost switch. Right-click for Open, the
quick toggle, shadow boost and Quit. Ctrl+Shift+N toggles warmth;
Ctrl+Shift+D toggles shadow boost.

Warmth runs from 6500 K (neutral) to 2700 K and is independent of brightness.
Warmth and dimming each have their own schedule: Off, Sunset (sunset to
sunrise at the saved location, computed locally) or Custom hours. Both fade
over the Fade time (Off, 20 or 60 minutes). The quick toggle holds until the
schedule's next change (warm until sunrise, or off until the window ends),
survives a restart, and then the schedule takes over. Dimming multiplies the
saved brightness and never rewrites it. The location comes from typed
coordinates or, on request, from the position Windows already knows.

The Brightness slider drives the monitor's own brightness over DDC/CI (VCP
0x10) across its full 0-100 % range when the monitor answers. It is saved per
monitor and applied at start, so it sticks across restarts and reboots; the
first run adopts the monitor's current value instead of changing it. Dimming
lowers the monitor's brightness, and changes made with the monitor's buttons are
picked up within about 30 seconds and kept as the saved value. Warmth and shadow
boost still use the gamma ramp. Without DDC/CI (or with `--dry-gamma`) the slider
falls back to scaling the gamma ramp (50-130 %). Exiting leaves the monitor's
brightness alone. `status.json` lists each monitor, its capabilities and the
brightness source (`monitor` or `gamma`).

Shadow boost is **off on every start**, including sign-in; warmth, dimming and
brightness resume from the saved settings.

Shadow boost levels Gentle, Balanced and Strong use editable black lift,
gamma, contrast and brightness curves. The schedule follows local sunrise/sunset (New York by
default), or fixed Day/Evening times, with an editable Night start. Picking a
level holds it until the next boundary; Back to schedule clears the hold.
Settings include sign-in startup.
The UI and monochrome tray glyph follow Windows light/dark themes live.

## Build

Requires .NET 8 SDK. On Windows:

```powershell
dotnet test ShadowBoost.sln -c Release
dotnet publish src/ShadowBoost.App/ShadowBoost.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

On macOS the core tests run with `dotnet test tests/ShadowBoost.Core.Tests`, and the
same publish command cross-builds the Windows exe (the project sets
EnableWindowsTargeting). From Studio, `scripts/pc-check.sh all && scripts/pc-e2e.sh` builds/tests on ssh
host `pc` and exercises an isolated, hidden, dry-gamma instance. It does not
change the installed app, gamma, sign-in registration or tray promotion.

## Commands

`ShadowBoost.exe --tray` starts hidden (the sign-in command). `--show` opens
schedule/settings; `--flyout` toggles the flyout. `--night on|off|toggle|schedule`
drives the warmth quick toggle, `--warmth <kelvin>` and `--brightness <percent>`
set the sliders (`--brightness` is 0-100 % of the monitor when DDC/CI works). `--on`, `--off`, `--level gentle|balanced|strong` and
`--schedule` drive shadow boost, and `--quit` stops the running instance. A second launch forwards commands through a per-user/data-dir pipe.

`--home <directory>` isolates settings and status. `--dry-gamma` is a startup
flag that computes ramps but makes no gamma or DDC/CI writes.
Either isolation flag suppresses Run/tray-pinning writes. For checks, always
use both and `--tray`, never `--show` or `--flyout`.

`--screenshot <directory>` renders hidden 2x light/dark views (the live
flyout, warm, daylight and boost flyouts, main, shadow boost and fine-tune) and
32px on/off tray glyphs (18 PNGs), without opening a window.

Settings live in `%APPDATA%\ShadowBoost\settings.json`; status is atomically
written to `%LOCALAPPDATA%\ShadowBoost\status.json`. With `--home`, both are in
that directory. Existing `%APPDATA%\ShadowBoost.txt` is imported on first run
only without `--home`. External settings edits reload after a short debounce.
Status includes the warmth and dimming state and the expected and read-back
red, green and blue ramp midpoints. Gamma resets to identity at exit; games resetting ramps and
resume/display/time changes trigger reapplication while enabled.
