[CmdletBinding()]
param(
  [string]$OrdersCsv,
  [string]$FillsCsv,
  [string]$OutDir = 'path_issues'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type -AssemblyName System.Windows.Forms.DataVisualization | Out-Null

function Ensure-Dir([string]$p) { if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }
function WriteUtf8([string]$p,[string]$t){ $enc=New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p,$t,$enc) }

Ensure-Dir $OutDir

function Percentiles([double[]]$arr, [double[]]$qs) {
  if (-not $arr -or $arr.Count -eq 0) { return @{} }
  $a = $arr | Sort-Object
  $n = $a.Count
  $res = @{}
  foreach ($q in $qs) {
    $rank = [math]::Clamp($q * ($n - 1), 0, [double]($n - 1))
    $lo = [int][math]::Floor($rank)
    $hi = [int][math]::Ceiling($rank)
    if ($lo -eq $hi) { $val = $a[$lo] } else { $val = $a[$lo] + ($rank - $lo) * ($a[$hi] - $a[$lo]) }
    $res[('p{0}' -f ([int]($q*100)))] = [double]::Round($val, 6)
  }
  return $res
}

function Plot-Histogram([double[]]$data, [string]$title, [string]$outPng, [int]$bins=50) {
  if (-not $data -or $data.Count -eq 0) { return }
  $min = ($data | Measure-Object -Minimum).Minimum
  $max = ($data | Measure-Object -Maximum).Maximum
  if ($min -eq $max) { $max = $min + 1 }
  $binSize = ($max - $min) / [double]$bins
  if ($binSize -le 0) { $binSize = 1 }
  $hist = @{}
  foreach ($v in $data) {
    $b = [math]::Floor(($v - $min) / $binSize)
    if ($b -ge $bins) { $b = $bins - 1 }
    $key = [double]($min + $b * $binSize)
    if (-not $hist.ContainsKey($key)) { $hist[$key] = 0 }
    $hist[$key] += 1
  }

  $chart = New-Object System.Windows.Forms.DataVisualization.Charting.Chart
  $chart.Width = 800; $chart.Height = 480
  $area = New-Object System.Windows.Forms.DataVisualization.Charting.ChartArea
  $area.AxisX.Title = $title
  $area.AxisY.Title = 'count'
  $chart.ChartAreas.Add($area)
  $series = New-Object System.Windows.Forms.DataVisualization.Charting.Series
  $series.ChartType = [System.Windows.Forms.DataVisualization.Charting.SeriesChartType]::Column
  $series.IsXValueIndexed = $true
  foreach ($k in ($hist.Keys | Sort-Object)) {
    $idx = $series.Points.AddXY([double]$k, [int]$hist[$k])
  }
  $chart.Series.Add($series)
  $chart.Titles.Add($title) | Out-Null
  $chart.SaveImage($outPng, 'Png')
  $chart.Dispose()
}

function Plot-LineXY([object[]]$xs, [double[]]$ys, [string]$title, [string]$yTitle, [string]$outPng) {
  if (-not $ys -or $ys.Count -eq 0) { return }
  $chart = New-Object System.Windows.Forms.DataVisualization.Charting.Chart
  $chart.Width = 800; $chart.Height = 480
  $area = New-Object System.Windows.Forms.DataVisualization.Charting.ChartArea
  $area.AxisX.Title = 'percentile'
  $area.AxisY.Title = $yTitle
  $chart.ChartAreas.Add($area)
  $series = New-Object System.Windows.Forms.DataVisualization.Charting.Series
  $series.ChartType = [System.Windows.Forms.DataVisualization.Charting.SeriesChartType]::Line
  for ($i=0; $i -lt $ys.Count; $i++) { [void]$series.Points.AddXY($xs[$i], $ys[$i]) }
  $chart.Series.Add($series)
  $chart.Titles.Add($title) | Out-Null
  $chart.SaveImage($outPng, 'Png')
  $chart.Dispose()
}

function To-DoubleArray($values) { $arr = @(); foreach ($v in $values) { $d = $null; if ([double]::TryParse([string]$v, [ref]$d)) { $arr += $d } }; return ,$arr }

