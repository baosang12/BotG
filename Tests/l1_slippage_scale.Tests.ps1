Describe "L1 slippage scale by symbol (PR#283, hardened PR#284)" {
  BeforeAll {
    $TMP = Join-Path $env:TEMP "botg_pr284_harden"
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
    $RUN = Join-Path $TMP "run"
    $ARTIFACTS = Join-Path $TMP "artifacts"
    New-Item -ItemType Directory -Force -Path $RUN | Out-Null
    New-Item -ItemType Directory -Force -Path $ARTIFACTS | Out-Null

    # risk tối thiểu
    @"
timestamp_utc,equity
2025-10-23T00:00:00Z,10000
2025-10-23T01:00:00Z,10010
2025-10-23T02:00:00Z,10020
2025-10-23T03:00:00Z,10030
2025-10-23T04:00:00Z,10040
2025-10-23T05:00:00Z,10050
2025-10-23T06:00:00Z,10060
"@ | Set-Content -Encoding ascii (Join-Path $RUN "risk_snapshots.csv")

    # Orders mẫu: EURUSD, XAUUSD (with many decimals), and edge case with missing L1 tick
    @"
symbol,side,price_requested,price_filled,timestamp_submit,timestamp_fill,lots,order_id
EURUSD,BUY,1.10000,1.10003,2025-10-23T00:00:01Z,2025-10-23T00:00:02Z,0.1,ord1
EURUSD,SELL,1.10000,1.09998,2025-10-23T00:01:01Z,2025-10-23T00:01:02Z,0.1,ord2
XAUUSD,BUY,2400.00,2400.053827491,2025-10-23T00:02:01Z,2025-10-23T00:02:02Z,0.01,ord3
XAUUSD,SELL,2400.00,2399.972291038,2025-10-23T00:03:01Z,2025-10-23T00:03:02Z,0.01,ord4
XAUUSD,BUY,2401.50,2401.519283746,2025-10-23T00:04:01Z,2025-10-23T00:04:02Z,0.01,ord5
GBPUSD,BUY,1.25000,1.25002,2025-10-23T10:00:00Z,2025-10-23T10:00:01Z,0.1,ord6
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")
    'id' | Set-Content -Encoding ascii (Join-Path $RUN "closed_trades_fifo_reconstructed.csv")

    # Create l1_stream.csv - note GBPUSD order has no matching tick (timestamp too far)
    @"
timestamp,bid,ask
2025-10-23T00:00:01.000Z,1.09995,1.10005
2025-10-23T00:01:01.000Z,1.09997,1.10003
2025-10-23T00:02:01.000Z,2399.95,2400.05
2025-10-23T00:03:01.000Z,2399.98,2400.02
2025-10-23T00:04:01.000Z,2401.48,2401.52
"@ | Set-Content -Encoding ascii (Join-Path $RUN "l1_stream.csv")

    # chạy postrun ở smoke để tạo L1
    powershell -NoProfile -File scripts\postrun_collect.ps1 -RunDir $RUN -ArtifactsDir $ARTIFACTS -WithL1 -ValidateMode smoke | Out-Null
  }

  It "creates fees_slippage.csv, kpi_slippage.json & scale_debug.json" {
    $feesPath = Join-Path $ARTIFACTS "fees_slippage.csv"
    $kpiPath = Join-Path $ARTIFACTS "kpi_slippage.json"
    $debugPath = Join-Path $ARTIFACTS "scale_debug.json"
    $feesPath | Should Exist
    $kpiPath | Should Exist
    $debugPath | Should Exist
  }

  It "XAUUSD uses point_used=0.01 from mapping (not fallback from many decimals)" {
    $csv = Import-Csv (Join-Path $ARTIFACTS "fees_slippage.csv")
    $xau = $csv | Where-Object { $_.symbol -eq "XAUUSD" -and $_.point_used }
    $xau.Count | Should BeGreaterThan 0
    # All XAUUSD rows should use 0.01 from mapping
    foreach($row in $xau) {
      [double]$row.point_used | Should Be 0.01
    }
  }

  It "XAUUSD slippage stays sane: max < 1000 pts, median < 200 pts" {
    $csv = Import-Csv (Join-Path $ARTIFACTS "fees_slippage.csv")
    $xau = $csv | Where-Object { $_.symbol -eq "XAUUSD" -and $_.slip_pts }
    $xau.Count | Should BeGreaterThan 0
    
    $slips = $xau | ForEach-Object { [Math]::Abs([double]$_.slip_pts) }
    $max = ($slips | Measure-Object -Maximum).Maximum
    $median = ($slips | Sort-Object)[[Math]::Floor($slips.Count / 2)]
    
    $max | Should BeLessThan 1000
    $median | Should BeLessThan 200
  }

  It "BUY orders use ASK as px_ref_side, SELL orders use BID" {
    $csv = Import-Csv (Join-Path $ARTIFACTS "fees_slippage.csv")
    $buys = $csv | Where-Object { $_.side -eq "BUY" -and $_.px_ref_side }
    $sells = $csv | Where-Object { $_.side -eq "SELL" -and $_.px_ref_side }
    
    if($buys.Count -gt 0) {
      foreach($b in $buys) { $b.px_ref_side | Should Be "ASK" }
    }
    if($sells.Count -gt 0) {
      foreach($s in $sells) { $s.px_ref_side | Should Be "BID" }
    }
  }

  It "Orders without L1 tick have empty px_ref and no slip_pts spike" {
    $csv = Import-Csv (Join-Path $ARTIFACTS "fees_slippage.csv")
    $missing = $csv | Where-Object { -not $_.px_ref -or $_.px_ref -eq "" }
    
    # GBPUSD order should have no ref (timestamp too far from L1 ticks)
    if($missing.Count -gt 0) {
      foreach($m in $missing) {
        # slip_pts should be empty/NaN, not a huge spike
        $m.slip_pts | Should BeNullOrEmpty
      }
    }
  }

  It "scale_debug.json contains symbol stats with point_used" {
    $debug = Get-Content (Join-Path $ARTIFACTS "scale_debug.json") -Raw | ConvertFrom-Json
    $debug.symbol_stats | Should Not BeNullOrEmpty
    
    # Check XAUUSD stats
    if($debug.symbol_stats.XAUUSD) {
      $debug.symbol_stats.XAUUSD.point_used | Should Be 0.01
      $debug.symbol_stats.XAUUSD.rows | Should BeGreaterThan 0
    }
  }
}
