param(
  [int]$TimeoutNotifySec = 900
)

$ErrorActionPreference = 'Stop'

$env:GH_REPO = 'baosang12/BotG'
& gh repo set-default baosang12/BotG | Out-Null

$results = @()

function Invoke-Scenario {
  param([string]$src)
  Write-Host "-- Running $src on main --"
  $code = 0
  try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ops_notify_e2e.ps1 -Source $src -NotifyRef main -TimeoutNotifySec $TimeoutNotifySec
  } catch {
    $code = 1
  }
  # Read proof if exists
  $proofPath = Join-Path $PWD 'path_issues/notify_e2e_proof.json'
  $proof = $null
  if (Test-Path $proofPath) {
    try { $proof = Get-Content $proofPath -Raw | ConvertFrom-Json } catch {}
  }
  $status = if ($proof -and $proof.status) { $proof.status } elseif ($code -eq 0) { 'PASS' } else { 'FAIL' }
  $results += [pscustomobject]@{ scenario = $src; status = $status }
}

Invoke-Scenario -src 'notify_test'
Invoke-Scenario -src 'notify_test_fail'

$allPass = $true
$results | ForEach-Object { if ($_.status -ne 'PASS') { $allPass = $false } }

if ($allPass) { Write-Host "NOTIFY_E2E_MAIN=PASS"; exit 0 } else { Write-Host "NOTIFY_E2E_MAIN=FAIL"; exit 1 }
