param([string]$ArtifactPath)

$ErrorActionPreference = "Stop"

# Find telemetry_run folder
$runDirs = Get-ChildItem $ArtifactPath -Directory -Filter "telemetry_run_*"
if ($runDirs.Count -eq 0) {
    throw "No telemetry_run_* folder found in $ArtifactPath"
}
$runDir = $runDirs[0].FullName

Write-Host "=== DoD-A Quick Check ===" -ForegroundColor Cyan
Write-Host "Artifact: $runDir`n"

# Check 1: File presence
$requiredFiles = @("risk_snapshots.csv", "orders.csv", "run_metadata.json")
foreach ($file in $requiredFiles) {
    $path = Join-Path $runDir $file
    if (Test-Path $path) {
        $size = (Get-Item $path).Length
        Write-Host "[OK] $file ($size bytes)" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $file missing" -ForegroundColor Red
    }
}

# Check 2: Risk snapshot count
$riskPath = Join-Path $runDir "risk_snapshots.csv"
if (Test-Path $riskPath) {
    $lines = Get-Content $riskPath
    $dataLines = $lines | Select-Object -Skip 1
    $count = $dataLines.Count
    Write-Host "`nRisk snapshots: $count samples" -ForegroundColor $(if ($count -ge 10) { "Green" } else { "Yellow" })
    
    # Show first and last timestamps
    if ($count -gt 0) {
        $first = ($dataLines[0] -split ',')[0]
        $last = ($dataLines[-1] -split ',')[0]
        Write-Host "  First: $first"
        Write-Host "  Last:  $last"
    }
}

# Check 3: Orders timestamp null rate
$ordersPath = Join-Path $runDir "orders.csv"
if (Test-Path $ordersPath) {
    $orders = Import-Csv $ordersPath
    $fillOrders = $orders | Where-Object { $_.phase -eq "FILL" }
    $totalFills = $fillOrders.Count
    
    if ($totalFills -gt 0) {
        $nullRequest = ($fillOrders | Where-Object { -not $_.timestamp_request }).Count
        $nullAck = ($fillOrders | Where-Object { -not $_.timestamp_ack }).Count
        $nullFill = ($fillOrders | Where-Object { -not $_.timestamp_fill }).Count
        
        $nullRateReq = [math]::Round($nullRequest / $totalFills * 100, 2)
        $nullRateAck = [math]::Round($nullAck / $totalFills * 100, 2)
        $nullRateFill = [math]::Round($nullFill / $totalFills * 100, 2)
        
        Write-Host "`nOrders timestamp null rates (FILL phase, n=$totalFills):" -ForegroundColor Cyan
        Write-Host "  timestamp_request: $nullRateReq% null" -ForegroundColor $(if ($nullRateReq -le 5) { "Green" } else { "Yellow" })
        Write-Host "  timestamp_ack:     $nullRateAck% null" -ForegroundColor $(if ($nullRateAck -le 5) { "Green" } else { "Yellow" })
        Write-Host "  timestamp_fill:    $nullRateFill% null" -ForegroundColor $(if ($nullRateFill -le 5) { "Green" } else { "Yellow" })
    }
}

# Check 4: Metadata validation
$metaPath = Join-Path $runDir "run_metadata.json"
if (Test-Path $metaPath) {
    $meta = Get-Content $metaPath -Raw | ConvertFrom-Json
    Write-Host "`nMetadata validation:" -ForegroundColor Cyan
    Write-Host "  hours:               $($meta.hours) $(if ($meta.hours -eq 24) { '✓' } else { '✗' })" -ForegroundColor $(if ($meta.hours -eq 24) { "Green" } else { "Red" })
    Write-Host "  mode:                $($meta.mode) $(if ($meta.mode -eq 'paper') { '✓' } else { '✗' })" -ForegroundColor $(if ($meta.mode -eq 'paper') { "Green" } else { "Red" })
    Write-Host "  simulation.enabled:  $($meta.simulation.enabled) $(if ($meta.simulation.enabled -eq $false) { '✓' } else { '✗' })" -ForegroundColor $(if ($meta.simulation.enabled -eq $false) { "Green" } else { "Red" })
    Write-Host "  seconds_per_hour:    $($meta.seconds_per_hour) $(if ($meta.seconds_per_hour -eq 3600) { '✓' } else { '✗' })" -ForegroundColor $(if ($meta.seconds_per_hour -eq 3600) { "Green" } else { "Red" })
}

Write-Host "`n=== Check Complete ===" -ForegroundColor Cyan
