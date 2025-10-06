param(
  [string]$Hours = '0.1',
  [string]$Mode = 'paper',
  [switch]$FailFast,
  [int]$WaitTimeoutSec = 600,
  [string]$NotifyRef = 'main',
  [string]$Source = 'notify_test',
  [int]$TimeoutNotifySec = 600
)

# E2E notify test driver
# - Dispatch Gate24h with a test source to keep it in_progress or fail-fast
# - Cancel/fail it to get a non-success conclusion
# - Manually dispatch notify_on_failure with the run metadata
# - Wait for notify run to complete, then show logs and any issue fallback

$ErrorActionPreference = 'Stop'

function Assert-GH() {
  if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) is required. Install from https://cli.github.com and run gh auth login.'
  }
}

function Wait-Until($ScriptBlock, [int]$TimeoutSec = 300, [int]$DelaySec = 5) {
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $val = & $ScriptBlock
    if ($val) { return $val }
    Start-Sleep -Seconds $DelaySec
  }
  return $null
}

Assert-GH

if ($FailFast) { $Source = 'notify_test_fail' }

Write-Host "Dispatching Gate24h (hours=$Hours mode=$Mode source=$Source)"
& gh workflow run ".github/workflows/gate24h_main.yml" -f "hours=$Hours" -f "mode=$Mode" -f "source=$Source" | Write-Host
$startAt = Get-Date

# Find the latest Gate24h run created after $startAt
function Get-LatestGateRun {
  $runsJson = & gh run list --workflow ".github/workflows/gate24h_main.yml" --json databaseId,status,conclusion,createdAt,url,headBranch --limit 20
  $runs = $null
  try { $runs = $runsJson | ConvertFrom-Json } catch { $runs = @() }
  $runs | Where-Object { [datetime]$_.createdAt -ge $startAt } | Sort-Object createdAt -Descending | Select-Object -First 1
}

$gateRun = Wait-Until { Get-LatestGateRun } -TimeoutSec 120 -DelaySec 3
if (-not $gateRun) { throw 'No Gate24h run detected' }
Write-Host "Gate run detected: id=$($gateRun.databaseId) status=$($gateRun.status) url=$($gateRun.url)"

# If not fail-fast, wait until in_progress then cancel
if ($Source -eq 'notify_test') {
  Write-Host 'Waiting for gate run to enter in_progress...'
  $inprog = Wait-Until {
    $r = Get-LatestGateRun
    if ($r -and $r.status -eq 'in_progress') { $r } else { $null }
  } -TimeoutSec 180 -DelaySec 3
  if (-not $inprog) { Write-Warning 'Run did not reach in_progress in time; continuing anyway' }

  Write-Host "Cancelling gate run $($gateRun.databaseId)"
  & gh run cancel $gateRun.databaseId | Write-Host
}

# Wait for a terminal conclusion
$final = Wait-Until {
  $r = Get-LatestGateRun
  if ($r -and ($r.conclusion -ne $null -and $r.conclusion -ne '')) { $r } else { $null }
} -TimeoutSec $WaitTimeoutSec -DelaySec 5
if (-not $final) { throw 'Gate run did not reach a terminal conclusion in time' }
Write-Host "Gate final: id=$($final.databaseId) conc=$($final.conclusion) url=$($final.url)"

<#
 Dispatch notify workflow with explicit inputs and deterministically lock to
 the workflow_dispatch run on the requested ref.
#>
Write-Host "Dispatching notify_on_failure via workflow_dispatch on ref '$NotifyRef'..."
$notifyDispatchAt = Get-Date
$dispatchOk = $true
try {
  & gh workflow run ".github/workflows/notify_on_failure.yml" --ref "$NotifyRef" -f "run_id=$($final.databaseId)" -f "conclusion=$($final.conclusion)" -f "url=$($final.url)" | Write-Host
} catch {
  Write-Warning "Dispatch failed (likely missing workflow_dispatch on target ref). Falling back to workflow_run listener..."
  $dispatchOk = $false
}

