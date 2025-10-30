param(
    [int]$TimeoutSec = 300,
    [string]$LogPath = "D:\botg\logs"
)

$ErrorActionPreference = "Stop"

# Create preflight directory
$preflightDir = Join-Path $LogPath "preflight"
if (-not (Test-Path $preflightDir)) {
    New-Item -ItemType Directory -Path $preflightDir -Force | Out-Null
}

# Check config.runtime.json exists
$configPath = Join-Path $LogPath "config.runtime.json"
if (-not (Test-Path $configPath)) {
    throw "Missing config.runtime.json at $configPath"
}

Write-Host "[CANARY] Enabling canary in config..." -ForegroundColor Cyan

# Read config
$configJson = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json

# Ensure Preflight.Canary.Enabled=true
if (-not $configJson.Preflight) {
    $configJson | Add-Member -MemberType NoteProperty -Name "Preflight" -Value @{} -Force
}
if (-not $configJson.Preflight.Canary) {
    $configJson.Preflight | Add-Member -MemberType NoteProperty -Name "Canary" -Value @{} -Force
}
$configJson.Preflight.Canary | Add-Member -MemberType NoteProperty -Name "Enabled" -Value $true -Force

# Write back (UTF-8 no BOM)
$configText = $configJson | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($configPath, $configText, [System.Text.UTF8Encoding]::new($false))

Write-Host "[CANARY] Config updated: Preflight.Canary.Enabled=true" -ForegroundColor Green
Write-Host ""
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "  ACTION REQUIRED: RE-ATTACH BotG.algo" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "1. Detach bot in cTrader" -ForegroundColor Cyan
Write-Host "2. Re-attach BotG.algo" -ForegroundColor Cyan
Write-Host "3. Wait for canary trade to execute..." -ForegroundColor Cyan
Write-Host ""
Write-Host "Press ENTER when bot is re-attached..." -ForegroundColor Yellow
Read-Host

Write-Host "[CANARY] Monitoring orders.csv for canary lifecycle (timeout: ${TimeoutSec}s)..." -ForegroundColor Cyan

$ordersPath = Join-Path $LogPath "orders.csv"
$startTime = Get-Date
$endTime = $startTime.AddSeconds($TimeoutSec)

$foundRequest = $false
$foundAck = $false
$foundFill = $false
$foundClose = $false

while ((Get-Date) -lt $endTime) {
    if (-not (Test-Path $ordersPath)) {
        Start-Sleep -Seconds 2
        continue
    }
    
    # Read orders.csv
    $allLines = Get-Content $ordersPath -Encoding UTF8
    
    # Search for BotG_CANARY lifecycle
    foreach ($line in $allLines) {
        if ($line -match "BotG_CANARY") {
            if ($line -match ",REQUEST,") { $foundRequest = $true }
            if ($line -match ",ACK,") { $foundAck = $true }
            if ($line -match ",FILL,") { $foundFill = $true }
            if ($line -match ",CLOSE,") { $foundClose = $true }
        }
    }
    
    # Check if all phases found
    if ($foundRequest -and $foundAck -and $foundFill -and $foundClose) {
        Write-Host "[CANARY] SUCCESS - All phases detected!" -ForegroundColor Green
        Write-Host "[CANARY] REQUEST: $foundRequest | ACK: $foundAck | FILL: $foundFill | CLOSE: $foundClose" -ForegroundColor Gray
        
        # Write orders_tail_canary.txt (last 120 rows)
        $tailPath = Join-Path $preflightDir "orders_tail_canary.txt"
        $startIdx = [Math]::Max(0, $allLines.Count - 120)
        $tailLines = $allLines[$startIdx..($allLines.Count - 1)]
        [System.IO.File]::WriteAllLines($tailPath, $tailLines, [System.Text.UTF8Encoding]::new($false))
        Write-Host "[CANARY] Tail saved: $tailPath ($($tailLines.Count) rows)" -ForegroundColor Gray
        
        # Write canary_proof.json
        $proof = @{
            ok = $true
            label = "BotG_CANARY"
            requested = $foundRequest
            ack = $foundAck
            fill = $foundFill
            close = $foundClose
            generated_at = (Get-Date).ToUniversalTime().ToString("o")
        }
        
        $proofPath = Join-Path $preflightDir "canary_proof.json"
        $proofJson = $proof | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($proofPath, $proofJson, [System.Text.UTF8Encoding]::new($false))
        Write-Host "[CANARY] Proof saved: $proofPath" -ForegroundColor Gray
        
        exit 0
    }
    
    # Progress update
    $elapsed = [int]((Get-Date) - $startTime).TotalSeconds
    if ($elapsed % 30 -eq 0 -and $elapsed -gt 0) {
        Write-Host "[CANARY] Progress: ${elapsed}s | REQUEST:$foundRequest ACK:$foundAck FILL:$foundFill CLOSE:$foundClose" -ForegroundColor Gray
    }
    
    Start-Sleep -Seconds 2
}

# Timeout - write failure proof
Write-Host "[CANARY] TIMEOUT - Not all phases detected" -ForegroundColor Red
Write-Host "[CANARY] REQUEST: $foundRequest | ACK: $foundAck | FILL: $foundFill | CLOSE: $foundClose" -ForegroundColor Gray

$proof = @{
    ok = $false
    label = "BotG_CANARY"
    requested = $foundRequest
    ack = $foundAck
    fill = $foundFill
    close = $foundClose
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
}

$proofPath = Join-Path $preflightDir "canary_proof.json"
$proofJson = $proof | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($proofPath, $proofJson, [System.Text.UTF8Encoding]::new($false))
Write-Host "[CANARY] Failure proof saved: $proofPath" -ForegroundColor Gray

exit 1
