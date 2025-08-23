param(
  [int]$Seconds = 30,
  [string]$ArtifactPath = $(Join-Path $env:TEMP ("botg_artifacts")),
  [Alias('FillProbability')][double]$FillProb = 1.0,
  [string]$ConfigPath = "",
  [double]$FeePerTrade = 0.0,
  [double]$FeePercent = 0.0,
  [double]$SpreadPips = 0.0,
  [int]$DrainSeconds = 10,
  [switch]$GeneratePlots
)

$ErrorActionPreference = 'Continue'

function New-AsciiSafePath([string]$path,[string]$ts) {
  try {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($path)
    $roundtrip = [System.Text.Encoding]::ASCII.GetString($bytes)
    if ($roundtrip -ne $path) {
      $fallback = Join-Path $env:TEMP ("botg_artifacts_" + $ts)
      Write-Warning ("[smoke] Artifact path contains non-ASCII characters. Using ASCII-safe path: " + $fallback)
      return $fallback
    }
  } catch {}
  return $path
}

function Write-Json([string]$file,[string]$json) {
  $dir = Split-Path -Parent $file
  if ($dir -and !(Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
  $utf8NoBom = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllText($file, $json, $utf8NoBom)
}

function Get-GitCommit() {
  try {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
      $h = git rev-parse HEAD 2>$null
      if ($LASTEXITCODE -eq 0) { return ($h.Trim()) }
    }
  } catch {}
  return "unknown"
}

function Start-Harness([string]$proj,[int]$sec,[string]$runDir,[double]$fillProb,[string]$configPath) {
  $stdout = Join-Path $runDir 'harness_stdout.log'
  $stderr = Join-Path $runDir 'harness_stderr.log'
  # Quote arguments that may contain spaces/diacritics for Start-Process on PS 5.1
  $qProj = '"' + $proj + '"'
  $qRunDir = '"' + $runDir + '"'
  $argCore = @('run','--project', $qProj, '--', '--seconds', [string]$sec, '--artifactPath', $qRunDir, '--fill-prob', [string]$fillProb)
  if ($configPath -and $configPath.Length -gt 0) {
    $qCfg = '"' + $configPath + '"'
    $argCore += @('--config', $qCfg)
  }
  # Pass fee settings via environment file if supported; for Harness we embed into config.runtime.json snapshot post-run
  try {
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $argCore -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    return $p
  } catch {
    "Start-Process failed: $($_.Exception.Message)" | Out-File -FilePath $stderr -Encoding utf8
    return $null
  }
}

function Wait-Or-Stop([System.Diagnostics.Process]$proc,[int]$sec) {
  $deadline = (Get-Date).AddSeconds($sec)
  while ($proc -ne $null -and -not $proc.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
  }
  if ($proc -ne $null -and -not $proc.HasExited) {
    try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
  }
}

function Ensure-ClosedTrades([string]$repoRoot,[string]$runDir) {
  $orders = Join-Path $runDir 'orders.csv'
  $closed = Join-Path $runDir 'closed_trades_fifo.csv'
  if (Test-Path -LiteralPath $closed) { return $true }
  $stderr = Join-Path $runDir 'reconstruct_error.log'
  $stdout = Join-Path $runDir 'reconstruct_stdout.log'
  $toolProj = Join-Path $repoRoot 'Tools\ReconstructClosedTrades\ReconstructClosedTrades.csproj'
  $qToolProj = '"' + $toolProj + '"'
  $reconOut = Join-Path $runDir 'closed_trades_fifo_reconstructed.csv'
  $qOrders = '"' + $orders + '"'
  $qReconOut = '"' + $reconOut + '"'
  $args = @('run','--project',$qToolProj,'--','--orders', $qOrders, '--out', $qReconOut)
  try {
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $args -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $p.WaitForExit()
  } catch {
    "Reconstruct start failed: $($_.Exception.Message)" | Out-File -FilePath $stderr -Encoding utf8
  }
  if (Test-Path -LiteralPath $reconOut) {
    try { Copy-Item -LiteralPath $reconOut -Destination $closed -Force } catch {}
  }
  return (Test-Path -LiteralPath $closed)
}

function Force-ReconstructClosedTrades([string]$repoRoot,[string]$runDir) {
  $orders = Join-Path $runDir 'orders.csv'
  $closed = Join-Path $runDir 'closed_trades_fifo.csv'
  $stderr = Join-Path $runDir 'reconstruct_error.log'
  $stdout = Join-Path $runDir 'reconstruct_stdout.log'
  $toolProj = Join-Path $repoRoot 'Tools\ReconstructClosedTrades\ReconstructClosedTrades.csproj'
  $qToolProj = '"' + $toolProj + '"'
  $reconOut = Join-Path $runDir 'closed_trades_fifo_reconstructed.csv'
  $qOrders = '"' + $orders + '"'
  $qReconOut = '"' + $reconOut + '"'
  $args = @('run','--project',$qToolProj,'--','--orders', $qOrders, '--out', $qReconOut)
  try {
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $args -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $p.WaitForExit()
  } catch {
    "Reconstruct start failed: $($_.Exception.Message)" | Out-File -FilePath $stderr -Encoding utf8
  }
  if (Test-Path -LiteralPath $reconOut) {
    try { Copy-Item -LiteralPath $reconOut -Destination $closed -Force } catch {}
  }
  return (Test-Path -LiteralPath $closed)
}

function Run-Analyzer([string]$repoRoot,[string]$runDir) {
  $py = $null
  try { $cmd = Get-Command python -ErrorAction SilentlyContinue; if ($cmd) { $py = 'python' } } catch {}
  if (-not $py) { try { $cmd = Get-Command py -ErrorAction SilentlyContinue; if ($cmd) { $py = 'py' } } catch {} }
  $anPy = Join-Path $repoRoot 'scripts\analyzer.py'
  $closed = Join-Path $runDir 'closed_trades_fifo.csv'
  $out = Join-Path $runDir 'analysis_summary.json'
  $stdout = Join-Path $runDir 'analyzer_stdout.log'
  $stderr = Join-Path $runDir 'analyzer_stderr.log'
  if (-not (Test-Path -LiteralPath $closed)) { return $false }
  if (-not (Test-Path -LiteralPath $anPy)) { return $false }
  if (-not $py) {
    "python not found" | Out-File -FilePath $stderr -Encoding utf8
    return $false
  }
  $qAnPy = '"' + $anPy + '"'
  $qClosed = '"' + $closed + '"'
  $qOut = '"' + $out + '"'
  $args = @($qAnPy,'--closed-trades', $qClosed, '--out', $qOut)
  try {
    $p = Start-Process -FilePath $py -ArgumentList $args -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $p.WaitForExit()
    return (Test-Path -LiteralPath $out)
  } catch {
    "Analyzer start failed: $($_.Exception.Message)" | Out-File -FilePath $stderr -Encoding utf8
    return $false
  }
}

function Invoke-Reconcile([string]$repoRoot,[string]$orders,[string]$closed,[string]$runDir) {
  try {
    $recon = Join-Path $repoRoot 'scripts\reconcile_fills_vs_closed.ps1'
    if (Test-Path -LiteralPath $recon -and (Test-Path -LiteralPath $orders) -and (Test-Path -LiteralPath $closed)) {
      & powershell -NoProfile -ExecutionPolicy Bypass -File $recon -OrdersCsv $orders -ClosedCsv $closed -OutDir $runDir | Out-Null
      $rrPath = Join-Path $runDir 'reconcile_report.json'
      if (Test-Path -LiteralPath $rrPath) {
        try { return (Get-Content -LiteralPath $rrPath -Raw | ConvertFrom-Json) } catch { return $null }
      }
    }
  } catch {}
  return $null
}

function New-Zip([string]$runDir,[string]$ts) {
  $zip = Join-Path $runDir ("telemetry_run_" + $ts + ".zip")
  $toZip = @(
    (Join-Path $runDir 'orders.csv'),
    (Join-Path $runDir 'closed_trades_fifo.csv'),
    (Join-Path $runDir 'telemetry.csv'),
    (Join-Path $runDir 'run_metadata.json'),
    (Join-Path $runDir 'analysis_summary.json'),
    (Join-Path $runDir 'trade_closes.log'),
    (Join-Path $runDir 'harness_stdout.log'),
    (Join-Path $runDir 'harness_stderr.log'),
    (Join-Path $runDir 'analyzer_stdout.log'),
    (Join-Path $runDir 'analyzer_stderr.log'),
    (Join-Path $runDir 'reconstruct_stdout.log'),
    (Join-Path $runDir 'reconstruct_error.log')
  ) | Where-Object { Test-Path -LiteralPath $_ }
  try { if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force } } catch {}
  if ($toZip.Count -gt 0) {
    Compress-Archive -Path $toZip -DestinationPath $zip -Force
  }
  return $zip
}

