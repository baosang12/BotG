#Requires -Version 5.1

<#
.SYNOPSIS
Post-run artifact collection with UTF-8 enforcement and direct Python calls

.DESCRIPTION
Executes 4-step pipeline:
1. Reconstruct FIFO trades from orders.csv (direct Python call)
2. Validate artifact schema compliance (strict validator)
3. Analyze reconstruction statistics
4. Archive validated run

.PARAMETER RunDir
Path to telemetry run directory (absolute path required)

.EXAMPLE
.\postrun_collect.ps1 -RunDir "D:\botg\logs\gate24h_run_20251003_120000"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$RunDir
)

$ErrorActionPreference = "Stop"

# Resolve and validate run directory
try {
    $resolvedPath = Resolve-Path -LiteralPath $RunDir -ErrorAction Stop
    $rd = $resolvedPath.ProviderPath.TrimEnd('\', '/')
} catch {
    throw "Failed to resolve run directory: $RunDir"
}

# Validate orders.csv exists
$ordersFile = Join-Path $rd 'orders.csv'
if (-not (Test-Path $ordersFile)) {
    throw "orders.csv missing in $rd"
}

Write-Host "╔════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║         POSTRUN COLLECTION PIPELINE                    ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "Run Directory: $rd" -ForegroundColor Gray
Write-Host ""

# CRITICAL: Enforce UTF-8 encoding for Python
$env:PYTHONIOENCODING = 'utf-8'
$python = 'python'

# Step 1: FIFO Reconstruction
Write-Host "[1/4] FIFO Trade Reconstruction" -ForegroundColor Yellow

$closesLog = Join-Path $rd 'trade_closes.log'
$metaFile = Join-Path $rd 'run_metadata.json'
$outputFile = Join-Path $rd 'closed_trades_fifo_reconstructed.csv'

$reconstructArgs = @(
    '-X', 'utf8',
    '.\path_issues\reconstruct_fifo.py',
    '--orders', $ordersFile,
    '--out', $outputFile
)

if (Test-Path $closesLog) {
    $reconstructArgs += '--closes', $closesLog
}

if (Test-Path $metaFile) {
    $reconstructArgs += '--meta', $metaFile
}

try {
    & $python @reconstructArgs
    if ($LASTEXITCODE -ne 0) {
        throw "reconstruct_fifo.py exited with code $LASTEXITCODE"
    }
    Write-Host "✓ FIFO reconstruction completed" -ForegroundColor Green
} catch {
    Write-Host "❌ FIFO reconstruction failed: $_" -ForegroundColor Red
    throw
}

Write-Host ""

# Step 2: Artifact Validation (STRICT)
Write-Host "[2/4] Schema Validation (STRICT)" -ForegroundColor Yellow

try {
    & $python -X utf8 .\path_issues\validate_artifacts.py --dir $rd
    $validationExitCode = $LASTEXITCODE
    
    if ($validationExitCode -eq 0) {
        Write-Host "✓ All artifacts validated successfully" -ForegroundColor Green
    } else {
        Write-Host "❌ Artifact validation failed (exit code: $validationExitCode)" -ForegroundColor Red
        throw "Schema validation failed - see JSON output above"
    }
} catch {
    Write-Host "❌ Validation step failed: $_" -ForegroundColor Red
    throw
}

Write-Host ""

# Step 3: Generate Analysis Summary
Write-Host "[3/4] Analysis Summary Generation" -ForegroundColor Yellow

$summaryFile = Join-Path $rd 'analysis_summary_stats.json'

try {
    if (Test-Path $outputFile) {
        $tradesCount = (Get-Content $outputFile | Measure-Object -Line).Lines - 1  # Exclude header
        
        $summary = @{
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
            run_directory = $rd
            trades_count = $tradesCount
            validation_status = "PASS"
        }
        
        $summary | ConvertTo-Json -Depth 5 | Out-File $summaryFile -Encoding utf8
        
        Write-Host "✓ Statistics saved: $summaryFile" -ForegroundColor Green
        Write-Host "  Trades Count: $tradesCount" -ForegroundColor Gray
    } else {
        Write-Host "⚠ No reconstructed trades file found, creating minimal summary" -ForegroundColor Yellow
        
        $summary = @{
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
            run_directory = $rd
            trades_count = 0
            validation_status = "PASS"
        }
        
        $summary | ConvertTo-Json -Depth 5 | Out-File $summaryFile -Encoding utf8
    }
} catch {
    Write-Host "❌ Summary generation failed: $_" -ForegroundColor Red
    throw
}

Write-Host ""

# Step 4: Archive Run
Write-Host "[4/4] Archive Creation" -ForegroundColor Yellow

try {
    $runName = Split-Path $rd -Leaf
    $parentDir = Split-Path $rd -Parent
    $zipFile = Join-Path $parentDir ("artifacts_" + $runName + ".zip")
    
    # Remove existing zip if present
    if (Test-Path $zipFile) {
        Remove-Item $zipFile -Force
    }
    
    # Create archive with proper error handling
    Compress-Archive -Path "$rd\*" -DestinationPath $zipFile -CompressionLevel Optimal -Force
    
    if (Test-Path $zipFile) {
        $zipSize = (Get-Item $zipFile).Length / 1MB
        Write-Host "✓ Archive created: $zipFile" -ForegroundColor Green
        Write-Host "  Size: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Gray
        Write-Host ""
        Write-Host "ARTIFACT_ZIP=$zipFile" -ForegroundColor Cyan
    } else {
        throw "Archive file was not created"
    }
} catch {
    Write-Host "❌ Archive creation failed: $_" -ForegroundColor Red
    throw
}

Write-Host ""
Write-Host "╔════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║         PIPELINE COMPLETED SUCCESSFULLY                ║" -ForegroundColor Green
Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Green
