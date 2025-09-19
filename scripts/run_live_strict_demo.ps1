# BotG Live Strict Demo Runner
# Runs live trading with strict safety checks

Write-Host "=== BotG Live Strict Demo Runner ===" -ForegroundColor Cyan

# Check required environment
if (-not $env:CTRADER_API_BASEURI -or -not $env:CTRADER_API_KEY) {
    Write-Host "CANNOT_RUN: Missing required environment variables" -ForegroundColor Red
    Write-Host "Required: CTRADER_API_BASEURI, CTRADER_API_KEY" -ForegroundColor Yellow
    exit 1
}

# Check arming conditions (2-layer safety)
if ($env:SEND_REAL_ORDERS -ne "true" -or -not (Test-Path ".\CONFIRM_SEND_ORDER")) {
    Write-Host "Demo real-send not armed. To arm demo real-send:" -ForegroundColor Yellow
    Write-Host '$env:SEND_REAL_ORDERS="true"' -ForegroundColor White
    Write-Host 'New-Item -ItemType File -Path ".\CONFIRM_SEND_ORDER" | Out-Null' -ForegroundColor White
    exit 3
}

# Set default log path if not set
if (-not $env:BOTG_LOG_PATH) {
    $env:BOTG_LOG_PATH = "D:\botg\logs"
}

$exePath = "BotG.Harness\bin\Release\net9.0\BotG.Harness.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "CANNOT_RUN: harness EXE not found at $exePath" -ForegroundColor Red
    exit 1
}

Write-Host "Starting live demo trading (240 bars, ~60h)..." -ForegroundColor Green
Write-Host "Environment: Live, Mode: Strict" -ForegroundColor Yellow
Write-Host "Press Ctrl+C to stop" -ForegroundColor Gray

try {
    & $exePath --mode live --trade-mode strict --symbol XAUUSD --tf 15 --trend-tf 60 --bars 240 --log-path $env:BOTG_LOG_PATH
    
    # After completion, show results
    $latestRun = Get-ChildItem $env:BOTG_LOG_PATH -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host "`nRun completed. Path: $($latestRun.FullName)" -ForegroundColor Green
    
    $ordersFile = Join-Path $latestRun.FullName "orders.csv"
    if (Test-Path $ordersFile) {
        $requestCount = (Select-String -Path $ordersFile -Pattern ",REQUEST,").Count
        $fillCount = (Select-String -Path $ordersFile -Pattern ",FILL,").Count
        Write-Host "REQUEST=$requestCount, FILL=$fillCount" -ForegroundColor Cyan
    }
    
} catch {
    Write-Host "Demo run failed: $_" -ForegroundColor Red
    exit 1
}