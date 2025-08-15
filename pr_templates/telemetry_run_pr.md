# PR Title
chore(telemetry): collect harness telemetry run <timestamp>

# Summary
This PR adds artifacts from a headless Harness/paper run and a short JSON summary for analysis.

- Branch: <branch>
- Commit: <commit>
- Run timestamp: <timestamp>
- Build status: <SUCCESS|FAIL>

# Artifacts
- Zip: <repo>/artifacts/telemetry_run_<timestamp>.zip
- Folder: <repo>/artifacts/telemetry_run_<timestamp>/
  - orders.csv
  - risk_snapshots.csv
  - telemetry.csv
  - datafetcher.log (if present)
  - build.log
  - summary.json

# How to reproduce locally
PowerShell (Windows):
```powershell
# From repo root
./scripts/run_harness_and_collect.ps1 -DurationSeconds 60
# Print tails
./scripts/print_last_lines.ps1 -Lines 50
```

bash (Linux/macOS):
```bash
# From repo root
chmod +x ./scripts/*.sh
./scripts/run_harness_and_collect.sh 60
# Print tails
./scripts/print_last_lines.sh 50
```

# Notes
- The scripts try to checkout branch `botg/telemetry-instrumentation` if it exists and the workspace is clean. They will abort if the workspace is dirty (unless ForceRun/SIMULATE used).
- If Harness is not found, use simulation mode (no broker calls):
  - PowerShell: add `-Simulate`
  - bash: `SIMULATE=true ./scripts/run_harness_and_collect.sh 30`
- Do not merge to main until shims are reviewed and replaced where needed.

# Checklist
- [ ] Build succeeds and artifacts generated
- [ ] Telemetry CSVs contain expected headers and rows
- [ ] No secrets or sensitive info included in logs
- [ ] Shims usage acknowledged; no merge to main until confirmed
- [ ] Observed any anomalies documented in comments
