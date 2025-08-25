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

$repo = (Resolve-Path '.').Path
$ts = TimestampNow

# Use ASCII-safe TEMP base to avoid issues with Unicode paths
$asciiBase = Join-Path $env:TEMP ("botg_artifacts\realtime_1h_" + $ts)
New-Item -ItemType Directory -Force -Path $asciiBase | Out-Null

$daemon = Join-Path $repo 'scripts\run_realtime_1h_daemon.ps1'
if (-not (Test-Path -LiteralPath $daemon)) { throw "Missing $daemon" }

$psArgs = @(
  '-NoProfile','-ExecutionPolicy','Bypass','-File',$daemon,
  '-OutBase',$asciiBase,
  '-Seconds',[string]$Seconds,
  '-SecondsPerHour',[string]$SecondsPerHour,
  '-FillProb',[string]$FillProb,
  '-Drain',[string]$Drain,
  '-Grace',[string]$Grace,
  '-PRNumber',[string]$PRNumber
)

$proc = Start-Process -FilePath 'powershell' -ArgumentList $psArgs -PassThru -WindowStyle Hidden

$ack = [pscustomobject]@{
  action = 'started'
  pid = $proc.Id
  outdir = $asciiBase
  start_time = (Get-Date).ToString('o')
}
$ack | ConvertTo-Json -Depth 4 | Write-Output
