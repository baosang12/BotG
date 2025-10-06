param(
  [string]$NotifyRef = 'main',
  [string]$Hours = '0.1',
  [string]$Mode = 'paper',
  [int]$TimeoutNotifySec = 600
)

$ErrorActionPreference = 'Stop'

function Invoke-Scenario {
  param([string]$source)
  Write-Host "=== Running scenario: $source ===" -ForegroundColor Cyan
  try {
    pwsh -File (Join-Path $PSScriptRoot 'ops_notify_e2e.ps1') -Hours $Hours -Mode $Mode -NotifyRef $NotifyRef -Source $source -TimeoutNotifySec $TimeoutNotifySec
  } catch {
    throw $_
  }
}

function Get-Results {
  $jsonPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'path_issues/notify_e2e_result.json'
  if (-not (Test-Path $jsonPath)) { return @() }
  try { return (Get-Content $jsonPath -Raw | ConvertFrom-Json) } catch { return @() }
}

function Get-LogLines {
  $logPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'path_issues/notify_e2e_log.txt'
  if (-not (Test-Path $logPath)) { return 0 }
  return (Get-Content $logPath -Raw | Select-String -Pattern "`n" -AllMatches).Matches.Count + 1
}

try {
  # Run cancel scenario then fail-fast
  Invoke-Scenario -source 'notify_test'
  Invoke-Scenario -source 'notify_test_fail'

  $results = Get-Results
  $lines = Get-LogLines

  $cancel = $results | Where-Object { $_.scenario -eq 'notify_test' } | Select-Object -Last 1
  $fail   = $results | Where-Object { $_.scenario -eq 'notify_test_fail' } | Select-Object -Last 1

  $okCancel = $false
  $okFail = $false

  if ($cancel -and $cancel.notify_runs.Count -ge 1) {
    $tgOk = ($cancel.telegram_status -like 'HTTP/200')
  $issueOk = ($null -ne $cancel.issue_fallback_number)
    $okCancel = ($tgOk -or $issueOk)
  }
  if ($fail -and $fail.notify_runs.Count -ge 1) {
    $tgOk = ($fail.telegram_status -like 'HTTP/200')
  $issueOk = ($null -ne $fail.issue_fallback_number)
    $okFail = ($tgOk -or $issueOk)
  }

  # Print concise summary
  Write-Host "--- Summary ---" -ForegroundColor Yellow
  if ($cancel) {
    $nr = $cancel.notify_runs | ForEach-Object { "#${($_.id)} ($($_.trigger)) $($_.url)" } -join ", "
    $issueStr = if ($null -ne $cancel.issue_fallback_number) { "#$($cancel.issue_fallback_number) $($cancel.issue_fallback_url)" } else { 'none' }
    Write-Host ("Cancel: Gate #{0} {1} → {2}; Notify: {3}; Telegram={4} Issue={5}" -f $cancel.gate_run_id, $cancel.gate_url, $cancel.gate_conclusion, $nr, $cancel.telegram_status, $issueStr)
  }
  if ($fail) {
    $nr = $fail.notify_runs | ForEach-Object { "#${($_.id)} ($($_.trigger)) $($_.url)" } -join ", "
    $issueStr = if ($null -ne $fail.issue_fallback_number) { "#$($fail.issue_fallback_number) $($fail.issue_fallback_url)" } else { 'none' }
    Write-Host ("FailFast: Gate #{0} {1} → {2}; Notify: {3}; Telegram={4} Issue={5}" -f $fail.gate_run_id, $fail.gate_url, $fail.gate_conclusion, $nr, $fail.telegram_status, $issueStr)
  }

  if ($okCancel -and $okFail -and $lines -ge 80) {
    Write-Host "ALERTS_READY=YES (main)" -ForegroundColor Green
  } else {
    Write-Host "ALERTS_READY=NO (missing notify evidence or logs)" -ForegroundColor Red
  }
} catch {
  Write-Host "CANNOT_RUN: $($_.Exception.Message)" -ForegroundColor Red
  Write-Host "# E2E — kịch bản cancel (notify_test) trên main"
  Write-Host "gh repo set-default baosang12/BotG"
  Write-Host "pwsh -File scripts/ops_notify_e2e.ps1 -Hours 0.1 -Mode paper -NotifyRef main -Source notify_test -TimeoutNotifySec 600"
  Write-Host "# E2E — kịch bản fail-fast (notify_test_fail) trên main"
  Write-Host "pwsh -File scripts/ops_notify_e2e.ps1 -Hours 0.1 -Mode paper -NotifyRef main -Source notify_test_fail -TimeoutNotifySec 600"
  exit 1
}