# Load fills or derive from orders
$fills = $null
$hourly = $null
if ($FillsCsv -and (Test-Path -LiteralPath $FillsCsv)) {
  $fills = Import-Csv -LiteralPath $FillsCsv
} elseif ($OrdersCsv -and (Test-Path -LiteralPath $OrdersCsv)) {
  $orders = Import-Csv -LiteralPath $OrdersCsv
  if ($orders.Count -eq 0) { throw "orders.csv is empty" }
  # guess columns
  $cols = $orders[0].PSObject.Properties.Name
  function Find-Col([string[]]$cols,[string[]]$cands){ foreach($c in $cands){ $m = $cols | Where-Object { $_ -ieq $c }; if($m){ return $m[0] } }; return $null }
  $ev = Find-Col $cols @('event','type','status','action')
  if (-not $ev) { $ev = 'event' }
  $ts = Find-Col $cols @('timestamp','time','event_time')
  if (-not $ts) { $ts = $cols[0] }
  $oid = Find-Col $cols @('orderid','order_id','id')
  if (-not $oid) { $oid = 'order_id' }
  $prq = Find-Col $cols @('price_requested','requested_price','request_price','price_request')
  $pfl = Find-Col $cols @('price_filled','filled_price','execution_price')
  $sz  = Find-Col $cols @('size','quantity','qty','filled_size','size_filled')

  # Build per-order request and fill times
  $reqTimes = @{}
  $fillTimes = @{}
  foreach ($r in $orders) {
    $t = [datetime]::MinValue
    [void][datetime]::TryParse($r.$ts, [ref]$t)
    $id = $r.$oid
    $e = [string]$r.$ev
    if ($e.ToUpper() -eq 'REQUEST') { if (-not $reqTimes.ContainsKey($id)) { $reqTimes[$id] = $t } }
    if ($e.ToUpper() -eq 'FILL') { $fillTimes[$id] = $t }
  }
  $fillsList = @()
  foreach ($id in $fillTimes.Keys) {
    if ($reqTimes.ContainsKey($id)) {
      $lat = ($fillTimes[$id] - $reqTimes[$id]).TotalMilliseconds
    } else { $lat = $null }
    $obj = [ordered]@{ order_id = $id; timestamp = $fillTimes[$id]; latency_ms = $lat }
    if ($prq -and $pfl) {
      $lastFill = ($orders | Where-Object { $_.$oid -eq $id -and ([string]$_.($ev)).ToUpper() -eq 'FILL' } | Sort-Object { [datetime]$_.($ts) } | Select-Object -Last 1)
      $firstReq = ($orders | Where-Object { $_.$oid -eq $id -and ([string]$_.($ev)).ToUpper() -eq 'REQUEST' } | Sort-Object { [datetime]$_.($ts) } | Select-Object -First 1)
      if ($lastFill -and $firstReq) {
        $pf=[double]0; $pr=[double]0
        [void][double]::TryParse([string]$lastFill.($pfl), [ref]$pf)
        [void][double]::TryParse([string]$firstReq.($prq), [ref]$pr)
        $obj['slippage'] = $pf - $pr
      }
    }
    if ($sz) { $lf = ($orders | Where-Object { $_.$oid -eq $id -and ([string]$_.($ev)).ToUpper() -eq 'FILL' } | Sort-Object { [datetime]$_.($ts) } | Select-Object -Last 1); if ($lf) { $obj['size'] = $lf.($sz) } }
    $fillsList += [pscustomobject]$obj
  }
  $fills = $fillsList

  # Hourly
  $reqCounts = @{}; $fillCounts = @{}
  foreach ($r in $orders) {
    $t=[datetime]::MinValue; [void][datetime]::TryParse($r.$ts,[ref]$t); if ($t -eq [datetime]::MinValue) { continue }
    $h = Get-Date $t -Format 'yyyy-MM-dd HH:00:00'
    $e=[string]$r.$ev
    if ($e.ToUpper() -eq 'REQUEST') { if (-not $reqCounts.ContainsKey($h)) { $reqCounts[$h]=0 }; $reqCounts[$h]++ }
    if ($e.ToUpper() -eq 'FILL') { if (-not $fillCounts.ContainsKey($h)) { $fillCounts[$h]=0 }; $fillCounts[$h]++ }
  }
  $hourly = @()
  $hours = New-Object System.Collections.Generic.HashSet[string]
  foreach ($k in $reqCounts.Keys) { $null = $hours.Add($k) }
  foreach ($k in $fillCounts.Keys) { $null = $hours.Add($k) }
  foreach ($h in ($hours | Sort-Object)) {
    $rq = if ($reqCounts.ContainsKey($h)) { $reqCounts[$h] } else { 0 }
    $fl = if ($fillCounts.ContainsKey($h)) { $fillCounts[$h] } else { 0 }
    $fr = if ($rq -gt 0) { [double]$fl / [double]$rq } else { [double]::NaN }
    $hourly += [pscustomobject]@{ timestamp = $h; requests = $rq; fills = $fl; fill_rate = $fr }
  }
}

if (-not $fills) { throw 'No inputs found. Provide -FillsCsv or -OrdersCsv.' }

