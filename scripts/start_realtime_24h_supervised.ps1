
  [int]$Hours = 24,
  [int]$SecondsPerHour = 3600,
  [double]$FillProbability = 1.0,
  [switch]$UseSimulation,
  [int]$PRNumber = 0,
  [string]$OutBase = '',
  [switch]$WaitForFinish
)

$ErrorActionPreference = 'Stop'
function IsoNow(){ (Get-Date).ToUniversalTime().ToString("o") }
function Q([string]$s){ '"' + $s + '"' }
function WriteUtf8([string]$p,[string]$t){ $enc=New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p,$t,$enc) }

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')
$runSmoke = Join-Path $repoRoot 'scripts/run_smoke.ps1'
if (-not (Test-Path -LiteralPath $runSmoke)) { Write-Error 'Missing scripts/run_smoke.ps1'; exit 1 }

if (-not $OutBase -or $OutBase.Trim().Length -eq 0) {
  $runsRoot = if ($env:BOTG_RUNS_ROOT) { $env:BOTG_RUNS_ROOT } else { 'D:\botg\runs' }
  $OutBase = Join-Path $runsRoot ("realtime_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss'))
}
New-Item -ItemType Directory -Path $OutBase -Force | Out-Null

$asciiBase = Join-Path $env:TEMP ("botg_artifacts_orchestrator_realtime_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $asciiBase -Force | Out-Null

$sec = [int]($Hours * $SecondsPerHour)
$log = Join-Path $OutBase ("run_smoke_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss') + '.log')
$err = Join-Path $OutBase ("run_smoke_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss') + '.err.log')

# Optional config override to disable simulation
$cfgPath = ''
if (-not $UseSimulation.IsPresent) {
  $cfgPath = Join-Path $asciiBase 'config.override.json'
  $cfg = @{ simulation = @{ enabled = $false; fill_probability = $FillProbability }; execution = @{ fee_per_trade = 0; fee_percent = 0; spread_pips = 0 }; seconds_per_hour = $SecondsPerHour; drain_seconds = 120 }
  $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 6), $enc)
}

$psRunArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Q $runSmoke), '-Seconds',[string]$sec, '-ArtifactPath',(Q $asciiBase), '-FillProbability',[string]$FillProbability, '-DrainSeconds','120','-SecondsPerHour',[string]$SecondsPerHour, '-GracefulShutdownWaitSeconds','15')
if ($UseSimulation.IsPresent) { $psRunArgs += '-UseSimulation' }
if ($cfgPath) { $psRunArgs += @('-ConfigPath',(Q $cfgPath)) }

$p = Start-Process -FilePath 'powershell.exe' -ArgumentList $psRunArgs -RedirectStandardOutput $log -RedirectStandardError $err -PassThru -WorkingDirectory $repoRoot -WindowStyle Hidden

$ack = @{ STATUS='STARTED'; started= $(IsoNow); pid=$p.Id; outbase=$OutBase; ascii_temp=$asciiBase; hours=$Hours; seconds_per_hour=$SecondsPerHour; use_simulation=$UseSimulation.IsPresent; fill_probability=$FillProbability }
WriteUtf8 (Join-Path $OutBase 'final_report.json') ($ack | ConvertTo-Json -Depth 6)
Write-Output ("RUN24_STARTED: " + $(IsoNow) + " - pid: " + $p.Id + " - outbase: " + $OutBase)
($ack | ConvertTo-Json -Depth 6) | Write-Output

if ($WaitForFinish.IsPresent) {
  $deadline = (Get-Date).AddHours([double]$Hours + 1)

  # Copy back and synthesize a final report similar to 1h daemon
  $cand = Get-ChildItem -LiteralPath $asciiBase -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($cand) {
    $runDir = $cand.FullName
    $leaf = Split-Path -Leaf $runDir
    $dest = Join-Path $OutBase $leaf
    try { $robo = Get-Command robocopy -ErrorAction SilentlyContinue; if ($robo) { $null = & robocopy $runDir $dest *.* /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null } else { Copy-Item -Path (Join-Path $runDir '*') -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue } } catch {}
    # Gather metrics
    $rrp = Join-Path $dest 'reconstruct_report.json'
    $rr=null; $orphAfter=null
  if (Test-Path -LiteralPath $rrp) { try { $rr = Get-Content -LiteralPath $rrp -Raw | ConvertFrom-Json; if ($null -ne $rr.orphan_after) { $orphAfter = [int]$rr.orphan_after } elseif ($null -ne $rr.estimated_orphan_fills_after_reconstruct) { $orphAfter = [int]$rr.estimated_orphan_fills_after_reconstruct } } catch {} }
    $status = if ($orphAfter -eq 0) { 'SUCCESS' } else { 'FAILURE' }
    $final = @{ STATUS=$status; runs=@(@{ name='realtime_24h'; outdir=$dest; status=(if($status -eq 'SUCCESS'){'PASSED'}else{'FAILED'}); reconstruct_report=$rr }) }
    $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText((Join-Path $OutBase 'final_report.json'), ($final | ConvertTo-Json -Depth 8), $enc)
    ($final | ConvertTo-Json -Depth 8) | Write-Output
  }
}