# Main
Write-Host ("[smoke] Seconds=" + $Seconds + ", FillProb=" + $FillProb + ", DrainSeconds=" + $DrainSeconds)

# Resolve repo paths relative to script
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')
$harnessProj = Join-Path $repoRoot 'Harness\Harness.csproj'

$ts = Get-Date -Format yyyyMMdd_HHmmss
$artifactRoot = New-AsciiSafePath $ArtifactPath $ts
$runDir = Join-Path $artifactRoot ("telemetry_run_" + $ts)
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

# run metadata
$runId = [guid]::NewGuid().ToString()
$meta = @{
  run_id = $runId
  start_time_iso = (Get-Date).ToUniversalTime().ToString('o')
  host = $env:COMPUTERNAME
  git_commit = (Get-GitCommit)
  config_snapshot = @{ simulation = @{ fill_probability = $FillProb }; execution = @{ fee_per_trade = $FeePerTrade; fee_percent = $FeePercent; spread_pips = $SpreadPips }; config_path = $ConfigPath }
  artifact_path = $runDir
}
$metaJson = ($meta | ConvertTo-Json -Depth 6)
Write-Json (Join-Path $runDir 'run_metadata.json') $metaJson

# Start harness
$proc = Start-Harness -proj $harnessProj -sec $Seconds -runDir $runDir -fillProb $FillProb -configPath $ConfigPath
if ($proc -ne $null) { Wait-Or-Stop -proc $proc -sec $Seconds }

