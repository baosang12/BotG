fix(postrun): reconcile PnL, slippage/latency report, runbook + logging patch

Summary:
- Reconstructed closed trades: path_issues/closed_trades_fifo_reconstructed.csv
- Reconstruction report: path_issues/reconstruct_report.json
- Slippage & latency analyses: path_issues/slip_latency_percentiles.json and PNGs
- Runbook: runbook_postrun.md
- CI scaffold: .github/workflows/smoke.yml
- Minor harness patch: ensure DRAIN REQUEST/ACK precede FILL for latency_ms derivation

Acceptance gates:
1. Build OK (no errors)
2. closed_trades_fifo_reconstructed.csv exists and reconstruct_report.json shows unmatched_orders_count <= 1% (or documented)
3. slip_latency_percentiles.json + PNGs present
4. If logger patch applied: deterministic short smoke completes and FILL completeness >= 99.5%

Attachments:
- path_issues/postrun_artifacts_20250827_143911.zip
- path_issues/postrun_summary.txt
- path_issues/build_and_test_output.txt
fix(postrun): reconcile PnL, slippage/latency report, runbook + logging patch

Summary:
- Reconstructed closed trades: path_issues/closed_trades_fifo_reconstructed.csv
- Reconstruction report: path_issues/reconstruct_report.json
- Slippage & latency analyses: path_issues/slip_latency_percentiles.json and PNGs
- Runbook: runbook_postrun.md
- CI scaffold: .github/workflows/smoke.yml
- Minor harness patch: ensure DRAIN REQUEST/ACK precede FILL for latency_ms derivation

Acceptance gates:
1. Build OK (no errors)
2. closed_trades_fifo_reconstructed.csv exists and reconstruct_report.json shows unmatched_orders_count <= 1% (or documented)
3. slip_latency_percentiles.json + PNGs present
4. If logger patch applied: deterministic short smoke completes and FILL completeness >= 99.5%

Attachments:
- path_issues/postrun_artifacts_<ts>.zip
- path_issues/postrun_summary.txt
- path_issues/build_and_test_output.txt
