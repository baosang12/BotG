PR merge checklist

- [ ] sim_seed present in run_metadata.json (persisted under extra or top-level)
- [ ] Compressed smoke with FillProb=0.9: orphan_after == 0 (reconcile_report.json and/or reconstruct_report.json)
- [ ] Real-time 1h smoke executed or scheduled; no unreconstructed orphans
- [ ] Unit tests green (8/8)
- [ ] CHANGELOG updated with run_metadata extra metadata note

Suggested PR comment

Validation done:
- Build & unit tests: PASS.
- Prior compressed smoke (FillProb=1.0): PASS, orphan_after=0.
- Patch adds persistent extra metadata (sim_seed) in run_metadata.json.

Next steps:
1) Run compressed 5-min with FillProb=0.9.
2) Run 1h real-time smoke to validate drain/fsync under real timing.
3) Optional: enable CI workflow smoke-on-pr to auto-attach artifacts.
