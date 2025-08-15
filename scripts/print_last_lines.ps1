param(
  [int]$Lines = 50,
  [string]$ArtifactsDir = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info($m){ Write-Host "[INFO] $m" -ForegroundColor Cyan }

if (-not $ArtifactsDir -or -not (Test-Path $ArtifactsDir)) {
  $root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
  $latest = Get-ChildItem -Directory -Path (Join-Path $root 'artifacts') -Filter 'telemetry_run_*' | Sort-Object Name -Descending | Select-Object -First 1
  if (-not $latest) { throw "No artifacts dir found" }
  $ArtifactsDir = $latest.FullName
}

Write-Info "Using artifacts dir: $ArtifactsDir"
$files = @('orders.csv','risk_snapshots.csv','telemetry.csv','datafetcher.log','build.log')
foreach ($f in $files) {
  $p = Join-Path $ArtifactsDir $f
  if (Test-Path $p) {
    Write-Host "--- $f (last $Lines lines) ---" -ForegroundColor Yellow
    Get-Content -Path $p -Tail $Lines
  }
}
