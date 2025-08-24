# Basic CI smoke test wrapper (robust, zip optional)
param(
  [int]$Seconds = 60,
  [int]$DrainSeconds = 10,
  [switch]$GeneratePlots
)
$ErrorActionPreference = 'Stop'
$art = Join-Path $env:TEMP ("botg_ci_smoke")
if (Test-Path -LiteralPath $art) { Remove-Item -Recurse -Force $art }
New-Item -ItemType Directory -Path $art | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\run_smoke.ps1" -Seconds $Seconds -ArtifactPath $art -FillProb 1.0 -FeePerTrade 0.00 -DrainSeconds $DrainSeconds -UseSimulation -GeneratePlots:$GeneratePlots

# Find latest run directory
$dir = Get-ChildItem -Path $art -Directory | Where-Object { $_.Name -like 'telemetry_run_*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dir) { Write-Error 'No telemetry_run_* directory found'; exit 1 }
Write-Host ("artifact_dir=" + $dir.FullName)

# Zip may be absent if no files matched; do not fail on that in CI
$zip = Get-ChildItem -Path $dir.FullName -Filter 'telemetry_run_*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($zip) { Write-Host ("zip=" + $zip.FullName) }

# Validate reconcile report: orphan_fills_count == 0
$rr = Join-Path $dir.FullName 'reconcile_report.json'
if (-not (Test-Path -LiteralPath $rr)) { Write-Error 'reconcile_report.json missing'; exit 1 }
$json = Get-Content -LiteralPath $rr -Raw | ConvertFrom-Json
$orph = [int]$json.orphan_fills_count
Write-Host ("orphan_fills_count=" + $orph)
if ($orph -gt 0) { Write-Error ("Orphan fills detected: $orph"); exit 2 }

# Validate reconstruct report: orphan_after == 0
$recon = Join-Path $dir.FullName 'reconstruct_report.json'
if (-not (Test-Path -LiteralPath $recon)) { Write-Error 'reconstruct_report.json missing'; exit 1 }
$rjson = Get-Content -LiteralPath $recon -Raw | ConvertFrom-Json
$after = [int]$rjson.orphan_after
Write-Host ("reconstruct.orphan_after=" + $after)
if ($after -gt 0) { Write-Error ("Reconstruct orphan_after > 0: $after"); exit 3 }
