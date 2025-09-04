[CmdletBinding()]
param(
  [int]$Seconds = 3600,
  [int]$SecondsPerHour = 3600,
  [double]$FillProb = 0.9,
  [int]$Drain = 60,
  [int]$Grace = 10,
  [int]$PRNumber = 13
)

$ErrorActionPreference = 'Stop'

function TimestampNow { (Get-Date).ToString('yyyyMMdd_HHmmss') }

# Resolve repo root from this script location to avoid dependency on current working directory
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $scriptRoot '..')).Path
$ts = TimestampNow

# Use ASCII-safe TEMP base to avoid issues with Unicode paths
$asciiBase = Join-Path $env:TEMP ("botg_artifacts\realtime_1h_" + $ts)
New-Item -ItemType Directory -Force -Path $asciiBase | Out-Null

$daemon = Join-Path $repo 'scripts\run_realtime_1h_daemon.ps1'
if (-not (Test-Path -LiteralPath $daemon)) { throw "Missing $daemon" }

function Q([string]$s){ return '"' + $s + '"' }

$psArgs = @(
  '-NoProfile','-ExecutionPolicy','Bypass','-File',(Q $daemon),
  '-OutBase',(Q $asciiBase),
  '-Seconds',[string]$Seconds,
  '-SecondsPerHour',[string]$SecondsPerHour,
  '-FillProb',[string]$FillProb,
  '-Drain',[string]$Drain,
  '-Grace',[string]$Grace,
  '-PRNumber',[string]$PRNumber
)

# Capture daemon stdout/stderr for diagnostics and set WorkingDirectory to repo root
$daemonStdout = Join-Path $asciiBase 'daemon_stdout.log'
$daemonStderr = Join-Path $asciiBase 'daemon_stderr.log'
$proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $psArgs -PassThru -WindowStyle Hidden -WorkingDirectory $repo -RedirectStandardOutput $daemonStdout -RedirectStandardError $daemonStderr

$ack = [pscustomobject]@{
  action = 'started'
  pid = $proc.Id
  outdir = $asciiBase
  start_time = (Get-Date).ToString('o')
}
$ack | ConvertTo-Json -Depth 4 | Write-Output
