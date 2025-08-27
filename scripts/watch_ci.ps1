# Watch latest CI run for current branch and write a JSON summary
param(
  [string]$Branch
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'

function Log($m) { $ts=(Get-Date).ToString('o'); Write-Host "[$ts] $m" }

# Resolve repo root and branch
try { $repoRoot = (git rev-parse --show-toplevel).Trim() } catch { $repoRoot = (Get-Location).Path }
if ($repoRoot -and (Test-Path -LiteralPath $repoRoot)) { Set-Location -LiteralPath $repoRoot }
if (-not $Branch -or [string]::IsNullOrWhiteSpace($Branch)) {
  try { $Branch = (git branch --show-current).Trim() } catch { $Branch = '' }
}
if (-not $Branch) { Write-Error 'Cannot resolve current branch.'; exit 2 }
Log ("Branch: $Branch")

# Find latest run for this commit (works for PR-triggered workflows)
$headSha = ''
try { $headSha = (git rev-parse HEAD).Trim() } catch {}
function Get-RunJson {
  param([string]$wf)
  try {
    # Prefer filtering by commit SHA; fallback to branch if needed
    $j = (gh run list --workflow $wf --commit $headSha --limit 1 --json databaseId,headSha,status,conclusion,url,displayTitle,workflowName -q '.[0]') 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -and $j) { return $j }
    $j = (gh run list --workflow $wf --branch $Branch --limit 1 --json databaseId,headSha,status,conclusion,url,displayTitle,workflowName -q '.[0]') 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -and $j) { return $j }
  } catch {}
  return $null
}

$runObj = $null
$attempt = 0
$workflowsToTry = @('ci.yml','CI — build & test','CI — build & test')
while ($attempt -lt 30 -and -not $runObj) {
  foreach ($wf in $workflowsToTry) {
    $j = Get-RunJson -wf $wf
    if ($j) { try { $runObj = $j | ConvertFrom-Json } catch { $runObj = $null } }
    if ($runObj) { break }
  }
  if (-not $runObj) { Start-Sleep -Seconds 6; $attempt++ }
}

if (-not $runObj) { Log 'No CI run found yet.'; Write-Host 'NO_RUN_FOUND'; exit 0 }
$runId = $runObj.databaseId
Log ("Run ID: $runId  Status: $($runObj.status)  Conclusion: $($runObj.conclusion)")

# Watch until completion
$watchExit = 1
try {
  gh run watch $runId --exit-status
  $watchExit = $LASTEXITCODE
} catch { $watchExit = 1 }

# Fetch final details
$viewJson = ''
try { $viewJson = (gh run view $runId --json status,conclusion,url,workflowName,jobs,headBranch,headSha) 2>&1 | Out-String } catch {}
$view = $null
try { if ($viewJson) { $view = $viewJson | ConvertFrom-Json } } catch {}

# Write report
$report = [pscustomobject]@{
  branch = $Branch
  run_id = $runId
  status = $(if ($view) { $view.status } else { $runObj.status })
  conclusion = $(if ($view) { $view.conclusion } else { $runObj.conclusion })
  url = $(if ($view) { $view.url } else { $runObj.url })
  workflow = $(if ($view) { $view.workflowName } else { $runObj.workflowName })
  head_sha = $(if ($view) { $view.headSha } else { $runObj.headSha })
  head_branch = $(if ($view) { $view.headBranch } else { $Branch })
  timestamp = (Get-Date).ToString('o')
}
$reportPath = Join-Path $repoRoot 'scripts/ci_watch_report.json'
$report | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $reportPath -Encoding utf8

# Print concise result
Write-Host ("RESULT: status=" + $report.status + ", conclusion=" + $report.conclusion + ", url=" + $report.url)
Write-Host ("REPORT_JSON=" + $reportPath)

if ($report.conclusion -and $report.conclusion.ToLower() -eq 'success') { exit 0 } else { exit 1 }
