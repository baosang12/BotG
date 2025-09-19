param(
    [string]$ProfileName="xauusd_mtf",
    [string]$Symbol="XAUUSD", 
    [string]$TF="M15",
    [string]$TrendTF="H1",
    [switch]$Paper
)

$ErrorActionPreference = "Stop"
$BOTG_ROOT = (Resolve-Path "$PSScriptRoot\..").Path
$env:BOTG_LOG_PATH = if ($env:BOTG_LOG_PATH) { $env:BOTG_LOG_PATH } else { "D:\botg\logs" }

Write-Output "=== SMOKE 15m STARTED ==="
Write-Output "ProfileName: $ProfileName"
Write-Output "Symbol: $Symbol"
Write-Output "Timeframes: $TF/$TrendTF"
Write-Output "Paper: $Paper"
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
    Write-Output "Then rerun run_smoke_15m.ps1"
    exit 2 
}

Write-Output "Using harness: $exe"

try {
    # Call real harness for 15 minutes
    & $exe --profile $ProfileName --symbol $Symbol --tf $TF --trend-tf $TrendTF --mode "paper" --duration "00:15:00" --log-path $env:BOTG_LOG_PATH
    
    Write-Output "=== SMOKE 15m COMPLETED ==="
    $LASTEXITCODE
} catch {
    Write-Error "Failed to run harness: $_"
    exit 1
} finally {
    Write-Output "Finished at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}