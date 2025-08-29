# Collect latest BotG logs into a timestamped folder, compute summary metrics, and zip artifacts.
param(
  [string]$LogDir = $env:BOTG_LOG_PATH
)

if (-not $LogDir) { $LogDir = ($env:BOTG_LOG_PATH ? $env:BOTG_LOG_PATH : 'D:\botg\logs') }
Write-Host "[INFO] Using LogDir = $LogDir"
if (-not (Test-Path -LiteralPath $LogDir)) { throw "Log directory not found: $LogDir" }

# Find latest telemetry_run_* under LogDir\artifacts, fallback to LogDir root files
function Get-LatestRunDir([string]$base) {
  $art = Join-Path $base 'artifacts'
  if (Test-Path -LiteralPath $art) {
    $runs = Get-ChildItem -LiteralPath $art -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Descending
    if ($runs -and $runs.Count -gt 0) { return $runs[0].FullName }
  }
  return $null
}

$latestRun = Get-LatestRunDir -base $LogDir
if ($latestRun) { Write-Host "[INFO] Latest runDir: $latestRun" } else { Write-Warning "[WARN] No telemetry_run_* found. Falling back to root files." }

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$outDir = Join-Path $LogDir ("smoke_{0}" -f $ts)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function Copy-IfExists([string]$path) {
  if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $outDir -Force }
}

# Prefer files from latest runDir; else from LogDir root (risk_snapshots is at base)
$orders = if ($latestRun) { Join-Path $latestRun 'orders.csv' } else { Join-Path $LogDir 'orders.csv' }
$tele = if ($latestRun) { Join-Path $latestRun 'telemetry.csv' } else { Join-Path $LogDir 'telemetry.csv' }
$sizecmp = if ($latestRun) { Join-Path $latestRun 'size_comparison.json' } else { Join-Path $LogDir 'size_comparison.json' }
$runmeta = if ($latestRun) { Join-Path $latestRun 'run_metadata.json' } else { $null }
$closed = if ($latestRun) { Join-Path $latestRun 'closed_trades_fifo.csv' } else { Join-Path $LogDir 'closed_trades_fifo.csv' }
$tradecl = if ($latestRun) { Join-Path $latestRun 'trade_closes.log' } else { Join-Path $LogDir 'trade_closes.log' }
$analysis = if ($latestRun) { Join-Path $latestRun 'analysis_summary.json' } else { Join-Path $LogDir 'analysis_summary.json' }
$recon = if ($latestRun) { Join-Path $latestRun 'reconcile_report.json' } else { Join-Path $LogDir 'reconcile_report.json' }
$risk = Join-Path $LogDir 'risk_snapshots.csv'

Copy-IfExists $orders
Copy-IfExists $tele
Copy-IfExists $sizecmp
if ($runmeta) { Copy-IfExists $runmeta }
Copy-IfExists $closed
Copy-IfExists $tradecl
Copy-IfExists $analysis
Copy-IfExists $recon
Copy-IfExists $risk

function Convert-ToInvariantDouble($s) { if ($null -eq $s -or $s -eq '') { return $null }; try { return [double]::Parse(($s.ToString()), [System.Globalization.CultureInfo]::InvariantCulture) } catch { return $null } }

$summary = [ordered]@{}
$summary.simulated = $true
$summary.run_dir = ($latestRun ? $latestRun : $LogDir)

if (Test-Path (Join-Path $outDir 'orders.csv')) {
  $rows = Import-Csv (Join-Path $outDir 'orders.csv')
  $req = $rows | Where-Object { $_.phase -eq 'REQUEST' }
  $ack = $rows | Where-Object { $_.phase -eq 'ACK' }
  $fill = $rows | Where-Object { $_.phase -eq 'FILL' }
  $summary.counts = @{ REQUEST = $req.Count; ACK = $ack.Count; FILL = $fill.Count }
  $summary.fill_rate = if ($req.Count -gt 0) { [math]::Round($fill.Count / $req.Count, 4) } else { 0 }

  $slips = @(); foreach ($r in $fill) {
    $intended = Convert-ToInvariantDouble $r.intendedPrice; $exec = Convert-ToInvariantDouble $r.execPrice; $sl = Convert-ToInvariantDouble $r.slippage
    if ($null -ne $exec -and $null -ne $intended) { $slips += ($exec - $intended) } elseif ($null -ne $sl) { $slips += $sl }
  }
  $summary.avg_slippage = if ($slips.Count -gt 0) { [math]::Round(($slips | Measure-Object -Average).Average, 10) } else { $null }

  $req50 = $req | Select-Object -Last 50
  $mismatches = @(); foreach ($r in $req50) {
    $tu = Convert-ToInvariantDouble $r.theoretical_units; $rv = Convert-ToInvariantDouble $r.requestedVolume; if ($null -ne $tu -and $null -ne $rv -and $tu -ne $rv) { $mismatches += [pscustomobject]@{ orderId=$r.orderId; theoretical_units=$tu; requestedVolume=$rv } }
  }
  $summary.requested_equals_theoretical_units = @{ mismatches = $mismatches.Count; samples = ($mismatches | Select-Object -First 5) }
}

if (Test-Path (Join-Path $outDir 'risk_snapshots.csv')) {
  $riskRows = Import-Csv (Join-Path $outDir 'risk_snapshots.csv')
  $vals = @(); foreach ($rr in $riskRows) { if ($rr.marginUtilPercent) { try { $vals += [double]::Parse($rr.marginUtilPercent, [System.Globalization.CultureInfo]::InvariantCulture) } catch {} } }
  if ($vals.Count -gt 0) { $summary.margin_util_pct = @{ max = (($vals | Measure-Object -Maximum).Maximum); avg = (($vals | Measure-Object -Average).Average) } }
}

$summaryPath = Join-Path $outDir 'summary_smoke.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding UTF8

$zipPath = Join-Path $LogDir ("smoke_{0}.zip" -f $ts)
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -Force

Write-Host "RUN_DIR: $outDir"
Write-Host "ZIP: $zipPath"
Write-Host "=== summary_smoke.json ==="
Get-Content -Raw -Path $summaryPath