# Extract arrays
$lat = To-DoubleArray ($fills | ForEach-Object { $_.latency_ms })
$slp = To-DoubleArray ($fills | ForEach-Object { $_.slippage })

$qs = @(0.5,0.75,0.9,0.95,0.99)
$summary = @{}
if ($lat.Count -gt 0) { $summary['latency_ms'] = Percentiles $lat $qs } else { $summary['latency_ms'] = @{ p50=$null;p75=$null;p90=$null;p95=$null;p99=$null } }
if ($slp.Count -gt 0) { $summary['slippage_abs'] = Percentiles ($slp | ForEach-Object { [math]::Abs($_) }) $qs } else { $summary['slippage_abs'] = @{ p50=$null;p75=$null;p90=$null;p95=$null;p99=$null } }

WriteUtf8 (Join-Path $OutDir 'slip_latency_percentiles.json') (($summary | ConvertTo-Json -Depth 6))

# Hourly medians
if ($fills -and $fills.Count -gt 0) {
  $byHour = @{}
  foreach ($r in $fills) {
    $t=[datetime]::MinValue; [void][datetime]::TryParse([string]$r.timestamp, [ref]$t); if ($t -eq [datetime]::MinValue) { continue }
    $h = Get-Date $t -Format 'yyyy-MM-dd HH:00:00'
    if (-not $byHour.ContainsKey($h)) { $byHour[$h] = @{ lat = @(); slp = @() } }
    if ($r.latency_ms -ne $null -and $r.latency_ms -ne '') { $d=[double]0; if ([double]::TryParse([string]$r.latency_ms, [ref]$d)) { $byHour[$h].lat += $d } }
    if ($r.slippage -ne $null -and $r.slippage -ne '') { $d=[double]0; if ([double]::TryParse([string]$r.slippage, [ref]$d)) { $byHour[$h].slp += $d } }
  }
  $hourly2 = @()
  foreach ($h in ($byHour.Keys | Sort-Object)) {
    $medLat = $null; $medSlp = $null
    if ($byHour[$h].lat.Count -gt 0) { $medLat = (Percentiles (,$byHour[$h].lat) @(0.5)).p50 }
    if ($byHour[$h].slp.Count -gt 0) { $medSlp = (Percentiles (,$byHour[$h].slp) @(0.5)).p50 }
    $hourly2 += [pscustomobject]@{ timestamp=$h; median_latency_ms=$medLat; median_slippage=$medSlp }
  }
  # Merge hourly2 with hourly counts if available
  if ($hourly) {
    $map = @{}; foreach($row in $hourly2){ $map[[string]$row.timestamp] = $row }
    $merged = @()
    foreach($row in ($hourly | Sort-Object timestamp)){
      $h=[string]$row.timestamp
      $medLat = $null; $medSlp = $null
      if ($map.ContainsKey($h)) { $medLat=$map[$h].median_latency_ms; $medSlp=$map[$h].median_slippage }
      $merged += [pscustomobject]@{ timestamp=$h; requests=$row.requests; fills=$row.fills; fill_rate=$row.fill_rate; median_latency_ms=$medLat; median_slippage=$medSlp }
    }
    $merged | Export-Csv -LiteralPath (Join-Path $OutDir 'fillrate_hourly.csv') -NoTypeInformation -Encoding UTF8
  } else {
    $hourly2 | Export-Csv -LiteralPath (Join-Path $OutDir 'fillrate_hourly.csv') -NoTypeInformation -Encoding UTF8
  }
}

# Top slippages
if ($slp.Count -gt 0) {
  $top = ($fills | Where-Object { $_.slippage -ne $null -and $_.slippage -ne '' } | ForEach-Object { [pscustomobject]@{ timestamp=$_.timestamp; slippage=[double]$_.slippage } } | Sort-Object { [math]::Abs($_.slippage) } -Descending | Select-Object -First 20)
  $top | Export-Csv -LiteralPath (Join-Path $OutDir 'top_slippage.csv') -NoTypeInformation -Encoding UTF8
} else {
  New-Item -ItemType File -Path (Join-Path $OutDir 'top_slippage.csv') -Force | Out-Null
}

# Plots
if ($slp.Count -gt 0) { Plot-Histogram $slp 'Slippage' (Join-Path $OutDir 'slippage_hist.png') }
if ($lat.Count -gt 0) {
  $latPerc = $summary['latency_ms']
  $xs = @('50','75','90','95','99')
  $ys = @($latPerc.p50, $latPerc.p75, $latPerc.p90, $latPerc.p95, $latPerc.p99)
  Plot-LineXY $xs $ys 'Latency percentiles' 'ms' (Join-Path $OutDir 'latency_percentiles.png')
}

'Postrun analysis complete.' | Write-Host
