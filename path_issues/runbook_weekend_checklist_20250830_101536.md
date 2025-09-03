\n## Short smoke executedCommand: powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\audit_and_smoke.ps1 -DurationSeconds 120 -FillProb 1.0 -ForceRun\n## Outputs collectedPath: .\path_issues\collect_20250830_101536Files:- build.log - closed_trades_fifo.csv - orders.csv - reconstruct_report.json - risk_snapshots.csv - smoke_summary_20250827_200336.json - summary.json - telemetry.csv - telemetry_run_20250830_101830.zip - trade_closes.log\n## Acceptance gates{
    "unmatched_orders":  "PASS",
    "fill_rate":  "N/A",
    "build":  "PASS",
    "smoke":  "PASS",
    "reconstruct":  "PASS"
}\nThresholds: orphan_after==0, unmatched==0, smoke PASS, fill_rate1.0 if available\n## Failure handlingCreate RUN_PAUSE sentinel, check logs under artifacts/, notify on-call per ops rota.\n## Monday operator steps1) Open repo root2) Review .\path_issues\postmerge_readme_20250830_101536.md3) Run .\\path_issues\\start_24h_command_monday_template.txt after loading secrets4) Monitor logs; ensure kill-switch path accessible
