# RUNBOOK: Gate2 one-shot 24h paper

This runbook defines what counts as PASS for a 24h paper run, how to run it, and how validation works.

## PASS Criteria

- Paper mode only: mode==paper, simulation.enabled==false, SecondsPerHour==3600
- Telemetry span >= 23h45'
- Required files present:
  - orders.csv, telemetry.csv, risk_snapshots.csv, trade_closes.log, run_metadata.json, closed_trades_fifo_reconstructed.csv
- Schemas:
  - orders.csv: request_id, side, type, status, reason, latency_ms, price_requested, price_filled, size_requested, size_filled, ts_request, ts_ack, ts_fill
  - risk_snapshots.csv: ts, equity, R_used, exposure, drawdown
- Validator result: gate2_validation.json pass=true

## How to run (one-shot)

Preferred: use the helper script to dispatch, wait, and download artifacts locally.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run_gate2_oneshot.ps1
```

## Validator

- Script: `scripts/postrun_gate2_validate.ps1 -ArtifactsDir <path>` (called automatically by the workflow)

## Guard proof (cheap)

- VS Code task: `gate2:assert-fail` triggers a short, forced-misconfig run:
  - It runs: `gh workflow run gate24h.yml -F hours=0.1 -F source=assert-fail -F force_misconfig=true`
  - Expected: the guard cancels early with reason and uploads early `run_metadata.json`.
- Outputs:
  - `gate2_validation.json` { pass, reasons[], telemetry_span_hours, files_present[], schema_ok, risk_violations{daily,weekly}, kpi{...} }
  - `page_gate2_summary.md` (3 what / 3 so-what / 3 next)
- Exits non-zero if pass=false

## Examples

- Expected PASS: Full 24h paper run with correct schemas and telemetry span
- Expected FAIL: Any run with simulation on, wrong SecondsPerHour, or telemetry span < 23.75h
