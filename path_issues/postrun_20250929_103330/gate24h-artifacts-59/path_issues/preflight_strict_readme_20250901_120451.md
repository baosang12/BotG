# Preflight Strict Check - 20250901_120451

## Verdict
PRECHECKS_FAILED

## Checks
| Name | Status | Details |
|------|--------|---------|
| env.BOTG_ROOT | FAIL | BOTG_ROOT not set or not repo root (expected D:\OneDrive\Tài liệu\cAlgo\Sources\Robots\BotG) |
| env.BOTG_LOG_PATH | PASS | Exists and writable: D:\botg\logs |
| env.FreeDisk | PASS | Free: 235,549,888,512 bytes |
| runtime.dotnet | PASS | SDK >= 6.0 present |
| runtime.python | PASS | Python 3.13.5 |
| runtime.venv | PASS | Found venv: D:\OneDrive\Tài liệu\cAlgo\Sources\Robots\BotG\.venv\Scripts\Activate.ps1 |
| runtime.powershell | PASS | PowerShell 5.1.26100.4652 |
| git.branch | PASS | On branch: ci/recon-validate-smoke |
| git.clean | WARN | Untracked files present:  M .github/workflows/ci.yml,  M .github/workflows/paper-pulse.yml,  M .github/workflows/smoke-fast.yml,  M .github/workflows/smoke-run.yml,  M path_issues/agent_ts.txt,  M path_issues/git_manual_run.txt,  M scripts/ci_reconstruct_validate.ps1, ?? path_issues/agent_summary_20250828_000000.json, ?? path_issues/agent_summary_20250828_000501.json, ?? path_issues/analyze_results_20250830_104108.json, ?? path_issues/analyze_results_20250830_104346.json, ?? path_issues/analyze_results_20250830_105347.json, ?? path_issues/analyze_results_20250830_105656.json, ?? path_issues/analyze_results_20250830_110145.json, ?? path_issues/analyze_results_20250830_110506.json, ?? path_issues/analyze_results_20250901_112841.json, ?? path_issues/build_and_test_output_20250830_104108.txt, ?? path_issues/build_and_test_output_20250830_104346.txt, ?? path_issues/build_and_test_output_20250830_105347.txt, ?? path_issues/build_and_test_output_20250830_105656.txt, ?? path_issues/build_and_test_output_20250830_110145.txt, ?? path_issues/build_and_test_output_20250830_110506.txt, ?? path_issues/build_and_test_output_20250901_112841.txt, ?? path_issues/closed_trades_fifo_reconstructed_20250830_104108.csv, ?? path_issues/closed_trades_fifo_reconstructed_20250830_104346.csv, ?? path_issues/closed_trades_fifo_reconstructed_20250830_110506.csv, ?? path_issues/closed_trades_fifo_reconstructed_20250901_112841.csv, ?? path_issues/collect_20250830_101536/, ?? path_issues/collect_20250830_104108/, ?? path_issues/collect_20250830_104346/, ?? path_issues/collect_20250830_105347/, ?? path_issues/collect_20250830_105656/, ?? path_issues/collect_20250830_110145/, ?? path_issues/collect_20250830_110506/, ?? path_issues/collect_20250901_112841/, ?? path_issues/copilot_acceptance_20250829_095421.json, ?? path_issues/copilot_acceptance_latest.json, ?? path_issues/copilot_action_items_20250829_095421.md, ?? path_issues/copilot_actions_latest.md, ?? path_issues/copilot_artifact_hashes_20250829_095421.json, ?? path_issues/copilot_artifacts_20250829_095421.json, ?? path_issues/copilot_artifacts_latest.json, ?? path_issues/copilot_blockers_20250830_104108.md, ?? path_issues/copilot_blockers_20250830_105656.md, ?? path_issues/copilot_blockers_20250830_110145.md, ?? path_issues/copilot_ci_20250829_095421.json, ?? path_issues/copilot_ci_latest.json, ?? path_issues/copilot_reconstruct_error_20250830_105347.txt, ?? path_issues/copilot_reconstruct_error_20250830_105656.txt, ?? path_issues/copilot_reconstruct_error_20250830_110145.txt, ?? path_issues/copilot_report_error_20250829_120525.txt, ?? path_issues/copilot_status_20250829_095421.md, ?? path_issues/copilot_status_latest.json, ?? path_issues/copilot_status_latest.md, ?? path_issues/full_readiness_run.ps1, ?? path_issues/generate_reports.ps1, ?? path_issues/git_error_20250828_000000.txt, ?? path_issues/git_error_20250828_000501.txt, ?? path_issues/postmerge_artifacts_checksums_20250829_122749.json, ?? path_issues/postmerge_check_20250829_122749.txt, ?? path_issues/postmerge_metrics_20250829_122749.json, ?? path_issues/postmerge_readme_20250829_122749.md, ?? path_issues/postmerge_readme_20250830_101536.md, ?? path_issues/postmerge_readme_20250830_104108.md, ?? path_issues/postmerge_readme_20250830_104346.md, ?? path_issues/postmerge_readme_20250830_105656.md, ?? path_issues/postmerge_readme_20250830_110145.md, ?? path_issues/postmerge_readme_20250830_110506.md, ?? path_issues/postmerge_readme_20250901_112841.md, ?? path_issues/postmerge_readme_pre_run.md, ?? path_issues/postmerge_readme_signed_20250830_104108.txt, ?? path_issues/postmerge_readme_signed_20250830_104346.txt, ?? path_issues/postmerge_readme_signed_20250830_105656.txt, ?? path_issues/postmerge_readme_signed_20250830_110145.txt, ?? path_issues/postmerge_readme_signed_20250830_110506.txt, ?? path_issues/postmerge_readme_signed_20250901_112841.txt, ?? path_issues/postmerge_summary_20250829_122749.json, ?? path_issues/postmerge_summary_20250830_101536.json, ?? path_issues/postmerge_summary_20250830_104108.json, ?? path_issues/postmerge_summary_20250830_104346.json, ?? path_issues/postmerge_summary_20250830_105347.json, ?? path_issues/postmerge_summary_20250830_105656.json, ?? path_issues/postmerge_summary_20250830_110145.json, ?? path_issues/postmerge_summary_20250830_110506.json, ?? path_issues/postmerge_summary_20250901_112841.json, ?? path_issues/postrun_artifacts_20250829_122749.zip, ?? path_issues/preflight_strict_check.ps1, ?? path_issues/reconstruct_report_20250830_104108.json, ?? path_issues/reconstruct_report_20250830_104346.json, ?? path_issues/reconstruct_report_20250830_110506.json, ?? path_issues/reconstruct_report_20250901_112841.json, ?? path_issues/run_manual_collect.txt, ?? path_issues/runbook_weekend_checklist_20250830_101536.md, ?? path_issues/start_24h_command_monday_template.txt, ?? path_issues/start_24h_command_ready.txt, ?? path_issues/user_collect.ps1, ?? path_issues/weekend_full_checksums_20250830_104108.csv, ?? path_issues/weekend_full_checksums_20250830_104108.json, ?? path_issues/weekend_full_checksums_20250830_104346.csv, ?? path_issues/weekend_full_checksums_20250830_104346.json, ?? path_issues/weekend_full_checksums_20250830_105347.csv, ?? path_issues/weekend_full_checksums_20250830_105347.json, ?? path_issues/weekend_full_checksums_20250830_105656.csv, ?? path_issues/weekend_full_checksums_20250830_105656.json, ?? path_issues/weekend_full_checksums_20250830_110145.csv, ?? path_issues/weekend_full_checksums_20250830_110145.json, ?? path_issues/weekend_full_checksums_20250830_110506.csv, ?? path_issues/weekend_full_checksums_20250830_110506.json, ?? path_issues/weekend_full_checksums_20250901_112841.csv, ?? path_issues/weekend_full_checksums_20250901_112841.json, ?? path_issues/weekend_postmerge_checksums_20250830_101536.json |
| orchestration.ready_command | PASS | start_24h_command_ready.txt present and paper mode |
| build | PASS | Build ok |
| tests | PASS | Tests ok |
| reconstruct.run | PASS | Reconstruct produced output |
| smoke.outputs | PASS | orders/telemetry/trade_closes/summary present |
| smoke.file_growth | FAIL | Could not confirm file growth |
| smoke.acceptance | PASS | orphan_after=0, unmatched_orders_count=0 |
| logging.archival | PASS | Found archival script: archive_and_prepare_24h.ps1 |
| logging.acl | PASS | Users have read/list |
| orchestration.supervisor | PASS | Supervisor script found |
| orchestration.prepare | PASS | archive_and_prepare_24h.ps1 present |
| orchestration.sentinels | WARN | Sentinel support not detected |
| monitor.snapshot | WARN | No snapshot script found |
| monitor.runbook | WARN | Runbook lacks clear escalation |
| safety.live_feed | PASS | Live feed enabled |
| safety.stop_instructions | PASS | Stop/pause instructions present |

## Blockers
- Set BOTG_ROOT to repo root

## Warnings
- Untracked files present
- File growth not confirmed
- Sentinel support not detected
- Snapshot script missing

## Evidence
- reconstruct_report: preflight_reconstruct_report_20250901_120451.json
- smoke_summary: summary.json

## External caveats
- Broker/live connectivity not exercised in preflight
- Market events/halts and extreme volatility not simulated
