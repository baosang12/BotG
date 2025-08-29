<#!
.SYNOPSIS
  Generate a concise smoke summary JSON from the latest telemetry_run_* artifacts.

.DESCRIPTION
  Finds the most recent telemetry_run_* directory under the given artifact root, reads
  analysis_summary.json, reconcile_report.json, and monitoring_summary.json, computes
  orphan_after and acceptance (PASS if orphan_after == 0 else FAIL), and writes a
  summary JSON into path_issues/smoke_summary_<ts>.json under the current repo root
  (or a specified OutputDir).

.PARAMETER ArtifactRoot
  Root folder containing telemetry_run_* subfolders. Defaults to D:\botg\logs\artifacts.

.PARAMETER OutputDir
  Directory to write summary JSON. Defaults to <cwd>\path_issues.
#>
param(
  [string]$ArtifactRoot = 'D:\botg\logs\artifacts',
  [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LatestRunDir([string]$root) {
  if (-not (Test-Path -LiteralPath $root)) {
    throw "ArtifactRoot not found: $root"
  }
  $dir = Get-ChildItem -LiteralPath $root -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $dir) { throw "No telemetry_run_* directories found under $root" }
  return $dir.FullName
}

function Get-TimestampFromRunName([string]$runPath) {
  $name = Split-Path -Leaf $runPath
  # Expect: telemetry_run_YYYYMMDD_HHMMSS
  $m = [regex]::Match($name, '^telemetry_run_(?<ts>\d{8}_\d{6})$')
  if ($m.Success) { return $m.Groups['ts'].Value }
  # Fallback to everything after first underscore
  $idx = $name.IndexOf('_')
  if ($idx -ge 0 -and $idx + 1 -lt $name.Length) { return $name.Substring($idx + 1) }
  return (Get-Date -Format 'yyyyMMdd_HHmmss')
}

try {
  if (-not $OutputDir) {
    $OutputDir = Join-Path -Path (Get-Location).Path -ChildPath 'path_issues'
  }
  New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

  $runDir = Get-LatestRunDir -root $ArtifactRoot
  $ts = Get-TimestampFromRunName -runPath $runDir

  $analysisFile = Join-Path -Path $runDir -ChildPath 'analysis_summary.json'
  $reconcileFile = Join-Path -Path $runDir -ChildPath 'reconcile_report.json'
  $monitoringFile = Join-Path -Path $runDir -ChildPath 'monitoring_summary.json'

  $a = Get-Content -Raw -LiteralPath $analysisFile | ConvertFrom-Json
  $r = $null
  if (Test-Path -LiteralPath $reconcileFile) { $r = Get-Content -Raw -LiteralPath $reconcileFile | ConvertFrom-Json }
  $mon = $null
  if (Test-Path -LiteralPath $monitoringFile) { $mon = Get-Content -Raw -LiteralPath $monitoringFile | ConvertFrom-Json }

  $or = 0
  if ($r -and ($r.PSObject.Properties.Name -contains 'orphan_after')) { $or = [int]$r.orphan_after }
  elseif ($mon -and ($mon.PSObject.Properties.Name -contains 'orphan_fills')) { $or = [int]$mon.orphan_fills }

  $zipName = "telemetry_run_${ts}.zip"
  $zipPath = Join-Path -Path $runDir -ChildPath $zipName
  if (-not (Test-Path -LiteralPath $zipPath)) {
    $firstZip = Get-ChildItem -LiteralPath $runDir -Filter '*.zip' | Select-Object -First 1
    if ($firstZip) { $zipPath = $firstZip.FullName }
  }

  $summary = [pscustomobject]@{
    ts = $ts
    run_dir = $runDir
    smoke_zip = $zipPath
    trades = $a.trades
    total_pnl = $a.total_pnl
    orphan_after = $or
    acceptance = $(if ($or -eq 0) { 'PASS' } else { 'FAIL' })
  }

  $outFile = Join-Path -Path $OutputDir -ChildPath ("smoke_summary_${ts}.json")
  $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outFile -Encoding UTF8
  Write-Host ("SMOKE_SUMMARY_PATH=" + $outFile)
}
catch {
  Write-Error $_
  exit 1
}
