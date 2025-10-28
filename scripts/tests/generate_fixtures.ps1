# Generate mock telemetry fixtures with specific ages
# CHANGE-002B: Use realistic header (timestamp_iso,symbol,bid,ask,tick_rate)
$fixturesDir = "$PSScriptRoot\fixtures"
New-Item -ItemType Directory -Force -Path $fixturesDir | Out-Null

$baseTime = Get-Date

# PASS scenario: Last timestamp 4 seconds old (well within limits)
$passLast = $baseTime.AddSeconds(-4).ToUniversalTime()
$passLines = @("timestamp_iso,symbol,bid,ask,tick_rate")
for ($i = 9; $i -ge 0; $i--) {
  $ts = $passLast.AddSeconds(-$i).ToString('o')
  $tick = 1.4 + ($i % 5) * 0.1
  $passLines += "$ts,EURUSD,1.0850,1.0852,$tick"
}
$passLines -join "`n" | Set-Content "$fixturesDir\telemetry_pass.csv" -Encoding UTF8 -NoNewline

# WARN scenario: Last timestamp 5.2 seconds old (soft-pass zone)
$warnLast = $baseTime.AddSeconds(-5.2).ToUniversalTime()
$warnLines = @("timestamp_iso,symbol,bid,ask,tick_rate")
for ($i = 9; $i -ge 0; $i--) {
  $ts = $warnLast.AddSeconds(-$i).ToString('o')
  $tick = 1.1 + ($i % 5) * 0.1
  $warnLines += "$ts,EURUSD,1.0850,1.0852,$tick"
}
$warnLines -join "`n" | Set-Content "$fixturesDir\telemetry_warn.csv" -Encoding UTF8 -NoNewline

# FAIL scenario: Last timestamp 6 seconds old (exceeds limits)
$failLast = $baseTime.AddSeconds(-6).ToUniversalTime()
$failLines = @("timestamp_iso,symbol,bid,ask,tick_rate")
for ($i = 9; $i -ge 0; $i--) {
  $ts = $failLast.AddSeconds(-$i).ToString('o')
  $tick = 1.1 + ($i % 5) * 0.1
  $failLines += "$ts,EURUSD,1.0850,1.0852,$tick"
}
$failLines -join "`n" | Set-Content "$fixturesDir\telemetry_fail.csv" -Encoding UTF8 -NoNewline

Write-Host "[OK] Generated fixtures (PASS last=4s old, WARN last=5.2s old, FAIL last=6s old)"
Write-Host "[INFO] Header: timestamp_iso,symbol,bid,ask,tick_rate (realistic format)"
