# PR Review Checklist

1. Fetch & checkout:
   ```
   git fetch origin
   git checkout botg/automation/reconcile-streaming-20250821_084334
   ```

2. Build & tests:
   ```
   dotnet build
   dotnet test
   ```
   Expect: success and tests pass.

3. Run sample wrapper:
   ```
   .\scripts\run_reconcile_and_compute.ps1 -ArtifactPath .\artifacts\telemetry_run_20250819_154459 -ChunkSize 10000
   ```
   Expect: exit 0; auto_reconcile_compute_summary.json created; closed_sum == closes_sum.

4. Inspect outputs:
   - fill_rate_by_side.csv
   - fill_breakdown_by_hour.csv
   - analysis_summary_stats.json

5. Performance checks: change -ChunkSize to tune memory/time if needed.

6. Repo hygiene: ensure no artifacts/ large files were committed.

7. Merge readiness:
   - [ ] Build & tests pass
   - [ ] Sample wrapper run successful
   - [ ] Reviewer approvals (1 backend + 1 QA)