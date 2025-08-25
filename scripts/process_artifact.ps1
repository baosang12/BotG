param(
  [string]$Artifact
)

$ErrorActionPreference = 'Stop'

function Find-LatestArtifact {
  $arts = Join-Path (Get-Location) 'artifacts'
  if (-not (Test-Path $arts)) { return $null }
  $dirs = Get-ChildItem $arts -Directory | Where-Object { $_.Name -like 'telemetry_run_*' } | Sort-Object LastWriteTime -Descending
  return ($dirs | Select-Object -First 1)
}

function New-SmokeFolder([string]$ArtifactRoot) {
  $ts = Get-Date -Format 'yyyyMMdd_HHmmss'
  $smoke = Join-Path $ArtifactRoot ("smoke_{0}" -f $ts)
  New-Item -ItemType Directory -Force -Path $smoke | Out-Null
  return $smoke
}

function Copy-IfExists([string]$src, [string]$dst) {
  if (Test-Path $src) { Copy-Item $src $dst -Force }
}

function Ensure-ClosedTrades([string]$SmokeDir) {
  $orders = Join-Path $SmokeDir 'orders.csv'
  $outCsv = Join-Path $SmokeDir 'closed_trades_fifo.csv'
  $meta = Join-Path $SmokeDir 'run_metadata.json'
  $closes = Join-Path $SmokeDir 'trade_closes.log'
  $py = Join-Path $PSScriptRoot 'compute_pnl_fifo.py'
  if (Test-Path $orders) {
    $args = @('--orders', $orders, '--out', $outCsv)
    if (Test-Path $meta) { $args += @('--meta', $meta) }
    if ((Test-Path $closes) -and ((Get-Item $closes).Length -gt 0)) { $args += @('--closes', $closes) }
    try { & python $py @args } catch { Write-Warning "compute_pnl_fifo.py failed: $_" }
  }
  if ((-not (Test-Path $outCsv)) -or ((Get-Item $outCsv).Length -eq 0)) {
    # Reconstruct placeholder file (empty with header)
    $outCsv = Join-Path $SmokeDir 'closed_trades_fifo_reconstructed.csv'
    'side_closed,entry_orderid,entry_time,entry_price,exit_orderid,exit_time,exit_price,size,pnl_price_units,realized_usd,commission,net_realized_usd' | Out-File -FilePath $outCsv -Encoding utf8 -Force
    return @($outCsv, $true)
  }
  return @($outCsv, $false)
}

