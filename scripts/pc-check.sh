#!/usr/bin/env bash
# Build and test on the Windows PC (ssh host "pc"; Studio has no .NET SDK).
# Usage: scripts/pc-check.sh [test|publish|all]   (default: all)
# Copies this worktree (no .git/bin/obj) to %LOCALAPPDATA%\Temp\sb-build on the PC,
# runs `dotnet test` on the solution, then publishes the app to sb-build\out.
set -euo pipefail
mode="${1:-all}"
root="$(cd "$(dirname "$0")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp:?}"' EXIT
COPYFILE_DISABLE=1 tar -C "$root" --exclude .git --exclude bin --exclude obj --exclude out -czf "$tmp/src.tgz" .
scp -q "$tmp/src.tgz" pc:sb-src.tgz
remote_build='C:\Users\aneym\AppData\Local\Temp\sb-build'
ps="\$ErrorActionPreference='Stop'; \$b='$remote_build';
if (Test-Path \$b) { Get-ChildItem \$b -Exclude out | Remove-Item -Recurse -Force };
New-Item -ItemType Directory -Force \$b | Out-Null; tar -xzf \$HOME\\sb-src.tgz -C \$b; Set-Location \$b;"
if [[ "$mode" == "test" || "$mode" == "all" ]]; then
  ps="$ps dotnet test ShadowBoost.sln -c Release --nologo -v q; if (\$LASTEXITCODE) { exit \$LASTEXITCODE };"
fi
if [[ "$mode" == "publish" || "$mode" == "all" ]]; then
  ps="$ps dotnet publish src/ShadowBoost.App/ShadowBoost.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o \$b\\out --nologo -v q; if (\$LASTEXITCODE) { exit \$LASTEXITCODE }; Get-Item \$b\\out\\ShadowBoost.exe | Select FullName,Length,LastWriteTime | Format-List;"
fi
ps="${ps//$'\n'/ }"
echo "$ps" | grep -q dotnet || { echo "no build step selected" >&2; exit 2; }
timeout 260 ssh pc "powershell -NoProfile -Command \"$ps\""
