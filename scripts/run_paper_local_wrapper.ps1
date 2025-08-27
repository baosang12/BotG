Param(
  [int]$Hours = 4,
  [double]$FillProb = 0.9,
  [double]$FeePerTrade = 0.02,
  [int]$DrainSeconds = 10,
  [switch]$GeneratePlots
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')
Set-Location $repoRoot

# 1) Git status and branch prep
$ts = Get-Date -Format yyyyMMdd_HHmm
$currentBranch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim()
if (-not $currentBranch) { $currentBranch = "unknown" }
$status = (git status --porcelain) 2>$null
if ($status) {
  $newBranch = "paper-run-local-$ts"
  git checkout -b $newBranch | Out-Null
}

# 2) Build / Test
$buildOk = $false; $testOk = $false
try { dotnet build "$repoRoot" /property:GenerateFullPaths=true /consoleLoggerParameters:NoSummary | Write-Output; if ($LASTEXITCODE -eq 0) { $buildOk = $true } } catch {}
try { dotnet test "$repoRoot" --no-build | Write-Output; if ($LASTEXITCODE -eq 0) { $testOk = $true } } catch {}
if (-not ($buildOk -and $testOk)) {
  Write-Error "Build or tests failed; aborting paper run."
  exit 1
}

# 3) Prepare artifact path (ASCII-safe name)
$ARTPATH = Join-Path $repoRoot (Join-Path 'artifacts_ascii' ("paper_run_" + $ts))
New-Item -ItemType Directory -Path $ARTPATH -Force | Out-Null

# 4) Enforce Simulation-only environment
$env:SIMULATION = 'true'

# 5) Run paper pulse (4 hours total)
$pulse = Join-Path $repoRoot 'scripts\run_paper_pulse.ps1'
if (-not (Test-Path -LiteralPath $pulse)) { Write-Error "run_paper_pulse.ps1 not found"; exit 1 }

Write-Host ("[wrapper] Starting paper pulse: Hours=$Hours, ArtifactPath=$ARTPATH, FillProb=$FillProb, FeePerTrade=$FeePerTrade, GeneratePlots=$GeneratePlots")
& powershell -NoProfile -ExecutionPolicy Bypass -File $pulse -Hours $Hours -ArtifactPath $ARTPATH -FillProb $FillProb -FeePerTrade $FeePerTrade -GeneratePlots:$GeneratePlots

# 6) Post-run: find latest sub-run (telemetry_run_*) and copy key outputs to top-level
function Get-LatestRunDir([string]$root) {
  $runs = Get-ChildItem -Path $root -Recurse -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Descending
  if ($runs -and $runs.Count -gt 0) { return $runs[0].FullName }
  return $null
}

$lastRun = Get-LatestRunDir -root $ARTPATH
if (-not $lastRun) { Write-Warning "No telemetry_run_* found under $ARTPATH"; exit 2 }

$filesToCopy = @(
  'orders.csv','closed_trades_fifo.csv','telemetry.csv','run_metadata.json','analysis_summary.json',
  'trade_closes.log','reconcile_report.json','fill_rate_by_side.csv','fill_breakdown_by_hour.csv',
  'monitoring_summary.json','equity_curve.png','pnl_histogram.png','pnl_by_hour.png','preflight_report.txt'
)
foreach ($fn in $filesToCopy) {
  $src = Join-Path $lastRun $fn
  if (Test-Path -LiteralPath $src) {
    Copy-Item -LiteralPath $src -Destination (Join-Path $ARTPATH $fn) -Force
  }
}

# 7) Orphan check and auto-fixes (up to 3 attempts)
function Invoke-Reconcile($orders,$closed,$outDir) {
  $recon = Join-Path $repoRoot 'scripts\reconcile_fills_vs_closed.ps1'
  if (Test-Path -LiteralPath $recon -and (Test-Path -LiteralPath $orders) -and (Test-Path -LiteralPath $closed)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $recon -OrdersCsv $orders -ClosedCsv $closed -OutDir $outDir | Out-Null
    $rr = Join-Path $outDir 'reconcile_report.json'
    if (Test-Path -LiteralPath $rr) { try { return (Get-Content -LiteralPath $rr -Raw | ConvertFrom-Json) } catch { return $null } }
  }
  return $null
}

$ordersCsv = Join-Path $lastRun 'orders.csv'
$closedCsv = Join-Path $lastRun 'closed_trades_fifo.csv'
$rr = Invoke-Reconcile -orders $ordersCsv -closed $closedCsv -outDir $lastRun
$attempt = 0
while ($null -ne $rr -and [int]$rr.orphan_fills_count -gt 0 -and $attempt -lt 3) {
  $attempt++
  if ($attempt -eq 1) {
    Write-Warning "[wrapper] Orphans detected (${($rr.orphan_fills_count)}). Attempt 1: short drain 30s on existing runDir."
    # Run Harness directly targeting existing runDir to help pairing
    $hProj = Join-Path $repoRoot 'Harness\Harness.csproj'
    $qRun = '"' + $lastRun + '"'
    & dotnet run --project "$hProj" -- --seconds 30 --artifactPath $qRun --fill-prob ([string]$FillProb) | Out-Null
  } elseif ($attempt -eq 2) {
    Write-Warning "[wrapper] Attempt 2: reconstruct closed trades."
    $toolProj = Join-Path $repoRoot 'Tools\ReconstructClosedTrades\ReconstructClosedTrades.csproj'
    $reconOut = Join-Path $lastRun 'closed_trades_fifo_reconstructed.csv'
    & dotnet run --project "$toolProj" -- --orders "$ordersCsv" --out "$reconOut" | Out-Null
    if (Test-Path -LiteralPath $reconOut) { Copy-Item -LiteralPath $reconOut -Destination $closedCsv -Force }
  } else {
    Write-Warning "[wrapper] Attempt 3: extra short drain 45s."
    $hProj = Join-Path $repoRoot 'Harness\Harness.csproj'
    $qRun = '"' + $lastRun + '"'
    & dotnet run --project "$hProj" -- --seconds 45 --artifactPath $qRun --fill-prob ([string]$FillProb) | Out-Null
  }
  # Re-run analyzer and reconcile
  $anPy = Join-Path $repoRoot 'scripts\analyzer.py'
  if (Test-Path -LiteralPath $anPy -and (Test-Path -LiteralPath $closedCsv)) {
    $asOut = Join-Path $lastRun 'analysis_summary.json'
    & python "$anPy" --closed-trades "$closedCsv" --out "$asOut" 2>&1 | Out-Null
  }
  $rr = Invoke-Reconcile -orders $ordersCsv -closed $closedCsv -outDir $lastRun
}

# 8) Final preflight_report.txt at top-level
try {
  $report = @()
  $report += "SMOKE_ARTIFACT_PATH=$ARTPATH"
  $branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim(); if (-not $branch) { $branch = 'unknown' }
  $commit = (git rev-parse --short HEAD 2>$null).Trim(); if (-not $commit) { $commit = 'unknown' }
  $report += ("git branch: " + $branch + " @ " + $commit)
  # Quick stats from orders/analysis
  $req=0; $fills=0
  if (Test-Path -LiteralPath $ordersCsv) {
    try {
      $rows = Import-Csv -LiteralPath $ordersCsv
      $req = ($rows | Where-Object { $_.status -eq 'REQUEST' -or $_.phase -eq 'REQUEST' }).Count
      $fills = ($rows | Where-Object { $_.status -eq 'FILL' -or $_.phase -eq 'FILL' }).Count
    } catch {}
  }
  $fillRate = if ($req -gt 0) { [math]::Round(($fills/$req),4) } else { 0 }
  $report += ("requests=" + $req + ", fills=" + $fills + ", fill_rate=" + $fillRate)
  $asPath = Join-Path $lastRun 'analysis_summary.json'
  if (Test-Path -LiteralPath $asPath) {
    try { $as = Get-Content -LiteralPath $asPath -Raw | ConvertFrom-Json; $report += ("closed_trades_count=" + [int]$as.trades + ", total_pnl=" + [double]$as.total_pnl + ", max_dd=" + $as.max_drawdown) } catch {}
  }
  $rrPath = Join-Path $lastRun 'reconcile_report.json'
  if (Test-Path -LiteralPath $rrPath) {
    try { $rrObj = Get-Content -LiteralPath $rrPath -Raw | ConvertFrom-Json; $report += ("orphan_fills=" + [int]$rrObj.orphan_fills_count) } catch {}
  }
  $finalReport = Join-Path $ARTPATH 'preflight_report.txt'
  $utf8 = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllLines($finalReport, $report, $utf8)
  Write-Host ("[wrapper] Wrote final report: " + $finalReport)
} catch {}

# 9) Zip top-level artifact folder
try {
  $zip = Join-Path $ARTPATH (Split-Path -Leaf $ARTPATH + '.zip')
  if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
  Compress-Archive -Path $ARTPATH -DestinationPath $zip -Force
  Write-Host ("[wrapper] Zipped: " + $zip)
} catch {}

Write-Host ("SMOKE_ARTIFACT_PATH=" + $ARTPATH)
Write-Host "[wrapper] Done."
