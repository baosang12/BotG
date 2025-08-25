# Dev Quick Run (5-minute compressed hour)

- Windows PowerShell (PS 5.1):
  - 1h->5min run with plots:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_paper_pulse.ps1 -Hours 1 -SecondsPerHour 300 -ArtifactPath .\artifacts_ascii -FillProb 0.9 -FeePerTrade 0.02 -DrainSeconds 30 -GeneratePlots

- Bash (optional):
  ./scripts/run_smoke.sh --seconds 300 --drain 30 --fill-prob 0.9

Expected artifacts inside telemetry_run_*/
- run_metadata.json (includes effective config)
- orders.csv, closed_trades_fifo.csv, analysis_summary.json
- reconcile_report.json, monitoring_summary.json
- trade_closes.log, telemetry.csv

Troubleshooting
- Orphan fills > 0: increase DrainSeconds to 30-45; re-run reconstruct via run_smoke output; check reconcile_report.json
- Ensure Python available for analyzer/plots; otherwise logs will include analyzer_stderr.log