function Get-NotifyDispatchRun {
  # Poll latest notify runs and pick newest with event==workflow_dispatch and headBranch==$NotifyRef
  $runsJson = & gh run list --workflow ".github/workflows/notify_on_failure.yml" --json databaseId,status,conclusion,createdAt,url,headBranch,event --limit 10
  $runs = $null
  try { $runs = $runsJson | ConvertFrom-Json } catch { $runs = @() }
  $runs |
    Where-Object { $_.event -eq 'workflow_dispatch' -and $_.headBranch -eq $NotifyRef -and [datetime]$_.createdAt -ge $notifyDispatchAt } |
    Sort-Object createdAt -Descending |
    Select-Object -First 1
}

function Get-NotifyRunById([int]$id) {
  $runsJson = & gh run list --workflow ".github/workflows/notify_on_failure.yml" --json databaseId,status,conclusion,createdAt,url,headBranch,event --limit 50
  $runs = $null
  try { $runs = $runsJson | ConvertFrom-Json } catch { $runs = @() }
  $runs | Where-Object { $_.databaseId -eq $id } | Select-Object -First 1
}

$notifyRun = $null
if ($dispatchOk) {
  $notifyRun = Wait-Until { Get-NotifyDispatchRun } -TimeoutSec 180 -DelaySec 3
} else {
  # Fallback: pick newest notify run after gate start time (workflow_run path), but this is non-deterministic
  function Get-AnyRecentNotifyRun {
    $runsJson = & gh run list --workflow ".github/workflows/notify_on_failure.yml" --json databaseId,status,conclusion,createdAt,url,headBranch,event --limit 20
    $runs = $null; try { $runs = $runsJson | ConvertFrom-Json } catch { $runs = @() }
    $runs | Where-Object { [datetime]$_.createdAt -ge $startAt } | Sort-Object createdAt -Descending | Select-Object -First 1
  }
  $notifyRun = Wait-Until { Get-AnyRecentNotifyRun } -TimeoutSec 180 -DelaySec 3
}

if (-not $notifyRun) { throw 'Notify run not found' }

Write-Host "Waiting for notify run to complete: id=$($notifyRun.databaseId)"
$notifyFinal = Wait-Until {
  Get-NotifyRunById -id $notifyRun.databaseId | Where-Object { $_.status -eq 'completed' }
} -TimeoutSec $TimeoutNotifySec -DelaySec 3
if (-not $notifyFinal) { throw 'Notify run did not complete in time' }
Write-Host "Notify final: id=$($notifyFinal.databaseId) conc=$($notifyFinal.conclusion) url=$($notifyFinal.url)"

$notifyTrigger = if ($dispatchOk) { 'workflow_dispatch' } else { 'workflow_run' }

# Also attempt to find a workflow_run-triggered notify that is not the dispatch one (best-effort)
$wrCandidate = $null
if ($dispatchOk) {
  $wrCandidate = Wait-Until {
    $runsJson = & gh run list --workflow ".github/workflows/notify_on_failure.yml" --json databaseId,status,conclusion,createdAt,url,headBranch --limit 50
    $runs = $null; try { $runs = $runsJson | ConvertFrom-Json } catch { $runs = @() }
    $runs | Where-Object { [datetime]$_.createdAt -ge $startAt } | Sort-Object createdAt -Descending | Select-Object -First 1
  } -TimeoutSec 60 -DelaySec 3
}

Write-Host "--- Notify logs (tail 120) ---"
try {
  $log = & gh run view $notifyFinal.databaseId --log
  if ($log) {
    $lines = $log -split "`n"
    $tail = if ($lines.Length -gt 120) { $lines[(-120)..(-1)] } else { $lines }
    $tail | ForEach-Object { Write-Host $_ }
    # Save full log to file for proofing
    $outDir = Join-Path $PWD 'path_issues'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $fullLogPath = Join-Path $outDir 'notify_e2e_log.txt'
    $lines | Out-File -FilePath $fullLogPath -Encoding utf8
    # Extract fields required for acceptance
    $httpLine = ($lines | Where-Object { $_ -match 'TELEGRAM_HTTP=' } | Select-Object -Last 1)
    if ($httpLine) {
      $m = [regex]::Match($httpLine, 'TELEGRAM_HTTP=(\d+)')
      if ($m.Success) { $script:tgHttp = $m.Groups[1].Value }
    }
    $statusLine = ($lines | Where-Object { $_ -match '^telegram_status=' } | Select-Object -Last 1)
    if ($statusLine) {
      $m2 = [regex]::Match($statusLine, '^telegram_status=(?<stat>\w+)')
      if ($m2.Success) { $script:tgStatus = $m2.Groups['stat'].Value }
    }
    $script:logCount = $lines.Length
  }
} catch {
  Write-Warning $_
}

