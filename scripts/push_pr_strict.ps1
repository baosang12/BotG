Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Config
$RepoRoot = (Get-Location).Path
$ScriptsDir = Join-Path $RepoRoot 'scripts'
if (-not (Test-Path $ScriptsDir)) { New-Item -ItemType Directory -Path $ScriptsDir -Force | Out-Null }
$LogPath = Join-Path $ScriptsDir 'run_push_pr_log.txt'
$ReportPath = Join-Path $ScriptsDir 'pr_publish_report.json'
$Title = 'feat(telemetry): add streaming compute + artifact reconcile wrapper (safe chunking + backup)'
$AllowedFiles = @(
  'scripts/compute_fill_breakdown_stream.py',
  'scripts/make_closes_from_reconstructed.py',
  'scripts/reconcile.py',
  'scripts/run_reconcile_and_compute.ps1'
)

function Log($msg) {
  $ts = (Get-Date).ToString('o')
  $line = "[$ts] $msg"
  Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
  Write-Host $line
}

function Write-Json($obj) { $obj | ConvertTo-Json -Depth 8 }

# Prepare log file without transcript to avoid file locks
try { New-Item -ItemType File -Path $LogPath -Force | Out-Null } catch {}

try {
  # 1) Repo root check
  if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { Write-Error 'Not a Git repository (.git not found).'; exit 1 }

  # 2) Show branch and HEAD files
  $branch = (git branch --show-current).Trim()
  if ([string]::IsNullOrWhiteSpace($branch)) { Write-Error 'No current branch (detached HEAD).'; exit 1 }
  Log ("Branch: {0}" -f $branch)
  $headFilesRaw = git show --name-only --pretty="format:" HEAD 2>&1
  $headFiles = @($headFilesRaw -split "`r`n|`n" | Where-Object { $_ -ne '' })
  Log 'HEAD files:'
  foreach ($f in $headFiles) { Log ("  {0}" -f $f) }

  # 3) Validate HEAD content strictly
  $blockedPrefixes = @('artifacts/','BotG/bin/','BotG/obj/','Harness/bin/','Harness/obj/','Tests/bin/','Tests/obj/')
  $blockedExt = @('.zip','.7z','.dll','.exe','.pdb')
  $sizeLimit = 5MB
  $offenders = @()
  foreach ($p in $headFiles) {
    $n = $p -replace '\\','/'
    if ($n -eq '') { continue }
    # Strict allowlist
    if (-not ($AllowedFiles -contains $n)) { $offenders += $p; continue }
    # disallowed prefixes
    foreach ($bp in $blockedPrefixes) { if ($n.StartsWith($bp)) { $offenders += $p; break } }
    # disallowed extensions
    foreach ($ext in $blockedExt) { if ($n.ToLower().EndsWith($ext)) { $offenders += $p; break } }
    # size check (if file exists in workspace)
    $full = Join-Path $RepoRoot $p
    if (Test-Path -LiteralPath $full) {
      try { if ((Get-Item -LiteralPath $full).Length -gt $sizeLimit) { $offenders += $p } } catch {}
    }
  }
  if ($offenders.Count -gt 0) {
    $msg = "HEAD commit contains disallowed paths (must only include the 4 allowed scripts). Offenders:`n  " + (($offenders | Select-Object -Unique) -join "`n  ")
    Log $msg
    Log "Remediation:"
    Log "  git reset --soft HEAD~1"
    Log "  git restore --staged ."
    Log "  git checkout -- ."
    Log "  git add scripts/compute_fill_breakdown_stream.py scripts/make_closes_from_reconstructed.py scripts/reconcile.py scripts/run_reconcile_and_compute.ps1"
    Log "  git commit -m \"$Title\""
    Write-Error $msg
    exit 4
  }

  # 4) Backup tag
  $tag = "pre_push_backup_" + (Get-Date -Format 'yyyyMMdd_HHmmss')
  git tag -f $tag | Out-Null
  Log ("Created/updated backup tag: {0}" -f $tag)

  # 5) Commit local changes but only allowed files
  $status = git status --porcelain | Out-String
  if (-not [string]::IsNullOrWhiteSpace($status)) {
    Log 'Uncommitted changes detected; staging only allowed scripts.'
    git add -- $AllowedFiles 2>&1 | Tee-Object -FilePath $LogPath -Append | Out-Null
    $staged = git diff --cached --name-only | Out-String
    if (-not [string]::IsNullOrWhiteSpace($staged)) {
      git commit -m $Title 2>&1 | Tee-Object -FilePath $LogPath -Append | Out-Null
      Log 'Committed allowed script changes.'
    } else {
      Log 'No allowed script changes staged; skipping commit.'
    }
  } else {
    Log 'No local changes.'
  }

  # 6) Ensure origin exists
  $origin = ''
  try { $origin = (git remote get-url origin).Trim() } catch { $origin = '' }
  if (-not $origin) {
    $m = "Remote 'origin' is missing. Please set it: git remote add origin <URL>"
    Log $m
    Write-Error $m
    exit 2
  }

  # 7) Push if branch absent on origin
  $pushed = $false
  $ls = git ls-remote --heads origin $branch 2>&1 | Out-String
  $existsRemote = -not [string]::IsNullOrWhiteSpace($ls)
  if (-not $existsRemote) {
    Log 'Branch not found on origin; pushing with upstream.'
    $pushOut = git push -u origin $branch 2>&1
    $exit = $LASTEXITCODE
    Add-Content -LiteralPath $LogPath -Value $pushOut -Encoding utf8
    if ($exit -ne 0) {
      Log 'Push failed. Diagnose auth:'
      Log 'If HTTPS remote:'
      Log '  1) Create a Personal Access Token (PAT) with repo scope.'
      Log '  2) Run: git push -u origin <branch> and when prompted use your GitHub username and PAT as password.'
      Log 'If SSH remote:'
      Log '  1) ssh-keygen -t ed25519 -C "you@example.com"'
      Log '  2) Start the SSH agent and add key: eval $(ssh-agent) ; ssh-add ~/.ssh/id_ed25519'
      Log '  3) Add the public key (~/.ssh/id_ed25519.pub) to GitHub -> Settings -> SSH and GPG keys.'
      Log '  4) Set remote to SSH: git remote set-url origin git@github.com:<owner>/<repo>.git'
      Log '  5) Retry: git push -u origin <branch>'
      Write-Error 'git push failed due to authentication or network.'
      exit 3
    }
    $pushed = $true
  } else {
    Log 'Branch already exists on origin.'
    $pushed = $true
  }

  # 8/9/10) Create Draft PR (gh) or open compare URL
  $base = ''
  try { $base = (git remote show origin | Select-String 'HEAD branch:' | ForEach-Object { $_.ToString().Split(':')[-1].Trim() }) } catch {}
  if ([string]::IsNullOrWhiteSpace($base) -or $base -eq $branch) { $base = 'main' }

  $prUrl = ''
  $compareUrl = ''
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  $ghLoggedIn = $false
  if ($gh) { try { gh auth status | Out-Null; if ($LASTEXITCODE -eq 0) { $ghLoggedIn = $true } } catch {} }

  if ($gh -and $ghLoggedIn) {
    try {
      $out = gh pr create --draft --title $Title --body-file (Join-Path $ScriptsDir 'pr_body_final.txt') --base $base --head $branch 2>&1
      Add-Content -LiteralPath $LogPath -Value $out -Encoding utf8
      $m = ($out | Select-String -Pattern 'https?://\S+' -AllMatches).Matches.Value | Select-Object -Last 1
      if ($m) { $prUrl = $m }
    } catch {
      Log 'gh pr create failed; will fall back to compare URL.'
    }
  }

  if (-not $prUrl) {
    if ($origin -match 'github\.com[/:]([^/]+)/([^/.]+)') {
      $owner = $Matches[1]; $repo = $Matches[2]
      $compareUrl = "https://github.com/$owner/$repo/compare/$base...$branch"
      Log ("Compare URL: {0}" -f $compareUrl)
      try { Start-Process $compareUrl } catch {}
    } else {
      Log 'Cannot parse owner/repo from origin; create PR manually.'
    }
  }

  # 11) Summary JSON
  $summaryFiles = @()
  foreach ($p in $headFiles) { if ($p) { $summaryFiles += $p } }
  $prOrCompare = if ($prUrl) { $prUrl } else { $compareUrl }
  $report = [ordered]@{
    branch = $branch
    remote = $origin
    pushed = $pushed
    pr_url_or_compare_url = $prOrCompare
    tag_backup = $tag
    summary_files_committed = $summaryFiles
    timestamp = (Get-Date).ToString('o')
  }
  [System.IO.File]::WriteAllText($ReportPath, (Write-Json $report), [System.Text.Encoding]::UTF8)

  # 12) Final status
  Log 'Done.'
  Log ("PR or Compare: {0}" -f $prOrCompare)
  # no transcript to stop

  # Print outputs
  Write-Host (Get-Content -LiteralPath $ReportPath -Raw)
  Write-Host ("PR body file: {0}" -f (Join-Path $ScriptsDir 'pr_body_final.txt'))
  exit 0
}
catch {
  # no transcript to stop
  $msg = "Unexpected error: " + $_.Exception.Message
  Log $msg
  Write-Error $msg
  exit 5
}
