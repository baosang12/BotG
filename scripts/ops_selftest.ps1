#requires -Version 5.1
Set-StrictMode -Version Latest
Write-Host "=== SMOKE 60m SELF-TEST ==="
. .\scripts\ops.ps1
Invoke-Smoke60mMergeLatest
Show-PhaseStats
Write-Host "OK Self-test completed successfully"
