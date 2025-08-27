# Adds CI workflow file, creates a branch, commits, pushes, and opens PR/Compare safely.
# Windows PowerShell 5.1 compatible; UTF-8 output
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'utf8'

# Repo root check
$repoRoot = (Get-Location).Path
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) { Write-Error 'Not a git repo.'; exit 2 }
$logPath = Join-Path $repoRoot 'scripts/run_push_pr_log.txt'
function Log($m){ $ts=(Get-Date).ToString('o'); Add-Content -LiteralPath $logPath -Value ("[$ts] " + $m) -Encoding utf8 }
Log 'START: CI bootstrap push'

# Preflight: ensure no staged artifacts/large files
$staged = ''
try { $staged = (git diff --cached --name-only) -join "`n" } catch { $staged = '' }
$blocked = @()
if ($staged) {
  foreach ($f in ($staged -split "`r`n|`n")) {
    if (-not $f) { continue }
    if ($f -match '^(artifacts/|bin/|obj/)') { $blocked += $f; continue }
    if (Test-Path -LiteralPath $f) {
      $len = (Get-Item -LiteralPath $f).Length
      if ($len -gt 5MB) { $blocked += $f }
    }
  }
}
if ($blocked.Count -gt 0) { Log ('Preflight blocked: ' + ($blocked -join ', ')); Write-Error 'Preflight failed: staged artifacts or large files.'; exit 3 }

# Stage only the workflow file
$workflowPath = '.github/workflows/ci.yml'
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $workflowPath))) { Write-Error 'Workflow file missing.'; exit 4 }
# Ensure only workflow is staged (safety): block if other files staged
$stagedList = @()
try { $stagedList = (git diff --cached --name-only) -split "`r`n|`n" | Where-Object { $_ -ne '' } } catch { $stagedList = @() }
$others = @($stagedList | Where-Object { $_ -ne $workflowPath })
if ($others.Count -gt 0) { Log ('Other staged files detected: ' + ($others -join ', ')); Write-Error 'Please unstage other files first. Only the workflow will be committed.'; exit 5 }

git add -- $workflowPath | Out-Null

# Create branch name
$branchName = 'ci/add-dotnet-ci-' + (Get-Date -Format 'yyyyMMdd_HHmmss')
$cur = (git branch --show-current).Trim()
if ($cur -ne $branchName) {
  git checkout -b $branchName 2>$null | Out-Null
  if ($LASTEXITCODE -ne 0) {
    # Fallback to checkout existing
    git checkout $branchName | Out-Null
  }
}

# Commit if something is staged
$stagedNow = (git diff --cached --name-only) -split "`r`n|`n" | Where-Object { $_ -ne '' }
if ($stagedNow.Length -gt 0) {
  git commit -m "ci: add GitHub Actions workflow for dotnet build and test" -- $workflowPath | Out-Null
  Log 'Committed workflow.'
} else {
  Log 'No staged changes to commit.'
}

# Push
$originUrl = (git remote get-url origin).Trim()
$pushed=$false
try {
  git push -u origin $branchName 2>&1 | Out-Null
  if ($LASTEXITCODE -eq 0) { $pushed = $true }
} catch { $pushed=$false }
if (-not $pushed) {
  Log 'Push failed or auth required.'
  Write-Host 'Push failed or required authentication.'
  Write-Host 'Authenticate git and re-run:'
  Write-Host '  - PAT over HTTPS: git remote set-url origin https://<token>@github.com/<owner>/<repo>.git'
  Write-Host '  - SSH: setup key and use git@github.com:<owner>/<repo>.git'
}

# PR or Compare URL
$prUrlOrCompare = ''
$ghOk = $false
try { $null = (gh auth status) 2>$null; if ($LASTEXITCODE -eq 0) { $ghOk = $true } } catch {}
if ($pushed -and $ghOk) {
  $body = Join-Path $repoRoot 'scripts/pr_body_final.txt'
  if (-not (Test-Path -LiteralPath $body)) { Set-Content -LiteralPath $body -Value 'Add CI workflow' -Encoding utf8 }
  $out = gh pr create --draft --title "ci: add .github/workflows/ci.yml - build and test" --body-file $body --base main --head $branchName 2>&1
  if ($LASTEXITCODE -eq 0) {
    $prUrl = ($out | Select-Object -Last 1)
    $prUrlOrCompare = $prUrl
    Log ('PR created: ' + $prUrl)
  }
}
if (-not $prUrlOrCompare) {
  if ($originUrl -match 'github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?$') {
    $owner=$Matches[1]; $repo=$Matches[2]
    $prUrlOrCompare = 'https://github.com/' + $owner + '/' + $repo + '/compare/main...' + $branchName
  try { Start-Process $prUrlOrCompare | Out-Null } catch {}
  }
}

# Report JSON
$report = [pscustomobject]@{
  branch = $branchName
  workflow_path = $workflowPath
  remote = $originUrl
  pushed = $pushed
  pr_url_or_compare_url = $prUrlOrCompare
  timestamp = (Get-Date).ToString('o')
}
$report | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $repoRoot 'scripts/pr_publish_report.json') -Encoding utf8
Log 'DONE: CI bootstrap push'

Write-Host ("Workflow: " + $workflowPath)
Write-Host ("Branch: " + $branchName)
Write-Host ("URL: " + $prUrlOrCompare)
Write-Host 'Next: check Actions tab, enable branch protection, adjust matrix if needed.'