function Get-OrdersFillStatsAndSlips([string]$OrdersCsv, [int]$Cap = 200000) {
  Add-Type -AssemblyName Microsoft.VisualBasic
  $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($OrdersCsv)
  $parser.HasFieldsEnclosedInQuotes = $true
  $parser.SetDelimiters(',')
  $headers = $parser.ReadFields()
  if (-not $headers) { return @{}, @(), @(), @() }
  $hmap = @{}
  for ($i=0; $i -lt $headers.Length; $i++) { $hmap[$headers[$i]] = $i }
  function Get-Col([string]$name, [string[]]$fields) { if ($hmap.ContainsKey($name)) { return $fields[$hmap[$name]] } else { return $null } }

  $totalReq = 0; $totalFill = 0
  $perSide = @{}
  $perHour = @{}
  $sample = New-Object System.Collections.ArrayList
  $seen = 0

  while (-not $parser.EndOfData) {
    $fields = $parser.ReadFields()
    if (-not $fields) { continue }
    $phase = (Get-Col 'phase' $fields)
    if (-not $phase) { continue }
    $phase = $phase.Trim().ToUpper()
    if (@('REQUEST','ACK','FILL') -notcontains $phase) { continue }
    $side = (Get-Col 'side' $fields)
    if ($side) { $side = $side.Trim().ToUpper() }
    $ts = (Get-Col 'timestamp_iso' $fields)
    $hourKey = ''
    if ($ts) {
      try { $dt = [datetime]::Parse($ts, [System.Globalization.CultureInfo]::InvariantCulture); $hourKey = $dt.ToString('s').Substring(0,13) + ':00:00' } catch {}
    }
    if ($phase -eq 'REQUEST') {
      $totalReq++
      if ($side) { if (-not $perSide.ContainsKey($side)) { $perSide[$side] = @{ REQUEST = 0; FILL = 0 } }; $perSide[$side]['REQUEST']++ }
      if ($hourKey) { if (-not $perHour.ContainsKey($hourKey)) { $perHour[$hourKey] = @{ REQUEST = 0; FILL = 0 } }; $perHour[$hourKey]['REQUEST']++ }
    } elseif ($phase -eq 'FILL') {
      $totalFill++
      if ($side) { if (-not $perSide.ContainsKey($side)) { $perSide[$side] = @{ REQUEST = 0; FILL = 0 } }; $perSide[$side]['FILL']++ }
      if ($hourKey) { if (-not $perHour.ContainsKey($hourKey)) { $perHour[$hourKey] = @{ REQUEST = 0; FILL = 0 } }; $perHour[$hourKey]['FILL']++ }
      # slippage sample
      $sl = $null
      $execP = Get-Col 'execPrice' $fields
      $intP = Get-Col 'intendedPrice' $fields
      if ($execP -and $intP) {
        try { $sl = [double]::Parse($execP, [System.Globalization.CultureInfo]::InvariantCulture) - [double]::Parse($intP, [System.Globalization.CultureInfo]::InvariantCulture) } catch {}
      } else {
        $sp = Get-Col 'slippage' $fields
        if ($sp) { try { $sl = [double]::Parse($sp, [System.Globalization.CultureInfo]::InvariantCulture) } catch {} }
      }
      if ($sl -ne $null) {
        $seen++
        if ($sample.Count -lt $Cap) { [void]$sample.Add($sl) }
        else { $j = Get-Random -Minimum 0 -Maximum $seen; if ($j -lt $Cap) { $sample[$j] = $sl } }
      }
    }
  }
  $parser.Close()

  $overall = @{ REQUEST = $totalReq; FILL = $totalFill; fill_rate = ($(if ($totalReq -gt 0) { [double]$totalFill / [double]$totalReq } else { 0.0 })) }
  $sideRows = @(); foreach ($k in ($perSide.Keys | Sort-Object)) { $v = $perSide[$k]; $rate = if ($v.REQUEST -gt 0) { [double]$v.FILL / [double]$v.REQUEST } else { 0.0 }; $sideRows += [pscustomobject]@{ side=$k; REQUEST=$v.REQUEST; FILL=$v.FILL; fill_rate=('{0:F6}' -f $rate) } }
  $hourRows = @(); foreach ($k in ($perHour.Keys | Sort-Object)) { $v = $perHour[$k]; $rate = if ($v.REQUEST -gt 0) { [double]$v.FILL / [double]$v.REQUEST } else { 0.0 }; $hourRows += [pscustomobject]@{ hour=$k; REQUEST=$v.REQUEST; FILL=$v.FILL; fill_rate=('{0:F6}' -f $rate) } }
  return $overall, $sideRows, $hourRows, $sample
}

function Get-Quantiles([double[]]$sample) {
  if (-not $sample -or $sample.Count -eq 0) { return @{ median=0.0; p90=0.0; avg_abs=0.0 } }
  $s = $sample | Sort-Object
  $n = $s.Count
  function Pct([double]$p) { if ($n -le 1) { return $s[0] }; $k = [int][math]::Round(($p/100.0)*($n-1)); if ($k -lt 0) { $k=0 }; if ($k -ge $n) { $k=$n-1 }; return [double]$s[$k] }
  $avgAbs = ($s | ForEach-Object { [math]::Abs($_) } | Measure-Object -Average).Average
  return @{ median = (Pct 50); p90 = (Pct 90); avg_abs = $avgAbs }
}

function Compute-EquityStats([string]$ClosedCsv) {
  if (-not (Test-Path $ClosedCsv)) { return @{ total_trades=0; net_realized_usd=0.0; max_drawdown_usd=0.0; max_drawdown_time=$null } }
  $equity = 0.0; $eqMax = 0.0; $maxDD = 0.0; $maxDDTime = $null; $trades=0
  try {
    Import-Csv -Path $ClosedCsv | ForEach-Object {
      $trades++
      $netStr = $null
      if ($_.net_realized_usd) { $netStr = $_.net_realized_usd } elseif ($_.realized_usd) { $netStr = $_.realized_usd }
      $net = 0.0; if ($netStr) { try { $net = [double]::Parse($netStr, [System.Globalization.CultureInfo]::InvariantCulture) } catch {} }
      $equity += $net
      if ($equity -gt $eqMax) { $eqMax = $equity }
      $dd = $equity - $eqMax
      if ($dd -lt $maxDD) { $maxDD = $dd; $maxDDTime = $_.exit_time }
    }
  } catch {}
  return @{ total_trades=$trades; net_realized_usd=[double]$equity; max_drawdown_usd=[double]$maxDD; max_drawdown_time=$maxDDTime }
}

