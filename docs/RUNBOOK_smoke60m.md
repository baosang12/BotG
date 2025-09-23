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



