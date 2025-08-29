#requires -Version 5.1
param(
  [int]$DurationMinutes = 60,
  [string]$LogRoot = 'C:\botg',
  [switch]$Force,
  [switch]$VerboseLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info($m){ Write-Host "[INFO] $m" -ForegroundColor Cyan }
function Write-Warn($m){ Write-Warning $m }
function Write-Err($m){ Write-Host "[ERROR] $m" -ForegroundColor Red }

$ts = (Get-Date).ToString('yyyyMMdd_HHmmss')
$logDir = Join-Path $LogRoot ("logs_paper_{0}" -f $ts)
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$env:BOTG_LOG_PATH = $logDir
$env:BOTG_MODE = 'paper'
$env:BOTG_TELEMETRY_FLUSH_SEC = '5'
Write-Info "Using BOTG_LOG_PATH=$logDir"



# Prefer real bot exe if present; else fallback to harness
$botExe = Join-Path $ws 'BotG\bin\Release\net6.0\BotG.exe'
$harnessExe = Join-Path $ws 'Harness\bin\Debug\net6.0\Harness.exe'

# Ensure built binaries
Push-Location $ws
try {
  Write-Info "Building solution..."
  dotnet build "$ws" -c Debug /property:GenerateFullPaths=true /consoleLoggerParameters:NoSummary | Out-Null
} finally { Pop-Location }

if (-not (Test-Path $botExe) -and -not (Test-Path $harnessExe)) {
  # Try building Release too
  Push-Location $ws
  try { dotnet build "$ws" -c Release /property:GenerateFullPaths=true /consoleLoggerParameters:NoSummary | Out-Null } finally { Pop-Location }
}

$proc = $null
try {
  if (Test-Path $botExe) {
    Write-Info "Starting Bot EXE: $botExe"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $botExe
    $psi.Arguments = "--mode paper"
    $psi.WorkingDirectory = (Split-Path -Parent $botExe)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $proc = [System.Diagnostics.Process]::Start($psi)
  } elseif (Test-Path $harnessExe) {
    Write-Info "Starting Harness EXE: $harnessExe"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $harnessExe
    $psi.WorkingDirectory = (Split-Path -Parent $harnessExe)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $proc = [System.Diagnostics.Process]::Start($psi)
  } else {
    throw "No runnable EXE found (BotG.exe or Harness.exe)"
  }
  Write-Info ("Running for {0} minutes..." -f $DurationMinutes)
  Start-Sleep -Seconds ($DurationMinutes * 60)
  Write-Info "Stopping run (PID=$($proc.Id))"
  try { Stop-Process -Id $proc.Id -ErrorAction Stop } catch { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { } }
} catch {
  Write-Warn "Run failed or could not be stopped: $_"
} finally {
  if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch { } }
}

# Summarize + compute PnL
try {
  Write-Info "Summarizing and computing PnL..."
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ws 'scripts\smoke_collect_and_summarize.ps1') -LogDir $logDir
} catch { Write-Warn "Post-run summarize failed: $_" }

# Attempt quick reconcile if files exist
try {
  $runDirs = Get-ChildItem -Path $logDir -Directory | Where-Object { $_.Name -like 'smoke_*' } | Sort-Object Name -Descending
  $rundir = if ($runDirs) { $runDirs[0].FullName } else { $logDir }
  $closed = Join-Path $rundir 'closed_trades_fifo.csv'
  $closes = Join-Path $rundir 'trade_closes.log'
  $risk = Join-Path $rundir 'risk_snapshots.csv'
  if (-not (Test-Path $closes)) { $closes = Join-Path $logDir 'trade_closes.log' }
  if (-not (Test-Path $risk)) { $risk = Join-Path $logDir 'risk_snapshots.csv' }
  if (Test-Path $closed -and (Test-Path $risk -or Test-Path $closes)) {
    $pyRecon = Join-Path $ws 'scripts\\reconcile.py'
    Write-Info "Running reconcile..."
    # Ensure pandas exists; otherwise skip gracefully
    $pandasOk = $false
    try { & python -c "import pandas" 2>$null; if ($LASTEXITCODE -eq 0) { $pandasOk = $true } } catch { $pandasOk = $false }
    if (-not $pandasOk) { Write-Warning "pandas not available; skipping reconcile." }
    else {
      $argList = @($pyRecon, '--closed', $closed)
      if (Test-Path $closes) { $argList += @('--closes', $closes) }
      if (Test-Path $risk) { $argList += @('--risk', $risk) }
      Start-Process -FilePath python -ArgumentList $argList -NoNewWindow -Wait
    }
  } else {
    Write-Warn "Reconcile inputs missing; skipped."
  }
} catch { Write-Warn "Reconcile failed: $_" }

Write-Info "Done. LogDir=$logDir"
