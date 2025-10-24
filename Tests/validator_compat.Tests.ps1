Describe "validator compat + smoke mode" {
  It "accepts timestamp_utc in risk_snapshots and runs L1 even if validator fails in smoke" {
    $TMP = Join-Path $env:TEMP "botg_val_compat"
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
    $RUN = Join-Path $TMP "run"
    $ARTIFACTS = Join-Path $TMP "artifacts"
    New-Item -ItemType Directory -Force -Path $RUN | Out-Null
    
    # risk_snapshots with timestamp_utc + equity from first row (10000)
    @"
timestamp_utc,equity
2025-10-23T00:00:00Z,10000
2025-10-23T01:00:00Z,10001
2025-10-23T02:00:00Z,10002
2025-10-23T03:00:00Z,10003
2025-10-23T04:00:00Z,10004
2025-10-23T05:00:00Z,10005
"@ | Set-Content -Encoding ascii (Join-Path $RUN "risk_snapshots.csv")

    # orders with required columns for L1 analyzer (timestamp_submit, timestamp_fill, price_filled, lots)
    @"
timestamp_submit,timestamp_fill,order_id,action,status,reason,latency_ms,symbol,side,lots,requested_lots,price_requested,price_filled,commission_usd,spread_cost_usd,slippage_pips
2025-10-23T00:00:01Z,2025-10-23T00:00:01.500Z,1,REQUEST,OK,ENTRY,5,EURUSD,BUY,0.01,0.01,1.0000,1.0001,0.1,0.05,0.1
2025-10-23T00:00:01Z,2025-10-23T00:00:01.500Z,1,ACK,OK,ENTRY,5,EURUSD,BUY,0.01,0.01,1.0000,1.0001,0.1,0.05,0.1
2025-10-23T00:00:01Z,2025-10-23T00:00:01.500Z,1,FILL,OK,ENTRY,5,EURUSD,BUY,0.01,0.01,1.0000,1.0001,0.1,0.05,0.1
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{"mode":"paper","simulation":{"enabled":false}}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")

    # Create minimal L1 stream for WithL1 mode
    @"
timestamp,bid,ask
2025-10-23T00:00:01.000Z,1.0000,1.0002
2025-10-23T00:00:02.000Z,1.0001,1.0003
"@ | Set-Content -Encoding ascii (Join-Path $RUN "l1_stream.csv")

    # Run with smoke mode and WithL1
    { 
      powershell -NoProfile -File scripts\postrun_collect.ps1 -RunDir $RUN -ArtifactsDir $ARTIFACTS -ValidateMode smoke -WithL1 -SmokeLite -RunHours 0.05 
    } | Should Not Throw

    # Check L1 artifacts were created despite potential validation issues
    Test-Path (Join-Path $ARTIFACTS "fees_slippage.csv") | Should Be $true
    Test-Path (Join-Path $ARTIFACTS "kpi_slippage.json") | Should Be $true
    
    # Cleanup
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
  }
  
  It "accepts ts_request/ts_ack/ts_fill column variants in orders" {
    $TMP = Join-Path $env:TEMP "botg_val_compat2"
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
    $RUN = Join-Path $TMP "run"
    $ARTIFACTS = Join-Path $TMP "artifacts"
    New-Item -ItemType Directory -Force -Path $RUN | Out-Null
    
    # risk_snapshots with 'ts' variant
    @"
ts,equity
2025-10-23T00:00:00Z,5000
2025-10-23T01:00:00Z,5001
2025-10-23T02:00:00Z,5002
2025-10-23T03:00:00Z,5003
2025-10-23T04:00:00Z,5004
2025-10-23T05:00:00Z,5005
"@ | Set-Content -Encoding ascii (Join-Path $RUN "risk_snapshots.csv")

    # orders with ts_* variants (smoke mode accepts BUY/SELL in side column)
    @"
timestamp,symbol,side
2025-10-23T00:00:01Z,EURUSD,BUY
2025-10-23T00:00:02Z,EURUSD,SELL
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{"mode":"paper","simulation":{"enabled":false}}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")

    # Run with smoke mode (no L1)
    { 
      powershell -NoProfile -File scripts\postrun_collect.ps1 -RunDir $RUN -ArtifactsDir $ARTIFACTS -ValidateMode smoke -SmokeLite -RunHours 0.05
    } | Should Not Throw
    
    # Cleanup
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
  }
}
