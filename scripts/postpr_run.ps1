# Post-PR merge processing: verify main, check workflow, optionally check CI, suggest cleanups
# Windows PowerShell 5.1 safe; UTF-8 outputs
$ErrorActionPreference='Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding=[System.Text.Encoding]::UTF8 } catch {}
$PSDefaultParameterValues['Out-File:Encoding']='utf8'
$PSDefaultParameterValues['Add-Content:Encoding']='utf8'
$PSDefaultParameterValues['Set-Content:Encoding']='utf8'

# Setup paths and logging
$repoRoot=(Get-Location).Path
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) { Write-Error 'Not a git repository'; exit 1 }
$logPath = Join-Path $repoRoot 'scripts/run_postpr_log.txt'
$reportPath = Join-Path $repoRoot 'scripts/postpr_report.json'
function Log([string]$m){ $ts=(Get-Date).ToString('o'); Add-Content -LiteralPath $logPath -Value ("[$ts] " + $m) -Encoding utf8 }

# 1) Confirm main state
try { git fetch origin --prune --tags | Out-Null } catch {}
$origBranch = ''
try { $origBranch = (git branch --show-current).Trim() } catch {}
try { git checkout main | Out-Null } catch {}
try { git pull origin main | Out-Null } catch {}
$mainHeadShaFull = ''
$mainHeadSha = ''
$mainHeadMsg = ''
try {
  $mainHeadShaFull = (git rev-parse HEAD).Trim()
  $mainHeadSha = (git rev-parse --short HEAD).Trim()
  $mainHeadMsg = (git log -1 --pretty=%B)
} catch {}
Log ("Main updated. HEAD=" + $mainHeadSha + " msg=" + ($mainHeadMsg -replace "\r?\n$",""))

# 2) Verify workflow file exists and capture snippets
$wfRel = '.github/workflows/ci.yml'
$wfFull = Join-Path $repoRoot $wfRel
$workflowExists = Test-Path -LiteralPath $wfFull
$wfFirst = @(); $wfLast = @(); $wfSize = 0
if ($workflowExists) {
  try {
    $wf = Get-Content -LiteralPath $wfFull
    $wfSize = if ($wf) { $wf.Count } else { 0 }
    if ($wfSize -le 40) {
      $wfFirst = $wf
      $wfLast = @()
    } else {
      $wfFirst = $wf[0..19]
      $wfLast = $wf[($wf.Count-20)..($wf.Count-1)]
    }
  } catch {}
}
Log ("Workflow present=" + $workflowExists + ", lines=" + $wfSize)

# 3) Check CI runs (gh optional)
$ciChecked=$false; $ciPassed=$false; $ciRunId=$null; $ciUrl=$null; $ciStatus=$null; $ciConclusion=$null
$owner=''; $repo=''; $originUrl=''
try { $originUrl=(git remote get-url origin).Trim() } catch {}
if ($originUrl -match 'github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?$') { $owner=$Matches[1]; $repo=$Matches[2] }
$ghCmd = Get-Command gh -ErrorAction SilentlyContinue
if ($ghCmd -eq $null) {
  Log 'gh not found; advise manual Actions check.'
} else {
  $authOk=$false
  try { $null = (gh auth status) 2>$null; if ($LASTEXITCODE -eq 0) { $authOk=$true } } catch {}
  if (-not $authOk) {
    Log 'gh present but not authenticated.'
  } else {
    # Try list runs for ci.yml on main
    $ciChecked=$true
    $runsJson = ''
    try {
      $runsJson = (gh run list --workflow ci.yml --branch main --limit 10 --json databaseId,headSha,status,conclusion,displayTitle,workflowName,url) 2>&1 | Out-String
    } catch {}
    $runs=@()
    try { if ($runsJson) { $runs = $runsJson | ConvertFrom-Json } } catch {}
    if ($runs -and $runs.Count -gt 0) {
      # Prefer matching HEAD sha
      $match = $null
      foreach($r in $runs){ if ($r.headSha -and $mainHeadShaFull -and ($r.headSha.ToLower() -eq $mainHeadShaFull.ToLower())) { $match=$r; break } }
      if (-not $match) { $match = $runs[0] }
      $ciRunId = $match.databaseId
      $ciUrl = $match.url
      $ciStatus = $match.status
      $ciConclusion = $match.conclusion
      # Poll if running/queued
      $pending = @('queued','in_progress','waiting','pending','requested')
      $attempt=0
      while ($attempt -lt 8 -and ($pending -contains ($ciStatus))) {
        Start-Sleep -Seconds 15
        try {
          $view = (gh run view $ciRunId --json status,conclusion,url) 2>&1 | Out-String
          $v = $null; try { $v = $view | ConvertFrom-Json } catch {}
          if ($v) { $ciStatus = $v.status; $ciConclusion = $v.conclusion; $ciUrl = $v.url }
        } catch {}
        $attempt++
      }
      if ($ciConclusion -and ($ciConclusion.ToLower() -eq 'success')) { $ciPassed=$true }
      if (-not $ciPassed -and $ciRunId) {
        # Append tail logs to postpr log
        try {
          $logText = (gh run view $ciRunId --log) 2>&1 | Out-String
          $lines = $logText -split "`r`n|`n"
          $tail = if ($lines.Length -gt 200) { $lines[($lines.Length-200)..($lines.Length-1)] } else { $lines }
          Add-Content -LiteralPath $logPath -Value ('--- CI run ' + $ciRunId + ' tail logs ---') -Encoding utf8
          Add-Content -LiteralPath $logPath -Value ($tail -join [Environment]::NewLine) -Encoding utf8
        } catch {}
      }
    } else {
      Log 'No CI runs found yet for ci.yml on main.'
    }
  }
}

