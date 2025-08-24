<#
Runs two smokes and emits a combined JSON summary:
  1) Compressed 5-min (Seconds=300) with FillProb=0.9
  2) Real-time 1h   (Seconds=3600) with FillProb=0.9

Notes:
- This script invokes scripts/run_smoke.ps1 (which already reconstructs, reconciles, and zips).
- OutDir base is set per run; run_smoke creates an inner telemetry_run_<ts> folder and prints SMOKE_ARTIFACT_PATH=...; we parse that.
- Combined JSON is saved under artifacts_ascii/combined_runs_report_<ts>.json and written to stdout.

Usage:
  pwsh .\scripts\run_two_smokes_and_report.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function TimestampNow {
  return (Get-Date).ToString('yyyyMMdd_HHmmss')
}

function Write-Json([string]$file,[object]$obj,[int]$depth=6) {
  $dir = Split-Path -Parent $file
  if ($dir -and !(Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  $utf8NoBom = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllText($file, ($obj | ConvertTo-Json -Depth $depth), $utf8NoBom)
}

$repoRoot = (Resolve-Path '.').Path
$scriptsDir = Join-Path $repoRoot 'scripts'
$runSmoke = Join-Path $scriptsDir 'run_smoke.ps1'
if (-not (Test-Path -LiteralPath $runSmoke)) { throw "Missing $runSmoke" }

$artifactsBase = Join-Path $repoRoot 'artifacts_ascii'
New-Item -ItemType Directory -Force -Path $artifactsBase | Out-Null

$tsAll = TimestampNow

# Define runs (SecondsPerHour is recorded in metadata by run_smoke; Seconds controls actual duration)
$runs = @(
  @{ Name = 'compressed_fillprob_0_9'; Seconds = 300; SecondsPerHour = 300; FillProb = 0.9; DrainSeconds = 30; Graceful = 5 },
  @{ Name = 'realtime_1h_fillprob_0_9'; Seconds = 3600; SecondsPerHour = 3600; FillProb = 0.9; DrainSeconds = 60; Graceful = 10 }
)

$results = @()

foreach ($r in $runs) {
  $ts = TimestampNow
  $baseOut = Join-Path $artifactsBase ("paper_run_" + $r.Name + "_" + $ts)
  New-Item -ItemType Directory -Path $baseOut -Force | Out-Null

  $psArgs = @(
    '-Seconds', [string]$r.Seconds,
    '-ArtifactPath', $baseOut,
    '-FillProbability', [string]$r.FillProb,
    '-DrainSeconds', [string]$r.DrainSeconds,
    '-SecondsPerHour', [string]$r.SecondsPerHour,
    '-GracefulShutdownWaitSeconds', [string]$r.Graceful,
    '-UseSimulation'
  )

  Write-Host "=== Starting run: $($r.Name) -> BASE OUTDIR = $baseOut ==="
  $outLines = & $runSmoke @psArgs 2>&1
  $match = $outLines | Select-String -Pattern '^SMOKE_ARTIFACT_PATH=(.+)$'
  if (-not $match) {
    # Fallback: try to locate newest telemetry_run_* under baseOut
    $cand = Get-ChildItem -LiteralPath $baseOut -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $cand) { throw "Could not determine run output directory for $($r.Name)." }
    $runDir = $cand.FullName
  } else {
    $runDir = $match.Matches[0].Groups[1].Value.Trim()
  }

  # Validate artifacts
  $runMetaPath = Join-Path $runDir 'run_metadata.json'
  $ordersPath = Join-Path $runDir 'orders.csv'
  $reconReportPath = Join-Path $runDir 'reconstruct_report.json'
  $reconcileReportPath = Join-Path $runDir 'reconcile_report.json'
  $zipPath = $null

  $missing = @()
  foreach ($p in @($runMetaPath,$ordersPath)) { if (-not (Test-Path -LiteralPath $p)) { $missing += $p } }
  if ($missing.Count -gt 0) {
    $results += @{ name = $r.Name; status = 'MISSING_ARTIFACTS'; base_out = $baseOut; run_dir = $runDir; missing = $missing }
    continue
  }

  # Ensure a zip exists (run_smoke typically created one inside runDir)
  $zipCand = Get-ChildItem -LiteralPath $runDir -Filter '*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($zipCand) { $zipPath = $zipCand.FullName } else {
    try {
      Add-Type -AssemblyName System.IO.Compression.FileSystem
      $zipPath = Join-Path $runDir (Split-Path -Leaf $runDir + '.zip')
      if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
      [System.IO.Compression.ZipFile]::CreateFromDirectory($runDir, $zipPath)
    } catch { $zipPath = $null }
  }

  # Load JSONs if available
  $runMeta = $null
  try { $runMeta = Get-Content -LiteralPath $runMetaPath -Raw | ConvertFrom-Json } catch {}
  $reconReport = $null
  if (Test-Path -LiteralPath $reconReportPath) { try { $reconReport = Get-Content -LiteralPath $reconReportPath -Raw | ConvertFrom-Json } catch {} }
  $reconcileReport = $null
  if (Test-Path -LiteralPath $reconcileReportPath) { try { $reconcileReport = Get-Content -LiteralPath $reconcileReportPath -Raw | ConvertFrom-Json } catch {} }

  $results += @{
    name = $r.Name
    status = 'COMPLETED'
    base_out = $baseOut
    run_dir = $runDir
    zip = $zipPath
    run_metadata = $runMeta
    reconstruct_report = $reconReport
    reconcile_report = $reconcileReport
  }
}

$final = @{ timestamp = (Get-Date).ToString('s'); results = $results }
$finalJson = $final | ConvertTo-Json -Depth 8
$finalPath = Join-Path $artifactsBase ("combined_runs_report_" + $tsAll + ".json")
Write-Json -file $finalPath -obj $final -depth 8
Write-Output $finalJson
