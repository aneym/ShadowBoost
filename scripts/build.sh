#!/usr/bin/env bash
# Core tests and the Windows exe, built on Studio (macOS cross-build; the project sets EnableWindowsTargeting).
# Usage: scripts/build.sh   -> out/ShadowBoost.exe
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
"$dotnet" test "$root/tests/ShadowBoost.Core.Tests" --nologo -v q
"$dotnet" publish "$root/src/ShadowBoost.App/ShadowBoost.App.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "$root/out" --nologo -v q
shasum -a 256 "$root/out/ShadowBoost.exe"
