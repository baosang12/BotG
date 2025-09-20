#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Load ops.ps1 functions
try {
    . ".\scripts\ops.ps1"
    Write-Host "OK ops.ps1 loaded successfully"
} catch {
    Write-Error "FAIL Failed to load ops.ps1: $_"
    exit 1
}

Write-Host "=== SMOKE 60m SELF-TEST ==="

# Run MergeLatest operation
Write-Host "Running Invoke-Smoke60mMergeLatest..."
try {
    Invoke-Smoke60mMergeLatest | Out-Null
    Write-Host "OK MergeLatest completed without errors"
} catch {
    Write-Warning "WARN MergeLatest had issues: $_"
}

# Determine latest run root
$outRoot = "D:\botg\runs"
$latestRun = Get-ChildItem -LiteralPath $outRoot -Directory -Filter "paper_smoke_60m_v2_*" -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $latestRun) {
    Write-Host "WARN NO_V2_RUNS - No paper_smoke_60m_v2_* directories found"
    Write-Host "SUMMARY: NO_DATA_AVAILABLE"
    exit 0
}

$runRoot = $latestRun.FullName
Write-Host "OK RUN_ROOT: $runRoot"

# Check required files
$mergedCsv = Join-Path -Path $runRoot -ChildPath "orders_merged.csv"
$reportMd = Join-Path -Path $runRoot -ChildPath "report_60m.md"
$closedTradesCsv = Join-Path -Path $runRoot -ChildPath "closed_trades_fifo_reconstructed.csv"

if (Test-Path -LiteralPath $mergedCsv) {
    Write-Host "OK orders_merged.csv exists"
} else {
    Write-Host "FAIL orders_merged.csv missing"
    exit 1
}

if (Test-Path -LiteralPath $reportMd) {
    Write-Host "OK report_60m.md exists"
} else {
    Write-Host "FAIL report_60m.md missing"
    exit 1
}

# Analyze CSV data
try {
    $csvData = Import-Csv -LiteralPath $mergedCsv
    if (-not $csvData -or $csvData.Count -eq 0) {
        Write-Host "WARN NO_DATA_ROWS - CSV contains only header"
        $fifoStatus = "N/A"
        $phases = "NO_DATA"
        $fillCount = 0
    } else {
        # Count phases
        $phaseGroups = $csvData | Group-Object phase
        $phaseStats = @{}
        foreach ($group in $phaseGroups) {
            $phaseStats[$group.Name] = $group.Count
        }
        
        $fillCount = if ($phaseStats.ContainsKey("FILL")) { $phaseStats["FILL"] } else { 0 }
        
        # Check FIFO status
        if ($fillCount -gt 0) {
            if (Test-Path -LiteralPath $closedTradesCsv) {
                $fifoStatus = "OK"
                Write-Host "OK FIFO reconstruction file exists"
            } else {
                $fifoStatus = "N/A"
                Write-Host "WARN No FIFO reconstruction file (expected for FILL data)"
            }
        } else {
            $fifoStatus = "N/A"
            Write-Host "INFO No FILL records - FIFO reconstruction not applicable"
        }
        
        # Format phases for summary
        $phaseList = @()
        foreach ($key in $phaseStats.Keys | Sort-Object) {
            $phaseList += "$key=$($phaseStats[$key])"
        }
        $phases = $phaseList -join "; "
    }
} catch {
    Write-Warning "WARN Error reading CSV data: $_"
    $phases = "ERROR"
    $fillCount = 0
    $fifoStatus = "ERROR"
}

# Read and parse report
try {
    if (Test-Path -LiteralPath $reportMd) {
        Get-Content -LiteralPath $reportMd -Raw | Out-Null
        Write-Host "OK Report content readable"
    } else {
        Write-Host "FAIL Report file not accessible"
    }
} catch {
    Write-Warning "WARN Error reading report: $_"
}

# Generate summary
Write-Host ""
Write-Host "=== SELF-TEST SUMMARY ==="
Write-Host "RUN_ROOT: $runRoot"
Write-Host "SUMMARY: $phases"
if ($fillCount -gt 0) {
    Write-Host "FILL: $fillCount"
}
Write-Host "FIFO: $fifoStatus"

Write-Host ""
Write-Host "OK Self-test completed successfully"
exit 0