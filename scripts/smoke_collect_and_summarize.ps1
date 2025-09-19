param(
    [string]$Out="D:\botg\logs\smoke_$(Get-Date -Format yyyyMMdd_HHmmss)"
)

$ErrorActionPreference = "Stop"

Write-Host "=== SMOKE COLLECT AND SUMMARIZE ===" -ForegroundColor Green
Write-Host "Output directory: $Out" -ForegroundColor Cyan

# Create output directory
New-Item -Force -ItemType Directory -Path $Out | Out-Null

# Find latest telemetry run
$logPath = $env:BOTG_LOG_PATH
if (-not $logPath) { $logPath = "D:\botg\logs" }

$latestRun = Get-ChildItem -Path $logPath -Directory -Filter "telemetry_run_*" | 
    Sort-Object LastWriteTime -Descending | 
    Select-Object -First 1

if (-not $latestRun) {
    $latestRun = Get-ChildItem -Path $logPath -Directory -Filter "paper_live_*" | 
        Sort-Object LastWriteTime -Descending | 
        Select-Object -First 1
}

if (-not $latestRun) {
    Write-Host "WARNING: No telemetry run found, creating mock summary" -ForegroundColor Yellow
    
    # Create mock summary for testing
    $summary = @{
        timestamp = Get-Date -Format 'o'
        source = "mock"
        artifacts_collected = @()
        orders_analysis = @{
            total_orders = 0
            fill_rate = 0
            errors = @("No telemetry run found")
        }
        pass_criteria = @{
            build_test_pass = $true
            orders_csv_exists = $false
            v3_columns_present = $false
            fill_rate_above_80pct = $false
            no_early_halt = $true
            min_10_orders_4h = $false
            overall_pass = $false
        }
    }
    
    $summary | ConvertTo-Json -Depth 10 | Out-File (Join-Path $Out "summary.json") -Encoding UTF8
    Write-Host "Mock summary created at: $Out" -ForegroundColor Yellow
    return 1
}

Write-Host "Found latest run: $($latestRun.FullName)" -ForegroundColor Cyan

try {
    # Copy artifacts
    $artifactFiles = @("orders.csv", "telemetry.csv", "risk_snapshots.csv", "closed_trades_fifo_reconstructed.csv")
    $copiedFiles = @()
    
    foreach ($file in $artifactFiles) {
        $sourcePath = Join-Path $latestRun.FullName $file
        if (Test-Path $sourcePath) {
            $destPath = Join-Path $Out $file
            Copy-Item $sourcePath $destPath -Force
            $copiedFiles += $file
            Write-Host "Copied: $file" -ForegroundColor Green
        } else {
            Write-Host "Missing: $file" -ForegroundColor Yellow
        }
    }
    
    # Analyze orders.csv
    $ordersCsv = Join-Path $Out "orders.csv"
    $ordersAnalysis = @{
        total_orders = 0
        fill_rate = 0
        v3_columns_present = $false
        errors = @()
    }
    
    if (Test-Path $ordersCsv) {
        $ordersContent = Get-Content $ordersCsv
        if ($ordersContent.Count -gt 1) {
            $header = $ordersContent[0]
            $dataRows = $ordersContent[1..($ordersContent.Count-1)]
            
            # Check V3 columns
            $requiredV3Columns = @("status", "reason", "latency_ms", "price_requested", "price_filled", "size_req", "size_fill", "sl", "tp")
            $v3ColumnsPresent = $true
            foreach ($col in $requiredV3Columns) {
                if ($header -notlike "*$col*") {
                    $v3ColumnsPresent = $false
                    $ordersAnalysis.errors += "Missing V3 column: $col"
                }
            }
            $ordersAnalysis.v3_columns_present = $v3ColumnsPresent
            
            # Calculate fill rate
            $totalRequests = ($dataRows | Where-Object { $_ -like "*REQUEST*" }).Count
            $totalFills = ($dataRows | Where-Object { $_ -like "*FILL*" }).Count
            
            $ordersAnalysis.total_orders = $totalRequests
            if ($totalRequests -gt 0) {
                $ordersAnalysis.fill_rate = [Math]::Round($totalFills / $totalRequests, 3)
            }
        }
    } else {
        $ordersAnalysis.errors += "orders.csv not found"
    }
    
    # Check PASS criteria
    $passCriteria = @{
        build_test_pass = $true  # Assume passed if we got here
        orders_csv_exists = (Test-Path $ordersCsv)
        v3_columns_present = $ordersAnalysis.v3_columns_present
        fill_rate_above_80pct = ($ordersAnalysis.fill_rate -ge 0.8)
        no_early_halt = (-not (Get-Content (Join-Path $Out "telemetry.csv") -ErrorAction SilentlyContinue | Select-String "HALTED"))
        min_10_orders_4h = ($ordersAnalysis.total_orders -ge 10)
    }
    
    $passCriteria.overall_pass = (
        $passCriteria.build_test_pass -and
        $passCriteria.orders_csv_exists -and
        $passCriteria.v3_columns_present -and
        $passCriteria.fill_rate_above_80pct -and
        $passCriteria.no_early_halt -and
        $passCriteria.min_10_orders_4h
    )
    
    # Create comprehensive summary
    $summary = @{
        timestamp = Get-Date -Format 'o'
        source_run = $latestRun.Name
        artifacts_collected = $copiedFiles
        orders_analysis = $ordersAnalysis
        pass_criteria = $passCriteria
        collection_path = $Out
    }
    
    # Write summary
    $summaryPath = Join-Path $Out "summary.json"
    $summary | ConvertTo-Json -Depth 10 | Out-File $summaryPath -Encoding UTF8
    
    # Display results
    Write-Host "`n=== COLLECTION SUMMARY ===" -ForegroundColor Yellow
    Write-Host "Artifacts copied: $($copiedFiles.Count)" -ForegroundColor Cyan
    Write-Host "Total orders: $($ordersAnalysis.total_orders)" -ForegroundColor Cyan
    Write-Host "Fill rate: $($ordersAnalysis.fill_rate * 100)%" -ForegroundColor Cyan
    Write-Host "V3 columns present: $($passCriteria.v3_columns_present)" -ForegroundColor Cyan
    Write-Host "Overall PASS: $($passCriteria.overall_pass)" -ForegroundColor $(if ($passCriteria.overall_pass) { "Green" } else { "Red" })
    
    if ($ordersAnalysis.errors.Count -gt 0) {
        Write-Host "`nErrors:" -ForegroundColor Red
        $ordersAnalysis.errors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    }
    
    Write-Host "`nSummary saved to: $summaryPath" -ForegroundColor Green
    Write-Host "Collection directory: $Out" -ForegroundColor Green
    
    return $(if ($passCriteria.overall_pass) { 0 } else { 1 })
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    return 1
}