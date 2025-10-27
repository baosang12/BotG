param(
  [string]$OutDir = "path_issues/ctrader_connect_proof",
  [int]$Seconds = 60,
  [string]$LogRoot = "D:\botg\logs",
  [string]$TelemetryFilePattern = "telemetry.csv",
  [switch]$RequireBidAsk,
  # CHANGE-002-REAL: Retry + soft-pass parameters
  [ValidateSet('gate2', 'gate3')]
  [string]$Mode = 'gate2',
  [int]$Probes = 3,
  [int]$IntervalSec = 10,
  [string]$MockTelemetryPath = $null
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "[INFO] Preflight Mode=$Mode, Probes=$Probes, IntervalSec=$IntervalSec" -ForegroundColor Cyan

# CHANGE-002-REAL: Helper function to collect metrics for one probe
# CHANGE-002B: Support missing tick_rate column (set to 0)
function Get-TelemetryProbe {
  param(
    [string]$TeleFullName,
    [string]$TsCol,
    [string]$TpCol,  # Can be $null if no tick_rate column
    [datetime]$WindowStart,
    [datetime]$WindowEnd
  )

  # Read header with FileShare
  $headerLine = $null
  $fs = [System.IO.File]::Open($TeleFullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
  try {
    $sr = New-Object System.IO.StreamReader($fs, [System.Text.UTF8Encoding]::new($false), $true)
    $headerLine = $sr.ReadLine()
  } finally { $sr.Close(); $fs.Close() }
  if (-not $headerLine) { throw "Cannot read header" }

  # Get tail
  $tailLines = 5000
  $tail = Get-Content -LiteralPath $TeleFullName -Tail $tailLines -Encoding UTF8
  $tailNoHeader = $tail | Where-Object {
    $_ -and ($_ -ne $headerLine) -and ($_ -notmatch '^(timestamp_iso|timestamp|time|Time|Timestamp),')
  }

  $csvLines = @()
  $csvLines += $headerLine
  $csvLines += $tailNoHeader
  if(-not $csvLines -or $csvLines.Count -lt 2) { throw "No telemetry data" }

  $rows = @($csvLines | ConvertFrom-Csv)
  if(-not $rows){ throw "Cannot parse CSV" }

  # Filter window
  $win = @($rows | Where-Object {
    $t = $_."$TsCol"
    if (-not $t) { return $false }
    $dt = [datetime]$t
    ($dt -ge $WindowStart) -and ($dt -le $WindowEnd)
  })

  if (-not $win -or $win.Count -lt 3) {
    # Use last row if window empty
    $lastTs = $null
    if ($rows.Count -gt 0) {
      try { $lastTs = [datetime]($rows[-1]."$TsCol") } catch { $lastTs = $null }
    }
    $lastAgeSec = if ($lastTs) { [math]::Round(((Get-Date) - $lastTs).TotalSeconds,1) } else { $null }
    
    return @{
      last_age_sec = $lastAgeSec
      active_ratio = 0
      tick_rate = 0
      samples = 0
      ts_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
  }

  # CHANGE-002B: Calculate metrics (tick_rate=0 if column missing)
  $ticks = @()
  if ($TpCol) {
    foreach($r in $win){ $ticks += [double]($r."$TpCol") }
  }
  
  $total  = $win.Count
  if ($ticks.Count -gt 0) {
    $active = ($ticks | Where-Object { $_ -gt 0 }).Count
    $ratio  = if($total){ [math]::Round($active/$total,3) } else { 0 }
    $avg    = [math]::Round(($ticks | Measure-Object -Average).Average,3)
  } else {
    # No tick_rate column - use presence of rows as "active"
    $active = $total
    $ratio  = if($total -gt 0) { 1 } else { 0 }
    $avg    = 0
  }

  $lastTs = [datetime](($win | Select-Object -Last 1)."$TsCol")
  $lastAgeSec = [math]::Round(((Get-Date) - $lastTs).TotalSeconds,1)

  return @{
    last_age_sec = $lastAgeSec
    active_ratio = $ratio
    tick_rate = $avg
    samples = $total
    ts_utc = (Get-Date).ToUniversalTime().ToString('o')
  }
}

# 1) Find telemetry file (or use mock)
if ($MockTelemetryPath) {
  $teleFullName = (Resolve-Path $MockTelemetryPath).Path
  Write-Host "[INFO] Using mock: $teleFullName" -ForegroundColor Yellow
} else {
  $pattern = Join-Path $LogRoot $TelemetryFilePattern
  $cands = Get-ChildItem -LiteralPath (Split-Path $pattern) -Filter (Split-Path $pattern -Leaf) -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
  if(-not $cands){ throw "No $pattern" }
  $teleFullName = $cands[0].FullName
  Write-Host "[INFO] Using telemetry: $teleFullName"
}

# 2) CHANGE-002B: Robust column mapping with expanded aliases
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