function Build-Unmatched([string]$OrdersCsv, [string]$ClosedCsv) {
  $fills = New-Object System.Collections.Generic.HashSet[string]
  Add-Type -AssemblyName Microsoft.VisualBasic
  $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($OrdersCsv)
  $parser.HasFieldsEnclosedInQuotes = $true
  $parser.SetDelimiters(',')
  $headers = $parser.ReadFields(); $hmap=@{}; for($i=0;$i -lt $headers.Length;$i++){ $hmap[$headers[$i]]=$i }
  function GC([string]$n,[string[]]$f){ if($hmap.ContainsKey($n)){ $f[$hmap[$n]] } else { $null } }
  while(-not $parser.EndOfData){
    $f=$parser.ReadFields(); if(-not $f){continue}
    $ph=(GC 'phase' $f)
    if (-not $ph) { $ph = '' }
    if ($ph.ToUpper() -eq 'FILL') {
      $oid=(GC 'orderId' $f); if($oid){[void]$fills.Add($oid)}
    }
  }
  $parser.Close()
  $exitIds = New-Object System.Collections.Generic.HashSet[string]
  if (Test-Path $ClosedCsv) {
    Import-Csv -Path $ClosedCsv | ForEach-Object {
      $eo = $null
      if ($_.exit_orderid) { $eo = $_.exit_orderid } elseif ($_.exit_order_id) { $eo = $_.exit_order_id }
      if ($eo) { [void]$exitIds.Add($eo) }
    }
  }
  $unmatched = @()
  foreach($fid in $fills){ if (-not $exitIds.Contains($fid)) { $unmatched += [pscustomobject]@{ fill_orderid=$fid } } }
  return $unmatched
}

