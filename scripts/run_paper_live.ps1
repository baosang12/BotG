param(
    [string]$Duration="04:00:00",
    [string]$ProfileName="xauusd_mtf",
    [string]$Symbol="XAUUSD",
    [string]$TF="M15",
    [string]$TrendTF="H1"
)

$ErrorActionPreference = "Stop"
$BOTG_ROOT = (Resolve-Path "$PSScriptRoot\..").Path
$env:BOTG_LOG_PATH = if ($env:BOTG_LOG_PATH) { $env:BOTG_LOG_PATH } else { "D:\botg\logs" }

Write-Output "=== PAPER LIVE STARTED ==="
Write-Output "Duration: $Duration"
Write-Output "ProfileName: $ProfileName"
Write-Output "Symbol: $Symbol"
Write-Output "Timeframes: $TF/$TrendTF"
Write-Output "LogPath: $env:BOTG_LOG_PATH"
Write-Output "Started at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

# Look for real harness/binary
$exe = Join-Path $BOTG_ROOT "bin\Release\net8.0\BotG.exe"
if (-not (Test-Path $exe)) { 
    $exe = Join-Path $BOTG_ROOT "BotG.Harness\bin\Release\net8.0\BotG.Harness.exe" 
}
if (-not (Test-Path $exe)) { 
    Write-Output "CANNOT_RUN"
    Write-Output "Missing harness/binary. Build release or fix path:"
    Write-Output "- Expected: bin\Release\net8.0\BotG.exe"  
    Write-Output "- Fallback: BotG.Harness\bin\Release\net8.0\BotG.Harness.exe"
    Write-Output "Then rerun run_paper_live.ps1"
    exit 2 
}

Write-Output "Using harness: $exe"

try {
    # Call real harness for specified duration
    & $exe --profile $ProfileName --symbol $Symbol --tf $TF --trend-tf $TrendTF --mode "paper" --duration $Duration --log-path $env:BOTG_LOG_PATH
    
    Write-Output "=== PAPER LIVE COMPLETED ==="
    $LASTEXITCODE
} catch {
    Write-Error "Failed to run harness: $_"
    exit 1
} finally {
    Write-Output "Finished at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}