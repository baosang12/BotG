#!/usr/bin/env bash
set -euo pipefail
LINES="${1:-50}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
art_dir="${2:-}"

if [[ -z "$art_dir" ]]; then
  art_dir="$(ls -1d "$repo_root"/artifacts/telemetry_run_* 2>/dev/null | sort -r | head -n1)"
fi

if [[ -z "$art_dir" || ! -d "$art_dir" ]]; then
  echo "No artifacts dir found" >&2
  exit 1
fi

echo "[INFO] Using artifacts dir: $art_dir"
for f in orders.csv risk_snapshots.csv telemetry.csv datafetcher.log build.log; do
  p="$art_dir/$f"
  if [[ -f "$p" ]]; then
    echo "--- $f (last $LINES lines) ---"
    tail -n "$LINES" "$p"
  fi
done
