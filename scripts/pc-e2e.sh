#!/usr/bin/env bash
# One isolated vertical scenario on the PC, through the factory pc-install runner (headless; never ssh pc).
# Never interacts with the installed executable: --home isolation and --dry-gamma throughout.
# Usage: scripts/pc-e2e.sh [exe]   (default: out/ShadowBoost.exe from scripts/build.sh)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rm -rf "$root/out/shots"
"$root/scripts/pc-run.sh" "$root/scripts/pc-e2e.ps1" "${1:-$root/out/ShadowBoost.exe}" "$root/out/shots" 600
