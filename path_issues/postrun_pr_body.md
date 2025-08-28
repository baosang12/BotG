# fix(postrun): finalize artifacts and report

## Summary
This PR finalizes the postrun artefacts and runbook for the latest paper run.  
Artifacts and analyses are attached under `path_issues/` in the repo (see `latest_zip.txt` for the zip file).

**Key outputs**
- Reconstructed trades: `path_issues/closed_trades_fifo_reconstructed.csv`
- Reconstruction report: `path_issues/reconstruct_report.json`
- Smoke summary: `path_issues/smoke_summary_20250827_200336.json` (short smoke PASS — orphan_after=0, fill_rate=100%)
- Slippage & latency: `path_issues/slip_latency_percentiles.json`, `path_issues/slippage_hist.png`, `path_issues/latency_percentiles.png`
- Packaged ZIP: see `path_issues/latest_zip.txt` → `telemetry_run_20250827_200336.zip`

## Acceptance gates (reviewer)
Please verify the following before merge:
1. **Build**: `build_and_test_output.txt` shows build PASS (no error).  
2. **Short smoke**: `smoke_summary_*.json` — `orphan_after == 0` and `fill_rate >= 95%`.  
3. **Reconstruct**: `reconstruct_report.json` — `unmatched_orders_count <= 1%` or documented explanation.  
4. **Logging completeness**: >99% FILL rows contain `price_filled`, `size_filled`, `latency_ms`.  
5. **Artifacts**: `postrun_artifacts_<ts>.zip` present and SHA256 matches `.sha256`.

## How to validate quickly
- Download the ZIP specified in `path_issues/latest_zip.txt`.
- Inspect `reconstruct_report.json` and `smoke_summary_*.json`.
- Optionally run the analyzer locally:


python .\scripts\analyze_postrun.py --fills path_issues/closed_trades_fifo_reconstructed.csv --outdir path_issues


**Do not merge until all gates are confirmed.** (See <attachments> above for file contents. You may not need to search or read the file again.)
