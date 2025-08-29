[CmdletBinding()]
param(
  [int]$SecondsPerHour = 3600,
  [int]$Hours = 1,
  [int]$DrainSeconds = 60,
  [int]$GraceSeconds = 10,
  [double]$FillProbability = 0.9,
  [int]$PRNumber = 0,
  [string]$OutBase = ''
)

$ErrorActionPreference = 'Stop'

# Ensure BOTG_ROOT
if (-not $env:BOTG_ROOT) {
  $envScript = Join-Path (Get-Location) 'scripts\set_repo_env.ps1'
  if (Test-Path -LiteralPath $envScript) { & powershell -NoProfile -ExecutionPolicy Bypass -File $envScript }
}

function Q([string]$s){ '"' + $s + '"' }
function NowUtcIso(){ (Get-Date).ToUniversalTime().ToString('o') }
function WriteUtf8([string]$p,[string]$t){ $enc=New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p,$t,$enc) }

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')

# A) Preflight
$preflight = Join-Path $repoRoot 'scripts/health_check_preflight.ps1'
if (Test-Path -LiteralPath $preflight) {
  $healthOut = ''
  try { $healthOut = (& powershell -NoProfile -ExecutionPolicy Bypass -File $preflight -LogTail 100) 2>&1 | Out-String } catch { $healthOut = $_.Exception.Message }
  if ($healthOut -match 'CRITICAL') {
    $cannot = [pscustomobject]@{
      STATUS = 'CANNOT_RUN'
      notes = 'Preflight reported CRITICAL. Run health_check_preflight and address issues.'
      commands = @(
        'powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\health_check_preflight.ps1 -LogTail 200',
        'powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\start_realtime_1h_ascii.ps1 -Seconds 3600 -SecondsPerHour 3600 -FillProb 0.9 -Drain 60 -Grace 10 -PRNumber <PR#>'
      )
      health_output = $healthOut
    }
    $cannot | ConvertTo-Json -Depth 6 | Write-Output
    exit 2
  }
}

# B) Start run
$launcher = Join-Path $repoRoot 'scripts/start_realtime_1h_ascii.ps1'
if (-not (Test-Path -LiteralPath $launcher)) {
  $cannot = [pscustomobject]@{ STATUS='CANNOT_RUN'; notes='Missing launcher script.'; commands=@('powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\start_realtime_1h_ascii.ps1 ...') }
  $cannot | ConvertTo-Json -Depth 4 | Write-Output
  exit 3
}

$seconds = [int]($Hours * $SecondsPerHour)
$args = @(
  '-NoProfile','-ExecutionPolicy','Bypass','-File',(Q $launcher),
  '-Seconds',[string]$seconds,
  '-SecondsPerHour',[string]$SecondsPerHour,
  '-FillProb',[string]$FillProbability,
  '-Drain',[string]$DrainSeconds,
  '-Grace',[string]$GraceSeconds
)
if ($PRNumber -gt 0) { $args += @('-PRNumber',[string]$PRNumber) }

# Capture immediate ack JSON from launcher
$ackJson = ''
try { $ackJson = (Start-Process -FilePath 'powershell.exe' -ArgumentList $args -NoNewWindow -PassThru -RedirectStandardOutput ([System.IO.Path]::GetTempFileName()) -WorkingDirectory $repoRoot).WaitForExit() } catch {}

# If redirect to temp file, load it
$ackTemp = Get-ChildItem $env:TEMP -Filter '*.tmp' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$ackObj = $null
if ($ackTemp -and (Test-Path -LiteralPath $ackTemp.FullName)) {
  try { $ackObj = Get-Content -LiteralPath $ackTemp.FullName -Raw | ConvertFrom-Json } catch { $ackObj = $null }
}
if (-not $ackObj) {
  # Fallback: run and capture output directly
  try { $ackText = (& powershell -NoProfile -ExecutionPolicy Bypass -File $launcher -Seconds $seconds -SecondsPerHour $SecondsPerHour -FillProb $FillProbability -Drain $DrainSeconds -Grace $GraceSeconds -PRNumber $PRNumber) 2>&1 | Out-String; $ackObj = $ackText | ConvertFrom-Json } catch {}
}
if (-not $ackObj) {
  $cannot = [pscustomobject]@{
    STATUS='CANNOT_RUN'
    notes='Could not start launcher or parse ack.'
    commands=@(
      ('powershell -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\start_realtime_1h_ascii.ps1 -Seconds ' + $seconds + ' -SecondsPerHour ' + $SecondsPerHour + ' -FillProb ' + $FillProbability + ' -Drain ' + $DrainSeconds + ' -Grace ' + $GraceSeconds + (if($PRNumber -gt 0){' -PRNumber ' + $PRNumber}else{''}))
    )
  }
  $cannot | ConvertTo-Json -Depth 6 | Write-Output
  exit 4
}

