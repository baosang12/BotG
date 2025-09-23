# RUNBOOK: Smoke 60m Operations

## Quick commands via ops.ps1
```powershell
. .\scripts\ops.ps1
Invoke-Smoke60mMergeLatest
Show-PhaseStats
```

## VS Code Tasks
- **Smoke: Self-test (no 60')** - Run self-test without full 60-minute execution
- **Smoke: Merge latest** - Merge latest smoke run results

## Self-Test
Run the self-test to verify operational readiness:
```powershell
.\scripts\ops_selftest.ps1
```

## Maintenance
Regular maintenance commands for keeping the system clean and operational.


## Manual force_ok (owner-only)
- Purpose: exercise incident auto-close logic without altering production triggers.
- Command: `gh workflow run ".github/workflows/smoke-selftest.yml" --ref main -f force_ok=true`
- Allowed actors: repository OWNER or MEMBER (`github.actor_association`). Expected step summary: `### Smoke selftest summary (FORCED)` with `STATUS: OK` and `Close incident issue when healthy` running.
- Denied actors: COLLABORATOR/other => summary shows `FORCED REQUEST DENIED`, outputs `status=UNKNOWN`, and incident-close step remains skipped.
- Inspect guard decisions via `gh run view <runId> --log --job <jobId> | Select-String "Summarize health-check"` to confirm association detection.

## Guard verification checklist
- [ ] Main branch CI (`ci.yml`) succeeds on the merged guard change.
- [ ] Manual `force_ok=true` dispatch as OWNER/MEMBER returns STATUS=OK and triggers `Close incident issue when healthy`.
- [ ] Any incident (e.g., `Selftest incident: ...`) is closed or commented with the healthy run link.
- [ ] Post results to the guarding PR and update documentation/runbook links where applicable.
