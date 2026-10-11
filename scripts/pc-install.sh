#!/usr/bin/env bash
# After the game: build + test on Studio, run the isolated e2e and the install through the factory pc-install runner
# (headless; it refuses while a game runs), then fetch the evidence and render the as-displayed images.
# Usage: scripts/pc-install.sh [evidence-dir]   (default: out/install). scripts/pc-hwcheck.ps1 runs separately:
#   scripts/pc-run.sh scripts/pc-hwcheck.ps1 out/ShadowBoost.exe out/hwcheck 300   (moves the monitor's values; announce first)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dest="${1:-$root/out/install}"
"$root/scripts/build.sh"
"$root/scripts/pc-e2e.sh" "$root/out/ShadowBoost.exe"
"$root/scripts/pc-run.sh" "$root/scripts/pc-install-job.ps1" "$root/out/ShadowBoost.exe" "$dest" 300
for when in before after; do
  [[ -f "$dest/screen-$when.png" && -f "$dest/ramp-$when.json" ]] && python3 "$root/scripts/as-displayed.py" "$dest/screen-$when.png" "$dest/ramp-$when.json" "$dest/as-displayed-$when.png"
done
echo "evidence in $dest"
