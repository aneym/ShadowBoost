#!/usr/bin/env bash
# After the game: build + test on the PC, run the isolated e2e, queue the install through the factory
# pc-install runner (it refuses while a game runs), then fetch the evidence and render the as-displayed images.
# Usage: scripts/pc-install.sh [evidence-dir]   (default: out/install)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dest="${1:-$root/out/install}"
drop="${PC_INSTALL_DROP:-/Volumes/StudioExt/repos/factory/bin/pc-install-drop}"
"$root/scripts/pc-check.sh" all
"$root/scripts/pc-e2e.sh"
sha="$(ssh pc 'powershell -NoProfile -Command "(Get-FileHash $env:LOCALAPPDATA\Temp\sb-build\out\ShadowBoost.exe -Algorithm SHA256).Hash"' | tr -d '\r\n')"
[[ "$sha" =~ ^[0-9A-F]{64}$ ]] || { echo "bad sha: $sha" >&2; exit 1; }
tmp="$(mktemp -d)"; trap 'rm -rf "${tmp:?}"' EXIT
sed "s/__SHA__/$sha/" "$root/scripts/pc-install-job.ps1" > "$tmp/shadowboost-install.ps1"
"$drop" "$tmp/shadowboost-install.ps1" --wait --wait-timeout 3600 | tee "$tmp/log"
evidence="$(sed -n 's/^EVIDENCE=//p' "$tmp/log" | tr -d '\r' | tr '\\' '/')"
[[ -n "$evidence" ]]
mkdir -p "$dest"
scp -q -r "pc:$evidence/*" "$dest/"
for when in before after; do
  [[ -f "$dest/screen-$when.png" && -f "$dest/ramp-$when.json" ]] && python3 "$root/scripts/as-displayed.py" "$dest/screen-$when.png" "$dest/ramp-$when.json" "$dest/as-displayed-$when.png"
done
echo "installed sha256 $sha; evidence in $dest"
