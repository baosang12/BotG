**Post-run summary & acceptance checklist**

Summary:
- Reconstructed trades: `path_issues/closed_trades_fifo_reconstructed.csv`
- Reconstruct report: `path_issues/reconstruct_report.json`
- Slippage/latency analysis: `path_issues/slip_latency_percentiles.json`, `path_issues/slippage_hist.png`, `path_issues/latency_percentiles.png`
- Packaged artifacts: see `path_issues/postrun_artifacts_20250827_143911.zip` (or `path_issues/latest_zip.txt`)

Acceptance gates (reviewer — please verify):
- [ ] Build: PASS (no errors)
- [ ] `closed_trades_fifo_reconstructed.csv` exists and `reconstruct_report.json` shows `unmatched_orders_count <= 1%` or documents reason
- [ ] `slip_latency_percentiles.json` exists with p50/p90/p95/p99
- [ ] PNG artifacts present and render
- [ ] If logger patch applied: run short deterministic smoke (2 min, FillProb=1.0) and confirm FILL completeness >= 99.5%

Manual validation steps:
1. Download ZIP from `path_issues/postrun_artifacts_20250827_143911.zip` (or use the path in `path_issues/latest_zip.txt`).
2. Open `reconstruct_report.json` and confirm orphan_after == 0.
3. Optionally run local inspect with `python scripts/analyze_postrun.py --fills path_issues/closed_trades_fifo_reconstructed.csv --outdir /tmp/report`.
4. If OK, approve; otherwise request further fixes.

Do not merge until all boxes are checked.
