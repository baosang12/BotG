Describe "Postrun L1 outputs acceptance (PR#286)" {
  BeforeAll {
    $TMP = Join-Path $env:TEMP "botg_pr286"
    Remove-Item $TMP -Recurse -Force -ErrorAction Ignore
    $RUN = Join-Path $TMP "run"
    $ARTIFACTS = Join-Path $TMP "artifacts"
    New-Item -ItemType Directory -Force -Path $RUN | Out-Null
    New-Item -ItemType Directory -Force -Path $ARTIFACTS | Out-Null

    # Minimal risk snapshots
    @"
timestamp_utc,equity
2025-10-25T00:00:00Z,10000
2025-10-25T01:00:00Z,10010
2025-10-25T02:00:00Z,10020
2025-10-25T03:00:00Z,10030
2025-10-25T04:00:00Z,10040
2025-10-25T05:00:00Z,10050
2025-10-25T06:00:00Z,10060
"@ | Set-Content -Encoding ascii (Join-Path $RUN "risk_snapshots.csv")

    # Orders with price_requested for L1 analysis
    @"
symbol,side,price_requested,price_filled,timestamp_submit,timestamp_fill,lots,order_id
EURUSD,BUY,1.10000,1.10003,2025-10-25T00:00:01Z,2025-10-25T00:00:02Z,0.1,ord1
EURUSD,SELL,1.10000,1.09998,2025-10-25T00:01:01Z,2025-10-25T00:01:02Z,0.1,ord2
XAUUSD,BUY,2400.00,2400.05,2025-10-25T00:02:01Z,2025-10-25T00:02:02Z,0.01,ord3
"@ | Set-Content -Encoding ascii (Join-Path $RUN "orders.csv")

    '{}' | Set-Content -Encoding ascii (Join-Path $RUN "run_metadata.json")
    'timestamp' | Set-Content -Encoding ascii (Join-Path $RUN "telemetry.csv")
    '' | Set-Content -Encoding ascii (Join-Path $RUN "trade_closes.log")
    'id' | Set-Content -Encoding ascii (Join-Path $RUN "closed_trades_fifo_reconstructed.csv")

    # L1 stream matching order timestamps
    @"
timestamp,bid,ask
2025-10-25T00:00:01.000Z,1.09995,1.10005
2025-10-25T00:01:01.000Z,1.09997,1.10003
2025-10-25T00:02:01.000Z,2399.95,2400.05
"@ | Set-Content -Encoding ascii (Join-Path $RUN "l1_stream.csv")

    # Run postrun_collect.ps1 with -WithL1 (use powershell.exe for compatibility)
    $scriptPath = "scripts\postrun_collect.ps1"
    $postrunCmd = "powershell -NoProfile -File $scriptPath -RunDir '$RUN' -ArtifactsDir '$ARTIFACTS' -WithL1 -ValidateMode smoke"
    Write-Host "[Test] Running: $postrunCmd" -ForegroundColor Cyan
    
    $global:PostrunOutput = Invoke-Expression $postrunCmd 2>&1
    $global:PostrunExitCode = $LASTEXITCODE
  }

  It "postrun script exits successfully (exit code 0)" {
    $global:PostrunExitCode | Should Be 0
  }

  It "creates l1 subdirectory under artifacts" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $l1Dir | Should Exist
  }

  It "creates fees_slippage.csv under l1 subdirectory" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $feesPath = Join-Path $l1Dir "fees_slippage.csv"
    $feesPath | Should Exist
    
    # Verify it contains data
    $csv = Import-Csv $feesPath
    $csv.Count | Should BeGreaterThan 0
  }

  It "creates kpi_slippage.json under l1 subdirectory" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $kpiPath = Join-Path $l1Dir "kpi_slippage.json"
    $kpiPath | Should Exist
    
    # Verify it's valid JSON
    $kpi = Get-Content $kpiPath -Raw | ConvertFrom-Json
    $kpi.coverage | Should Not BeNullOrEmpty
  }

  It "creates scale_debug.json under l1 subdirectory (PR#285)" {
    $l1Dir = Join-Path $ARTIFACTS "l1"
    $debugPath = Join-Path $l1Dir "scale_debug.json"
    $debugPath | Should Exist
    
    # Verify structure
    $debug = Get-Content $debugPath -Raw | ConvertFrom-Json
    $debug.EURUSD | Should Not BeNullOrEmpty
    $debug.XAUUSD | Should Not BeNullOrEmpty
  }

  It "does not throw 'Postrun missing outputs' error" {
    $errorText = $global:PostrunOutput | Out-String
    $errorText | Should Not Match "Postrun missing outputs"
    $errorText | Should Not Match "Postrun missing L1 outputs"
  }

  It "creates zip file including l1 subdirectory" {
    # Find the zip file
    $zipPattern = Join-Path (Split-Path $ARTIFACTS -Parent) "artifacts_*.zip"
    $zipFiles = Get-ChildItem -Path $zipPattern -ErrorAction SilentlyContinue
    
    $zipFiles.Count | Should BeGreaterThan 0
    
    # Verify zip contains l1 subdirectory
    $zip = $zipFiles[0].FullName
    $tempExtract = Join-Path $env:TEMP "botg_pr286_extract"
    Remove-Item $tempExtract -Recurse -Force -ErrorAction Ignore
    Expand-Archive -Path $zip -DestinationPath $tempExtract -Force
    
    $l1InZip = Join-Path $tempExtract "l1"
    $l1InZip | Should Exist
    
    # Verify L1 files are in zip
    $feesInZip = Join-Path $l1InZip "fees_slippage.csv"
    $kpiInZip = Join-Path $l1InZip "kpi_slippage.json"
    $feesInZip | Should Exist
    $kpiInZip | Should Exist
    
    # Cleanup
    Remove-Item $tempExtract -Recurse -Force -ErrorAction Ignore
  }

  It "copies L1 files back to RunDir for compatibility" {
    # Postrun should copy fees/kpi back to RunDir
    $feesInRun = Join-Path $RUN "fees_slippage.csv"
    $kpiInRun = Join-Path $RUN "kpi_slippage.json"
    
    $feesInRun | Should Exist
    $kpiInRun | Should Exist
  }
}
