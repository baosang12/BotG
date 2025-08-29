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
param(
  [switch]$OnlyCompressed,
  [switch]$OnlyRealtime
)

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
$allRuns = @(
  @{ Name = 'compressed_fillprob_0_9'; Pretty = 'compressed'; Seconds = 300; SecondsPerHour = 300; FillProb = 0.9; DrainSeconds = 30; Graceful = 5 },
  @{ Name = 'realtime_1h_fillprob_0_9'; Pretty = 'realtime'; Seconds = 3600; SecondsPerHour = 3600; FillProb = 0.9; DrainSeconds = 60; Graceful = 10 }
)

if ($OnlyCompressed -and $OnlyRealtime) { throw 'Cannot specify both -OnlyCompressed and -OnlyRealtime' }
$runs = if ($OnlyCompressed) { @($allRuns[0]) } elseif ($OnlyRealtime) { @($allRuns[1]) } else { $allRuns }

$results = @()

foreach ($r in $runs) {
  $ts = TimestampNow
  $baseOut = Join-Path $artifactsBase ("paper_run_" + $r.Name + "_" + $ts)
  New-Item -ItemType Directory -Path $baseOut -Force | Out-Null

  Write-Host "=== Starting run: $($r.Name) -> BASE OUTDIR = $baseOut ==="
  # Use an ASCII-safe temp directory as the actual artifact path to avoid non-ASCII issues
  $asciiTempBase = Join-Path $env:TEMP ("botg_artifacts_orchestrator_" + $r.Pretty + "_" + (TimestampNow))
  New-Item -ItemType Directory -Path $asciiTempBase -Force | Out-Null
  $tmpLog = Join-Path $baseOut ("run_smoke_" + $r.Pretty + "_" + (TimestampNow) + ".log")
  $tmpErr = Join-Path $baseOut ("run_smoke_" + $r.Pretty + "_" + (TimestampNow) + ".err.log")
  $qRunSmoke = '"' + $runSmoke + '"'
  $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $runSmoke,
    '-Seconds', [string]$r.Seconds,
    '-ArtifactPath', $asciiTempBase,
    '-FillProbability', [string]$r.FillProb,
    '-DrainSeconds', [string]$r.DrainSeconds,
    '-SecondsPerHour', [string]$r.SecondsPerHour,
    '-GracefulShutdownWaitSeconds', [string]$r.Graceful,
    '-UseSimulation')
  # Ensure the script path is quoted to survive spaces/diacritics in the repo path
  $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $qRunSmoke,
    '-Seconds', [string]$r.Seconds,
    '-ArtifactPath', $asciiTempBase,
    '-FillProbability', [string]$r.FillProb,
    '-DrainSeconds', [string]$r.DrainSeconds,
    '-SecondsPerHour', [string]$r.SecondsPerHour,
    '-GracefulShutdownWaitSeconds', [string]$r.Graceful,
    '-UseSimulation')
  $proc = Start-Process -FilePath 'powershell' -ArgumentList $argList -RedirectStandardOutput $tmpLog -RedirectStandardError $tmpErr -PassThru -WindowStyle Hidden
  $proc.WaitForExit()
  $outLines = @()
  try {
    if (Test-Path -LiteralPath $tmpLog) {
      $text = Get-Content -LiteralPath $tmpLog -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
      if ($text) { $outLines = $text -split "`r?`n" }
    }
  } catch {}
  $smokeLine = $outLines | Where-Object { $_ -like 'SMOKE_ARTIFACT_PATH=*' } | Select-Object -Last 1
  # Prefer discovering under the ASCII temp base that we provided
  $cand = Get-ChildItem -LiteralPath $asciiTempBase -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $cand -and $smokeLine) {
    try { $runDir = ($smokeLine -split '=',2)[1].Trim() } catch { $runDir = $null }
  } elseif ($cand) {
    $runDir = $cand.FullName
  }
  if (-not $runDir) {
    # Fallback: scan TEMP botg_artifacts* modified recently
    try {
      $tenMinAgo = (Get-Date).AddMinutes(-10)
      $tmp = Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'botg_artifacts*' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $tenMinAgo } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
      if ($tmp) {
        $cand2 = Get-ChildItem -LiteralPath $tmp.FullName -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($cand2) { $runDir = $cand2.FullName }
      }
    } catch {}
  }
  if (-not $runDir) { throw "Could not determine run output directory for $($r.Name)." }

  # If run_dir is outside repo (ASCII fallback into TEMP), copy its contents back into base_out for archival
  try {
    if ($runDir -and (Test-Path -LiteralPath $runDir)) {
  $leaf = Split-Path -Leaf $runDir
  $dest = Join-Path $baseOut $leaf
      if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
  # Use -Path (not -LiteralPath) to allow wildcard expansion for copying contents
  Copy-Item -Path (Join-Path $runDir '*') -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue
      # Prefer the copied path as canonical OUTDIR under repo
      $runDir = $dest
    }
  } catch {}

  # Validate artifacts
  $runMetaPath = Join-Path $runDir 'run_metadata.json'
  $ordersPath = Join-Path $runDir 'orders.csv'
  $reconReportPath = Join-Path $runDir 'reconstruct_report.json'
  $reconcileReportPath = Join-Path $runDir 'reconcile_report.json'
  $analysisSummaryPath = Join-Path $runDir 'analysis_summary.json'
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
  $analysisSummary = $null
  if (Test-Path -LiteralPath $analysisSummaryPath) { try { $analysisSummary = Get-Content -LiteralPath $analysisSummaryPath -Raw | ConvertFrom-Json } catch {} }

  # Derive common metrics
  $requests = $null; $fills = $null; $fillRate = $null; $closedCount = $null
  $orphanBefore = $null; $orphanAfter = $null; $totalPnl = $null; $maxDrawdown = $null
  if ($reconcileReport) {
    if ($reconcileReport.request_count) { $requests = [int]$reconcileReport.request_count }
    if ($reconcileReport.fill_count) { $fills = [int]$reconcileReport.fill_count }
    if ($reconcileReport.closed_trades_count) { $closedCount = [int]$reconcileReport.closed_trades_count }
    if ($reconcileReport.orphan_fills_count -ne $null) { $orphanBefore = [int]$reconcileReport.orphan_fills_count }
  }
  if ($requests -ne $null -and $fills -ne $null -and $requests -gt 0) { $fillRate = [math]::Round($fills / $requests, 4) }
  if ($analysisSummary) {
    if ($analysisSummary.trades -ne $null) { $closedCount = [int]$analysisSummary.trades }
    if ($analysisSummary.total_pnl -ne $null) { $totalPnl = [double]$analysisSummary.total_pnl }
    if ($analysisSummary.max_drawdown -ne $null) { $maxDrawdown = [double]$analysisSummary.max_drawdown }
  }
  if ($reconReport) {
    if ($reconReport.estimated_orphan_fills_after_reconstruct -ne $null) { $orphanAfter = [int]$reconReport.estimated_orphan_fills_after_reconstruct }
  } elseif ($reconcileReport -and $reconcileReport.orphan_fills_count -ne $null) {
    $orphanAfter = [int]$reconcileReport.orphan_fills_count
  }

  # Auto-retry up to 3 times with escalating drain/graceful/top-ups
  $retries = 0
  $retryPlans = @(
    @{ drainDelta = 60; gracefulDelta = 5; topups = '5,5,7,9' },
    @{ drainDelta = 120; gracefulDelta = 10; topups = '7,9,11,15,20' },
    @{ drainDelta = 180; gracefulDelta = 15; topups = '10,12,15,20,25' }
  )
  while ($orphanAfter -ne $null -and $orphanAfter -gt 0 -and $retries -lt $retryPlans.Count) {
    $plan = $retryPlans[$retries]
    $retries++
    Write-Warning "[orchestrator] Orphans detected ($orphanAfter). Retry #$retries with drain+=${($plan.drainDelta)}, graceful+=${($plan.gracefulDelta)}, topups=${($plan.topups)}"
    $asciiTempBase2 = Join-Path $env:TEMP ("botg_artifacts_orchestrator_retry_" + $r.Pretty + "_" + (TimestampNow))
    New-Item -ItemType Directory -Path $asciiTempBase2 -Force | Out-Null
    $tmpLog2 = Join-Path $baseOut ("run_smoke_" + $r.Pretty + "_retry_" + (TimestampNow) + ".log")
    $tmpErr2 = Join-Path $baseOut ("run_smoke_" + $r.Pretty + "_retry_" + (TimestampNow) + ".err.log")
    $qRunSmoke = '"' + $runSmoke + '"'
    $argList2 = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $qRunSmoke,
      '-Seconds', [string]$r.Seconds,
      '-ArtifactPath', $asciiTempBase2,
      '-FillProbability', [string]$r.FillProb,
      '-DrainSeconds', [string]([int]$r.DrainSeconds + [int]$plan.drainDelta),
      '-SecondsPerHour', [string]$r.SecondsPerHour,
      '-GracefulShutdownWaitSeconds', [string]([int]$r.Graceful + [int]$plan.gracefulDelta),
      '-TopUpSeconds', [string]$plan.topups,
      '-UseSimulation')
    $proc2 = Start-Process -FilePath 'powershell' -ArgumentList $argList2 -RedirectStandardOutput $tmpLog2 -RedirectStandardError $tmpErr2 -PassThru -WindowStyle Hidden
    $proc2.WaitForExit()
    $outLines2 = @(); $runDir2 = $null
    try {
      if (Test-Path -LiteralPath $tmpLog2) {
        $text2 = Get-Content -LiteralPath $tmpLog2 -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($text2) { $outLines2 = $text2 -split "`r?`n" }
      }
    } catch {}
    $smokeLine2 = $outLines2 | Where-Object { $_ -like 'SMOKE_ARTIFACT_PATH=*' } | Select-Object -Last 1
    $candR = Get-ChildItem -LiteralPath $asciiTempBase2 -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($candR) { $runDir2 = $candR.FullName } elseif ($smokeLine2) { try { $runDir2 = ($smokeLine2 -split '=',2)[1].Trim() } catch {} }
    if ($runDir2 -and (Test-Path -LiteralPath $runDir2)) {
      $leaf2 = Split-Path -Leaf $runDir2
      $dest2 = Join-Path $baseOut $leaf2
      if (-not (Test-Path -LiteralPath $dest2)) { New-Item -ItemType Directory -Path $dest2 -Force | Out-Null }
      Copy-Item -Path (Join-Path $runDir2 '*') -Destination $dest2 -Recurse -Force -ErrorAction SilentlyContinue
      $runDir = $dest2
      # Reload reports and metrics from retry
      $runMetaPath = Join-Path $runDir 'run_metadata.json'
      $ordersPath = Join-Path $runDir 'orders.csv'
      $reconReportPath = Join-Path $runDir 'reconstruct_report.json'
      $reconcileReportPath = Join-Path $runDir 'reconcile_report.json'
      $analysisSummaryPath = Join-Path $runDir 'analysis_summary.json'
      $zipCand = Get-ChildItem -LiteralPath $runDir -Filter '*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($zipCand) { $zipPath = $zipCand.FullName }
      try { $runMeta = Get-Content -LiteralPath $runMetaPath -Raw | ConvertFrom-Json } catch {}
      $reconReport = $null; if (Test-Path -LiteralPath $reconReportPath) { try { $reconReport = Get-Content -LiteralPath $reconReportPath -Raw | ConvertFrom-Json } catch {} }
      $reconcileReport = $null; if (Test-Path -LiteralPath $reconcileReportPath) { try { $reconcileReport = Get-Content -LiteralPath $reconcileReportPath -Raw | ConvertFrom-Json } catch {} }
      $analysisSummary = $null; if (Test-Path -LiteralPath $analysisSummaryPath) { try { $analysisSummary = Get-Content -LiteralPath $analysisSummaryPath -Raw | ConvertFrom-Json } catch {} }
      $requests = $null; $fills = $null; $fillRate = $null; $closedCount = $null
      $orphanBefore = $null; $orphanAfter = $null; $totalPnl = $null; $maxDrawdown = $null
      if ($reconcileReport) {
        if ($reconcileReport.request_count) { $requests = [int]$reconcileReport.request_count }
        if ($reconcileReport.fill_count) { $fills = [int]$reconcileReport.fill_count }
        if ($reconcileReport.closed_trades_count) { $closedCount = [int]$reconcileReport.closed_trades_count }
        if ($reconcileReport.orphan_fills_count -ne $null) { $orphanBefore = [int]$reconcileReport.orphan_fills_count }
      }
      if ($requests -ne $null -and $fills -ne $null -and $requests -gt 0) { $fillRate = [math]::Round($fills / $requests, 4) }
      if ($analysisSummary) {
        if ($analysisSummary.trades -ne $null) { $closedCount = [int]$analysisSummary.trades }
        if ($analysisSummary.total_pnl -ne $null) { $totalPnl = [double]$analysisSummary.total_pnl }
        if ($analysisSummary.max_drawdown -ne $null) { $maxDrawdown = [double]$analysisSummary.max_drawdown }
      }
      if ($reconReport) {
        if ($reconReport.estimated_orphan_fills_after_reconstruct -ne $null) { $orphanAfter = [int]$reconReport.estimated_orphan_fills_after_reconstruct }
      } elseif ($reconcileReport -and $reconcileReport.orphan_fills_count -ne $null) {
        $orphanAfter = [int]$reconcileReport.orphan_fills_count
      }
    } else {
      break
    }
  }

  $results += @{
    name = $r.Pretty
    status = 'COMPLETED'
    base_out = $baseOut
    run_dir = $runDir
    zip = $zipPath
  retries = $retries
    run_metadata = $runMeta
    reconstruct_report = $reconReport
    reconcile_report = $reconcileReport
    requests = $requests
    fills = $fills
    fill_rate = $fillRate
    closed_trades_count = $closedCount
    orphan_before = $orphanBefore
    orphan_after = $orphanAfter
    total_pnl = $totalPnl
    max_drawdown = $maxDrawdown
  }
}

$final = @{ timestamp = (Get-Date).ToString('s'); results = $results }
$finalJson = $final | ConvertTo-Json -Depth 8
$finalPath = Join-Path $artifactsBase ("combined_runs_report_" + $tsAll + ".json")
Write-Json -file $finalPath -obj $final -depth 8
Write-Output $finalJson
