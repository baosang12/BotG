param(
  [string]$Hours = '0.1',
  [string]$Mode = 'paper',
  [switch]$FailFast,
  [int]$WaitTimeoutSec = 600,
  [string]$NotifyRef = 'ci/notify-dispatch-e2e'
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

$Source = if ($FailFast) { 'notify_test_fail' } else { 'notify_test' }

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
if (-not $FailFast) {
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

# Dispatch notify workflow with explicit inputs
Write-Host "Dispatching notify_on_failure via workflow_dispatch on ref '$NotifyRef'..."
& gh workflow run ".github/workflows/notify_on_failure.yml" --ref "$NotifyRef" -f "run_id=$($final.databaseId)" -f "conclusion=$($final.conclusion)" -f "url=$($final.url)" | Write-Host
$notifyStart = Get-Date

function Get-LatestNotifyRun {
  $runsJson = & gh run list --workflow ".github/workflows/notify_on_failure.yml" --json databaseId,status,conclusion,createdAt,url --limit 20
  $runs = $null
  try { $runs = $runsJson | ConvertFrom-Json } catch { $runs = @() }
  $runs | Where-Object { [datetime]$_.createdAt -ge $notifyStart } | Sort-Object createdAt -Descending | Select-Object -First 1
}

$notifyRun = Wait-Until { Get-LatestNotifyRun } -TimeoutSec 120 -DelaySec 3
if (-not $notifyRun) { throw 'Notify run not found' }

Write-Host "Waiting for notify run to complete: id=$($notifyRun.databaseId)"
$notifyFinal = Wait-Until {
  $r = Get-LatestNotifyRun
  if ($r -and $r.status -eq 'completed') { $r } else { $null }
} -TimeoutSec 180 -DelaySec 3
if (-not $notifyFinal) { throw 'Notify run did not complete in time' }
Write-Host "Notify final: id=$($notifyFinal.databaseId) conc=$($notifyFinal.conclusion) url=$($notifyFinal.url)"

Write-Host "--- Notify logs (tail 120) ---"
try {
  $log = & gh run view $notifyFinal.databaseId --log
  if ($log) {
    $lines = $log -split "`n"
    $tail = if ($lines.Length -gt 120) { $lines[(-120)..(-1)] } else { $lines }
    $tail | ForEach-Object { Write-Host $_ }
  }
} catch {
  Write-Warning $_
}

Write-Host "--- gate-alert issues (latest) ---"
try {
  $issuesJson = & gh issue list --label gate-alert --state all --limit 5 --json number,title,url,createdAt,state
  $issues = $issuesJson | ConvertFrom-Json
  if ($issues) { $issues | Format-Table number,title,state,createdAt,url -AutoSize | Out-String | Write-Host }
} catch {
  Write-Warning $_
}

Write-Host 'E2E notify test complete.'
