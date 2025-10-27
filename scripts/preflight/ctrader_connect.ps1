param(
  [string]$OutDir = "path_issues/ctrader_connect_proof",
  [int]$Seconds = 60,
  [string]$LogRoot = "D:\botg\logs",
  [string]$TelemetryFilePattern = "telemetry.csv",
  [switch]$RequireBidAsk
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 1) Chọn telemetry.csv mới nhất
$tele = Get-ChildItem $LogRoot -Recurse -Filter $TelemetryFilePattern |
        Sort-Object LastWriteTime | Select-Object -Last 1
if(-not $tele){
  Write-Error "Không tìm thấy $TelemetryFilePattern dưới $LogRoot"
  exit 1
}

# 2) Ghi nhận mốc thời gian và chờ $Seconds
$start = Get-Date
Start-Sleep -Seconds $Seconds
$end   = Get-Date

# --- PS5-safe: luôn cung cấp header thật cho ConvertFrom-Csv ---

# 1) Đọc header (dòng đầu) với FileShare ReadWrite để không khóa file
$headerLine = $null
$fs = [System.IO.File]::Open($tele.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
try {
  $sr = New-Object System.IO.StreamReader($fs, [System.Text.UTF8Encoding]::new($false), $true)
  $headerLine = $sr.ReadLine()
} finally { $sr.Close(); $fs.Close() }
if (-not $headerLine) { Write-Error "Không đọc được header từ $($tele.FullName)"; exit 1 }

# 2) Lấy tail (nhiều hơn 60–90s để dư mẫu)
$tailLines = 5000
$tail = Get-Content -LiteralPath $tele.FullName -Tail $tailLines -Encoding UTF8

# 3) Loại bỏ mọi dòng header lặp ở phần tail (khi file rotate/append)
$tailNoHeader = $tail | Where-Object {
  $_ -and ($_ -ne $headerLine) -and ($_ -notmatch '^(timestamp_iso|timestamp|time|Time|Timestamp),')
}

# 4) Ghép header + tail và ConvertFrom-Csv
$csvLines = @()
$csvLines += $headerLine
$csvLines += $tailNoHeader

if(-not $csvLines -or $csvLines.Count -lt 2) {
  Write-Error "File telemetry không có dữ liệu sau header."
  exit 1
}

$rows = @($csvLines | ConvertFrom-Csv)
if(-not $rows){ Write-Error "Không parse được CSV sau khi ghép header."; exit 1 }

# === Column detection (robust for PS5) ===
# Coerce names to [string] explicitly
$available = @()
if ($rows.Count -gt 0 -and $rows[0]) {
  $available = @($rows[0].PSObject.Properties | ForEach-Object { [string]$_.Name })
}

function Find-Col {
  param([string[]]$candidates, [string[]]$avail)
  foreach ($c in $candidates) { if ($avail -contains $c) { return [string]$c } }
  return $null
}

$tsCol = Find-Col @('timestamp_iso','timestamp','time','Time','Timestamp') $available
if (-not $tsCol) { Write-Error "Thiếu cột timestamp (timestamp_iso/timestamp)"; exit 1 }

$tpCol = Find-Col @('ticksPerSec','ticks_per_sec','tickRate','tick_rate','TPS') $available
if (-not $tpCol) { Write-Error "Thiếu cột tick-rate (ticksPerSec/tickRate)"; exit 1 }

$bidCol = Find-Col @('bid','Bid','bestBid','bidPrice') $available
$askCol = Find-Col @('ask','Ask','bestAsk','askPrice') $available

if ($RequireBidAsk -and (-not $bidCol -or -not $askCol)) {
  Write-Error "Thiếu cột bid/ask trong telemetry (RequireBidAsk=true)"
  exit 1
}

# --- Chuẩn hóa tên cột về [string] ---
$col_ts  = [string]$tsCol
$col_tp  = [string]$tpCol
$col_bid = if ($bidCol) { [string]$bidCol } else { $null }
$col_ask = if ($askCol) { [string]$askCol } else { $null }

# === Window filter (đã tính $win) ===
$windowStart = $start.AddSeconds(-3)
$win = @($rows | Where-Object {
  $t = $_."$col_ts"
  if (-not $t) { return $false }
  $dt = [datetime]$t
  ($dt -ge $windowStart) -and ($dt -le $end)
})

# === NEW: luôn sinh proof nếu thiếu mẫu, thoát 0 để các step sau vẫn chạy ===
if (-not $win -or $win.Count -lt 3) {
  $lastTs = $null
  if ($rows.Count -gt 0) {
    try { $lastTs = [datetime]($rows[-1]."$col_ts") } catch { $lastTs = $null }
  }
  $lastAgeSec = if ($lastTs) { [math]::Round(((Get-Date) - $lastTs).TotalSeconds,1) } else { $null }

  $selectColumns = @(
    ([hashtable]@{ Name='timestamp_iso'; Expression = { [string]($_."$col_ts") } }),
    ([hashtable]@{ Name='ticksPerSec' ; Expression = { [double]($_."$col_tp") } })
  )
  if ($col_bid) { $selectColumns += ([hashtable]@{ Name='bid' ; Expression = { [double]($_."$col_bid") } }) }
  if ($col_ask) { $selectColumns += ([hashtable]@{ Name='ask' ; Expression = { [double]($_."$col_ask") } }) }
  if ($col_bid -and $col_ask) { $selectColumns += ([hashtable]@{ Name='spread'; Expression = { [double]($_."$col_ask") - [double]($_."$col_bid") } }) }

  $l1Path = Join-Path $OutDir 'l1_sample.csv'
  $l1Selected = $win | Select-Object -Property $selectColumns
  if ($l1Selected) {
    $l1Selected | Export-Csv -NoTypeInformation -Encoding UTF8 $l1Path
  } else {
    $headers = ($selectColumns | ForEach-Object { $_['Name'] }) -join ','
    $headers | Out-File $l1Path -Encoding UTF8
  }

  # PS5-safe: pre-calculate values outside hashtable
  $samplesCount = @($win).Count
  $lastTsIso    = if ($lastTs) { $lastTs.ToUniversalTime().ToString('o') } else { $null }

  $proof = [pscustomobject]@{
    host               = $env:COMPUTERNAME
    telemetry_file     = $tele.FullName
    window_start_iso   = $start.ToUniversalTime().ToString('o')
    window_end_iso     = $end.ToUniversalTime().ToString('o')
    duration_sec       = [int]($end - $start).TotalSeconds
    timestamp_col      = $col_ts
    tickrate_col       = $col_tp
    bid_col            = $col_bid
    ask_col            = $col_ask
    samples            = $samplesCount
    active_ratio       = 0
    tick_rate_avg      = 0
    last_timestamp_iso = $lastTsIso
    last_age_sec       = $lastAgeSec
    has_bid_ask        = [bool]($col_bid -and $col_ask)
    spread_median      = $null
    ok                 = $false
    reason             = "insufficient_samples count=$samplesCount"
    generated_at_iso   = (Get-Date).ToUniversalTime().ToString('o')
  }
  $proofPath = Join-Path $OutDir 'connection_ok.json'
  $proof | ConvertTo-Json -Depth 5 | Out-File $proofPath -Encoding UTF8

  if (!(Test-Path $proofPath)) { Write-Error "Không tạo được connection_ok.json"; exit 1 }
  if (!(Test-Path $l1Path))    { Write-Error "Không tạo được l1_sample.csv";     exit 1 }

  Write-Host "[WARN] Preflight: thiếu mẫu nhưng đã sinh proof (ok=false)."
  exit 0
}

# --- Metrics ---
$ticks = @()
foreach($r in $win){ $ticks += [double]($r."$col_tp") }
$total  = $ticks.Count
$active = ($ticks | Where-Object { $_ -gt 0 }).Count
$ratio  = if($total){ [math]::Round($active/$total,3) } else { 0 }
$avg    = if($total){ [math]::Round(($ticks | Measure-Object -Average).Average,3) } else { 0 }

$lastTs = [datetime](($win | Select-Object -Last 1)."$col_ts")
$lastAgeSec = [math]::Round(((Get-Date) - $lastTs).TotalSeconds,1)

# --- Bid/Ask & spread median ---
$hasBA = $false; $spread_median = $null
if ($col_bid -and $col_ask) {
  $hasBA = $true
  $spreads = @()
  foreach($r in $win){
    $b=[double]($r."$col_bid"); $a=[double]($r."$col_ask")
    $spreads += ($a - $b)
  }
  $pos = $spreads | Where-Object { $_ -gt 0 }
  if ($pos.Count) {
    $sorted = $pos | Sort-Object
    $mid = [int][math]::Floor($sorted.Count/2)
    $spread_median = if ($sorted.Count % 2 -eq 0) { [math]::Round( ($sorted[$mid-1]+$sorted[$mid]) / 2.0, 10) } else { [math]::Round($sorted[$mid], 10) }
  }
}

# --- Điều kiện pass ---
$ok = ($lastAgeSec -le 5) -and ($ratio -ge 0.7) -and ($avg -ge 0.5)
$reason = if($ok){"ok"}else{"freshness=$lastAgeSec; active_ratio=$ratio; tick_rate_avg=$avg"}

# --- l1_sample.csv (tên cột chuẩn, PS5-safe) ---
$selectColumns = @(
  ([hashtable]@{ Name = 'timestamp_iso'; Expression = { [string]($_."$col_ts") } }),
  ([hashtable]@{ Name = 'ticksPerSec' ; Expression = { [double]($_."$col_tp") } })
)
if ($col_bid) { $selectColumns += ([hashtable]@{ Name='bid'   ; Expression = { [double]($_."$col_bid") } }) }
if ($col_ask) { $selectColumns += ([hashtable]@{ Name='ask'   ; Expression = { [double]($_."$col_ask") } }) }
if ($col_bid -and $col_ask) { $selectColumns += ([hashtable]@{ Name='spread'; Expression = { [double]($_."$col_ask") - [double]($_."$col_bid") } }) }

$l1 = $win | Select-Object -Property $selectColumns
$l1Path = Join-Path $OutDir "l1_sample.csv"
$l1 | Export-Csv -NoTypeInformation -Encoding UTF8 $l1Path

# --- connection_ok.json ---
$proof = [pscustomobject]@{
  host = $env:COMPUTERNAME
  telemetry_file = $tele.FullName
  window_start_iso = $start.ToUniversalTime().ToString("o")
  window_end_iso   = $end.ToUniversalTime().ToString("o")
  duration_sec = [int]($end - $start).TotalSeconds
  timestamp_col = $col_ts
  tickrate_col  = $col_tp
  bid_col = $col_bid
  ask_col = $col_ask
  samples = $total
  active_ratio = $ratio
  tick_rate_avg = $avg
  last_timestamp_iso = $lastTs.ToUniversalTime().ToString("o")
  last_age_sec = $lastAgeSec
  has_bid_ask = $hasBA
  spread_median = $spread_median
  ok = $ok
  reason = $reason
  generated_at_iso = (Get-Date).ToUniversalTime().ToString("o")
  note = "Gate3 yêu cầu has_bid_ask=true & spread_median>0"
}
$proofPath = Join-Path $OutDir "connection_ok.json"
$proof | ConvertTo-Json -Depth 5 | Out-File $proofPath -Encoding UTF8

if(!(Test-Path $proofPath) -or !(Test-Path $l1Path)){ Write-Error "Thiếu proof files"; exit 1 }
if(-not $ok){ Write-Error "Preflight CONNECTIVITY FAIL: $reason"; exit 1 }
Write-Host "[PASS] Preflight CONNECTIVITY ok=true"
exit 0
