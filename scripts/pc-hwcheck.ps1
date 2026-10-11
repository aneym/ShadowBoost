# Hardware check body for scripts/pc-run.sh: drives the INSTALLED app's monitor controls over DDC/CI from the
# hardware minimum to maximum, reads each value back through the app's status and through an independent DDC read,
# then restores every value and app setting it touched. Runs only after the install; changes the display briefly.
$exe = Join-Path $env:LOCALAPPDATA 'Programs\ShadowBoost\ShadowBoost.exe'
$status = Join-Path $env:LOCALAPPDATA 'ShadowBoost\status.json'; $settingsFile = Join-Path $env:APPDATA 'ShadowBoost\settings.json'
$evidence = Join-Path $env:LOCALAPPDATA ('Temp\sb-hwcheck-' + (Get-Date -Format 'yyyyMMddTHHmmss')); New-Item -ItemType Directory -Force $evidence | Out-Null
function App([string[]]$a) { $p = Start-Process -FilePath $exe -ArgumentList $a -WindowStyle Hidden -PassThru; if (-not $p.WaitForExit(10000)) { throw "command $a timed out" } }
function Mon { (Get-Content $status -Raw | ConvertFrom-Json).monitors | Select-Object -First 1 }
function Await([string]$what, [scriptblock]$ok) { $until = (Get-Date).AddSeconds(15); do { Start-Sleep -Milliseconds 400; $m = Mon; if ($m -and (& $ok $m)) { return $m } } while ((Get-Date) -lt $until); throw "timeout waiting for $what" }
$fails = 0
function Leg([string]$name, [string[]]$a, [scriptblock]$ok, [byte[]]$codes) {
    App $a; try { $m = Await $name $ok; $r = 'PASS' } catch { $m = Mon; $r = 'FAIL'; $script:fails++ }
    $line = "$r $name app=$(ConvertTo-Json -Compress -Depth 6 $m)"; Write-Output $line; Add-Content (Join-Path $evidence 'hwcheck.log') $line; Vcp $name $codes
}
if (-not (Get-Process ShadowBoost -ErrorAction SilentlyContinue)) { throw 'installed ShadowBoost is not running' }
Copy-Item $settingsFile (Join-Path $evidence 'settings-before.json'); Copy-Item $status (Join-Path $evidence 'status-before.json')
$before = Mon; if (-not $before) { throw 'status has no hardware monitor' }
Write-Output "monitor: $($before.model) key=$($before.key) mccs=$($before.mccs) codes=$($before.codes)"
Vcp 'before' @(0x10, 0x12, 0x14, 0x16, 0x18, 0x1A, 0x0C, 0x72)
$saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
$b0 = $before.brightness.percent
try {
    Leg 'brightness-min' @('--brightness', '0') { param($m) $m.brightness.readback -eq 0 } @(0x10)
    Leg 'brightness-max' @('--brightness', '100') { param($m) $m.brightness.readback -eq $m.brightness.max } @(0x10)
    if ($null -ne $before.contrast) {
        $c0 = $before.contrast.percent
        Leg 'contrast-min' @('--contrast', '0') { param($m) $m.contrast.readback -eq 0 } @(0x12)
        Leg 'contrast-max' @('--contrast', '100') { param($m) $m.contrast.readback -eq $m.contrast.max } @(0x12)
    }
    if ($before.warmth -eq 'monitor') {
        Leg 'warmth-6500K' @('--night', 'on', '--warmth', '6500') { param($m) ($m.gains.readback -join ',') -eq ($m.gains.neutral -join ',') } @(0x14, 0x16, 0x18, 0x1A)
        Leg 'warmth-2700K' @('--night', 'on', '--warmth', '2700') { param($m) $g = $m.gains.readback; $g[2] -lt $m.gains.neutral[2] -and ($g -join ',') -eq ($m.gains.target -join ',') } @(0x14, 0x16, 0x18, 0x1A)
    }
} finally {
    # Restore: his saved warmth target and schedule hold, then his hardware values.
    App @('--warmth', "$($saved.warmKelvin)"); App @('--night', 'schedule'); if ($saved.warmHold) { Copy-Item (Join-Path $evidence 'settings-before.json') $settingsFile -Force; Start-Sleep -Seconds 2 }
    if ($null -ne $before.contrast) { App @('--contrast', "$c0") }
    App @('--brightness', "$b0"); Start-Sleep -Seconds 3
    Vcp 'restored' @(0x10, 0x12, 0x14, 0x16, 0x18, 0x1A, 0x0C, 0x72)
    Copy-Item $status (Join-Path $evidence 'status-after.json'); Copy-Item $settingsFile (Join-Path $evidence 'settings-after.json')
}
Write-Output "hwcheck failures: $fails"
if ($fails) { exit 1 }