Write-Host "--- fallback issues (search by run id or source) ---"
try {
  $issueRecent = $null
  $fiveMinAgo = (Get-Date).AddMinutes(-5)
  # Search by run id across all issues
  $issuesByIdJson = & gh issue list --state all --limit 20 --search "$($final.databaseId)" --json number,title,url,createdAt
  $issuesById = $null; try { $issuesById = $issuesByIdJson | ConvertFrom-Json } catch { $issuesById = @() }
  if ($issuesById) {
    $issueRecent = $issuesById | Where-Object { [datetime]$_.createdAt -ge $fiveMinAgo } | Select-Object -First 1
  }
  if (-not $issueRecent) {
    # Fallback search by source token
    $issuesBySrcJson = & gh issue list --state all --limit 20 --search "$Source" --json number,title,url,createdAt
    $issuesBySrc = $null; try { $issuesBySrc = $issuesBySrcJson | ConvertFrom-Json } catch { $issuesBySrc = @() }
    if ($issuesBySrc) {
      $issueRecent = $issuesBySrc | Where-Object { [datetime]$_.createdAt -ge $fiveMinAgo } | Select-Object -First 1
    }
  }
  if ($issueRecent) {
    "Issue found: #$($issueRecent.number) $($issueRecent.url)" | Write-Host
  } else {
    Write-Host 'No recent issue found.'
  }
} catch { Write-Warning $_ }

# Build proof object (single scenario) and acceptance
$startedAtIso = $startAt.ToUniversalTime().ToString('o')
$finishedAt = Get-Date
$finishedAtIso = $finishedAt.ToUniversalTime().ToString('o')
$httpCode = if ($script:tgHttp) { $script:tgHttp } else { '' }
$tgStatus = if ($script:tgStatus) { $script:tgStatus } else { '' }
$logLines = if ($script:logCount) { $script:logCount } else { 0 }
$fallbackUrl = if ($issueRecent) { $issueRecent.url } else { '' }

$proof = [ordered]@{
  scenario = $Source
  run_id = [int]$notifyFinal.databaseId
  ref = $NotifyRef
  http_code = $httpCode
  telegram_status = $tgStatus
  log_line_count = $logLines
  fallback_issue_url = $fallbackUrl
  started_at = $startedAtIso
  finished_at = $finishedAtIso
}

# Acceptance rules
$hasHttpAndStatus = ($httpCode -match '^\d+$' -and $tgStatus -match '^(ok|fail)$')
$hasIssue = ([string]::IsNullOrWhiteSpace($fallbackUrl) -eq $false)
$logsOk = ($logLines -ge 100)
$passed = ($logsOk -and ($hasHttpAndStatus -or $hasIssue))

# Persist proof JSON and zip artifacts
$outDir = Join-Path $PWD 'path_issues'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$proofPath = Join-Path $outDir 'notify_e2e_proof.json'
$proof | ConvertTo-Json -Depth 4 | Out-File -FilePath $proofPath -Encoding utf8

$alertsDir = Join-Path $outDir 'alerts'
New-Item -ItemType Directory -Force -Path $alertsDir | Out-Null
$ts = (Get-Date).ToString('yyyyMMdd_HHmmss')
$zipPath = Join-Path $alertsDir ("notify_e2e_main_${ts}.zip")
try {
  if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
  Compress-Archive -Path $proofPath, (Join-Path $outDir 'notify_e2e_log.txt') -DestinationPath $zipPath -Force
} catch { Write-Warning "Zip failed: $_" }

if (-not $passed) {
  Write-Error "ACCEPTANCE FAILED: logsOk=$logsOk hasHttpAndStatus=$hasHttpAndStatus hasIssue=$hasIssue"
  exit 1
}

Write-Host 'E2E notify test complete.'
