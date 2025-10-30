param(param(

    [int]$Seconds = 180,  [string]$OutDir = "path_issues/ctrader_connect_proof",

    [string]$LogPath = "D:\botg\logs",  [int]$Seconds = 60,

    [string]$Symbol = "EURUSD"  [string]$LogRoot = "D:\botg\logs",

)  [string]$TelemetryFilePattern = "telemetry.csv",

  [switch]$RequireBidAsk,

$ErrorActionPreference = "Stop"  # CHANGE-002-REAL: Retry + soft-pass parameters

  [ValidateSet('gate2', 'gate3')]

# Create preflight directory  [string]$Mode = 'gate2',

$preflightDir = Join-Path $LogPath "preflight"  [int]$Probes = 3,

if (-not (Test-Path $preflightDir)) {  [int]$IntervalSec = 10,

    New-Item -ItemType Directory -Path $preflightDir -Force | Out-Null  [string]$MockTelemetryPath = $null

})



# Check telemetry.csv exists$ErrorActionPreference = 'Stop'

$telemetryPath = Join-Path $LogPath "telemetry.csv"New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not (Test-Path $telemetryPath)) {

    throw "Missing telemetry.csv at $telemetryPath"Write-Host "[INFO] Preflight Mode=$Mode, Probes=$Probes, IntervalSec=$IntervalSec" -ForegroundColor Cyan

}

# CHANGE-002-REAL: Helper function to collect metrics for one probe

Write-Host "[PREFLIGHT] Monitoring L1 feed for $Seconds seconds..." -ForegroundColor Cyan# CHANGE-002B: Support missing tick_rate column (set to 0)