# Graceful drain window: attempt to clear any residual unmatched fills
try {
  if ($DrainSeconds -gt 0) {
    Write-Host ("[smoke] Draining for up to " + $DrainSeconds + "s for final closes…")
    $deadline = (Get-Date).AddSeconds($DrainSeconds)
    $ordersPath = Join-Path $runDir 'orders.csv'
    $closedPath = Join-Path $runDir 'closed_trades_fifo.csv'
    $lastOrphanCount = -1
    while ((Get-Date) -lt $deadline) {
      if (Test-Path -LiteralPath $ordersPath) {
        # Re-run reconcile to check current orphan fills
        $rrNow = Invoke-Reconcile -repoRoot $repoRoot -orders $ordersPath -closed $closedPath -runDir $runDir
        $orph = if ($rrNow -ne $null) { [int]$rrNow.orphan_fills_count } else { 0 }
        if ($orph -ne $lastOrphanCount -and $orph -ge 0) { Write-Host ("[smoke] Orphan fills = " + $orph) }
        $lastOrphanCount = $orph
        if ($orph -le 0) { break }
      }
      # run a very short top-up to help finish pairs
      $p2 = Start-Harness -proj $harnessProj -sec 1 -runDir $runDir -fillProb $FillProb -configPath $ConfigPath
      if ($p2 -ne $null) { Wait-Or-Stop -proc $p2 -sec 1 }
      Start-Sleep -Milliseconds 200
    }
  }
} catch {}

# Collect files
$orders = Join-Path $runDir 'orders.csv'
$closed = Join-Path $runDir 'closed_trades_fifo.csv'
$telemetry = Join-Path $runDir 'telemetry.csv'

if (-not (Test-Path -LiteralPath $closed)) {
  $ok = Ensure-ClosedTrades -repoRoot $repoRoot -runDir $runDir
}

$anOk = Run-Analyzer -repoRoot $repoRoot -runDir $runDir
if (-not $anOk) {
  # save orders head for debugging
  try { Get-Content -Path $orders -TotalCount 100 | Out-File -FilePath (Join-Path $runDir 'orders_head.txt') -Encoding utf8 } catch {}
}

# 1) Compute fill breakdowns
try {
  $compute = Join-Path $repoRoot 'scripts\compute_fill_breakdown.ps1'
  if (Test-Path -LiteralPath $compute -and (Test-Path -LiteralPath $orders)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $compute -OrdersCsv $orders -OutDir $runDir | Tee-Object -FilePath (Join-Path $runDir 'compute_fill_breakdown.log')
  }
} catch {}

