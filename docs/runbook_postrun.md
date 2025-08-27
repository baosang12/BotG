# Postrun Runbook and Alerts

Acceptance thresholds (suggested):
  - fill_rate < 0.95: stop and investigate.
  - latency median > 200 ms: review.
  - slippage p95 above instrument thresholds: review.
Kill switch: create a file named `RUN_PAUSE` in the run folder to stop further execution loops.
Re-run deterministic smoke locally (2–5 min): set `BOTG_LOG_PATH` to a writable folder, `FillProb=1.0`.
Artifacts to review: orders.csv, closed_trades_fifo.csv, trade_closes.log, run_metadata.json, analysis_summary.json.

## CI smoke (skeleton)

- Non-blocking template at `ci/smoke.yml` for scheduled nightly smoke.
- Steps: checkout → dotnet build/test → short Harness run → run `scripts/analyze_smoke.py` → upload artifacts to CI workspace.

---

## Troubleshooting
- Unicode paths on Windows PowerShell 5.1: prefer using environment variables and avoid passing -File with Unicode paths.
- If plotting fails, `matplotlib` is optional; JSON/CSV outputs still generated.