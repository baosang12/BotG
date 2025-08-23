# Basic CI smoke test wrapper
param(
  [int]$Seconds = 60,
  [int]$DrainSeconds = 10,
  [switch]$GeneratePlots
)
$ErrorActionPreference = 'Stop'
$art = Join-Path $env:TEMP ("botg_ci_smoke")
if (Test-Path -LiteralPath $art) { Remove-Item -Recurse -Force $art }
New-Item -ItemType Directory -Path $art | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\run_smoke.ps1" -Seconds $Seconds -ArtifactPath $art -FillProb 1.0 -FeePerTrade 0.00 -DrainSeconds $DrainSeconds -GeneratePlots:$GeneratePlots
$zip = Get-ChildItem -Path $art -Recurse -Filter 'telemetry_run_*.zip' | Select-Object -First 1
if (-not $zip) { Write-Error 'zip artifact not found'; exit 1 }
Write-Host ("zip: " + $zip.FullName)

# Validate reconcile report has zero orphan fills
$dir = Get-ChildItem -Path $art -Directory | Where-Object { $_.Name -like 'telemetry_run_*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dir) { Write-Error 'No telemetry_run_* directory found'; exit 1 }
$rr = Join-Path $dir.FullName 'reconcile_report.json'
if (-not (Test-Path -LiteralPath $rr)) { Write-Error 'reconcile_report.json missing'; exit 1 }
$json = Get-Content -LiteralPath $rr -Raw | ConvertFrom-Json
$orph = [int]$json.orphan_fills_count
Write-Host ("orphan_fills_count=" + $orph)
if ($orph -gt 0) { Write-Error ("Orphan fills detected: $orph"); exit 2 }