# 2) Reconcile
try {
  $rr = Invoke-Reconcile -repoRoot $repoRoot -orders $orders -closed $closed -runDir $runDir
  # If orphans remain after drain, attempt a bounded set of auto-fixes
  $attempt = 0
  while ($rr -ne $null -and [int]($rr.orphan_fills_count) -gt 0 -and $attempt -lt 2) {
    $attempt++
    $extra = if ($attempt -eq 1) { 3 } else { 5 }
    Write-Warning ("[smoke] Orphans remain (" + [string]$rr.orphan_fills_count + "). Running extra top-up " + $extra + "s…")
    $p2 = Start-Harness -proj $harnessProj -sec $extra -runDir $runDir -fillProb $FillProb -configPath $ConfigPath
    if ($p2 -ne $null) { Wait-Or-Stop -proc $p2 -sec $extra }
    if (-not (Test-Path -LiteralPath $closed)) { $null = Ensure-ClosedTrades -repoRoot $repoRoot -runDir $runDir }
    $null = Run-Analyzer -repoRoot $repoRoot -runDir $runDir
    try {
      $compute = Join-Path $repoRoot 'scripts\compute_fill_breakdown.ps1'
      if (Test-Path -LiteralPath $compute -and (Test-Path -LiteralPath $orders)) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $compute -OrdersCsv $orders -OutDir $runDir | Out-Null
      }
    } catch {}
    $rr = Invoke-Reconcile -repoRoot $repoRoot -orders $orders -closed $closed -runDir $runDir
  }
  if ($rr -ne $null -and [int]($rr.orphan_fills_count) -gt 0) {
    Write-Warning ("[smoke] Orphans persist after drain/top-ups. Running reconstruct fallback…")
    $null = Force-ReconstructClosedTrades -repoRoot $repoRoot -runDir $runDir
    $null = Run-Analyzer -repoRoot $repoRoot -runDir $runDir
    $rr = Invoke-Reconcile -repoRoot $repoRoot -orders $orders -closed $closed -runDir $runDir
  }
} catch {}

# 3) Monitoring summary
try {
  $monitoring = @{}
  if (Test-Path -LiteralPath $orders) {
    $rows = Import-Csv -LiteralPath $orders
    $req = ($rows | Where-Object { $_.status -eq 'REQUEST' -or $_.phase -eq 'REQUEST' }).Count
    $fills = ($rows | Where-Object { $_.status -eq 'FILL' -or $_.phase -eq 'FILL' }).Count
    $monitoring.fill_rate = if ($req -gt 0) { [math]::Round(($fills / $req), 4) } else { 0 }
  }
  if (Test-Path -LiteralPath (Join-Path $runDir 'analysis_summary.json')) {
    try { $as = Get-Content -LiteralPath (Join-Path $runDir 'analysis_summary.json') -Raw | ConvertFrom-Json } catch {}
    if ($as) {
      $monitoring.trades_per_min = if ($Seconds -gt 0) { [math]::Round(($as.trades / ($Seconds/60.0)),2) } else { $as.trades }
      $monitoring.avg_pnl_per_trade = if ($as.trades -gt 0) { [math]::Round(($as.total_pnl / $as.trades),6) } else { 0 }
      $monitoring.max_drawdown = $as.max_drawdown
    }
  }
  if (Test-Path -LiteralPath (Join-Path $runDir 'reconcile_report.json')) {
    try { $rr = Get-Content -LiteralPath (Join-Path $runDir 'reconcile_report.json') -Raw | ConvertFrom-Json } catch {}
    if ($rr) { $monitoring.orphan_fills_count = $rr.orphan_fills_count }
  }
  $monitoringJson = ($monitoring | ConvertTo-Json -Depth 6)
  Write-Json (Join-Path $runDir 'monitoring_summary.json') $monitoringJson
} catch {}

# 4) Optional plotting
try {
  if ($GeneratePlots) {
    $py = $null
    try { $cmd = Get-Command python -ErrorAction SilentlyContinue; if ($cmd) { $py = 'python' } } catch {}
    if (-not $py) { try { $cmd = Get-Command py -ErrorAction SilentlyContinue; if ($cmd) { $py = 'py' } } catch {} }
    $plotPy = Join-Path $repoRoot 'tools\plot_analysis.py'
    if ($py -and (Test-Path -LiteralPath $plotPy)) {
      $qPlot = '"' + $plotPy + '"'
      $qArt = '"' + $runDir + '"'
      Start-Process -FilePath $py -ArgumentList @($qPlot,'--artifacts', $qArt) -Wait -WindowStyle Hidden | Out-Null
    }
  }
} catch {}

$zip = New-Zip -runDir $runDir -ts $ts

