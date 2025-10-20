param(
  [string]$OutDir = "path_issues/ctrader_connect_proof",
  [int]$Seconds = 60,
  [string]$LogRoot = "D:\botg\logs",
  [string]$TelemetryFilePattern = "telemetry.csv"
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

# 3) Đọc phần cuối file để tránh tải lớn (5k dòng cuối), parse CSV
$tailLines = 5000
$raw = Get-Content $tele.FullName -Tail $tailLines
if(-not $raw){ Write-Error "File telemetry rỗng: $($tele.FullName)"; exit 1 }
$rows = $raw | ConvertFrom-Csv
if(-not $rows){ Write-Error "Không parse được CSV từ $($tele.FullName)"; exit 1 }

# 4) Chuẩn hoá cột thời gian
$tsCol = @('timestamp_iso','timestamp') | Where-Object { $_ -in $rows[0].PSObject.Properties.Name } | Select-Object -First 1
if(-not $tsCol){ Write-Error "Thiếu cột timestamp (timestamp_iso/timestamp)"; exit 1 }

# 5) Lọc mẫu thuộc cửa sổ [$start, $end] (nới thêm 3s đầu)
$windowStart = $start.AddSeconds(-3)
$win = $rows | Where-Object {
  $t = $_.$tsCol; if(-not $t){ return $false }
  $dt = [datetime]$t
  return ($dt -ge $windowStart) -and ($dt -le $end)
}

if(-not $win -or $win.Count -lt 3){
  Write-Error "Không đủ mẫu trong cửa sổ $Seconds s. Số mẫu: $($win.Count)"
  exit 1
}

# 6) Lấy ticksPerSec
$tpCol = @('ticksPerSec','ticks_per_sec','tickRate','tick_rate') | Where-Object { $_ -in $win[0].PSObject.Properties.Name } | Select-Object -First 1
if(-not $tpCol){ Write-Error "Thiếu cột ticksPerSec/tickRate"; exit 1 }

# 7) Tính chỉ số
$ticks = $win | ForEach-Object { [double]($_.$tpCol) }
$total  = $ticks.Count
$active = ($ticks | Where-Object { $_ -gt 0 }).Count
$ratio  = if($total){ [math]::Round($active/$total,3) } else { 0 }
$avg    = if($total){ [math]::Round(($ticks | Measure-Object -Average).Average,3) } else { 0 }

$lastTs = ($win | Select-Object -Last 1).$tsCol
$lastAgeSec = [math]::Round(((Get-Date) - [datetime]$lastTs).TotalSeconds,1)

# 8) Điều kiện pass
$ok = ($lastAgeSec -le 5) -and ($ratio -ge 0.7) -and ($avg -ge 0.5)
$reason = if($ok){"ok"}else{"freshness=$lastAgeSec; active_ratio=$ratio; tick_rate_avg=$avg"}

# 9) Xuất l1_sample.csv (cột thiết yếu)
$l1 = $win | Select-Object @{N=$tsCol;E={[string]($_.$tsCol)}},
                      @{N=$tpCol;E={[double]($_.$tpCol)}},
                      ordersRequestedLastMinute, ordersFilledLastMinute, errorsLastMinute
$l1Path = Join-Path $OutDir "l1_sample.csv"
$l1 | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $l1Path

# 10) Xuất connection_ok.json
$proof = [pscustomobject]@{
  host = $env:COMPUTERNAME
  telemetry_file = $tele.FullName
  window_start_iso = $start.ToUniversalTime().ToString("o")
  window_end_iso   = $end.ToUniversalTime().ToString("o")
  duration_sec = [int]($end - $start).TotalSeconds
  timestamp_col = $tsCol
  tickrate_col  = $tpCol
  samples = $total
  active_ratio = $ratio
  tick_rate_avg = $avg
  last_timestamp_iso = ([datetime]$lastTs).ToUniversalTime().ToString("o")
  last_age_sec = $lastAgeSec
  ok = $ok
  reason = $reason
  generated_at_iso = (Get-Date).ToUniversalTime().ToString("o")
}
$proofPath = Join-Path $OutDir "connection_ok.json"
$proof | ConvertTo-Json -Depth 5 | Out-File $proofPath -Encoding UTF8

# 11) Kiểm tra file bắt buộc + exit code theo ok
if(!(Test-Path $proofPath) -or !(Test-Path $l1Path)){
  Write-Error "Thiếu file proof yêu cầu (connection_ok.json / l1_sample.csv)"
  exit 1
}

if(-not $ok){
  Write-Error "Preflight cTrader CONNECTIVITY **FAIL**: $reason"
  exit 1
}

Write-Host "[PASS] Preflight cTrader CONNECTIVITY ok=true"
exit 0
