# Pack artifacts from BOTG_LOG_PATH into a timestamped zip
param(
  [string]$LogDir = $env:BOTG_LOG_PATH
)

if (-not $LogDir) { $LogDir = ($env:BOTG_LOG_PATH ? $env:BOTG_LOG_PATH : 'D:\botg\logs') }
if (-not (Test-Path $LogDir)) { throw "Log directory not found: $LogDir" }

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$runDir = Join-Path $LogDir ("smoke_{0}" -f $ts)
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

$files = @('orders.csv','risk_snapshots.csv','telemetry.csv','size_comparison.json')
foreach ($f in $files) { $p = Join-Path $LogDir $f; if (Test-Path $p) { Copy-Item $p $runDir -Force } }

$zipPath = Join-Path $LogDir ("smoke_{0}.zip" -f $ts)
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $runDir '*') -DestinationPath $zipPath -Force

Write-Host $zipPath