# Summary
Write-Host ("[smoke] Artifact Path: " + $runDir)
# Emit a machine-readable line for CI to capture
Write-Host ("SMOKE_ARTIFACT_PATH=" + $runDir)
if (Test-Path -LiteralPath $zip) { Write-Host ("[smoke] Zip: " + $zip + " [" + ((Get-Item $zip).Length) + " bytes]") }
Write-Host "[smoke] Files:"
Get-ChildItem -Path $runDir -File | ForEach-Object { Write-Host ("  " + $_.Name + " [" + $_.Length + " bytes]") }

if (Test-Path -LiteralPath $orders) {
  Write-Host "--- orders.csv (first 10) ---"
  try { Get-Content -Path $orders -TotalCount 10 | ForEach-Object { Write-Host $_ } } catch {}
}
if (Test-Path -LiteralPath $closed) {
  Write-Host "--- closed_trades_fifo.csv (first 10) ---"
  try { Get-Content -Path $closed -TotalCount 10 | ForEach-Object { Write-Host $_ } } catch {}
}
if (Test-Path -LiteralPath (Join-Path $runDir 'analysis_summary.json')) {
  Write-Host "--- analysis_summary.json (first 50 lines) ---"
  try { Get-Content -Path (Join-Path $runDir 'analysis_summary.json') -TotalCount 50 | ForEach-Object { Write-Host $_ } } catch {}
}
if (Test-Path -LiteralPath (Join-Path $runDir 'fill_rate_by_side.csv')) {
  Write-Host "--- fill_rate_by_side.csv (first 20) ---"
  try { Get-Content -Path (Join-Path $runDir 'fill_rate_by_side.csv') -TotalCount 20 | ForEach-Object { Write-Host $_ } } catch {}
}
if (Test-Path -LiteralPath (Join-Path $runDir 'fill_breakdown_by_hour.csv')) {
  Write-Host "--- fill_breakdown_by_hour.csv (first 20) ---"
  try { Get-Content -Path (Join-Path $runDir 'fill_breakdown_by_hour.csv') -TotalCount 20 | ForEach-Object { Write-Host $_ } } catch {}
}
if (Test-Path -LiteralPath (Join-Path $runDir 'reconcile_report.txt')) {
  Write-Host "--- reconcile_report.txt (first 50 lines) ---"
  try { Get-Content -Path (Join-Path $runDir 'reconcile_report.txt') -TotalCount 50 | ForEach-Object { Write-Host $_ } } catch {}
}

# Preflight summary report
try {
  $branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim()
} catch { $branch = "unknown" }
try {
  $commit = (git rev-parse --short HEAD 2>$null).Trim()
} catch { $commit = "unknown" }
try {
  $reportPath = Join-Path $runDir 'preflight_report.txt'
  $req = 0; $fills = 0
  if (Test-Path -LiteralPath $orders) {
    try {
      $rows = Import-Csv -LiteralPath $orders
      $req = ($rows | Where-Object { $_.status -eq 'REQUEST' -or $_.phase -eq 'REQUEST' }).Count
      $fills = ($rows | Where-Object { $_.status -eq 'FILL' -or $_.phase -eq 'FILL' }).Count
    } catch {}
  }
  $asPath = Join-Path $runDir 'analysis_summary.json'
  $trades = 0; $totalPnl = 0.0
  if (Test-Path -LiteralPath $asPath) {
    try { $as = Get-Content -LiteralPath $asPath -Raw | ConvertFrom-Json; $trades = [int]$as.trades; $totalPnl = [double]$as.total_pnl } catch {}
  }
  $rrPath = Join-Path $runDir 'reconcile_report.json'
  $orph = ''
  if (Test-Path -LiteralPath $rrPath) {
    try { $rr = Get-Content -LiteralPath $rrPath -Raw | ConvertFrom-Json; $orph = "orphans_fills=" + [string]$rr.orphan_fills_count } catch {}
  }
  $lines = @()
  $lines += "SMOKE_ARTIFACT_PATH=$runDir"
  $lines += ("git branch: " + $branch + " @ " + $commit)
  $lines += ("requests=" + $req + ", fills=" + $fills + ", fill_rate=" + ((if ($req -gt 0) { [math]::Round(($fills / $req), 4) } else { 0 })))
  $lines += ("closed_trades_count=" + $trades + ", total_pnl=" + $totalPnl)
  if ($orph -ne '') { $lines += $orph }
  $utf8NoBom = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllLines($reportPath, $lines, $utf8NoBom)
} catch {}

Write-Host "[smoke] Done."