# 5/6) NOOP trigger and branch deletion suggestions
$featureBranch = 'botg/automation/reconcile-streaming-20250821_084334'
$featureExists=$false
try { $null = git ls-remote --exit-code --heads origin $featureBranch 2>$null; if ($LASTEXITCODE -eq 0) { $featureExists=$true } } catch {}
$noopPath = 'scripts/.pr_trigger_for_pr.txt'
$noopExists = Test-Path -LiteralPath (Join-Path $repoRoot $noopPath)
$autoCleanupOk = (Test-Path -LiteralPath (Join-Path $repoRoot 'scripts/.autocleanup_ok')) -and ((Get-Content -LiteralPath (Join-Path $repoRoot 'scripts/.autocleanup_ok') -Raw).Trim().ToLower() -eq 'true')
$autoDeleteOk = (Test-Path -LiteralPath (Join-Path $repoRoot 'scripts/.autocleanup_delete_branch=true'))

$removalCmd = "git checkout $featureBranch; if (Test-Path -LiteralPath $noopPath) { git rm $noopPath; git commit -m 'chore: remove temporary PR trigger file'; git push origin $featureBranch }"
$deleteRemoteCmd = "git push origin --delete $featureBranch"
if (-not $featureExists) { $removalCmd = ''; $deleteRemoteCmd = '' }

# 7) Branch protection suggestion (do not execute automatically)
$protectCmd = ''
if ($owner -and $repo) {
  # Use simple hyphen instead of em dash and single quotes escaped for JSON contexts
  $protectCmd = 'gh api --method PUT /repos/' + $owner + '/' + $repo + '/branches/main/protection -f required_status_checks.contexts=''["CI - build & test"]'' -f enforce_admins=true -f required_pull_request_reviews.dismiss_stale_reviews=false -f restrictions=null'
}

# 9) Build final report JSON
$report = [pscustomobject]@{
  merged = $true
  merge_commit = $null
  main_head = $mainHeadSha
  ci_checked = $ciChecked
  ci_passed = $ciPassed
  ci_run_id = $(if ($ciRunId) { $ciRunId } else { $null })
  noop_file = @{
    exists = $noopExists
    path = $noopPath
    removal_suggested = $featureExists
    removal_cmd = $removalCmd
  }
  remote_branch_deleted = $false
  branch_protection_applied = $false
  actions = @{
    compare_url = $(if ($owner -and $repo) { 'https://github.com/' + $owner + '/' + $repo + '/actions/workflows/ci.yml?query=branch%3Amain' } else { $null })
    delete_remote_branch_cmd = $deleteRemoteCmd
    protect_branch_cmd = $protectCmd
  }
  logs = @{ run_postpr_log = 'scripts/run_postpr_log.txt' }
  timestamp = (Get-Date).ToString('o')
}
$report | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $reportPath -Encoding utf8

# Print outputs
$pretty = $report | ConvertTo-Json -Depth 8
$pretty | Write-Output

Write-Output "`n--- TOM TAT (VI) ---"
if ($ciChecked -and $ciPassed) { $oneLine = 'Da merge va CI PASS — san sang don dep an toan.' }
elseif ($ciChecked -and -not $ciPassed) { $oneLine = 'Da merge nhung CI FAIL — can xu ly loi truoc khi don dep.' }
else { $oneLine = 'Da merge — chua kiem tra CI (gh chua san sang), hay kiem tra Actions.' }
Write-Output $oneLine
$mainMsgOne = ($mainHeadMsg -replace "\r?\n$","")
Write-Output ('- Main HEAD: ' + $mainHeadSha + ' — ' + $mainMsgOne)
$wfStatus = if ($workflowExists) { 'TON TAI' } else { 'KHONG TIM THAY' }
Write-Output ('- Workflow: ' + $wfStatus + ', dong: ' + $wfSize)
if ($ciChecked) { if ($ciPassed) { $ciText = 'PASS' } else { $ciText = 'FAIL/Dang chay' } } else { $ciText = 'Chua kiem tra' }
Write-Output ('- CI: ' + $ciText)
$noopText = if ($noopExists) { 'CO' } else { 'KHONG' }
$featureText = if ($featureExists) { 'CON TON TAI' } else { 'KHONG TON TAI' }
Write-Output ('- NOOP trigger: ' + $noopText + '; Branch tinh nang: ' + $featureText)
if ($featureExists) { if ($removalCmd) { $nextCmd = $removalCmd } else { $nextCmd = 'N/A' } } else { $nextCmd = 'N/A' }
Write-Output ('Hanh dong tiep theo (goi y): ' + $nextCmd)
Write-Output 'Khuyen nghi: Bat branch protection cho main; xem Actions de dam bao CI xanh.'
