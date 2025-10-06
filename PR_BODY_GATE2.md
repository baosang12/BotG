ci+scripts: Gate2 hard guards + postrun validator + docs

Goal
- Enforce paper-only Gate2 24h runs and produce auditable results

Changes
- .github/workflows/gate24h.yml: add Assert-PaperMode (mode=paper, simulation.enabled=false, SecondsPerHour=3600), run metadata emission/upload, cancel on violation; wire postrun collect + validator
- scripts/postrun_gate2_validate.ps1: validate required files, schema, telemetry span>=23.75h, basic KPIs; outputs gate2_validation.json + page_gate2_summary.md; non-zero exit on fail
- docs/RUNBOOK_gate2.md: criteria, how to run, validator
- README: link to runbook

Acceptance
- On violation, early artifact run_metadata.json is uploaded and run is cancelled with reason
- On pass, artifacts contain gate2_validation.json with pass=true and page_gate2_summary.md
- Telemetry span >= 23h45', required files present, schemas OK

How to run
- Actions > Gate24h paper run > Run workflow (hours=24, source=gate2-paper)

Notes
- Linter may warn about env context for ASSERT_FAIL/RUN_META_DIR; runtime is valid as they're set via $GITHUB_ENV. Adjust if CI flags.
