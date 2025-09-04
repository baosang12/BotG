# High priority
- Harden nested PowerShell invocations: always use call operator (&) with fully-qualified paths; avoid -File with Unicode paths.
- Add schema checks for orders.csv (required columns: phase, order_id, price_filled, size_filled, latency_ms).

# Medium
- CI: ensure smoke artifacts are attached and a single aggregated status is posted.
- Record SHA256 for produced zips in latest_zip.txt.

# Commands
```powershell
# Re-run smoke
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_smoke.ps1 -Seconds 120 -ArtifactPath D:\botg\logs\artifacts -FillProbability 1.0 -DrainSeconds 10 -UseSimulation
# Re-run this report
powershell -NoProfile -ExecutionPolicy Bypass -File .\path_issues\copilot_report_runner.ps1 -Ts 20250827_212421
```
