[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function TimestampNow { (Get-Date).ToString('yyyyMMdd_HHmmss') }

$repo = (Resolve-Path '.').Path
$ts = TimestampNow
$suffix = 'paper_run_realtime_1h_' + $ts
$out = Join-Path $repo (Join-Path 'artifacts_ascii' $suffix)
New-Item -ItemType Directory -Force -Path $out | Out-Null

$daemon = Join-Path $repo 'scripts\run_realtime_1h_daemon.ps1'
if (-not (Test-Path -LiteralPath $daemon)) { throw "Missing $daemon" }

$proc = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$daemon,'-OutBase',$out) -PassThru -WindowStyle Hidden
$ack = [pscustomobject]@{
  action = 'started'
  pid = $proc.Id
  outdir = $out
  start_time = (Get-Date).ToString('o')
}
$ack | ConvertTo-Json -Depth 4 | Write-Output
