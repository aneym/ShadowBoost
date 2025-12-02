# ShadowBoost

A lightweight Windows utility to enhance visibility in dark games by adjusting gamma, black levels, and contrast in real-time.

![Windows](https://img.shields.io/badge/platform-Windows-blue)

## Features

- **Black Lift** - Raises black levels to reveal hidden shadows
- **Gamma Adjustment** - Brighten or darken shadow areas
- **Contrast Control** - Adjust difference between dark and light
- **Brightness** - Overall screen brightness adjustment
- **Hardware DDC/CI** - Direct monitor brightness control (if supported)
- **Presets** - Balanced, Aggressive, and Subtle presets
- **Hotkey** - Toggle with `Ctrl+Shift+D`
- **System Tray** - Runs minimized in the background
- **Start with Windows** - Optional auto-start on boot

## Download

Download the latest `ShadowBoost.exe` from the [Releases](../../releases) page.

## Usage

1. Run `ShadowBoost.exe`
2. Adjust sliders to your preference or click a preset button
3. Use `Ctrl+Shift+D` to quickly toggle the effect on/off
4. Close the window to minimize to system tray

## Recommended Settings

| Preset | Black Lift | Gamma | Contrast | Brightness | Use Case |
|--------|------------|-------|----------|------------|----------|
| **Balanced** | 10% | 0.70 | 105% | 100% | Good visibility without looking washed out |
| **Aggressive** | 18% | 0.55 | 115% | 105% | Maximum visibility in very dark games |
| **Subtle** | 4% | 0.82 | 100% | 100% | Minor enhancement |

## Building from Source

Requires .NET Framework 4.0+

```cmd
csc /target:winexe /out:ShadowBoost.exe ShadowBoost.cs
```

## How It Works

ShadowBoost uses the Windows GDI `SetDeviceGammaRamp` API to adjust the display's gamma lookup table. This affects all colors displayed on screen, making dark areas more visible without modifying game files.

The processing order is:
1. **Black Lift** - Raises the floor for dark pixels
2. **Gamma** - Applies power curve (lower = brighter shadows)
3. **Contrast** - Adjusts around midpoint
4. **Brightness** - Final multiplier

## License

MIT License - feel free to use and modify.
