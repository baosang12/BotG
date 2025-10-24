Describe "validator gate2 core" {
  It "passes with fill-evidence (timestamp_fill + price_filled) instead of status lifecycle" {
    $TMP = Join-Path $env:TEMP "botg_v282_fill"
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
    $RUN = Join-Path $TMP "run"
    New-Item -ItemType Directory -Force -Path $RUN | Out-Null

    # risk with only timestamp_utc + equity (no drawdown/exposure)
    @"
timestamp_utc,equity
2025-10-23T00:00:00Z,10000
2025-10-23T01:00:00Z,10010
2025-10-23T02:00:00Z,10005
2025-10-23T03:00:00Z,10020
2025-10-23T04:00:00Z,10015
2025-10-23T05:00:00Z,10025
2025-10-23T06:00:00Z,10030
"@ | Set-Content -Encoding ascii (Join-Path $RUN "risk_snapshots.csv")

    # orders with timestamp_fill/price_filled but NO status column
    @"
timestamp,timestamp_fill,price_filled,symbol,side
2025-10-23T00:00:01Z,2025-10-23T00:00:01.500Z,1.0001,EURUSD,BUY
2025-10-23T00:00:02Z,2025-10-23T00:00:02.500Z,1.0002,EURUSD,SELL
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{"mode":"paper","simulation":{"enabled":false}}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")
    
    # Create empty FIFO file (normally created by postrun)
    'order_id' | Set-Content -Encoding ascii (Join-Path $RUN "closed_trades_fifo_reconstructed.csv")

    # Run validator directly (smoke mode to avoid row count requirements)
    $result = & python -X utf8 path_issues\validate_artifacts.py --dir $RUN --smoke-lite --run-hours 0.05 2>&1 | ConvertFrom-Json
    
    # Should PASS (warnings are logged but don't cause failure)
    $result.overall | Should Be "PASS"
    $result.warnings.Count | Should BeGreaterThan 0
    
    # Cleanup
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
  }
  
  It "accepts BUY/SELL in orders with warning, passes with status=REQUEST/ACK/FILL" {
    $TMP = Join-Path $env:TEMP "botg_v282_status"
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
    $RUN = Join-Path $TMP "run"
    New-Item -ItemType Directory -Force -Path $RUN | Out-Null

    # risk with timestamp + equity only
    @"
timestamp,equity
2025-10-23T00:00:00Z,5000
2025-10-23T01:00:00Z,5005
2025-10-23T02:00:00Z,5010
2025-10-23T03:00:00Z,5008
2025-10-23T04:00:00Z,5012
2025-10-23T05:00:00Z,5015
2025-10-23T06:00:00Z,5018
"@ | Set-Content -Encoding ascii (Join-Path $RUN "risk_snapshots.csv")

    # orders with status column containing lifecycle
    @"
timestamp,status,symbol,side
2025-10-23T00:00:01Z,REQUEST,EURUSD,BUY
2025-10-23T00:00:01Z,ACK,EURUSD,BUY
2025-10-23T00:00:01Z,FILL,EURUSD,BUY
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{"mode":"paper","simulation":{"enabled":false}}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")
    'order_id' | Set-Content -Encoding ascii (Join-Path $RUN "closed_trades_fifo_reconstructed.csv")

    # Run validator (smoke mode)
    $result = & python -X utf8 path_issues\validate_artifacts.py --dir $RUN --smoke-lite --run-hours 0.05 2>&1 | ConvertFrom-Json
    
    # Should PASS
    $result.overall | Should Be "PASS"
    
    # Cleanup
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
  }
  
  It "derives drawdown from equity when columns missing" {
    $TMP = Join-Path $env:TEMP "botg_v282_derive"
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
    $RUN = Join-Path $TMP "run"
    New-Item -ItemType Directory -Force -Path $RUN | Out-Null

    # risk with ts variant and equity only - no R_used, no drawdown_*, no exposure_*
    @"
ts,equity
2025-10-23T00:00:00Z,1000
2025-10-23T01:00:00Z,1100
2025-10-23T02:00:00Z,1050
2025-10-23T03:00:00Z,1200
2025-10-23T04:00:00Z,1150
2025-10-23T05:00:00Z,1250
2025-10-23T06:00:00Z,1300
"@ | Set-Content -Encoding ascii (Join-Path $RUN "risk_snapshots.csv")

    # orders with event column (alternative to status)
    @"
timestamp,event,symbol,side,timestamp_fill,price_filled
2025-10-23T00:00:01Z,REQUEST,EURUSD,BUY,2025-10-23T00:00:01Z,1.0001
2025-10-23T00:00:01Z,ACK,EURUSD,BUY,2025-10-23T00:00:01Z,1.0001
2025-10-23T00:00:01Z,FILL,EURUSD,BUY,2025-10-23T00:00:01Z,1.0001
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{"mode":"paper","simulation":{"enabled":false}}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")
    'order_id' | Set-Content -Encoding ascii (Join-Path $RUN "closed_trades_fifo_reconstructed.csv")

    # Run validator (smoke mode)
    $result = & python -X utf8 path_issues\validate_artifacts.py --dir $RUN --smoke-lite --run-hours 0.05 2>&1 | ConvertFrom-Json
    
    # Should PASS with warnings about derived/missing columns
    $result.overall | Should Be "PASS"
    $result.warnings.Count | Should BeGreaterThan 0
    
    # Check for specific warnings
    ($result.warnings | Where-Object { $_.key -eq "risk.drawdown" }) | Should Not BeNullOrEmpty
    ($result.warnings | Where-Object { $_.key -eq "risk.exposure" }) | Should Not BeNullOrEmpty
    ($result.warnings | Where-Object { $_.key -eq "risk.R_used" }) | Should Not BeNullOrEmpty
    
    # Cleanup
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
  }
}
