# BotG Preflight Readiness Check
# Determines if system is ready for demo (live) or replay trading

Write-Host "=== BotG Preflight Readiness Check ===" -ForegroundColor Cyan

# Part 1 - Check binary & logger
Write-Host "`n[1/4] Checking binary & logger..." -ForegroundColor Yellow

$exePath = "BotG.Harness\bin\Release\net9.0\BotG.Harness.exe"
if (Test-Path $exePath) {
    $fullExePath = (Resolve-Path $exePath).Path
    Write-Host "OK Found EXE: $fullExePath" -ForegroundColor Green
} else {
    Write-Host "CANNOT_RUN: harness EXE not found at $exePath" -ForegroundColor Red
    exit 1
}

# Set up log path
if (-not $env:BOTG_LOG_PATH) {
    $env:BOTG_LOG_PATH = "D:\botg\logs"
    Write-Host "INFO Created default BOTG_LOG_PATH: $env:BOTG_LOG_PATH" -ForegroundColor Yellow
}

if (-not (Test-Path $env:BOTG_LOG_PATH)) {
    New-Item -ItemType Directory -Path $env:BOTG_LOG_PATH -Force | Out-Null
    Write-Host "INFO Created log directory: $env:BOTG_LOG_PATH" -ForegroundColor Yellow
}

# Test minimal run to verify orders.csv header
Write-Host "Testing logger initialization..." -ForegroundColor Gray
try {
    $output = & $exePath --mode paper --trade-mode strict --symbol XAUUSD --bars 1 --log-path $env:BOTG_LOG_PATH 2>&1
    $latestRun = Get-ChildItem $env:BOTG_LOG_PATH -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $ordersFile = Join-Path $latestRun.FullName "orders.csv"
    
    if (Test-Path $ordersFile) {
        $header = Get-Content $ordersFile -TotalCount 1
        if ($header -match "tp") {
            Write-Host "OK Logger V3 verified (tp column present)" -ForegroundColor Green
        } else {
            Write-Host "CANNOT_RUN: orders.csv header missing tp column" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "CANNOT_RUN: orders.csv not created" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "CANNOT_RUN: logger test failed - $_" -ForegroundColor Red
    exit 1
}

# Part 2 - Check data & environment
Write-Host "`n[2/4] Checking data & environment..." -ForegroundColor Yellow

# Check XAUUSD data
$dataFile = "data\bars\XAUUSD_M15.csv"
$DATA_REPLAY_OK = $false
if (Test-Path $dataFile) {
    $lineCount = (Get-Content $dataFile | Measure-Object -Line).Lines
    if ($lineCount -ge 2000) {
        $DATA_REPLAY_OK = $true
        Write-Host "OK XAUUSD data sufficient ($lineCount lines)" -ForegroundColor Green
    } else {
        Write-Host "INFO XAUUSD data insufficient ($lineCount lines, need >=2000)" -ForegroundColor Yellow
    }
} else {
    Write-Host "INFO XAUUSD data file not found" -ForegroundColor Yellow
}

# Check cTrader environment
$LIVE_ENV_OK = $false
$apiUri = $env:CTRADER_API_BASEURI
$apiKey = $env:CTRADER_API_KEY
if ($apiUri -and $apiKey) {
    $LIVE_ENV_OK = $true
    Write-Host "OK cTrader environment configured" -ForegroundColor Green
} else {
    Write-Host "INFO cTrader environment not configured (CTRADER_API_BASEURI, CTRADER_API_KEY)" -ForegroundColor Yellow
}

# Part 3 - Check send guards
Write-Host "`n[3/4] Checking send guards..." -ForegroundColor Yellow

# Verify SEND_REAL_ORDERS is false/empty (safety)
if ($env:SEND_REAL_ORDERS -eq "true") {
    Write-Host "WARN SEND_REAL_ORDERS is armed" -ForegroundColor Yellow
} else {
    Write-Host "OK SEND_REAL_ORDERS is safe (false/empty)" -ForegroundColor Green
}

# Test live handshake if environment available
$LIVE_HANDSHAKE_OK = $false
if ($LIVE_ENV_OK) {
    Write-Host "Testing live handshake..." -ForegroundColor Gray
    try {
        $output = & $exePath --mode live --trade-mode strict --bars 1 2>&1
        if ($LASTEXITCODE -eq 0) {
            $LIVE_HANDSHAKE_OK = $true
            Write-Host "OK Live handshake successful" -ForegroundColor Green
        } elseif ($output -match "CANNOT_RUN") {
            Write-Host "INFO Live handshake failed: connection/permission issue" -ForegroundColor Yellow
        } else {
            Write-Host "INFO Live handshake failed: exit code $LASTEXITCODE" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "INFO Live handshake failed: $_" -ForegroundColor Yellow
    }
}

# Part 4 - Generate READY SIGNAL
Write-Host "`n[4/4] Generating readiness signal..." -ForegroundColor Yellow

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$artifactsDir = "artifacts"
if (-not (Test-Path $artifactsDir)) {
    New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
}

if ($LIVE_ENV_OK -and $LIVE_HANDSHAKE_OK) {
    # Ready for demo
    $readyFile = Join-Path $artifactsDir "READY_TO_RUN_DEMO_$timestamp.txt"
    $readyContent = @"
READY_TO_RUN_DEMO
exe=$fullExePath
symbol=XAUUSD tf=M15 trend=H1
logPath=$env:BOTG_LOG_PATH
"@
    Set-Content -Path $readyFile -Value $readyContent
    Write-Host "`n=== RUN_SIGNAL: READY_TO_RUN_DEMO ===" -ForegroundColor Green
    Write-Host "Ready file: $readyFile" -ForegroundColor Gray
    
} elseif ($DATA_REPLAY_OK) {
    # Ready for replay
    $readyFile = Join-Path $artifactsDir "READY_TO_RUN_REPLAY_$timestamp.txt"
    $readyContent = @"
READY_TO_RUN_REPLAY
exe=$fullExePath
symbol=XAUUSD tf=M15 trend=H1
logPath=$env:BOTG_LOG_PATH
dataLines=$lineCount
"@
    Set-Content -Path $readyFile -Value $readyContent
    Write-Host "`n=== RUN_SIGNAL: READY_TO_RUN_REPLAY ===" -ForegroundColor Green
    Write-Host "Ready file: $readyFile" -ForegroundColor Gray
    
} else {
    # Cannot run
    Write-Host "`nCANNOT_RUN: need LIVE env (CTRADER_*), or XAUUSD_M15.csv >= 2000 bars" -ForegroundColor Red
    exit 1
}