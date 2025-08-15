#!/usr/bin/env bash
set -euo pipefail

DURATION_SECONDS="${1:-60}"
SIMULATE="${SIMULATE:-false}"
FORCE_RUN="${FORCE_RUN:-false}"

info(){ echo "[INFO] $*"; }
warn(){ echo "[WARN] $*" >&2; }
err(){ echo "[ERROR] $*" >&2; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"

get_git_meta(){
  local branch="MISSING" commit="MISSING"
  if command -v git >/dev/null 2>&1; then
    if git -C "$repo_root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
      branch="$(git -C "$repo_root" rev-parse --abbrev-ref HEAD)"
      commit="$(git -C "$repo_root" rev-parse HEAD)"
    fi
  fi
  printf '%s\n' "$branch|$commit"
}

workspace_clean(){
  if ! command -v git >/dev/null 2>&1; then return 0; fi
  git -C "$repo_root" status --porcelain | grep -q '.' && return 1 || return 0
}

ensure_branch(){
  local target="botg/telemetry-instrumentation"
  if [[ "$FORCE_RUN" == "true" ]]; then info "FORCE_RUN=true; skipping branch checkout"; return 0; fi
  if ! command -v git >/dev/null 2>&1; then warn "git not found; skip"; return 0; fi
  local current="$(git -C "$repo_root" rev-parse --abbrev-ref HEAD 2>/dev/null || echo '')"
  if [[ "$current" == "$target" ]]; then info "Already on $target"; return 0; fi
  if ! git -C "$repo_root" show-ref --verify --quiet "refs/heads/$target" && ! git -C "$repo_root" show-ref --verify --quiet "refs/remotes/origin/$target"; then
    info "Branch $target not found; continuing on $current"; return 0; fi
  if ! workspace_clean; then warn "Workspace dirty; will not checkout $target. Abort."; return 1; fi
  git -C "$repo_root" fetch origin "$target" || true
  git -C "$repo_root" checkout "$target"
}

new_artifacts(){
  ts="$(date +%Y%m%d_%H%M%S)"
  dir="$repo_root/artifacts/telemetry_run_${ts}"
  mkdir -p "$dir"
  zip="$repo_root/artifacts/telemetry_run_${ts}.zip"
  echo "$ts|$dir|$zip|$dir/build.log|$dir/summary.json"
}

restore_and_build(){
  local build_log="$1"
  local sln="$repo_root/BotG.sln"
  if [[ -f "$sln" ]]; then
    info "dotnet restore $sln"; if ! dotnet restore "$sln" 2>&1 | tee "$build_log"; then return 1; fi
    info "dotnet build $sln"; if ! dotnet build "$sln" -c Debug /property:GenerateFullPaths=true /consoleLoggerParameters:NoSummary 2>&1 | tee -a "$build_log"; then return 1; fi
  else
    info "dotnet restore $repo_root"; if ! dotnet restore "$repo_root" 2>&1 | tee "$build_log"; then return 1; fi
    info "dotnet build $repo_root"; if ! dotnet build "$repo_root" -c Debug /property:GenerateFullPaths=true /consoleLoggerParameters:NoSummary 2>&1 | tee -a "$build_log"; then return 1; fi
  fi
}

find_harness_project(){
  local p
  p="$(find "$repo_root" -name Harness.csproj 2>/dev/null | head -n1 || true)"
  if [[ -n "$p" ]]; then echo "$p"; return; fi
  if [[ -f "$repo_root/Harness/Harness.csproj" ]]; then echo "$repo_root/Harness/Harness.csproj"; return; fi
  echo ""
}

find_executable_csproj(){
  local p
  while IFS= read -r p; do
    if grep -q "<OutputType>.*Exe.*</OutputType>" "$p" 2>/dev/null; then echo "$p"; return; fi
  done < <(find "$repo_root" -name '*.csproj')
  echo ""
}

start_run(){
  local project="$1" duration="$2"
  info "Starting: dotnet run --project $project"
  set +e
  dotnet run --project "$project" &
  pid=$!
  set -e
  sleep "$duration"
  info "Stopping PID=$pid"
  kill "$pid" 2>/dev/null || true
}

ensure_telemetry_dir(){
  local config="$repo_root/config.runtime.json"
  local candidates=()
  if [[ -f "$config" ]]; then
    local dir
    dir=$(jq -r '.Telemetry.LogDir // .Telemetry.LogDirectory // .Telemetry.OutputDir // .Telemetry.OutputDirectory // .Telemetry.Dir // empty' "$config" 2>/dev/null || true)
    if [[ -n "$dir" ]]; then candidates+=("$dir"); fi
  fi
  candidates+=("$repo_root/logs" "/c/botg/logs" "/botg/logs")
  for p in "${candidates[@]}"; do
    if [[ -n "$p" ]]; then
      mkdir -p "$p" 2>/dev/null || true
      [[ -d "$p" ]] && { echo "$p"; return; }
    fi
  done
  echo "$repo_root/logs"
}

copy_if_exists(){ [[ -f "$1" ]] && cp -f "$1" "$2"; }
count_lines(){ [[ -f "$1" ]] && wc -l < "$1" || echo "MISSING"; }

readme(){
  cat <<EOF
Usage:
  ./scripts/run_harness_and_collect.sh [DURATION_SECONDS]
Env:
  SIMULATE=true    # emit sample CSVs if no harness
  FORCE_RUN=true   # skip branch checkout logic
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then readme; exit 0; fi

info "Repo root: $repo_root"
IFS='|' read -r ts artdir zipfile buildlog summary <<<"$(new_artifacts)"

ensure_branch || { err "Branch check failed"; exit 2; }

if ! restore_and_build "$buildlog"; then
  read -r branch commit <<<"$(get_git_meta | tr '|' ' ')"
  tele_dir="$(ensure_telemetry_dir)"
  orders="$tele_dir/orders.csv"; risk="$tele_dir/risk_snapshots.csv"; telem="$tele_dir/telemetry.csv"; dataf="$tele_dir/datafetcher.log"
  cp -f "$buildlog" "$artdir/" || true
  copy_if_exists "$orders" "$artdir"; copy_if_exists "$risk" "$artdir"; copy_if_exists "$telem" "$artdir"; copy_if_exists "$dataf" "$artdir"
  (cd "$artdir" && zip -qr "$zipfile" .) || true
  ts_iso="$(date -Iseconds)"
  cat > "$summary" <<JSON
{"branch":"${branch:-MISSING}","commit":"${commit:-MISSING}","build_status":"FAIL","build_log":"$buildlog","artifacts":{"orders_csv":"${orders:-MISSING}","risk_snapshots_csv":"${risk:-MISSING}","telemetry_csv":"${telem:-MISSING}","datafetcher_log":"${dataf:-MISSING}","zip":"$zipfile"},"counts":{"orders_rows":"MISSING","risk_snapshots_rows":"MISSING","telemetry_rows":"MISSING"},"timestamp":"$ts_iso"}
JSON
  echo "ZIP: $zipfile"; tr -d '\n' < "$summary"; echo
  exit 1
fi

project="$(find_harness_project)"; [[ -z "$project" ]] && project="$(find_executable_csproj)"

if [[ -z "$project" ]]; then
  if [[ "$SIMULATE" != "true" ]]; then warn "No harness/runnable csproj. Re-run with SIMULATE=true"; exit 3; fi
  tele_dir="$(ensure_telemetry_dir)"; mkdir -p "$tele_dir"
  orders="$tele_dir/orders.csv"; risk="$tele_dir/risk_snapshots.csv"; telem="$tele_dir/telemetry.csv"
  printf 'id,symbol,side,qty,price,timestamp\n1,EURUSD,BUY,1000,1.1000,%s\n2,EURUSD,SELL,500,1.1010,%s\n3,GBPUSD,BUY,200,1.2800,%s\n' "$(date -Iseconds)" "$(date -Iseconds)" "$(date -Iseconds)" > "$orders"
  printf 'timestamp,equity,balance,margin,risk_state\n%s,10000,10000,0,NORMAL\n' "$(date -Iseconds)" > "$risk"
  printf 'timestamp,metric,value\n%s,ticks_processed,12345\n' "$(date -Iseconds)" > "$telem"
else
  tele_dir="$(ensure_telemetry_dir)"; mkdir -p "$tele_dir"
  start_run "$project" "$DURATION_SECONDS"
  orders="$tele_dir/orders.csv"; risk="$tele_dir/risk_snapshots.csv"; telem="$tele_dir/telemetry.csv"; dataf="$tele_dir/datafetcher.log"
fi

cp -f "$buildlog" "$artdir/" || true
copy_if_exists "$orders" "$artdir"; copy_if_exists "$risk" "$artdir"; copy_if_exists "$telem" "$artdir"; copy_if_exists "$dataf" "$artdir"

orders_count="$(count_lines "$artdir/orders.csv")"
risk_count="$(count_lines "$artdir/risk_snapshots.csv")"
telemetry_count="$(count_lines "$artdir/telemetry.csv")"

(cd "$artdir" && zip -qr "$zipfile" .)

read -r branch commit <<<"$(get_git_meta | tr '|' ' ')"
ts_iso="$(date -Iseconds)"
cat > "$summary" <<JSON
{"branch":"${branch:-MISSING}","commit":"${commit:-MISSING}","build_status":"SUCCESS","build_log":"$buildlog","artifacts":{"orders_csv":"$artdir/orders.csv","risk_snapshots_csv":"$artdir/risk_snapshots.csv","telemetry_csv":"$artdir/telemetry.csv","datafetcher_log":"$artdir/datafetcher.log","zip":"$zipfile"},"counts":{"orders_rows":${orders_count:-MISSING},"risk_snapshots_rows":${risk_count:-MISSING},"telemetry_rows":${telemetry_count:-MISSING}},"timestamp":"$ts_iso"}
JSON

echo "ZIP: $zipfile"; tr -d '\n' < "$summary"; echo
