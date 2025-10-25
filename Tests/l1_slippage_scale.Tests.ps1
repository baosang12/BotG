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
    @"
symbol,side,price_requested,price_filled,timestamp_submit,timestamp_fill,lots,order_id
EURUSD,BUY,1.10000,1.10003,2025-10-23T00:00:01Z,2025-10-23T00:00:02Z,0.1,ord1
EURUSD,SELL,1.10000,1.09998,2025-10-23T00:01:01Z,2025-10-23T00:01:02Z,0.1,ord2
XAUUSD,BUY,2400.00,2400.05,2025-10-23T00:02:01Z,2025-10-23T00:02:02Z,0.01,ord3
XAUUSD,SELL,2400.00,2399.97,2025-10-23T00:03:01Z,2025-10-23T00:03:02Z,0.01,ord4
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")
    'id' | Set-Content -Encoding ascii (Join-Path $RUN "closed_trades_fifo_reconstructed.csv")

    # Create minimal l1_stream.csv (required for L1 analysis) - timestamps match order submits
    @"
timestamp,bid,ask
2025-10-23T00:00:01.000Z,1.09995,1.10005
2025-10-23T00:01:01.000Z,1.09997,1.10003
2025-10-23T00:02:01.000Z,2399.95,2400.05
2025-10-23T00:03:01.000Z,2399.98,2400.02
"@ | Set-Content -Encoding ascii (Join-Path $RUN "l1_stream.csv")

    # chạy postrun ở smoke để tạo L1 (use powershell.exe not pwsh)
    powershell -NoProfile -File scripts\postrun_collect.ps1 -RunDir $RUN -ArtifactsDir $ARTIFACTS -WithL1 -ValidateMode smoke | Out-Null
  }

  It "creates fees_slippage.csv & kpi_slippage.json" {
    $feesPath = Join-Path $ARTIFACTS "fees_slippage.csv"
    $kpiPath = Join-Path $ARTIFACTS "kpi_slippage.json"
    $feesPath | Should Exist
    $kpiPath | Should Exist
  }

  It "keeps EURUSD slippage within sane range (< 50 pts abs)" {
    $csv = Import-Csv (Join-Path $ARTIFACTS "fees_slippage.csv")
    $eur = $csv | Where-Object { $_.symbol -eq "EURUSD" }
    $eur.Count | Should BeGreaterThan 0
    # Với mapping EURUSD point_size=0.0001: chênh 0.00003 -> 0.3 pip -> 0.3 pts theo định nghĩa point='pip'; đặt ngưỡng 50 pts để tránh flakiness
    $avg = [double]($eur | Measure-Object slip_pts -Average).Average
    [Math]::Abs($avg) | Should BeLessThan 50
  }

  It "keeps XAUUSD slippage < 1000 pts abs (không còn hàng triệu pts)" {
    $csv = Import-Csv (Join-Path $ARTIFACTS "fees_slippage.csv")
    $xau = $csv | Where-Object { $_.symbol -eq "XAUUSD" }
    $xau.Count | Should BeGreaterThan 0
    $max = [double]($xau | Measure-Object slip_pts -Maximum).Maximum
    [Math]::Abs($max) | Should BeLessThan 1000
  }
}