$childPid = [int]$ackObj.pid
$outbase = [string]$ackObj.outdir
$startLocal = Get-Date
$startIsoUtc = & NowUtcIso

# Rule 1: print exactly one line ACK
Write-Output ("RUN_STARTED: " + ($startIsoUtc -replace '\\.\d{1,7}([+-]|Z)$','Z'))

# Optional short ack JSON
([pscustomobject]@{ action='started'; pid=$childPid; outbase=$outbase; start_time=$startIsoUtc } | ConvertTo-Json -Depth 4) | Write-Output

# C) Monitor & wait
$deadline = (Get-Date).AddHours(2)
$finalPath = Join-Path $outbase 'final_report.json'
while ((Get-Date) -lt $deadline) {
  if (Test-Path -LiteralPath $finalPath) { break }
  $alive = $false
  try { $p = Get-Process -Id $childPid -ErrorAction SilentlyContinue; if ($p -and -not $p.HasExited) { $alive = $true } } catch {}
  if (-not $alive) {
    # Give a short grace for finalize to write final_report
    Start-Sleep -Seconds 10
    if (Test-Path -LiteralPath $finalPath) { break }
    # D) Fallback finalize if available
    $finalize = Join-Path $repoRoot 'scripts/finalize_realtime_1h_report.ps1'
    if (Test-Path -LiteralPath $finalize) {
      try { & powershell -NoProfile -ExecutionPolicy Bypass -File $finalize -OutBase $outbase -PrNumber $PRNumber | Out-Null } catch {}
    }
    break
  }
  Start-Sleep -Seconds 30
}

$endIsoUtc = & NowUtcIso
$finalObj = $null
if (Test-Path -LiteralPath $finalPath) {
  try { $finalObj = Get-Content -LiteralPath $finalPath -Raw | ConvertFrom-Json } catch {}
}

# Gather extras
$zipPath = $null; $recon = $null
try {
  if ($finalObj -and $finalObj.runs -and $finalObj.runs.Count -gt 0) {
    $run = $finalObj.runs[0]
    if ($run.zip) { $zipPath = $run.zip }
    if ($run.reconstruct_report) { $recon = $run.reconstruct_report }
  } else {
    $z = Get-ChildItem -LiteralPath $outbase -Recurse -Filter '*.zip' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($z) { $zipPath = $z.FullName }
  }
} catch {}

$status = 'SUCCESS'
if (-not $finalObj) { $status = 'SMOKE_FAILED' }
if ((Get-Date) -gt $deadline -and -not $finalObj) { $status = 'TIMED_OUT' }

$result = [ordered]@{
  STATUS = $status
  start_time_iso = $startIsoUtc
  end_time_iso = $endIsoUtc
  duration_seconds = [int]([timespan]((Get-Date ($endIsoUtc)) - (Get-Date ($startIsoUtc)))).TotalSeconds
  pid = $childPid
  outbase = $outbase
  artifact_zip = $zipPath
  reconstruct_report = $recon
  final_report = $finalObj
  pr_updated = $false
  notes = ''
}

# G) Post PR comment if possible
if ($PRNumber -gt 0) {
  try {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
      $tmp = Join-Path $outbase 'supervisor_pr_comment.md'
      $body = @()
      $body += "Supervisor final JSON:"; $body += '```json'; $body += (([pscustomobject]$result) | ConvertTo-Json -Depth 12); $body += '```'
      WriteUtf8 $tmp ($body -join "`n")
      & gh pr comment $PRNumber -F $tmp 2>$null | Out-Null
      $result['pr_updated'] = $true
    } else {
      $result['notes'] = 'gh CLI not available; include supervisor_pr_comment.md in OutBase to comment manually.'
    }
  } catch {
    $result['notes'] = 'gh comment failed: ' + $_.Exception.Message
  }
}

([pscustomobject]$result) | ConvertTo-Json -Depth 12 | Write-Output
