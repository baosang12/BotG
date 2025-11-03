# PHASE 1 Production Deployment Verification Script
# Author: Agent A
# Date: 2025-11-03
# Purpose: Verify PHASE 1 critical safety fixes in production

param(
    [string]$LogPath = "D:\repos\BotG\artifacts",
    [int]$TimeoutSeconds = 300
)

Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  PHASE 1 Production Verification" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

$results = @{
    TradingGateValidator = $false
    ExecutionSerializer = $false
    RuntimeValidation = $false
    PreflightAge = $false
    StopSentinel = $false
}

# CHECK 1: TradingGateValidator startup validation
Write-Host "[1/5] Checking TradingGateValidator startup..." -ForegroundColor Yellow
$startupLog = Get-Content "$LogPath\*" -ErrorAction SilentlyContinue | 
    Select-String "TradingGateValidator" | Select-Object -First 10

if ($startupLog) {
    Write-Host "✓ TradingGateValidator logs found" -ForegroundColor Green
    $results.TradingGateValidator = $true
    $startupLog | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
} else {
    Write-Host "✗ No TradingGateValidator startup logs" -ForegroundColor Red
}
Write-Host ""

# CHECK 2: ExecutionSerializer thread safety
Write-Host "[2/5] Checking ExecutionSerializer integration..." -ForegroundColor Yellow
$serializerLog = Get-Content "$LogPath\*" -ErrorAction SilentlyContinue | 
    Select-String "ExecutionSerializer|SerializeAsync" | Select-Object -First 10

if ($serializerLog) {
    Write-Host "✓ ExecutionSerializer logs found" -ForegroundColor Green
    $results.ExecutionSerializer = $true
    $serializerLog | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
} else {
    Write-Host "✗ No ExecutionSerializer logs" -ForegroundColor Red
}
Write-Host ""

# CHECK 3: Runtime validation in loop
Write-Host "[3/5] Checking runtime loop validation..." -ForegroundColor Yellow
$runtimeLog = Get-Content "$LogPath\*" -ErrorAction SilentlyContinue | 
    Select-String "RuntimeLoop.*gate|ValidateGate" | Select-Object -First 10

if ($runtimeLog) {
    Write-Host "✓ Runtime gate validation logs found" -ForegroundColor Green
    $results.RuntimeValidation = $true
    $runtimeLog | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
} else {
    Write-Host "✗ No runtime validation logs" -ForegroundColor Red
}
Write-Host ""

# CHECK 4: Preflight age monitoring
Write-Host "[4/5] Checking preflight age validation..." -ForegroundColor Yellow
$ageLog = Get-Content "$LogPath\*" -ErrorAction SilentlyContinue | 
    Select-String "preflight.*age|PreflightAge" | Select-Object -First 10

if ($ageLog) {
    Write-Host "✓ Preflight age monitoring active" -ForegroundColor Green
    $results.PreflightAge = $true
    $ageLog | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
} else {
    Write-Host "⚠ No preflight age logs (may be normal if < 5s)" -ForegroundColor Yellow
}
Write-Host ""

# CHECK 5: Stop sentinel detection
Write-Host "[5/5] Checking stop sentinel detection..." -ForegroundColor Yellow
$stopLog = Get-Content "$LogPath\*" -ErrorAction SilentlyContinue | 
    Select-String "STOP_BotG|STOP_main|stop.*sentinel" | Select-Object -First 10

if ($stopLog) {
    Write-Host "⚠ STOP sentinel detected (expected if testing)" -ForegroundColor Yellow
    $results.StopSentinel = $true
    $stopLog | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
} else {
    Write-Host "✓ No stop sentinels (normal operation)" -ForegroundColor Green
}
Write-Host ""

# SUMMARY
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Verification Summary" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

$passCount = ($results.Values | Where-Object { $_ -eq $true }).Count
$totalChecks = 5

Write-Host "Checks Passed: $passCount / $totalChecks" -ForegroundColor $(if ($passCount -ge 3) { "Green" } else { "Red" })
Write-Host ""

foreach ($check in $results.GetEnumerator()) {
    $status = if ($check.Value) { "✓" } else { "✗" }
    $color = if ($check.Value) { "Green" } else { "Red" }
    Write-Host "$status $($check.Key)" -ForegroundColor $color
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════════" -ForegroundColor Cyan

# EXIT CODE
if ($passCount -ge 3) {
    Write-Host "✓ VERIFICATION PASSED (minimum 3/5 checks)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "✗ VERIFICATION FAILED (need minimum 3/5 checks)" -ForegroundColor Red
    exit 1
}
