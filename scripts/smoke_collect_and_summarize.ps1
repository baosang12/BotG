# Collect latest BotG logs into a timestamped folder, compute summary metrics, and zip artifacts.
param(
  [string]$LogDir = $env:BOTG_LOG_PATH
)

if (-not $LogDir) { $LogDir = 'D:\botg\logs' }
Write-Host "[INFO] Using LogDir = $LogDir"
if (-not (Test-Path $LogDir)) { throw "Log directory not found: $LogDir" }

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$runDir = Join-Path $LogDir ("smoke_{0}" -f $ts)
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

function Copy-IfExists([string]$path) {
  if (Test-Path $path) { Copy-Item $path $runDir -Force }
}

$orders = Join-Path $LogDir 'orders.csv'
$risk = Join-Path $LogDir 'risk_snapshots.csv'
$tele = Join-Path $LogDir 'telemetry.csv'
$sizecmp = Join-Path $LogDir 'size_comparison.json'
Copy-IfExists $orders
Copy-IfExists $risk
Copy-IfExists $tele
Copy-IfExists $sizecmp

function Convert-ToInvariantDouble($s) { if ($null -eq $s -or $s -eq '') { return $null }; try { return [double]::Parse(($s.ToString()), [System.Globalization.CultureInfo]::InvariantCulture) } catch { return $null } }

$summary = [ordered]@{}
$summary.simulated = $true
$summary.run_dir = $runDir

if (Test-Path (Join-Path $runDir 'orders.csv')) {
  $rows = Import-Csv (Join-Path $runDir 'orders.csv')
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

if (Test-Path (Join-Path $runDir 'risk_snapshots.csv')) {
  $riskRows = Import-Csv (Join-Path $runDir 'risk_snapshots.csv')
  $vals = @(); foreach ($rr in $riskRows) { if ($rr.marginUtilPercent) { try { $vals += [double]::Parse($rr.marginUtilPercent, [System.Globalization.CultureInfo]::InvariantCulture) } catch {} } }
  if ($vals.Count -gt 0) { $summary.margin_util_pct = @{ max = (($vals | Measure-Object -Maximum).Maximum); avg = (($vals | Measure-Object -Average).Average) } }
}

$summaryPath = Join-Path $runDir 'summary_smoke.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath -Encoding UTF8

$zipPath = Join-Path $LogDir ("smoke_{0}.zip" -f $ts)
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $runDir '*') -DestinationPath $zipPath -Force

Write-Host "RUN_DIR: $runDir"
Write-Host "ZIP: $zipPath"
Write-Host "=== summary_smoke.json ==="
Get-Content -Raw -Path $summaryPath
