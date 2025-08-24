# Smoke and Paper Runs

Quick smoke (60s):

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_smoke.ps1 -Seconds 60 -ArtifactPath .\artifacts_ascii -FillProb 1.0 -FeePerTrade 0.02 -GeneratePlots $true
```

Outputs: orders.csv (v2), closed_trades_fifo.csv (with fee), analysis_summary.json, fill_rate_by_side.csv, fill_breakdown_by_hour.csv, reconcile_report.json, monitoring_summary.json, quick_preview.log, optional PNGs, and telemetry_run_*.zip.
# BotG Smoke Run

## Quick start (Windows PowerShell 5.1)

- 30s smoke to temp folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_smoke.ps1 -Seconds 30 -ArtifactPath $env:TEMP\botg_artifacts -FillProb 1.0
```

- Print artifact summary and top rows are shown in console; zip file `telemetry_run_{ts}.zip` is created in the run folder.

## Troubleshooting

- If your path contains diacritics or non-ASCII characters, the script will fallback to an ASCII-safe temp path.
- PowerShell 5.1 execution policy: run with `-ExecutionPolicy Bypass` as shown above.
- If closed_trades file is missing, the script auto-reconstructs it from `orders.csv`.
- Python is optional; if available, analyzer writes `analysis_summary.json`.
