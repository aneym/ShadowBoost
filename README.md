# ShadowBoost

A Windows 11 tray app that gently lifts dark detail on every attached display.
Boost is **off on every start**, including sign-in. Ctrl+Shift+D toggles it; click
the tray ring for the flyout, or right-click for levels, settings and Quit.

Gentle, Balanced and Strong use editable black lift, gamma, contrast and
brightness curves. The schedule follows local sunrise/sunset (New York by
default), or fixed Day/Evening times, with an editable Night start. Picking a
level holds it until the next boundary; Back to schedule clears the hold.
Settings include sign-in startup and optional DDC/CI monitor brightness.
The UI and monochrome tray glyph follow Windows light/dark themes live.

## Build

Requires .NET 8 SDK on Windows:

```powershell
dotnet test ShadowBoost.sln -c Release
dotnet publish src/ShadowBoost.App/ShadowBoost.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

From Studio, `scripts/pc-check.sh all && scripts/pc-e2e.sh` builds/tests on ssh
host `pc` and exercises an isolated, hidden, dry-gamma instance. It does not
change the installed app, gamma, sign-in registration or tray promotion.

## Commands

`ShadowBoost.exe --tray` starts hidden (the sign-in command). `--show` opens
schedule/settings; `--flyout` toggles the flyout. `--on`, `--off`,
`--level gentle|balanced|strong`, `--schedule` and `--quit` control the running
instance. A second launch forwards commands through a per-user/data-dir pipe.

`--home <directory>` isolates settings and status. `--dry-gamma` is a startup
flag that computes ramps but makes no gamma or monitor-brightness writes.
Either isolation flag suppresses Run/tray-pinning writes. For checks, always
use both and `--tray`, never `--show` or `--flyout`.

`--screenshot <directory>` renders hidden 2x light/dark flyout, main and
fine-tune views, additional off/scheduled flyouts, and 32px on/off tray glyphs
(14 PNGs), without opening a window.

Settings live in `%APPDATA%\ShadowBoost\settings.json`; status is atomically
written to `%LOCALAPPDATA%\ShadowBoost\status.json`. With `--home`, both are in
that directory. Existing `%APPDATA%\ShadowBoost.txt` is imported on first run
only without `--home`. External settings edits reload after a short debounce.
Gamma resets to identity at startup and exit; games resetting ramps and
resume/display/time changes trigger reapplication while enabled. Saved
monitor brightness is never applied at startup.
