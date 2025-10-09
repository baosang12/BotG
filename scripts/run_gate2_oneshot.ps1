[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'

function Fail($msg) { Write-Error $msg; exit 1 }

# [1] Preflight
try {
  if (-not $env:GH_REPO -or $env:GH_REPO -notmatch '^baosang12/BotG$') {
    $env:GH_REPO = 'baosang12/BotG'
  }
  # CI green check (best-effort): last run of CI - build & test
  $ci = gh run list --workflow "CI - build & test" --limit 1 --json status,conclusion,createdAt 2>$null | ConvertFrom-Json
  if ($ci -and $ci.Length -gt 0) {
    $last = $ci[0]
    if ($last.conclusion -ne 'success') { Fail "CI last run not green: $($last.conclusion)" }
  }
  # Disk space on C: and D:
  $minGB = 10
  $drives = @('C:', 'D:') | ForEach-Object { Get-PSDrive -Name $_ -ErrorAction SilentlyContinue } | Where-Object { $_ }
  foreach ($d in $drives) {
    $freeGB = [math]::Round($d.Free/1GB, 2)
    if ($freeGB -lt $minGB) { Fail "Low disk on $($d.Name): ${freeGB}GB (<${minGB}GB)" }
  }
  $sentinelDir = 'D:\botg\runs\sentinel'
  New-Item -ItemType Directory -Force -Path $sentinelDir | Out-Null
  $stopFile  = Join-Path $sentinelDir 'RUN_STOP'
  $pauseFile = Join-Path $sentinelDir 'RUN_PAUSE'
  if (Test-Path -LiteralPath $stopFile) { Fail "RUN_STOP present, aborting" }
  if (Test-Path -LiteralPath $pauseFile) { Remove-Item -LiteralPath $pauseFile -Force }
} catch { Fail "Preflight error: $_" }

# [2] Force paper mode via config.runtime.json (if exists)
try {
  $cfgPath = Join-Path $PWD 'config.runtime.json'
  $cfg = @{}
  if (Test-Path -LiteralPath $cfgPath) {
    try { $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json -ErrorAction Stop } catch {}
  }
  if (-not $cfg.mode) { $cfg | Add-Member -NotePropertyName mode -NotePropertyValue 'paper' -Force } else { $cfg.mode = 'paper' }
  if (-not $cfg.simulation) { $cfg | Add-Member -NotePropertyName simulation -NotePropertyValue (@{ enabled = $false }) -Force } else { $cfg.simulation.enabled = $false }
  $cfg.SecondsPerHour = 3600
  ($cfg | ConvertTo-Json -Depth 8) | Out-File -FilePath $cfgPath -Encoding utf8
  $env:BOTG_SIMULATION = 'false'
  $env:BOTG_MODE = 'paper'
} catch { Fail "Force paper-mode error: $_" }

# [3] Dispatch gate24h
$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
try {
  gh workflow run gate24h.yml -F hours=24 -F source="gate2-paper" | Out-Null
  Start-Sleep -Seconds 5
  $runs = gh run list --workflow gate24h.yml --limit 1 --json databaseId,headBranch,createdAt,status 2>$null | ConvertFrom-Json
  if (-not $runs -or $runs.Length -eq 0) { Fail "No workflow run found" }
  $rid = $runs[0].databaseId
  Write-Host "GATE2_ONESHOT_STARTED $ts run_id=$rid"
} catch { Fail "Dispatch error: $_" }

# [4] Watch and download artifacts
try {
  gh run watch $rid --exit-status | Out-Null
  $outDir = "D:\botg\runs\gate2_$ts"
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  gh run download $rid --dir $outDir | Out-Null
  Write-Host "GATE2_ONESHOT_DONE artifacts=$outDir"
} catch { Fail "Watch/download error: $_" }
