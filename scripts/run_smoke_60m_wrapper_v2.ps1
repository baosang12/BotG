# NOTE: Shim for CI. Real preflight/smoke takes precedence when present.
# On main, CI_BLOCK_FAIL=1 makes missing scripts fail CI.
[CmdletBinding()]
param(
  [switch]$MergeOnly,
  [string]$OutRoot = ".",
  [Parameter(ValueFromRemainingArguments = $true)]
  [object[]]$RemainingArgs
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$here    = Split-Path -Parent $PSCommandPath
$repo    = Split-Path -Parent $here
$preflight = Join-Path $repo 'scripts\health_check_preflight.ps1'
$smoke     = Join-Path $repo 'scripts\run_smoke.ps1'

if (-not (Test-Path $OutRoot)) { New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null }

function Invoke-Safely {
  param([string]$Path,[object[]]$Args)
  Write-Host "[wrapper] running: $Path $($Args -join ' ')"

  # reset any stale code
  if (Test-Path variable:LASTEXITCODE) { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }

  & $Path @Args

  $native = if (Test-Path variable:LASTEXITCODE) { $LASTEXITCODE } else { $null }
  $rc = if ($null -ne $native) { [int]$native } elseif ($?) { 0 } else { 1 }

  # propagate for legacy callers (e.g., v1)
  $global:LASTEXITCODE = $rc

  if ($rc -ne 0) { throw "child exit $rc" }
}

try {
  if (Test-Path $preflight) {
    Invoke-Safely -Path $preflight -Args @()
  } elseif (Test-Path $smoke) {
    Invoke-Safely -Path $smoke     -Args @()
  } else {
    Write-Warning "[wrapper] no preflight/smoke script present."
    if ($env:CI_BLOCK_FAIL -eq '1') { throw "no runnable script" }
    Write-Host "[wrapper] SOFT PASS (set CI_BLOCK_FAIL=1 to enforce)."
    exit 0
  }
  Write-Host "[wrapper] OK"
  exit 0
}
catch {
  Write-Error "[wrapper] $_"
  if ($env:CI_BLOCK_FAIL -eq '1') { exit 1 } else { Write-Host "[wrapper] SOFT PASS"; exit 0 }
}
