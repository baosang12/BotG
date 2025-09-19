# BotG 30-Minute Healthcheck Script (Strict, Safe-read-only)
# Purpose: Analyze actual artifacts in D:\botg\logs, provide comprehensive health summary
# NO modifications to bot logic, NO fake CSV writes, ONLY read-only analysis

param(
    [string]$LogPath = "D:\botg\logs",
    [int]$WindowMins = 60,
    [int]$LatencyStopMs = 5000,
    [double]$DailyStopPct = -5.0,
    [int]$MinReqWarn = 5
)

# Set UTF-8 encoding for consistent output
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Output "=== BotG 30m Healthcheck ==="

# Step 1: Find latest run folder
try {
    if (-not (Test-Path $LogPath)) {
        Write-Output "CANNOT_RUN: LogPath not found: $LogPath"
        exit 1
    }

    $runFolders = Get-ChildItem $LogPath -Directory | Where-Object { $_.Name -like "telemetry_run_*" } | Sort-Object LastWriteTime -Descending
    if (-not $runFolders) {
        Write-Output "CANNOT_RUN: No telemetry_run_* folders found in $LogPath"
        exit 1
    }

    $latestRun = $runFolders[0]
    $runPath = $latestRun.FullName
    
    # Check if recent (within WindowMins)
    $cutoffTime = (Get-Date).AddMinutes(-$WindowMins)
    $recent = $latestRun.LastWriteTime -gt $cutoffTime
    
    Write-Output "Run path         : $runPath"
    Write-Output "Recent (<=60m)   : $recent"
}
catch {
    Write-Output "CANNOT_RUN: Error finding run folder: $($_.Exception.Message)"
    exit 1
}

# Step 2: Check orders.csv existence
$ordersPath = Join-Path $runPath "orders.csv"
if (-not (Test-Path $ordersPath)) {
    Write-Output "CANNOT_RUN: orders.csv missing"
    exit 1
}

# Step 3: Verify Logger V3 (tp column)
try {
    $header = Get-Content $ordersPath -TotalCount 1 -Encoding UTF8
    $v3_ok = $header -like "*,tp,*"
    Write-Output "Logger V3 (tp)   : $v3_ok"
}
catch {
    Write-Output "Logger V3 (tp)   : false (read error)"
    $v3_ok = $false
}

# Step 4: Count REQUEST/FILL/CLOSE entries
try {
    $allLines = Get-Content $ordersPath -Encoding UTF8
    $req = ($allLines | Where-Object { $_ -like "*,REQUEST,*" }).Count
    $fill = ($allLines | Where-Object { $_ -like "*,FILL,*" -or $_ -like "*,FILLED,*" }).Count
    $close = ($allLines | Where-Object { $_ -like "*,CLOSE,*" }).Count
    
    $fill_rate = if ($req -eq 0) { "n/a" } else { [math]::Round($fill / $req, 3) }
}
catch {
    $req = 0
    $fill = 0
    $close = 0
    $fill_rate = "n/a"
}

# Step 5: Calculate latency and slippage statistics
$latency_avg = "n/a"
$latency_p95 = "n/a"
$slippage_avg = "n/a"
$slippage_p95 = "n/a"

try {
    if ((Get-Content $ordersPath -TotalCount 2 -Encoding UTF8).Count -gt 1) {
        $csvData = Import-Csv $ordersPath -Encoding UTF8
        
        # Latency analysis
        $latencyValues = $csvData | Where-Object { 
            $_.latency_ms -and $_.latency_ms -ne "" -and [double]::TryParse($_.latency_ms, [ref]$null) -and [double]$_.latency_ms -ge 0 
        } | ForEach-Object { [double]$_.latency_ms }
        
        if ($latencyValues.Count -gt 0) {
            $latency_avg = [math]::Round(($latencyValues | Measure-Object -Average).Average, 1)
            $latency_p95 = [math]::Round(($latencyValues | Sort-Object)[[math]::Floor($latencyValues.Count * 0.95)], 1)
        }
        
        # Slippage analysis
        $slippageValues = $csvData | Where-Object { 
            $_.slippage -and $_.slippage -ne "" -and [double]::TryParse($_.slippage, [ref]$null) 
        } | ForEach-Object { [double]$_.slippage }
        
        if ($slippageValues.Count -gt 0) {
            $slippage_avg = [math]::Round(($slippageValues | Measure-Object -Average).Average, 4)
            $slippage_p95 = [math]::Round(($slippageValues | Sort-Object)[[math]::Floor($slippageValues.Count * 0.95)], 4)
        }
    }
}
catch {
    # Keep default n/a values
}

