# Run the Harness headless for a given duration (seconds)
param(
  [int]$DurationSeconds = 60,
  [string]$LogDir = $env:BOTG_LOG_PATH,
  [string]$Mode = 'paper',
  [int]$FlushSec = 10
)

if (-not $LogDir) { $LogDir = 'D:\botg\logs' }
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
$env:BOTG_LOG_PATH = $LogDir
$env:BOTG_MODE = $Mode
$env:BOTG_TELEMETRY_FLUSH_SEC = "$FlushSec"

Write-Host "[RUN] Harness for $DurationSeconds sec -> $LogDir"
dotnet run --project "$(Join-Path $PSScriptRoot '..\Harness\Harness.csproj')" -- $DurationSeconds
