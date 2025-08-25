[CmdletBinding()]
param(
  [int]$LogTail = 120
)

$ErrorActionPreference = 'Stop'

function Find-LatestOutBase {
  $root = Join-Path $env:TEMP 'botg_artifacts'
  if (-not (Test-Path -LiteralPath $root)) { return $null }
  $dirs = Get-ChildItem -LiteralPath $root -Directory -Filter 'realtime_1h_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
  if ($dirs -and $dirs.Count -gt 0) { return $dirs[0].FullName }
  return $null
}

$outBase = Find-LatestOutBase
if (-not $outBase) {
  Write-Error 'No realtime_1h_* directory found under %TEMP%\botg_artifacts.'
  exit 1
}

$pidFile = Join-Path $outBase 'daemon.pid'
if (-not (Test-Path -LiteralPath $pidFile)) {
  Write-Error "daemon.pid not found in $outBase"
  exit 1
}

$pid = 0
try { $pid = [int](Get-Content -LiteralPath $pidFile -Raw).Trim() } catch { $pid = 0 }
if ($pid -le 0) { Write-Error "Invalid pid in $pidFile"; exit 1 }

$script = Join-Path (Resolve-Path '.').Path 'scripts\health_check_preflight.ps1'
if (-not (Test-Path -LiteralPath $script)) { Write-Error "Missing $script"; exit 1 }

& powershell -NoProfile -ExecutionPolicy Bypass -File $script -ProcId $pid -OutDir $outBase -LogTail $LogTail
