# Basic CI smoke test wrapper
param(
  [int]$Seconds = 15
)
$ErrorActionPreference = 'Stop'
$art = Join-Path $env:TEMP ("botg_ci_smoke")
if (Test-Path -LiteralPath $art) { Remove-Item -Recurse -Force $art }
New-Item -ItemType Directory -Path $art | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\run_smoke.ps1" -Seconds $Seconds -ArtifactPath $art -FillProb 1.0
$zip = Get-ChildItem -Path $art -Recurse -Filter 'telemetry_run_*.zip' | Select-Object -First 1
if (-not $zip) { Write-Error 'zip artifact not found'; exit 1 }
Write-Host ("zip: " + $zip.FullName)
