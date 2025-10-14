## What
- All 19 workflow YAML files on origin/main are UTF-8 without BOM and no Unicode echo commands (see bom_scan.txt).
- Upload-artifact steps in notify_on_failure/selftest/selftest_new/smoke-on-pr all gate with if: always() (guards_scan.txt).
- smoke-fast-on-pr workflow uploads workflow-events artifact under both successful run 18484793270 and forced failure 18484804369 while logging concurrency guard output.

## So What
- Canonical GitHub workflows no longer trip BOM/Unicode parsers, reducing risk of pipeline aborts on Windows agents.
- Guard coverage guarantees post-run telemetry uploads for diagnostics even when shadow jobs exit non-zero.
- Concurrency guard proof demonstrates cancel-in-progress true, preventing duplicate smoke runs on the same ref.

## Next
- Promote Phase 2b bundle into ops runbook and retire legacy sentinel checklist.
- Begin Phase 3 scope (Gate2 wiring) using this branch state as baseline.
- Monitor smoke-fast-on-pr for 24h to ensure no regression in artifact retention.

**Conclusion:** READY cho Phase 3.
