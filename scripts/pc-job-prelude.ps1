# timeout: __TIMEOUT__
# Prelude for every ShadowBoost job queued through the factory pc-install runner (scripts/pc-run.sh glues it to a body).
# Pulls the exe staged on pc-wsl into sb-build\out after checking its sha256, runs the body, then ships $evidence
# (if the body set it) back to pc-wsl as evidence.zip. Runs hidden and Limited; no ssh to the Windows side.
param([string]$Id = '__ID__', [string]$Sha = '__SHA__', [string]$Wsl = 'C:\Windows\System32\wsl.exe')
$ErrorActionPreference = 'Stop'
$stageDir = "/home/jobs/pc-install/stage/shadowboost/$Id"; $outDir = "/home/jobs/pc-install/out/$Id"
function Wsl-Process([string]$Arguments, [byte[]]$InputBytes) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Wsl; $info.Arguments = "-d rails -u jobs --exec $Arguments"
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.RedirectStandardInput = $true
    $p = [System.Diagnostics.Process]::Start($info)
    $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr = $p.StandardError.ReadToEndAsync()
    if ($null -ne $InputBytes) { $p.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length) }
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(120000)) { $p.Kill(); throw 'WSL transfer timed out' }
    if ($p.ExitCode -ne 0) { throw "WSL transfer failed: $($stderr.Result)" }
    return $stdout.Result
}
function Put-File([string]$Name, [byte[]]$Bytes) { Wsl-Process "/bin/sh -c `"mkdir -p $outDir; cat > $outDir/$Name`"" $Bytes | Out-Null }
# Every job aborts while a game (or League's client) runs; bodies call Assert-NoGame again right before they touch the display or the installed app.
function Assert-NoGame { foreach ($game in 'PioneerGame', 'LeagueClient', 'League of Legends', 'VALORANT-Win64-Shipping', 'cs2', 'dota2') { if (Get-Process -Name $game -ErrorAction SilentlyContinue) { throw "game running: $game.exe; aborting before any change" } } }
Assert-NoGame
$buildOut = Join-Path $env:LOCALAPPDATA 'Temp\sb-build\out'; New-Item -ItemType Directory -Force $buildOut | Out-Null
$stagedExe = Join-Path $buildOut 'ShadowBoost.exe'
[IO.File]::WriteAllBytes($stagedExe, [Convert]::FromBase64String((Wsl-Process "/bin/cat $stageDir/exe.b64" $null).Trim()))
if ((Get-FileHash $stagedExe -Algorithm SHA256).Hash -ne $Sha) { throw "staged exe sha256 differs from $Sha" }
Write-Output "staged $stagedExe sha256 $Sha"
$evidence = $null
# Direct DDC/CI reads of the primary monitor, independent of the app, for before/after evidence.
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Threading;
public static class SbVcp {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PM { public IntPtr H; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string D; }
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PT p, uint flags);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, out uint n);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
    public static string Read(byte[] codes) {
        uint n; var m = MonitorFromPoint(new PT(), 1); if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m, out n) || n == 0) return "no physical monitor";
        var a = new PM[n]; if (!GetPhysicalMonitorsFromHMONITOR(m, n, a)) return "GetPhysicalMonitors failed";
        var s = "";
        try { foreach (var c in codes) { uint t, cur = 0, max = 0; bool ok = false; for (int i = 0; i < 3 && !ok; i++) { ok = GetVCPFeatureAndVCPFeatureReply(a[0].H, c, out t, out cur, out max); if (!ok) Thread.Sleep(60); } s += string.Format("{0:X2}={1} ", c, ok ? cur + "/" + max : "fail"); Thread.Sleep(60); } }
        finally { foreach (var p in a) DestroyPhysicalMonitor(p.H); }
        return s.Trim();
    }
}
'@
function Vcp([string]$label, [byte[]]$codes = @(0x10, 0x12, 0x14, 0x16, 0x18, 0x1A)) { $line = "VCP $label $((Get-Date).ToUniversalTime().ToString('HH:mm:ss'))Z $([SbVcp]::Read($codes))"; Write-Output $line; if ($evidence) { Add-Content (Join-Path $evidence 'vcp.log') $line } }
try {
# __BODY__
} finally {
    if ($evidence -and (Test-Path $evidence)) {
        $zip = "$evidence.zip"; if (Test-Path $zip) { Remove-Item $zip -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($evidence, $zip)
        Put-File 'evidence.zip' ([IO.File]::ReadAllBytes($zip)); Remove-Item $zip -Force
        Write-Output "EVIDENCE_OUT=$outDir/evidence.zip"
    }
}
