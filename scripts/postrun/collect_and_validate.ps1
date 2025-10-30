param(
    [string]$LogPath = "D:\botg\logs"
)

$ErrorActionPreference = "Stop"

# Create postrun directory
$postrunDir = Join-Path $LogPath "postrun"
if (-not (Test-Path $postrunDir)) {
    New-Item -ItemType Directory -Path $postrunDir -Force | Out-Null
}

Write-Host "[POSTRUN] Validating schema and calculating KPIs..." -ForegroundColor Cyan
Write-Host "[POSTRUN] Log path: $LogPath" -ForegroundColor Gray

# Expected headers
$expectedHeaders = @{
    "orders.csv" = "timestamp_iso,action,symbol,qty,price,status,reason,latency_ms,price_requested,price_filled"
    "telemetry.csv" = "timestamp_iso,symbol,bid,ask"
    "risk_snapshots.csv" = "timestamp_iso,equity,balance,floating"
}

# Initialize validation report
$report = @{
    orders = @{ exists = $false; headersOk = $false; rows = 0; missing = @() }
    telemetry = @{ exists = $false; headersOk = $false; rows = 0; missing = @() }
    risk = @{ exists = $false; headersOk = $false; rows = 0; missing = @() }
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
}

# Validate each file
foreach ($file in $expectedHeaders.Keys) {
    $filePath = Join-Path $LogPath $file
    $reportKey = $file.Replace(".csv", "").Replace("_snapshots", "")
    
    Write-Host "[POSTRUN] Checking $file..." -ForegroundColor Gray
    
    if (Test-Path $filePath) {
        $report[$reportKey].exists = $true
        
        # Read header
        $firstLine = (Get-Content $filePath -First 1 -Encoding UTF8)
        $expectedHeader = $expectedHeaders[$file]
        
        if ($firstLine -eq $expectedHeader) {
            $report[$reportKey].headersOk = $true
            Write-Host "[POSTRUN]   ✓ Headers OK" -ForegroundColor Green
        } else {
            $report[$reportKey].headersOk = $false
            
            # Find missing columns
            $actualCols = $firstLine -split ','
            $expectedCols = $expectedHeader -split ','
            $missing = $expectedCols | Where-Object { $actualCols -notcontains $_ }
            $report[$reportKey].missing = @($missing)
            
            Write-Host "[POSTRUN]   ✗ Headers MISMATCH" -ForegroundColor Red
            Write-Host "[POSTRUN]     Expected: $expectedHeader" -ForegroundColor Yellow
            Write-Host "[POSTRUN]     Actual:   $firstLine" -ForegroundColor Yellow
            if ($missing) {
                Write-Host "[POSTRUN]     Missing: $($missing -join ', ')" -ForegroundColor Red
            }
        }
        
        # Count rows (exclude header)
        $rowCount = (Get-Content $filePath -Encoding UTF8 | Measure-Object -Line).Lines - 1
        $report[$reportKey].rows = [Math]::Max(0, $rowCount)
        Write-Host "[POSTRUN]   Rows: $rowCount" -ForegroundColor Gray
    } else {
        Write-Host "[POSTRUN]   ✗ File NOT FOUND" -ForegroundColor Red
    }
}

# Write validator_report.json
$reportPath = Join-Path $postrunDir "validator_report.json"
$reportJson = $report | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($reportPath, $reportJson, [System.Text.UTF8Encoding]::new($false))
Write-Host "[POSTRUN] Report saved: $reportPath" -ForegroundColor Gray

# Calculate KPIs if orders.csv exists and has valid headers
$fillRate = $null
$rowsOrders = $report.orders.rows
$rowsTelemetry = $report.telemetry.rows
$rowsRisk = $report.risk.rows

if ($report.orders.exists -and $report.orders.headersOk -and $rowsOrders -gt 0) {
    Write-Host "[POSTRUN] Calculating KPIs..." -ForegroundColor Cyan
    
    $ordersPath = Join-Path $LogPath "orders.csv"
    $ordersData = Import-Csv $ordersPath -Encoding UTF8
    
    $requests = @($ordersData | Where-Object { $_.action -eq "REQUEST" })
    $fills = @($ordersData | Where-Object { $_.action -eq "FILL" })
    
    if ($requests.Count -gt 0) {
        $fillRate = [Math]::Round($fills.Count / $requests.Count, 3)
        Write-Host "[POSTRUN]   Fill rate: $fillRate ($($fills.Count)/$($requests.Count))" -ForegroundColor Gray
    } else {
        Write-Host "[POSTRUN]   No REQUEST rows found" -ForegroundColor Yellow
    }
}

# Write postrun_summary.json
$summary = @{
    fill_rate = $fillRate
    rows_orders = $rowsOrders
    rows_telemetry = $rowsTelemetry
    rows_risk = $rowsRisk
}

$summaryPath = Join-Path $postrunDir "postrun_summary.json"
$summaryJson = $summary | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($summaryPath, $summaryJson, [System.Text.UTF8Encoding]::new($false))
Write-Host "[POSTRUN] Summary saved: $summaryPath" -ForegroundColor Gray

# Determine overall result
$allValid = $report.orders.exists -and $report.orders.headersOk -and `
            $report.telemetry.exists -and $report.telemetry.headersOk -and `
            $report.risk.exists -and $report.risk.headersOk

if ($allValid) {
    Write-Host "[POSTRUN] PASSED - All files valid" -ForegroundColor Green
    
    if ($fillRate -ne $null -and $fillRate -lt 0.95) {
        Write-Host "[POSTRUN] WARNING - Fill rate below 95%: $fillRate" -ForegroundColor Yellow
    }
    
    exit 0
} else {
    Write-Host "[POSTRUN] FAILED - Schema validation errors" -ForegroundColor Red
    exit 1
}