try {
  # 1) Detect artifact
  $art = $null
  if ($Artifact) { $art = Get-Item $Artifact -ErrorAction Stop }
  else { $art = Find-LatestArtifact }
  if (-not $art) { throw "Artifact not found. Provide -Artifact or ensure ./artifacts exists." }

  # 2) Create smoke via smoke_collect_and_summarize
  try {
    & (Join-Path $PSScriptRoot 'smoke_collect_and_summarize.ps1') -LogDir $art.FullName
  } catch {
    Write-Warning "smoke_collect_and_summarize.ps1 failed, continuing with fallback: $_"
  }
  $smokeDir = (Get-ChildItem $art.FullName -Directory | Where-Object Name -like 'smoke_*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
  if (-not $smokeDir) { $smokeDir = New-SmokeFolder $art.FullName; # copy inputs
    foreach($n in 'orders.csv','run_metadata.json','trade_closes.log','risk_snapshots.csv'){ Copy-IfExists (Join-Path $art.FullName $n) (Join-Path $smokeDir.FullName $n) }
  }

  $smoke = $smokeDir.FullName
  $ordersCsv = Join-Path $smoke 'orders.csv'
  if (-not (Test-Path $ordersCsv)) { throw "orders.csv not found under smoke folder: $smoke" }

  # 3) Ensure closed_trades_fifo.csv
  $ctPath,$reconstructed = Ensure-ClosedTrades $smoke

  # 4) Fill breakdowns
  try { & (Join-Path $PSScriptRoot 'compute_fill_breakdown.ps1') -OrdersCsv $ordersCsv -OutDir $smoke } catch { Write-Warning "compute_fill_breakdown failed: $_" }
  $fillBySide = Join-Path $smoke 'fill_rate_by_side.csv'
  $fillByHour = Join-Path $smoke 'fill_breakdown_by_hour.csv'

  # 5) Slips sample + stats
  $overall,$sideRows,$hourRows,$slips = Get-OrdersFillStatsAndSlips -OrdersCsv $ordersCsv -Cap 200000
  $rawSlips = Join-Path $smoke 'raw_slips_sample.csv'
  $slipObjects = @(); foreach($s in $slips){ $slipObjects += [pscustomobject]@{ slippage=$s } }
  $slipObjects | Export-Csv -Path $rawSlips -NoTypeInformation -Encoding utf8
  $slipStats = Get-Quantiles($slips)

  # 6) Analysis summary (equity + fill)
  $eqStats = Compute-EquityStats -ClosedCsv $ctPath
  $analysis = @{ equity = $eqStats; fill = @{ overall = $overall; per_side = $sideRows }; slip_stats = $slipStats }
  $analysisPath = Join-Path $smoke 'analysis_summary_stats.json'
  $analysis | ConvertTo-Json -Depth 6 | Out-File -FilePath $analysisPath -Encoding utf8

  # 7) Reconcile
  $reconcilePath = Join-Path $smoke 'reconcile_report.json'
  try {
  $rc = & python (Join-Path $PSScriptRoot 'reconcile.py') --closed $ctPath --closes (Join-Path $smoke 'trade_closes.log') --risk (Join-Path $smoke 'risk_snapshots.csv')
    if ($LASTEXITCODE -eq 0 -and $rc) { $rc | Out-File -FilePath $reconcilePath -Encoding utf8 } else { '{"status":"unknown"}' | Out-File -FilePath $reconcilePath -Encoding utf8 }
  } catch { '{"status":"unknown"}' | Out-File -FilePath $reconcilePath -Encoding utf8 }
  $unmatchedRows = Build-Unmatched -OrdersCsv $ordersCsv -ClosedCsv $ctPath
  $unmatchedCsv = Join-Path $smoke 'unmatched_fills.csv'
  if ($unmatchedRows.Count -gt 0) { $unmatchedRows | Export-Csv -Path $unmatchedCsv -NoTypeInformation -Encoding utf8 } else { '# none' | Out-File -FilePath $unmatchedCsv -Encoding utf8 }

  # 8) Final summary
  $ordersCount = 0; try { $ordersCount = (Import-Csv -Path $ordersCsv).Count } catch {}
  $closedCount = 0; try { if (Test-Path $ctPath) { $closedCount = (Import-Csv -Path $ctPath).Count } } catch {}
  $pnlPath = Join-Path $smoke 'pnl_summary.json'
  $totalNet = $null; if (Test-Path $pnlPath) { try { $ps = Get-Content -Raw -Path $pnlPath | ConvertFrom-Json; $totalNet = $ps.total_net_realized_usd } catch {} }
  if ($null -eq $totalNet) { $totalNet = $eqStats.net_realized_usd }
  $perSideRates = @{}
  foreach($r in $sideRows){ try { $perSideRates[$r.side] = [double]::Parse($r.fill_rate, [System.Globalization.CultureInfo]::InvariantCulture) } catch { $perSideRates[$r.side] = 0.0 } }
  $rcObj = @{ status = 'unknown'; report_path = $reconcilePath; unmatched_fills_csv = $unmatchedCsv }
  try {
    $rcTmp = Get-Content -Raw -Path $reconcilePath | ConvertFrom-Json
    $st = 'unknown'; if ($rcTmp -and $rcTmp.status) { $st = $rcTmp.status }
    $rcObj = @{ status = $st; report_path = $reconcilePath; unmatched_fills_csv = $unmatchedCsv }
  } catch {}
  $final = @{ 
    artifact_path = $art.FullName;
    smoke_path = $smoke;
    closed_trades_fifo_path = $ctPath;
    reconstructed = [bool]$reconstructed;
    counts = @{ orders = $ordersCount; closed_trades_count = $closedCount };
    total_net_realized_usd = $totalNet;
    fill_rate_overall = $overall.fill_rate;
    per_side_fill_rates = $perSideRates;
    slip_stats = $slipStats;
    reconcile = $rcObj;
    generated_files = @($fillBySide,$fillByHour,$rawSlips,$analysisPath,$reconcilePath,$unmatchedCsv,$pnlPath)
  }

  '=== SMOKE ANALYSIS COMPLETE ===' | Write-Host
  $final | ConvertTo-Json -Depth 6 | Write-Output
  $final | ConvertTo-Json -Depth 6 | Out-File -FilePath (Join-Path $smoke 'final_summary.json') -Encoding utf8

  exit 0
}
catch {
  '=== ERRORS ===' | Write-Host
  Write-Host "ERROR: $($_.Exception.Message)"
  exit 1
}
