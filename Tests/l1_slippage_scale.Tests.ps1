Describe "L1 slippage scale by symbol (PR#283)" {
  BeforeAll {
    $TMP = Join-Path $env:TEMP "botg_pr283"
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

    # Orders mẫu: EURUSD & XAUUSD (with timestamp_submit/timestamp_fill for join_l1_fills.py)
    # PR#285: Add price_requested for fallback testing
    @"
symbol,side,price_requested,price_filled,timestamp_submit,timestamp_fill,lots,order_id
EURUSD,BUY,1.10000,1.10003,2025-10-23T00:00:01Z,2025-10-23T00:00:02Z,0.1,ord1
EURUSD,SELL,1.10000,1.09998,2025-10-23T00:01:01Z,2025-10-23T00:01:02Z,0.1,ord2
XAUUSD,BUY,2400.00,2400.05,2025-10-23T00:02:01Z,2025-10-23T00:02:02Z,0.01,ord3
XAUUSD,SELL,2400.00,2399.97,2025-10-23T00:03:01Z,2025-10-23T00:03:02Z,0.01,ord4
GBPUSD,BUY,1.25000,1.25005,2025-10-23T00:04:01Z,2025-10-23T00:04:02Z,0.1,ord5
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")
    'id' | Set-Content -Encoding ascii (Join-Path $RUN "closed_trades_fifo_reconstructed.csv")

    # Create minimal l1_stream.csv (required for L1 analysis) - timestamps match order submits
    # PR#285: Add GBPUSD with invalid ref (big gap from px_fill) to test fallback
    @"
timestamp,bid,ask
2025-10-23T00:00:01.000Z,1.09995,1.10005
2025-10-23T00:01:01.000Z,1.09997,1.10003
2025-10-23T00:02:01.000Z,2399.95,2400.05
2025-10-23T00:03:01.000Z,2399.98,2400.02
2025-10-23T00:04:01.000Z,1.35000,1.35010
"@ | Set-Content -Encoding ascii (Join-Path $RUN "l1_stream.csv")

    # chạy postrun ở smoke để tạo L1 (use powershell.exe not pwsh)
    powershell -NoProfile -File scripts\postrun_collect.ps1 -RunDir $RUN -ArtifactsDir $ARTIFACTS -WithL1 -ValidateMode smoke | Out-Null
  }

  It "creates fees_slippage.csv & kpi_slippage.json in l1 subdirectory (PR#285)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $kpiPath = Join-Path $l1Dir "kpi_slippage.json"
    $scaleDebugPath = Join-Path $l1Dir "scale_debug.json"
    
    $l1Dir | Should Exist
    $feesPath | Should Exist
    $kpiPath | Should Exist
    $scaleDebugPath | Should Exist
  }

  It "keeps EURUSD slippage within sane range (< 50 pts abs)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $csv = Import-Csv $feesPath
    $eur = $csv | Where-Object { $_.symbol -eq "EURUSD" }
    $eur.Count | Should BeGreaterThan 0
    # Với mapping EURUSD point_size=0.0001: chênh 0.00003 -> 0.3 pip -> 0.3 pts theo định nghĩa point='pip'; đặt ngưỡng 50 pts để tránh flakiness
    $avg = [double]($eur | Measure-Object slip_pts -Average).Average
    [Math]::Abs($avg) | Should BeLessThan 50
  }

  It "keeps XAUUSD slippage < 1000 pts abs (không còn hàng triệu pts)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $csv = Import-Csv $feesPath
    $xau = $csv | Where-Object { $_.symbol -eq "XAUUSD" }
    $xau.Count | Should BeGreaterThan 0
    $max = [double]($xau | Measure-Object slip_pts -Maximum).Maximum
    [Math]::Abs($max) | Should BeLessThan 1000
  }

  It "uses L1 ref_source for valid L1 prices (PR#285)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $csv = Import-Csv $feesPath
    $xau = $csv | Where-Object { $_.symbol -eq "XAUUSD" }
    $xau.Count | Should BeGreaterThan 0
    # XAUUSD has valid L1 refs within threshold
    foreach ($row in $xau) {
      $row.ref_source | Should Be "L1"
    }
  }

  It "falls back to REQUESTED when L1 ref invalid (PR#285)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $csv = Import-Csv $feesPath
    $gbp = $csv | Where-Object { $_.symbol -eq "GBPUSD" }
    
    if ($gbp.Count -gt 0) {
      # GBPUSD has L1 ref 1.35010 (ASK for BUY) but px_fill is 1.25005 - gap ~7.4% > 5% threshold
      # Should fallback to price_requested=1.25000
      $gbp[0].ref_source | Should Be "REQUESTED"
      $px_ref = [double]$gbp[0].px_ref
      # Should be close to price_requested (1.25000), not L1 ref (1.35010)
      [Math]::Abs($px_ref - 1.25000) | Should BeLessThan 0.001
    }
  }

  It "tracks px_ref_side correctly (BUY->ASK, SELL->BID) (PR#285)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $csv = Import-Csv $feesPath
    $eur_buy = $csv | Where-Object { $_.symbol -eq "EURUSD" -and $_.side -eq "BUY" }
    $eur_sell = $csv | Where-Object { $_.symbol -eq "EURUSD" -and $_.side -eq "SELL" }
    
    if ($eur_buy.Count -gt 0 -and $eur_buy[0].ref_source -eq "L1") {
      $eur_buy[0].px_ref_side | Should Be "ASK"
    }
    if ($eur_sell.Count -gt 0 -and $eur_sell[0].ref_source -eq "L1") {
      $eur_sell[0].px_ref_side | Should Be "BID"
    }
  }

  It "populates scale_debug.json with symbol stats (PR#285)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $debugPath = Join-Path $l1Dir "scale_debug.json"
    $debug = Get-Content $debugPath -Raw | ConvertFrom-Json
    
    # Should have EURUSD and XAUUSD stats
    $debug.EURUSD | Should Not BeNullOrEmpty
    $debug.XAUUSD | Should Not BeNullOrEmpty
    
    # Check EURUSD stats structure
    $debug.EURUSD.total_rows | Should BeGreaterThan 0
    $debug.EURUSD.point_used | Should Not BeNullOrEmpty
    $debug.EURUSD.point_used -contains 0.0001 | Should Be $true
    
    # Check XAUUSD uses correct point_size
    $debug.XAUUSD.point_used -contains 0.01 | Should Be $true
    
    # Check GBPUSD has invalid_ref and fallback_requested > 0
    if ($debug.GBPUSD) {
      $debug.GBPUSD.invalid_ref | Should BeGreaterThan 0
      $debug.GBPUSD.fallback_requested | Should BeGreaterThan 0
    }
  }

  It "includes point_used column in output CSV (PR#285)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $csv = Import-Csv $feesPath
    $headers = $csv[0].PSObject.Properties.Name
    $headers -contains "point_used" | Should Be $true
    $headers -contains "ref_source" | Should Be $true
    $headers -contains "px_ref_side" | Should Be $true
  }
}