# Step 6: Read risk_snapshots.csv
$halted = "n/a"
$dailyDrawdownPct = "n/a"

$riskPath = Join-Path $runPath "risk_snapshots.csv"
if (Test-Path $riskPath) {
    try {
        $riskData = Import-Csv $riskPath -Encoding UTF8
        if ($riskData.Count -gt 0) {
            $lastRisk = $riskData[-1]
            $halted = if ($lastRisk.halted -eq "true") { "true" } else { "false" }
            if ($lastRisk.dailyDrawdownPct -and $lastRisk.dailyDrawdownPct -ne "") {
                $dailyDrawdownPct = [math]::Round([double]$lastRisk.dailyDrawdownPct, 2)
            }
        }
    }
    catch {
        # Keep default values
    }
}

# Step 7: Analyze no-trade reasons
$topNoTradeReasons = @()

# Try telemetry.csv first
$telemetryPath = Join-Path $runPath "telemetry.csv"
if (Test-Path $telemetryPath) {
    try {
        $telemetryData = Import-Csv $telemetryPath -Encoding UTF8
        $noTradeReasons = $telemetryData | Where-Object { $_.reason -and $_.reason -like "no_*" } | 
                         Group-Object reason | Sort-Object Count -Descending | Select-Object -First 5
        $topNoTradeReasons = $noTradeReasons | ForEach-Object { @{reason = $_.Name; count = $_.Count} }
    }
    catch {
        # Fallback to orders.csv analysis
    }
}

# Fallback: analyze REQUEST reasons from orders.csv
if ($topNoTradeReasons.Count -eq 0) {
    try {
        $csvData = Import-Csv $ordersPath -Encoding UTF8
        $requestReasons = $csvData | Where-Object { $_.phase -eq "REQUEST" -and $_.reason -and $_.reason -like "no_*" } | 
                         Group-Object reason | Sort-Object Count -Descending | Select-Object -First 5
        $topNoTradeReasons = $requestReasons | ForEach-Object { @{reason = $_.Name; count = $_.Count} }
    }
    catch {
        # Keep empty array
    }
}

# Step 8: Read config_stamp.json
$tradeMode = "n/a"
$liveProvider = "n/a"
$handshakeOnly = "n/a"

$configPath = Join-Path $runPath "config_stamp.json"
if (Test-Path $configPath) {
    try {
        $configContent = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $tradeMode = if ($configContent.tradeMode) { $configContent.tradeMode } else { "n/a" }
        $liveProvider = if ($configContent.liveProvider) { $configContent.liveProvider } else { "n/a" }
        $handshakeOnly = if ($configContent.handshakeOnly) { $configContent.handshakeOnly.ToString().ToLower() } else { "n/a" }
    }
    catch {
        # Keep default values
    }
}

# Step 9: Decision Logic
$decision = "PASS"
$decision_reason = "Normal operation"

# Calculate run duration for stop conditions
$runDurationMins = if ($recent) { [math]::Round(((Get-Date) - $latestRun.LastWriteTime).TotalMinutes, 1) } else { 999 }

# STOP conditions
if ($halted -eq "true") {
    $decision = "STOP"
    $decision_reason = "Risk manager halted trading"
}
elseif ($dailyDrawdownPct -ne "n/a" -and [double]$dailyDrawdownPct -le $DailyStopPct) {
    $decision = "STOP"
    $decision_reason = "Daily drawdown limit exceeded"
}
elseif ($req -ge 1 -and $fill -eq 0 -and $recent -and $runDurationMins -gt 20) {
    $decision = "STOP"
    $decision_reason = "No fills after 20+ minutes with requests"
}
elseif ($latency_p95 -ne "n/a" -and [double]$latency_p95 -ge $LatencyStopMs) {
    $decision = "STOP"
    $decision_reason = "P95 latency exceeds threshold"
}
# PAUSE conditions
elseif ($req -ge $MinReqWarn -and $fill_rate -ne "n/a" -and [double]$fill_rate -lt 0.2) {
    $decision = "PAUSE"
    $decision_reason = "Low fill rate with significant requests"
}
elseif ($topNoTradeReasons.Count -ge 3 -and 
        ($topNoTradeReasons[0].reason -like "*no_trend*" -or 
         $topNoTradeReasons[0].reason -like "*no_bos*" -or 
         $topNoTradeReasons[0].reason -like "*no_ob_or_fvg*")) {
    $decision = "PAUSE"
    $decision_reason = "Persistent structural analysis issues"
}

