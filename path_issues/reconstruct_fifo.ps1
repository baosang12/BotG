param(
    [Parameter(Mandatory = $true)]
    [string]$RunDir,

    [Parameter(Mandatory = $false)]
    [string]$BarsDir = $null,

    [Parameter(Mandatory = $false)]
    [string]$Python = "python",

    [Parameter(Mandatory = $false)]
    [string]$FillPhase = "FILL"
)

$ErrorActionPreference = "Stop"

function Resolve-ExistingPath {
    param(
        [string]$Path,
        [string]$Description
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $resolved) {
        throw "Required $Description not found: $Path"
    }
    return $resolved.ProviderPath
}

function Test-OptionalPath {
    param(
        [string]$Path,
        [string]$Description
    )
    
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) {
        Write-Host "⚠ Optional $Description not found: $Path" -ForegroundColor Yellow
        return $null
    }
    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

$runDir = Resolve-Path -LiteralPath $RunDir -ErrorAction Stop
$runDir = $runDir.ProviderPath

# Auto-detect required files
$ordersPath = Join-Path $runDir "orders.csv"
$ordersPath = Resolve-ExistingPath -Path $ordersPath -Description "orders.csv"

# Optional files with fallback handling
$closesPath = Join-Path $runDir "trade_closes.log"
$closesPath = Test-OptionalPath -Path $closesPath -Description "trade_closes.log"

$metaPath = Join-Path $runDir "run_metadata.json"
$metaPath = Test-OptionalPath -Path $metaPath -Description "run_metadata.json"

$outputPath = Join-Path $runDir "closed_trades_fifo_reconstructed.csv"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$pythonScript = Join-Path $scriptRoot "reconstruct_fifo.py"
$pythonScript = Resolve-ExistingPath -Path $pythonScript -Description "reconstruct_fifo.py"

# Build arguments array
$arguments = @(
    $pythonScript,
    "--orders", $ordersPath,
    "--out", $outputPath,
    "--fill-phase", $FillPhase
)

if ($closesPath) {
    $arguments += @("--closes", $closesPath)
}

if ($metaPath) {
    $arguments += @("--meta", $metaPath)
}

if ($BarsDir) {
    $barsResolved = Test-OptionalPath -Path $BarsDir -Description "bars directory"
    if ($barsResolved) {
        $arguments += @("--bars-dir", $barsResolved)
    }
}

Write-Host "Running enhanced FIFO reconstruction..." -ForegroundColor Cyan

Push-Location $runDir
try {
    & $Python @arguments
    $exitCode = $LASTEXITCODE
} catch {
    throw "Failed to launch Python: $($_.Exception.Message)"
} finally {
    Pop-Location
}

if ($exitCode -ne 0) {
    throw "reconstruct_fifo.py exited with code $exitCode"
}

if (-not (Test-Path -LiteralPath $outputPath)) {
    throw "Expected output file was not created: $outputPath"
}

# Extract summary from Python output and validate results
try {
    $csvContent = Import-Csv -LiteralPath $outputPath
    $tradeCount = $csvContent.Count
    $totalPnl = ($csvContent | Measure-Object -Property pnl_currency -Sum).Sum
    
    $checkMark = [char]0x2713
    Write-Host ("{0} FIFO reconstruction complete -> {1}" -f $checkMark, $outputPath) -ForegroundColor Green
    Write-Host ("{0} Summary: {1} trades, Total P&L: {2:F2}" -f $checkMark, $tradeCount, $totalPnl) -ForegroundColor Green
    
    if ($csvContent.Count -eq 0) {
        Write-Host "⚠ Warning: No trades were reconstructed" -ForegroundColor Yellow
    }
} catch {
    Write-Host "⚠ Could not parse output CSV for summary: $($_.Exception.Message)" -ForegroundColor Yellow
}