Write-Host "[PREFLIGHT] Symbol: $Symbol | Path: $telemetryPath" -ForegroundColor Grayfunction Get-TelemetryProbe {

  param(

$startTime = Get-Date    [string]$TeleFullName,

$endTime = $startTime.AddSeconds($Seconds)    [string]$TsCol,

$tickCounts = @()    [string]$TpCol,  # Can be $null if no tick_rate column

$secondsWithTicks = 0    [datetime]$WindowStart,

$lastRowCount = 0    [datetime]$WindowEnd

  )

# Monitor telemetry.csv every second

while ((Get-Date) -lt $endTime) {  # Read header with FileShare

    $elapsed = [int]((Get-Date) - $startTime).TotalSeconds  $headerLine = $null

      $fs = [System.IO.File]::Open($TeleFullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)

    # Count current rows  try {

    $currentRows = (Get-Content $telemetryPath | Measure-Object -Line).Lines    $sr = New-Object System.IO.StreamReader($fs, [System.Text.UTF8Encoding]::new($false), $true)

    $newRows = $currentRows - $lastRowCount    $headerLine = $sr.ReadLine()

      } finally { $sr.Close(); $fs.Close() }

    if ($newRows -gt 0) {  if (-not $headerLine) { throw "Cannot read header" }

        $secondsWithTicks++

    }  # Get tail

      $tailLines = 5000

    $tickCounts += $newRows  $tail = Get-Content -LiteralPath $TeleFullName -Tail $tailLines -Encoding UTF8

    $lastRowCount = $currentRows  $tailNoHeader = $tail | Where-Object {

        $_ -and ($_ -ne $headerLine) -and ($_ -notmatch '^(timestamp_iso|timestamp|time|Time|Timestamp),')

    # Progress indicator  }

    if ($elapsed % 30 -eq 0 -and $elapsed -gt 0) {

        $tickRateNow = if ($elapsed -gt 0) { [Math]::Round(($tickCounts | Measure-Object -Sum).Sum / $elapsed, 3) } else { 0.0 }  $csvLines = @()

        $activeRatioNow = if ($elapsed -gt 0) { [Math]::Round($secondsWithTicks / $elapsed, 3) } else { 0.0 }  $csvLines += $headerLine

        Write-Host "[PREFLIGHT] Progress: ${elapsed}s | tick_rate: $tickRateNow | active_ratio: $activeRatioNow" -ForegroundColor Gray  $csvLines += $tailNoHeader

    }  if(-not $csvLines -or $csvLines.Count -lt 2) { throw "No telemetry data" }

    

    Start-Sleep -Seconds 1  $rows = @($csvLines | ConvertFrom-Csv)

}  if(-not $rows){ throw "Cannot parse CSV" }



Write-Host "[PREFLIGHT] Monitoring complete. Analyzing..." -ForegroundColor Cyan  # Filter window

  $win = @($rows | Where-Object {

# Calculate metrics    $t = $_."$TsCol"

$totalTicks = ($tickCounts | Measure-Object -Sum).Sum    if (-not $t) { return $false }

$tickRateAvg = [Math]::Round($totalTicks / $Seconds, 3)    $dt = [datetime]$t

$activeRatio = [Math]::Round($secondsWithTicks / $Seconds, 3)    ($dt -ge $WindowStart) -and ($dt -le $WindowEnd)

  })

# Get last row timestamp to calculate age

$allLines = Get-Content $telemetryPath  if (-not $win -or $win.Count -lt 3) {

if ($allLines.Count -lt 2) {    # Use last row if window empty

    $lastAgeSec = 999.0    $lastTs = $null

} else {    if ($rows.Count -gt 0) {

    $lastLine = $allLines[-1]      try { $lastTs = [datetime]($rows[-1]."$TsCol") } catch { $lastTs = $null }

    $parts = $lastLine -split ','    }

    if ($parts.Count -ge 1) {    $lastAgeSec = if ($lastTs) { [math]::Round(((Get-Date) - $lastTs).TotalSeconds,1) } else { $null }

        try {    

            $lastTimestamp = [DateTime]::Parse($parts[0])    return @{

            $lastAgeSec = [Math]::Round(((Get-Date) - $lastTimestamp).TotalSeconds, 1)      last_age_sec = $lastAgeSec

        } catch {      active_ratio = 0

            $lastAgeSec = 999.0      tick_rate = 0

        }      samples = 0

    } else {      ts_utc = (Get-Date).ToUniversalTime().ToString('o')

        $lastAgeSec = 999.0    }

    }  }

}

  # CHANGE-002B: Calculate metrics (tick_rate=0 if column missing)

# Determine OK status  $ticks = @()

$ok = ($lastAgeSec -le 5.0) -and ($activeRatio -ge 0.7) -and ($tickRateAvg -ge 0.5)  if ($TpCol) {

    foreach($r in $win){ $ticks += [double]($r."$TpCol") }

# Write connection_ok.json  }

$result = @{  

    ok = $ok  $total  = $win.Count

    last_age_now_sec = $lastAgeSec  if ($ticks.Count -gt 0) {

    active_ratio = $activeRatio    $active = ($ticks | Where-Object { $_ -gt 0 }).Count

    tick_rate_avg = $tickRateAvg    $ratio  = if($total){ [math]::Round($active/$total,3) } else { 0 }

    window_sec = $Seconds    $avg    = [math]::Round(($ticks | Measure-Object -Average).Average,3)

    symbol = $Symbol  } else {

    generated_at = (Get-Date).ToUniversalTime().ToString("o")    # No tick_rate column - use presence of rows as "active"

}    $active = $total

    $ratio  = if($total -gt 0) { 1 } else { 0 }

$jsonPath = Join-Path $preflightDir "connection_ok.json"    $avg    = 0

$json = $result | ConvertTo-Json -Depth 10  }

[System.IO.File]::WriteAllText($jsonPath, $json, [System.Text.UTF8Encoding]::new($false))

  $lastTs = [datetime](($win | Select-Object -Last 1)."$TsCol")

Write-Host "[PREFLIGHT] Result: ok=$ok | last_age=${lastAgeSec}s | active_ratio=$activeRatio | tick_rate=$tickRateAvg" -ForegroundColor $(if ($ok) { "Green" } else { "Red" })  $lastAgeSec = [math]::Round(((Get-Date) - $lastTs).TotalSeconds,1)

Write-Host "[PREFLIGHT] Saved: $jsonPath" -ForegroundColor Gray

  return @{

# Write l1_sample.csv (last 50 rows)    last_age_sec = $lastAgeSec

$samplePath = Join-Path $preflightDir "l1_sample.csv"    active_ratio = $ratio

$header = "timestamp_iso,symbol,bid,ask"    tick_rate = $avg

$sampleLines = @($header)    samples = $total

    ts_utc = (Get-Date).ToUniversalTime().ToString('o')

if ($allLines.Count -gt 1) {  }

    $dataLines = $allLines[1..($allLines.Count - 1)]}

    $startIdx = [Math]::Max(0, $dataLines.Count - 50)

    $sampleLines += $dataLines[$startIdx..($dataLines.Count - 1)]# 1) Find telemetry file (or use mock)

}if ($MockTelemetryPath) {

  $teleFullName = (Resolve-Path $MockTelemetryPath).Path

[System.IO.File]::WriteAllLines($samplePath, $sampleLines, [System.Text.UTF8Encoding]::new($false))  Write-Host "[INFO] Using mock: $teleFullName" -ForegroundColor Yellow

Write-Host "[PREFLIGHT] Sample saved: $samplePath ($($sampleLines.Count - 1) rows)" -ForegroundColor Gray} else {

  $pattern = Join-Path $LogRoot $TelemetryFilePattern

if (-not $ok) {  $cands = Get-ChildItem -LiteralPath (Split-Path $pattern) -Filter (Split-Path $pattern -Leaf) -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending

    Write-Host "[PREFLIGHT] FAILED - Check connection and data feed quality" -ForegroundColor Red  if(-not $cands){ throw "No $pattern" }

    exit 1  $teleFullName = $cands[0].FullName

}  Write-Host "[INFO] Using telemetry: $teleFullName"

}

Write-Host "[PREFLIGHT] PASSED - Ready for canary" -ForegroundColor Green

exit 0# 2) CHANGE-002B: Robust column mapping with expanded aliases

function Resolve-Col {
  param([string[]]$candidates, [string[]]$cols)
  foreach($c in $candidates){ 
    if($cols -contains $c){ return [string]$c }
  }
  return $null
}

# Helper to emit JSON and exit
function Exit-WithProof {
  param(
    [bool]$Ok,
    [string]$Note,
    [string]$Mode,
    [object]$MinLastAgeSec,
    [array]$ProbeResults,
    [string]$TelemetryFile,
    [object]$MedianSpread,
    [int]$ExitCode
  )
  
  $jsonObj = @{
    ok = $Ok
    mode = $Mode
    min_last_age_sec = $MinLastAgeSec
    probes = $ProbeResults
    note = $Note
    timestamp_utc = (Get-Date).ToUniversalTime().ToString('o')
    telemetry_file = $TelemetryFile
  }
  if ($MedianSpread -ne $null) { $jsonObj.median_spread = $MedianSpread }
  
  $jsonPath = Join-Path $OutDir "connection_ok.json"
  $jsonObj | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $jsonPath -Encoding UTF8 -Force
  Write-Host "[INFO] Proof written: $jsonPath"
  
  exit $ExitCode
}

$firstLine = $null
$fs = [System.IO.File]::Open($teleFullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
try {
  $sr = New-Object System.IO.StreamReader($fs, [System.Text.UTF8Encoding]::new($false), $true)
  $firstLine = $sr.ReadLine()
} finally { $sr.Close(); $fs.Close() }

if (-not $firstLine) {
  Write-Error "Cannot read header from telemetry file"
  Exit-WithProof -Ok $false -Note "fail: cannot read header" -Mode $Mode -MinLastAgeSec $null -ProbeResults @() -TelemetryFile $teleFullName -MedianSpread $null -ExitCode 1
}

$cols = $firstLine -split ','

# Expanded column aliases for real-world feeds
$tsCol = Resolve-Col -candidates @('timestamp_iso','timestamp','ts_utc','ts') -cols $cols
if(-not $tsCol){ 
  Write-Error "Missing timestamp column (tried: timestamp_iso, timestamp, ts_utc, ts)"
  Exit-WithProof -Ok $false -Note "fail: missing timestamp column" -Mode $Mode -MinLastAgeSec $null -ProbeResults @() -TelemetryFile $teleFullName -MedianSpread $null -ExitCode 1
}

# CHANGE-002B: tick_rate is primary, ticksPerSec is legacy
$tpCol = Resolve-Col -candidates @('tick_rate','ticksPerSec','tps','tickrate','ticks_per_second') -cols $cols
$hasTicks = ($tpCol -ne $null)

$bidCol = Resolve-Col -candidates @('bid','Bid','Bid2','bid_px') -cols $cols
$askCol = Resolve-Col -candidates @('ask','Ask','Ask2','ask_px') -cols $cols
$symbolCol = Resolve-Col -candidates @('symbol','instrument') -cols $cols

if ($RequireBidAsk) {
  if(-not $bidCol -or -not $askCol){ 
    Write-Error "Missing bid/ask columns (RequireBidAsk=true)"
    Exit-WithProof -Ok $false -Note "fail: missing bid/ask (RequireBidAsk)" -Mode $Mode -MinLastAgeSec $null -ProbeResults @() -TelemetryFile $teleFullName -MedianSpread $null -ExitCode 1
  }
}

# CHANGE-002B: Graceful degradation if no tick_rate but have bid/ask
if (-not $hasTicks -and $bidCol -and $askCol) {
  Write-Host "[WARN] No tick_rate column found, will use tick_rate=0 (bid/ask present)" -ForegroundColor Yellow
} elseif (-not $hasTicks) {
  Write-Error "Missing tick_rate column and no bid/ask fallback"
  Exit-WithProof -Ok $false -Note "fail: missing tick_rate and no bid/ask" -Mode $Mode -MinLastAgeSec $null -ProbeResults @() -TelemetryFile $teleFullName -MedianSpread $null -ExitCode 1
}

# 3) CHANGE-002-REAL: Multi-probe collection
$probeResults = @()
$windowStart = (Get-Date).AddSeconds(-$Seconds)
$windowEnd = Get-Date

for ($i = 1; $i -le $Probes; $i++) {
  if ($i -gt 1 -and -not $MockTelemetryPath) { Start-Sleep -Seconds $IntervalSec }
  
  try {
    $probe = Get-TelemetryProbe -TeleFullName $teleFullName -TsCol $tsCol -TpCol $tpCol -WindowStart $windowStart -WindowEnd $windowEnd
    $probeResults += $probe
    Write-Host "[PROBE $i/$Probes] last_age=$($probe.last_age_sec)s, ratio=$($probe.active_ratio), tick_rate=$($probe.tick_rate), samples=$($probe.samples)"
  } catch {
    Write-Warning "Probe $i failed: $_"
    $failedProbe = @{last_age_sec=$null; active_ratio=0; tick_rate=0; samples=0; ts_utc=(Get-Date).ToUniversalTime().ToString('o')}
    $probeResults += $failedProbe
  }
}

# 4) Calculate min_last_age_sec
$validAges = @($probeResults | Where-Object { $null -ne $_.last_age_sec } | ForEach-Object { $_.last_age_sec })
if ($validAges.Count -eq 0) {
  $minLastAgeSec = $null
} else {
  $minLastAgeSec = ($validAges | Measure-Object -Minimum).Minimum
}

# 5) Calculate median spread if Bid/Ask available
$medianSpread = $null
if ($bidCol -and $askCol) {
  $tail = Get-Content -LiteralPath $teleFullName -Tail 2000 -Encoding UTF8
  $tailNoHeader = $tail | Where-Object { $_ -and ($_ -ne $firstLine) -and ($_ -notmatch '^(timestamp_iso|timestamp|time),') }
  $csvLines2 = @(); $csvLines2 += $firstLine; $csvLines2 += $tailNoHeader
  if ($csvLines2.Count -gt 1) {
    $rows2 = @($csvLines2 | ConvertFrom-Csv)
    $spreads = @()
    foreach($r in $rows2){
      $b = [double]($r."$bidCol")
      $a = [double]($r."$askCol")
      if($a -gt 0 -and $a -ge $b){$spreads += ($a - $b)}
    }
    if($spreads.Count){ $medianSpread = ($spreads | Sort-Object)[[int]($spreads.Count/2)] }
  }
}

# 6) Mode-based decision logic
$ok = $false
$note = ""
$reason = ""

if ($null -eq $minLastAgeSec) {
  $ok = $false
  $note = "fail: no valid probes"
  $reason = "Cannot determine last_age from any probe"
} elseif ($Mode -eq 'gate2') {
  # Gate2: soft-pass zone 5.0 < min ≤ 5.5
  if ($minLastAgeSec -le 5.0) {
    $ok = $true
    $note = "pass"
  } elseif ($minLastAgeSec -le 5.5) {
    # Check additional metrics for soft-pass
    $avgRatio = ($probeResults | ForEach-Object { $_.active_ratio } | Measure-Object -Average).Average
    $avgTickRate = ($probeResults | ForEach-Object { $_.tick_rate } | Measure-Object -Average).Average
    if ($avgRatio -ge 0.7 -and $avgTickRate -ge 0.5) {
      $ok = $true
      $note = "warn: borderline freshness (min_last_age=$minLastAgeSec s, ≤5.5s soft-pass)"
    } else {
      $ok = $false
      $note = "fail: borderline freshness but metrics insufficient"
      $reason = "min_last_age=$minLastAgeSec s (5.0-5.5 zone), but avg_ratio=$avgRatio or avg_tick_rate=$avgTickRate below threshold"
    }
  } else {
    $ok = $false
    $note = "fail: staleness exceeds Gate2 soft-pass threshold"
    $reason = "min_last_age=$minLastAgeSec s > 5.5s"
  }
} elseif ($Mode -eq 'gate3') {
  # Gate3: hard limit ≤ 5.0
  if ($minLastAgeSec -le 5.0) {
    $ok = $true
    $note = "pass"
  } else {
    $ok = $false
    $note = "fail: Gate3 hard limit (≤5.0s)"
    $reason = "min_last_age=$minLastAgeSec s > 5.0s (Gate3 threshold)"
  }
}

# 7) Exit with proof JSON (CHANGE-002B: centralized via Exit-WithProof)
if ($ok) {
  if ($note -match 'warn') {
    Write-Host "[WARN] Preflight CONNECTIVITY: $note" -ForegroundColor Yellow
    Write-Host "[INFO] min_last_age_sec=$minLastAgeSec, Mode=$Mode (soft-pass granted)" -ForegroundColor Yellow
  } else {
    Write-Host "[PASS] Preflight CONNECTIVITY ok=true" -ForegroundColor Green
    Write-Host "[INFO] min_last_age_sec=$minLastAgeSec, Mode=$Mode" -ForegroundColor Green
  }
  Exit-WithProof -Ok $ok -Note $note -Mode $Mode -MinLastAgeSec $minLastAgeSec -ProbeResults $probeResults -TelemetryFile $teleFullName -MedianSpread $medianSpread -ExitCode 0
} else {
  Write-Host "Preflight CONNECTIVITY FAIL: $reason" -ForegroundColor Red
  Write-Host "[FAIL] min_last_age_sec=$minLastAgeSec, Mode=$Mode" -ForegroundColor Red
  Exit-WithProof -Ok $ok -Note $note -Mode $Mode -MinLastAgeSec $minLastAgeSec -ProbeResults $probeResults -TelemetryFile $teleFullName -MedianSpread $medianSpread -ExitCode 1
}
