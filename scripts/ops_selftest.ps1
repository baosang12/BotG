#requires -Version 5.1
Set-StrictMode -Version Latest
Write-Host "=== SMOKE 60m SELF-TEST ==="
. .\scripts\ops.ps1
Invoke-Smoke60mMergeLatest
if (Get-Command Show-PhaseStats -ErrorAction SilentlyContinue) {
  Show-PhaseStats
} else {
  Write-Host "[selftest] Show-PhaseStats not found; skipping."
}
Write-Host "OK Self-test completed successfully"
