# timeout: 300
# Install job for the factory pc-install runner (queued with pc-install-drop; the runner refuses while a game runs).
# Installs sb-build\out\ShadowBoost.exe (from scripts/pc-check.sh) after checking its sha256, keeps the previous exe as
# ShadowBoost.exe.prev, starts the new one with --tray (hidden, no focus), and records before/after evidence:
# status, settings, a primary-screen capture and the full gamma ramp read back from the primary display.
# Screen captures are taken before the gamma ramp, so the warmth shows in the ramp readback, not in the pixels.
# The runner passes no arguments: substitute __SHA__ before queueing.
$Sha = '__SHA__'; $Popover = $true
$ErrorActionPreference = 'Stop'
$stage = Join-Path $env:LOCALAPPDATA 'Temp\sb-build\out\ShadowBoost.exe'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\ShadowBoost'; $exe = Join-Path $dir 'ShadowBoost.exe'
$status = Join-Path $env:LOCALAPPDATA 'ShadowBoost\status.json'; $settings = Join-Path $env:APPDATA 'ShadowBoost\settings.json'
$evidence = Join-Path $env:LOCALAPPDATA ('Temp\sb-nightvision-' + (Get-Date -Format 'yyyyMMddTHHmmss'))
New-Item -ItemType Directory -Force $evidence | Out-Null
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SbScreen {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
    public static ushort[] Ramp() { var r = new ushort[768]; var dc = GetDC(IntPtr.Zero); try { if (!GetDeviceGammaRamp(dc, r)) return null; } finally { ReleaseDC(IntPtr.Zero, dc); } return r; }
    public static int[] Size() { var dc = GetDC(IntPtr.Zero); try { return new[] { GetDeviceCaps(dc, 118), GetDeviceCaps(dc, 117) }; } finally { ReleaseDC(IntPtr.Zero, dc); } }
}
'@
[SbScreen]::SetProcessDPIAware() | Out-Null
function Capture([string]$name, [int]$x = 0, [int]$y = 0, [int]$w = 0, [int]$h = 0) {
    $size = [SbScreen]::Size(); if ($w -eq 0) { $w = $size[0]; $h = $size[1] }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size); $bmp.Save((Join-Path $evidence "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    catch { Write-Output "capture $name failed: $($_.Exception.Message)" } finally { $g.Dispose(); $bmp.Dispose() }
}
function Ramp([string]$name) { $r = [SbScreen]::Ramp(); if ($r) { ConvertTo-Json -Compress @{ red = $r[0..255]; green = $r[256..511]; blue = $r[512..767] } | Set-Content (Join-Path $evidence "$name.json") -Encoding UTF8; return @($r[128], $r[384], $r[640]) } }
function Snap([string]$name) {
    foreach ($f in @(@($status, 'status'), @($settings, 'settings'))) { if (Test-Path $f[0]) { Copy-Item $f[0] (Join-Path $evidence "$($f[1])-$name.json") } }
    Capture "screen-$name"; $mid = Ramp "ramp-$name"; Write-Output "$name ramp mid R,G,B = $($mid -join ',')"
}
if ((Get-FileHash $stage -Algorithm SHA256).Hash -ne $Sha) { throw "staged exe sha256 differs from $Sha" }
Snap 'before'
# Stop the running copy through its own --quit (it resets gamma), then make sure no copy is left.
if (Get-Process ShadowBoost -ErrorAction SilentlyContinue) {
    $q = Start-Process -FilePath $exe -ArgumentList '--quit' -PassThru -WindowStyle Hidden; $q.WaitForExit(10000) | Out-Null
    $deadline = (Get-Date).AddSeconds(10); while ((Get-Process ShadowBoost -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Get-Process ShadowBoost -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" -or $_.Path -like '*ShadowBoost*.exe' } | Stop-Process -Force
}
# Atomic swap on the same volume: File.Replace keeps the old exe as .prev.
$new = Join-Path $dir 'ShadowBoost.exe.new'; Copy-Item -Force $stage $new
[System.IO.File]::Replace($new, $exe, (Join-Path $dir 'ShadowBoost.exe.prev'))
$app = Start-Process -FilePath $exe -ArgumentList '--tray' -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddSeconds(20)
do { Start-Sleep -Milliseconds 500; $s = if (Test-Path $status) { Get-Content $status -Raw | ConvertFrom-Json } } while ((-not $s -or $s.pid -ne $app.Id -or -not $s.warm) -and (Get-Date) -lt $deadline)
if (-not $s -or $s.pid -ne $app.Id -or -not $s.warm) { throw 'new app did not write a status with warmth' }
# A quick-toggle hold from before the install keeps the display out of the schedule; return to it.
Start-Process -FilePath $exe -ArgumentList @('--night', 'schedule') -Wait
Start-Sleep -Seconds 3
Snap 'after'
$procs = @(Get-Process ShadowBoost -ErrorAction SilentlyContinue)
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ShadowBoost -ErrorAction SilentlyContinue).ShadowBoost
Write-Output "installed sha256 $((Get-FileHash $exe -Algorithm SHA256).Hash); prev kept: $(Test-Path (Join-Path $dir 'ShadowBoost.exe.prev'))"
Write-Output "processes: $($procs.Count) ($($procs.Id -join ',')); run key: $run"
$s = Get-Content $status -Raw | ConvertFrom-Json
Write-Output "status: boost enabled=$($s.enabled) warm on=$($s.warm.on) kelvin=$($s.warm.kelvin) appliedKelvin=$($s.warm.appliedKelvin) held=$($s.warm.held) next=$($s.warm.nextChange) brightness=$($s.brightness) rampCheck=$($s.rampCheck) expectedMid=$($s.expectedMid -join ',') rampMid=$($s.rampMid -join ',')"
# The taskbar pin followed the legacy rename; point it and any Start Menu shortcut at the installed exe (backups kept),
# then read every target back.
$shell = New-Object -ComObject WScript.Shell
$links = @(Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\ShadowBoost.lnk')
$links += @(Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter '*ShadowBoost*.lnk' -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($path in ($links | Where-Object { Test-Path $_ } | Select-Object -Unique)) {
    $lnk = $shell.CreateShortcut($path)
    if ($lnk.TargetPath -ne $exe) { Copy-Item $path (Join-Path $evidence ((Split-Path $path -Leaf) + '.' + [guid]::NewGuid().ToString('N').Substring(0,6) + '.bak')); $was = $lnk.TargetPath; $lnk.TargetPath = $exe; $lnk.Arguments = ''; $lnk.WorkingDirectory = $dir; $lnk.Save(); Write-Output "shortcut $path : $was -> $exe" }
    Write-Output "shortcut read-back $path -> $($shell.CreateShortcut($path).TargetPath)"
}
$rendered = Join-Path $evidence 'rendered'
Start-Process -FilePath $exe -ArgumentList @('--screenshot', ('"' + $rendered + '"')) -Wait
$deadline = (Get-Date).AddSeconds(20)
while (@(Get-ChildItem $rendered -Filter '*.png' -ErrorAction SilentlyContinue).Count -lt 18 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
Write-Output "rendered views: $(@(Get-ChildItem $rendered -Filter '*.png' -ErrorAction SilentlyContinue).Count) PNGs"
if ($Popover) {
    # The real flyout on the real screen, bottom-right above the taskbar, then hidden again.
    Start-Process -FilePath $exe -ArgumentList '--flyout' -Wait; Start-Sleep -Milliseconds 1500
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    Capture 'popover-live' ($wa.Right - 560) ($wa.Bottom - 900) 560 900
    Start-Process -FilePath $exe -ArgumentList '--flyout' -Wait
}
Write-Output "EVIDENCE=$evidence"
