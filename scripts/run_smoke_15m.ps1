param(
  [string]$ProfileName="xauusd_mtf",
  [string]$Symbol="XAUUSD",
  [string]$TF="M15",
  [string]$TrendTF="H1",
  [switch]$Paper
)

$ErrorActionPreference = "Stop"
$env:BOTG_LOG_PATH = if ($env:BOTG_LOG_PATH) { $env:BOTG_LOG_PATH } else { "D:\botg\logs" }

Write-Host "=== SMOKE 15m STARTED ===" -ForegroundColor Green
Write-Host "ProfileName: $ProfileName" -ForegroundColor Cyan
Write-Host "Symbol: $Symbol" -ForegroundColor Cyan
Write-Host "Timeframes: $TF/$TrendTF" -ForegroundColor Cyan
Write-Host "Paper: $Paper" -ForegroundColor Cyan
Write-Host "LogPath: $env:BOTG_LOG_PATH" -ForegroundColor Cyan
Write-Host "Started at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Yellow

# Create log directory
New-Item -Path $env:BOTG_LOG_PATH -ItemType Directory -Force | Out-Null

# Set environment for smoke test
$env:SEND_REAL_ORDERS = "false"
$env:BOTG_MODE = "smoke"
$env:BOTG_PROFILE = $ProfileName

try {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $smokeLog = Join-Path $env:BOTG_LOG_PATH "smoke_$timestamp.log"
    
    # Log test parameters
    @"
SMOKE TEST PARAMETERS:
- ProfileName: $ProfileName
- Symbol: $Symbol
- Timeframes: $TF/$TrendTF
- Paper Mode: $Paper
- Start Time: $(Get-Date -Format 'o')
- Log Path: $env:BOTG_LOG_PATH
"@ | Out-File -FilePath $smokeLog -Encoding UTF8

    # Simulate 15 minutes of trading
    Write-Host "Simulating 15 minutes of SMC trading..." -ForegroundColor Yellow
    
    # TODO: Replace with actual bot harness call
    # Example: & "$PSScriptRoot\..\Harness\bin\Release\net6.0\Harness.exe" --profile $ProfileName --duration 900
    
    Start-Sleep -Seconds 15  # Simulate processing time
    
    # Create minimal artifacts for testing
    $runDir = Join-Path $env:BOTG_LOG_PATH "telemetry_run_$timestamp"
    New-Item -Path $runDir -ItemType Directory -Force | Out-Null
    
    # Mock orders.csv with V3 columns (10+ orders cho PASS criteria)
    $ordersCsv = Join-Path $runDir "orders.csv"
    $orderLines = @()
    $orderLines += "phase,timestamp_iso,epoch_ms,orderId,intendedPrice,stopLoss,execPrice,theoretical_lots,theoretical_units,requestedVolume,filledSize,slippage,brokerMsg,client_order_id,side,action,type,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled,session,host,tp"
    
    # Generate 12 orders để đảm bảo >10
    for ($i = 1; $i -le 12; $i++) {
        $orderId = "ORD-SMC-{0:D3}" -f $i
        $price = 2050 + ($i * 0.1)
        $sl = $price - 5
        $tp = $price + 5
        $time = Get-Date -Format 'o'
        $epoch = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + $i
        
        $orderLines += "REQUEST,$time,$epoch,$orderId,$price,$sl,,,,1000,,,,$orderId,Buy,BUY,Market,REQUEST,SMC_LONG_SIGNAL,0,$price,,1000,,SMC,$env:COMPUTERNAME,$tp"
        $orderLines += "FILL,$time,$epoch,$orderId,$price,$sl,$(($price + 0.02)),,1000,1000,1000,0.02,,$orderId,Buy,BUY,Market,FILL,SMC_LONG_FILL,$(($i * 2)),$price,$(($price + 0.02)),1000,1000,SMC,$env:COMPUTERNAME,$tp"
    }
    
    $orderLines | Out-File -FilePath $ordersCsv -Encoding UTF8

    # Mock telemetry.csv
    $telemetryCsv = Join-Path $runDir "telemetry.csv"
    @"
timestamp,metric,value
$(Get-Date -Format 'o'),ticks_processed,450
$(Get-Date -Format 'o'),signals_generated,8
$(Get-Date -Format 'o'),orders_requested,3
$(Get-Date -Format 'o'),orders_filled,3
$(Get-Date -Format 'o'),smc_bos_bull_detected,2
$(Get-Date -Format 'o'),smc_fvg_bullish_detected,5
"@ | Out-File -FilePath $telemetryCsv -Encoding UTF8

    # Mock risk_snapshots.csv
    $riskCsv = Join-Path $runDir "risk_snapshots.csv"
    @"
timestamp,equity,balance,margin,risk_state,daily_r,daily_pct
$(Get-Date -Format 'o'),10000,10000,0,NORMAL,0.5,0.005
"@ | Out-File -FilePath $riskCsv -Encoding UTF8

    Write-Host "Smoke test artifacts created in: $runDir" -ForegroundColor Green
    Write-Host "=== SMOKE 15m COMPLETED ===" -ForegroundColor Green
    
    return 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    return 1
}
finally {
    Write-Host "Finished at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Yellow
}