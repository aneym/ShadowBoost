#!/usr/bin/env bash
# One isolated vertical scenario. Never interacts with the installed executable.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp:?}"' EXIT
cat > "$tmp/e2e.ps1" <<'PS'
$ErrorActionPreference='Stop'
$exe='C:\Users\aneym\AppData\Local\Temp\sb-build\out\ShadowBoost.exe'
$homeDir=Join-Path $env:TEMP ('sb-e2e-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $homeDir | Out-Null
$statusFile=Join-Path $homeDir 'status.json'
$taskName='ShadowBoost-e2e-'+[guid]::NewGuid().ToString('N')
$taskUsed=$false
$launchMode='ssh Start-Process'
$ownedPid=$null
function Status { if (Test-Path $statusFile) { return Get-Content $statusFile -Raw | ConvertFrom-Json }; return $null }
function Wait-State([scriptblock]$predicate,[int]$seconds=15) { $until=(Get-Date).AddSeconds($seconds); do { $s=Status; if ($s -and (& $predicate $s)) { return $s }; Start-Sleep -Milliseconds 300 } while ((Get-Date) -lt $until); throw 'state timeout' }
function Command([string[]]$commands) { $p=Start-Process -FilePath $exe -ArgumentList (@('--home',('"'+$homeDir+'"'),'--dry-gamma')+$commands) -PassThru; if (-not $p.WaitForExit(10000)) { Stop-Process -Id $p.Id; throw 'command timeout' }; if ($p.ExitCode -ne 0) { throw "command exit $($p.ExitCode)" } }
function Cleanup {
    $s=Status
    if ($s -and $s.dataDir -eq $homeDir) {
        $script:ownedPid=[int]$s.pid
        if (Get-Process -Id $ownedPid -ErrorAction SilentlyContinue) {
            try { Command @('--quit') } catch { Write-Output "cleanup: $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 600
            $p=Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
            if ($p -and $p.Path -eq $exe) { Stop-Process -Id $ownedPid }
        }
    }
    if ($script:taskUsed) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false; $script:taskUsed=$false }
}
function Start-App {
    if (Test-Path $statusFile) { Remove-Item -LiteralPath $statusFile }
    $p=Start-Process -FilePath $exe -ArgumentList @('--home',('"'+$homeDir+'"'),'--dry-gamma','--tray') -PassThru
    $script:ownedPid=$p.Id
    try { return Wait-State { param($s) $s.dryGamma -eq $true } 8 } catch {
        $live=Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if ($live -and $live.Path -eq $exe) { Stop-Process -Id $p.Id }
        $action=New-ScheduledTaskAction -Execute $exe -Argument ('--home "'+$homeDir+'" --dry-gamma --tray')
        $principal=New-ScheduledTaskPrincipal -UserId 'aneym' -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
        $script:taskUsed=$true
        Start-ScheduledTask -TaskName $taskName
        $state=Wait-State { param($s) $s.dryGamma -eq $true } 15
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        $script:taskUsed=$false
        $script:launchMode='scheduled task (Interactive)'
        return $state
    }
}
$leg='L1'
try {
    $now=Get-Date
    $evening=$now.Date.AddHours($now.Hour).AddMinutes($now.Minute+2)
    $settings=@{followSun=$false;dayStart=$now.AddHours(-2).ToString('HH:mm');eveningStart=$evening.ToString('HH:mm');nightStart=$now.AddHours(3).ToString('HH:mm');userBrightness=85;periodLevels=@{Day='Strong';Evening='Balanced';Night='Gentle'}}
    $settings | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $homeDir 'settings.json') -Encoding UTF8
    $s=Start-App
    Write-Output ('Launch path: '+$launchMode)
    $ownedPid=[int]$s.pid
    if ($s.enabled -or $s.rampCheck -ne 'dry' -or $null -ne $s.appliedCurve) { throw 'startup not disabled/dry/null' }
    Write-Output 'PASS L1 startup: disabled, dry, appliedCurve null'
    $leg='L2'; Command @('--on'); $s=Wait-State { param($s) $s.enabled -and $s.period -eq 'Day' -and $s.activeLevel -eq 'Strong' -and -not $s.isOverride }; Write-Output 'PASS L2 on: Day, Strong, scheduled'
    $leg='L3'; Command @('--level','gentle'); $s=Wait-State { param($s) $s.activeLevel -eq 'Gentle' -and $s.isOverride }
    if ([datetimeoffset]::Parse($s.nextChange).LocalDateTime -ne $evening) { throw 'override boundary differs from eveningStart' }
    Write-Output 'PASS L3 gentle: override held until eveningStart'
    $leg='L4'; Command @('--screenshot',('"'+(Join-Path $homeDir 'shots')+'"'))
    $shotDeadline=(Get-Date).AddSeconds(20)
    do { $shots=@(Get-ChildItem (Join-Path $homeDir 'shots') -Filter '*.png' -ErrorAction SilentlyContinue); if ($shots.Count -eq 14) { break }; Start-Sleep -Milliseconds 300 } while ((Get-Date) -lt $shotDeadline)
    if ($shots.Count -ne 14 -or @($shots | Where-Object Length -eq 0).Count) { throw "expected 14 nonempty PNGs, got $($shots.Count)" }
    Write-Output 'PASS L4 screenshots: 14 nonempty PNGs'
    Write-Output ('SHOTS='+$homeDir.Replace('\','/')+'/shots')
    $leg='L4b'
    $fixedHome=$homeDir; $fixedStatus=$statusFile; $fixedPid=$ownedPid
    try {
        $homeDir=Join-Path $env:TEMP ('sb-sun-e2e-'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $homeDir | Out-Null
        $statusFile=Join-Path $homeDir 'status.json'
        @{followSun=$true;placeName='New York';latitude=40.7128;longitude=-74.0060} | ConvertTo-Json | Set-Content (Join-Path $homeDir 'settings.json') -Encoding UTF8
        $s=Start-App; $ownedPid=[int]$s.pid
        if ($s.enabled -or -not $s.dryGamma -or $s.dataDir -ne $homeDir) { throw 'solar instance not isolated/off/dry' }
        Command @('--screenshot',('"'+(Join-Path $homeDir 'shots')+'"'))
        $shotDeadline=(Get-Date).AddSeconds(20)
        do { $shots=@(Get-ChildItem (Join-Path $homeDir 'shots') -Filter '*.png' -ErrorAction SilentlyContinue); if ($shots.Count -eq 14) { break }; Start-Sleep -Milliseconds 300 } while ((Get-Date) -lt $shotDeadline)
        if ($shots.Count -ne 14 -or @($shots | Where-Object Length -eq 0).Count) { throw 'solar screenshots incomplete' }
        Write-Output ('Solar launch path: '+$launchMode)
        Write-Output 'PASS L4b solar screenshots: isolated New York, 14 nonempty PNGs'
        Write-Output ('SUNSHOTS='+$homeDir.Replace('\','/')+'/shots')
    } finally {
        Cleanup
        $homeDir=$fixedHome; $statusFile=$fixedStatus; $ownedPid=$fixedPid
    }
    $leg='L5'; $deadline=$evening.AddSeconds(25)
    while ((Get-Date) -lt $deadline) { $s=Status; Start-Sleep -Seconds ([Math]::Min(10,[Math]::Max(1,($deadline-(Get-Date)).TotalSeconds))) }
    $s=Status
    if ($s.period -ne 'Evening' -or $s.activeLevel -ne 'Balanced' -or $s.isOverride) { throw 'schedule did not take over at evening boundary' }
    if ($s.userBrightness -ne 85) { throw 'boundary changed user brightness' }
    Write-Output 'PASS L5 boundary: Evening, Balanced, override cleared'
    $leg='L6'; Command @('--off'); $s=Wait-State { param($s) -not $s.enabled }; Write-Output 'PASS L6 off: disabled'
    $leg='L7'; Cleanup; $s=Start-App; if ($s -is [array]) { $s=$s[-1] }; $ownedPid=[int]$s.pid
    if ($s.enabled) { throw 'restart enabled' }
    Write-Output 'PASS L7 restart: disabled'; Cleanup
    $leg='L8'
    $p=Start-Process -FilePath $exe -ArgumentList @('--home',('"'+$homeDir+'"'),'--dry-gamma','--off') -PassThru
    if (-not $p.WaitForExit(10000)) { Stop-Process -Id $p.Id; throw 'orphan command stayed alive' }
    if ($p.ExitCode -ne 3) { throw "orphan command exit $($p.ExitCode), expected 3" }
    $processes=@(Get-CimInstance Win32_Process -Filter "Name='ShadowBoost.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($homeDir) })
    if ($processes.Count) { throw 'orphan command started a primary' }
    Write-Output 'PASS L8 orphan off: exit 3, no primary process'
    $leg='L9'; $s=Start-App; $ownedPid=[int]$s.pid; Command @('--off')
    $s=Wait-State { param($s) -not $s.enabled }
    $processes=@(Get-CimInstance Win32_Process -Filter "Name='ShadowBoost.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($homeDir) })
    if ($processes.Count -ne 1 -or $processes[0].ProcessId -ne $ownedPid) { throw 'second off created another primary' }
    Write-Output 'PASS L9 second off: exactly one isolated primary'
    $leg='L10'
    if ($s.userBrightness -ne 85 -or $s.enabled) { throw 'brightness did not survive disabled restart' }
    Write-Output 'PASS L10 brightness: 85 persisted through boundary and disabled restart'
    Cleanup
} catch { Write-Output "FAIL $leg`: $($_.Exception.Message)"; exit 1 }
finally { Cleanup }
PS
remote_script="sb-e2e-$(basename "$tmp").ps1"
scp -q "$tmp/e2e.ps1" "pc:$remote_script"
ssh pc "powershell -NoProfile -File $remote_script" | tee "$tmp/result"
shots="$(sed -n 's/^SHOTS=//p' "$tmp/result" | tr -d '\r')"
[[ -n "$shots" ]]
mkdir -p "$root/out/shots"
scp -q "pc:$shots/*.png" "$root/out/shots/"
echo "Screenshots copied to $root/out/shots/"
sunshots="$(sed -n 's/^SUNSHOTS=//p' "$tmp/result" | tr -d '\r')"
[[ -n "$sunshots" ]]
mkdir -p "$root/out/shots-sun"
scp -q "pc:$sunshots/*.png" "$root/out/shots-sun/"
echo "Solar screenshots copied to $root/out/shots-sun/"
