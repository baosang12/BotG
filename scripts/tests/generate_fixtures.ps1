# Generate mock telemetry fixtures with specific ages
$fixturesDir = "$PSScriptRoot\fixtures"
New-Item -ItemType Directory -Force -Path $fixturesDir | Out-Null

$baseTime = Get-Date

# PASS scenario: Last timestamp 4 seconds old (well within limits)
$passLast = $baseTime.AddSeconds(-4).ToUniversalTime()
$passLines = @("timestamp_iso,ticksPerSec,bid,ask,provider")
for ($i = 9; $i -ge 0; $i--) {
  $ts = $passLast.AddSeconds(-$i).ToString('o')
  $tick = 1.4 + ($i % 5) * 0.1
  $passLines += "$ts,$tick,1.0850,1.0852,ctrader"
}
$passLines -join "`n" | Set-Content "$fixturesDir\telemetry_pass.csv" -Encoding UTF8 -NoNewline

# WARN scenario: Last timestamp 5.2 seconds old (soft-pass zone)
$warnLast = $baseTime.AddSeconds(-5.2).ToUniversalTime()
$warnLines = @("timestamp_iso,ticksPerSec,bid,ask,provider")
for ($i = 9; $i -ge 0; $i--) {
  $ts = $warnLast.AddSeconds(-$i).ToString('o')
  $tick = 1.1 + ($i % 5) * 0.1
  $warnLines += "$ts,$tick,1.0850,1.0852,ctrader"
}
$warnLines -join "`n" | Set-Content "$fixturesDir\telemetry_warn.csv" -Encoding UTF8 -NoNewline

# FAIL scenario: Last timestamp 6 seconds old (exceeds limits)
$failLast = $baseTime.AddSeconds(-6).ToUniversalTime()
$failLines = @("timestamp_iso,ticksPerSec,bid,ask,provider")
for ($i = 9; $i -ge 0; $i--) {
  $ts = $failLast.AddSeconds(-$i).ToString('o')
  $tick = 1.1 + ($i % 5) * 0.1
  $failLines += "$ts,$tick,1.0850,1.0852,ctrader"
}
$failLines -join "`n" | Set-Content "$fixturesDir\telemetry_fail.csv" -Encoding UTF8 -NoNewline

Write-Host "[OK] Generated fixtures (PASS last=4s old, WARN last=5.2s old, FAIL last=6s old)"
