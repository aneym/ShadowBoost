#!/usr/bin/env bash
# Run a ShadowBoost job on the PC through the factory pc-install runner, headless (no Windows-side ssh).
# Usage: scripts/pc-run.sh <body.ps1> <exe> [evidence-dir] [timeout-seconds]
# Stages <exe> on pc-wsl, glues scripts/pc-job-prelude.ps1 to <body.ps1>, queues it with pc-install-drop --wait,
# prints the job log and unzips the job's evidence into [evidence-dir] when the body set $evidence.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
body="$1"; exe="$2"; dest="${3:-}"; timeout="${4:-600}"
drop="${PC_INSTALL_DROP:-/Volumes/StudioExt/repos/factory/bin/pc-install-drop}"
id="$(uuidgen | tr -d - | tr 'A-F' 'a-f')"
sha="$(shasum -a 256 "$exe" | cut -d' ' -f1 | tr 'a-f' 'A-F')"
tmp="$(mktemp -d)"; trap 'rm -rf "${tmp:?}"' EXIT
base64 -i "$exe" | tr -d '\n' > "$tmp/exe.b64"
ssh -o BatchMode=yes pc-wsl "mkdir -p pc-install/stage/shadowboost/$id && cat > pc-install/stage/shadowboost/$id/exe.b64" < "$tmp/exe.b64"
job="$tmp/sb-$(basename "$body" .ps1).ps1"
python3 - "$root/scripts/pc-job-prelude.ps1" "$body" "$job" "$id" "$sha" "$timeout" <<'PY'
import sys
prelude, body, out, id_, sha, timeout = sys.argv[1:]
text = open(prelude).read().replace('__ID__', id_).replace('__SHA__', sha).replace('__TIMEOUT__', timeout)
open(out, 'w').write(text.replace('# __BODY__', open(body).read().replace('__SHA__', sha)))
PY
status=0; "$drop" "$job" --wait --wait-timeout $((timeout + 300)) | tee "$tmp/log" || status=$?
if [[ -n "$dest" ]] && grep -q '^EVIDENCE_OUT=' "$tmp/log"; then
  mkdir -p "$dest"; scp -q "pc-wsl:pc-install/out/$id/evidence.zip" "$tmp/evidence.zip" && unzip -oq "$tmp/evidence.zip" -d "$dest" && echo "evidence in $dest"
fi
ssh -o BatchMode=yes pc-wsl "rm -rf pc-install/stage/shadowboost/$id" || true
echo "exe sha256 $sha"
exit "$status"
