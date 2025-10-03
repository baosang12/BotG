param(
    [Parameter(Mandatory = $true)]
    [string]$RunDir,

    [Parameter(Mandatory = $false)]
    [string]$Python = "python",

    [Parameter(Mandatory = $false)]
    [string]$BarsDir = $null
)

$ErrorActionPreference = "Stop"

function Write-StepHeader {
    param([string]$Message)
    Write-Host "`n🔄 $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    $checkMark = [char]0x2713
    Write-Host "$checkMark $Message" -ForegroundColor Green
}

function Write-Warning {
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-Error {
    param([string]$Message)
    Write-Host "❌ $Message" -ForegroundColor Red
}

$runDir = Resolve-Path -LiteralPath $RunDir -ErrorAction Stop
$runDir = $runDir.ProviderPath

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$artifactZip = Join-Path (Split-Path $runDir -Parent) "artifacts_$timestamp.zip"
$summaryJson = Join-Path $runDir "analysis_summary_stats.json"

Write-Host "Gate24h Post-Run Collection Pipeline" -ForegroundColor Magenta
Write-Host "Run Directory: $runDir" -ForegroundColor Gray
Write-Host "Timestamp: $timestamp" -ForegroundColor Gray

# Step 1: FIFO Reconstruction
Write-StepHeader "Step 1: FIFO Trade Reconstruction"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$reconstructScript = Join-Path $repoRoot "path_issues\reconstruct_fifo.py"

if (-not (Test-Path $reconstructScript)) {
    throw "reconstruct_fifo.py not found at: $reconstructScript"
}

$ordersFile = Join-Path $runDir "orders.csv"
$closesLog = Join-Path $runDir "trade_closes.log"
$metaFile = Join-Path $runDir "run_metadata.json"
$outputFile = Join-Path $runDir "closed_trades_fifo_reconstructed.csv"

$reconstructArgs = @(
    $reconstructScript,
    "--orders", $ordersFile,
    "--out", $outputFile
)

if (Test-Path $closesLog) {
    $reconstructArgs += "--closes", $closesLog
}

if (Test-Path $metaFile) {
    $reconstructArgs += "--meta", $metaFile
}

if ($BarsDir -and (Test-Path $BarsDir)) {
    $reconstructArgs += "--bars-dir", $BarsDir
}

try {
    & $Python @reconstructArgs
    if ($LASTEXITCODE -ne 0) {
        throw "reconstruct_fifo.py exited with code $LASTEXITCODE"
    }
    Write-Success "FIFO reconstruction completed"
} catch {
    Write-Error "FIFO reconstruction failed: $($_.Exception.Message)"
    throw
}

# Step 2: Artifact Validation
Write-StepHeader "Step 2: Schema Validation"

$validateScript = Join-Path $repoRoot "path_issues\validate_artifacts.py"
if (-not (Test-Path $validateScript)) {
    throw "validate_artifacts.py not found at: $validateScript"
}

$validationOutput = Join-Path $runDir "validation_results.json"

try {
    & $Python $validateScript --dir $runDir --output $validationOutput
    $validationExitCode = $LASTEXITCODE
    
    if ($validationExitCode -eq 0) {
        Write-Success "All artifacts validated successfully"
    } else {
        Write-Error "Artifact validation failed (exit code: $validationExitCode)"
        throw "Schema validation failed"
    }
} catch {
    Write-Error "Validation step failed: $($_.Exception.Message)"
    throw
}

# Step 3: Generate Analysis Summary
Write-StepHeader "Step 3: Analysis Summary Generation"

try {
    $summary = @{
        timestamp = $timestamp
        run_directory = $runDir
        trades_count = 0
        total_pnl = 0.0
        fill_rate = 0.0
        slippage_stats = @{
            p50_buy = 0.0
            p95_buy = 0.0
            p50_sell = 0.0
            p95_sell = 0.0
        }
    }

    # Extract trade statistics
    $tradesPath = Join-Path $runDir "closed_trades_fifo_reconstructed.csv"
    if (Test-Path $tradesPath) {
        $trades = Import-Csv $tradesPath
        $summary.trades_count = $trades.Count
        
        if ($trades.Count -gt 0) {
            $summary.total_pnl = ($trades | Measure-Object -Property pnl_currency -Sum).Sum
        }
    }

    # Extract fill rate from orders
    $ordersPath = Join-Path $runDir "orders.csv"
    if (Test-Path $ordersPath) {
        $orders = Import-Csv $ordersPath
        $requests = ($orders | Where-Object { $_.phase -eq "REQUEST" }).Count
        $fills = ($orders | Where-Object { $_.phase -eq "FILL" }).Count
        
        if ($requests -gt 0) {
            $summary.fill_rate = [math]::Round(($fills / $requests), 4)
        }

        # Calculate slippage percentiles by side
        $fillsWithSlippage = $orders | Where-Object { $_.phase -eq "FILL" -and $_.slippage }
        if ($fillsWithSlippage.Count -gt 0) {
            $buySlippage = $fillsWithSlippage | Where-Object { $_.side -eq "Buy" } | ForEach-Object { [double]$_.slippage }
            $sellSlippage = $fillsWithSlippage | Where-Object { $_.side -eq "Sell" } | ForEach-Object { [double]$_.slippage }
            
            if ($buySlippage.Count -gt 0) {
                $buySlippageSorted = $buySlippage | Sort-Object
                $summary.slippage_stats.p50_buy = [math]::Round($buySlippageSorted[[math]::Floor($buySlippageSorted.Count * 0.5)], 6)
                $summary.slippage_stats.p95_buy = [math]::Round($buySlippageSorted[[math]::Floor($buySlippageSorted.Count * 0.95)], 6)
            }
            
            if ($sellSlippage.Count -gt 0) {
                $sellSlippageSorted = $sellSlippage | Sort-Object
                $summary.slippage_stats.p50_sell = [math]::Round($sellSlippageSorted[[math]::Floor($sellSlippageSorted.Count * 0.5)], 6)
                $summary.slippage_stats.p95_sell = [math]::Round($sellSlippageSorted[[math]::Floor($sellSlippageSorted.Count * 0.95)], 6)
            }
        }
    }

    # Write summary JSON
    $summary | ConvertTo-Json -Depth 3 | Out-File -FilePath $summaryJson -Encoding UTF8
    Write-Success "Analysis summary generated: $summaryJson"
    
    # Display key metrics
    Write-Host "`nKey Metrics:" -ForegroundColor White
    Write-Host "  Trades: $($summary.trades_count)" -ForegroundColor Gray
    Write-Host "  Total P&L: $($summary.total_pnl)" -ForegroundColor Gray
    Write-Host "  Fill Rate: $($summary.fill_rate * 100)%" -ForegroundColor Gray

} catch {
    Write-Warning "Failed to generate analysis summary: $($_.Exception.Message)"
    # Create minimal summary
    @{
        timestamp = $timestamp
        run_directory = $runDir
        error = $_.Exception.Message
    } | ConvertTo-Json | Out-File -FilePath $summaryJson -Encoding UTF8
}

# Step 4: Archive Creation
Write-StepHeader "Step 4: Archive Creation"

try {
    if (Test-Path $artifactZip) {
        Remove-Item $artifactZip -Force
    }
    
    Compress-Archive -Path "$runDir\*" -DestinationPath $artifactZip -CompressionLevel Optimal
    
    $zipSize = [math]::Round((Get-Item $artifactZip).Length / 1MB, 2)
    Write-Success "Archive created: $artifactZip ($zipSize MB)"
    
} catch {
    Write-Error "Archive creation failed: $($_.Exception.Message)"
    throw
}

# Final Summary
Write-Host "`n🎉 Post-run collection completed successfully!" -ForegroundColor Green
Write-Host "Archive: $artifactZip" -ForegroundColor White
Write-Host "Summary: $summaryJson" -ForegroundColor White