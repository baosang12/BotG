#!/usr/bin/env bash
set -euo pipefail
SECONDS_ARG=30
ARTIFACT_PATH="${TMPDIR:-/tmp}/botg_artifacts"
FILL_PROB=1.0
DRAIN_SECONDS=30
while [[ $# -gt 0 ]]; do
  case "$1" in
    --seconds|--Seconds|-s) SECONDS_ARG="$2"; shift 2;;
    --artifact|--ArtifactPath) ARTIFACT_PATH="$2"; shift 2;;
    --fill-prob|--FillProb|--fill|--fill-probability) FILL_PROB="$2"; shift 2;;
    --drain|--DrainSeconds) DRAIN_SECONDS="$2"; shift 2;;
    *) shift;;
  esac
done
pwsh -NoProfile -ExecutionPolicy Bypass -File "$(dirname "$0")/run_smoke.ps1" -Seconds "$SECONDS_ARG" -ArtifactPath "$ARTIFACT_PATH" -FillProb "$FILL_PROB" -DrainSeconds "$DRAIN_SECONDS" -GeneratePlots
