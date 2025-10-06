#!/usr/bin/env bash
set -euo pipefail

ts=20250827_143911
branch="fix/postrun-${ts}-finalize"

echo "Creating branch: $branch"
git checkout -b "$branch"

echo "Staging PR body + checklist"
git add path_issues/postrun_pr_body.md path_issues/postrun_checklist.md

echo "Staging artifacts (if present)"
git add path_issues/closed_trades_fifo_reconstructed.csv \
        path_issues/reconstruct_report.json \
        path_issues/slip_latency_percentiles.json \
        path_issues/slippage_hist.png \
        path_issues/latency_percentiles.png \
        path_issues/fillrate_hourly.csv \
        path_issues/top_slippage.csv \
        path_issues/postrun_summary.txt \
        path_issues/postrun_artifacts_*.zip || true

echo "Committing"
git commit -m "chore(postrun): add PR body + checklist and packaged artifacts for review"

echo "Pushing"
git push -u origin HEAD

echo "$branch" > path_issues/last_branch_pushed.txt
echo "DONE: pushed $branch"