# Step 10: Display report
Write-Output "Mode/Provider    : tradeMode=$tradeMode liveProvider=$liveProvider handshakeOnly=$handshakeOnly"
Write-Output "REQUEST/FILL/CL  : $req/$fill/$close  fill_rate=$fill_rate"
Write-Output "Latency ms       : avg=$latency_avg  p95=$latency_p95"
Write-Output "Slippage         : avg=$slippage_avg  p95=$slippage_p95"
Write-Output "Risk             : halted=$halted  dailyDrawdownPct=$dailyDrawdownPct"

$reasonsStr = if ($topNoTradeReasons.Count -gt 0) {
    ($topNoTradeReasons | ForEach-Object { "$($_.reason)=$($_.count)" }) -join ", "
} else {
    "none"
}
Write-Output "Top no-trade     : $reasonsStr"
Write-Output "DECISION         : $decision - $decision_reason"

# Step 11: Save JSON summary
$summary = @{
    runPath = $runPath
    recent = $recent
    v3_ok = $v3_ok
    tradeMode = $tradeMode
    liveProvider = $liveProvider
    handshakeOnly = $handshakeOnly
    req = $req
    fill = $fill
    close = $close
    fill_rate = if ($fill_rate -eq "n/a") { $null } else { [double]$fill_rate }
    latency_avg_ms = if ($latency_avg -eq "n/a") { $null } else { [double]$latency_avg }
    latency_p95_ms = if ($latency_p95 -eq "n/a") { $null } else { [double]$latency_p95 }
    slippage_avg = if ($slippage_avg -eq "n/a") { $null } else { [double]$slippage_avg }
    slippage_p95 = if ($slippage_p95 -eq "n/a") { $null } else { [double]$slippage_p95 }
    halted = if ($halted -eq "n/a") { $null } else { [bool]($halted -eq "true") }
    dailyDrawdownPct = if ($dailyDrawdownPct -eq "n/a") { $null } else { [double]$dailyDrawdownPct }
    top_no_trade_reasons = $topNoTradeReasons
    decision = $decision
    decision_reason = $decision_reason
    generated_at_iso = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
}

try {
    $summaryPath = Join-Path $runPath "summary_30m.json"
    $summary | ConvertTo-Json -Depth 3 | Out-File $summaryPath -Encoding UTF8
    Write-Output ""
    Write-Output "Summary saved: $summaryPath"
}
catch {
    Write-Output "Warning: Could not save summary JSON: $($_.Exception.Message)"
}

# Step 12: Exit with appropriate code
if ($decision -eq "STOP") {
    Write-Output ""
    Write-Output "REMEDIATION SUGGESTIONS:"
    if ($decision_reason -like "*latency*") {
        Write-Output "- Check network connection and broker server performance"
        Write-Output "- Consider switching to a closer server location"
    }
    elseif ($decision_reason -like "*fill*") {
        Write-Output "- Check slippage settings and market conditions"
        Write-Output "- Verify broker execution and order types"
    }
    elseif ($decision_reason -like "*drawdown*") {
        Write-Output "- Review risk management parameters"
        Write-Output "- Analyze recent losing trades for pattern"
    }
    exit 2
}
elseif ($decision -eq "PAUSE") {
    Write-Output ""
    Write-Output "REMEDIATION SUGGESTIONS:"
    if ($decision_reason -like "*fill rate*") {
        Write-Output "- Monitor slippage and broker execution quality"
        Write-Output "- Consider adjusting entry timing or order types"
    }
    elseif ($decision_reason -like "*structural*") {
        Write-Output "- Review SMC criteria sensitivity (vol/atr thresholds)"
        Write-Output "- Consider market conditions and timeframe suitability"
    }
    exit 0
}
else {
    Write-Output ""
    Write-Output "System operating normally."
    exit 0
}

# Usage instructions
<#
Quick usage:
powershell -File .\scripts\check_30m.ps1 -LogPath "D:\botg\logs" -WindowMins 60

Parameters:
-LogPath        : Path to logs directory (default: D:\botg\logs)
-WindowMins     : Minutes to consider "recent" (default: 60)
-LatencyStopMs  : P95 latency threshold for STOP (default: 5000)
-DailyStopPct   : Daily drawdown % threshold for STOP (default: -5.0)
-MinReqWarn     : Minimum requests before warning about fill rate (default: 5)
#>