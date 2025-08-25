Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Paths
$RepoRoot = (Get-Location).Path
$ScriptsDir = Join-Path $RepoRoot 'scripts'
$newItem = New-Item -ItemType Directory -Path $ScriptsDir -Force -ErrorAction SilentlyContinue
$LogPath = Join-Path $ScriptsDir 'run_push_pr_log.txt'
$ReportPath = Join-Path $ScriptsDir 'pr_publish_report.json'
$TriggerPath = Join-Path $ScriptsDir '.pr_trigger_for_pr.txt'

function Log($msg) {
  $ts = (Get-Date).ToString('o')
  $line = "[$ts] $msg"
  Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
  Write-Host $line
}

function Run-Git([string]$gitArgs) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = 'git'
  $psi.Arguments = $gitArgs
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $p = New-Object System.Diagnostics.Process
  $p.StartInfo = $psi
  $null = $p.Start()
  $stdout = $p.StandardOutput.ReadToEnd()
  $stderr = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  if ($stdout) { Log ("git $gitArgs | stdout: " + ($stdout.Trim())) }
  if ($stderr) { Log ("git $gitArgs | stderr: " + ($stderr.Trim())) }
  return @{ Code = $p.ExitCode; Out = $stdout; Err = $stderr }
}

try {
  # 1. Preflight
  if (-not (Test-Path (Join-Path $RepoRoot '.git'))) {
    Log 'ERROR: Not a Git repository (.git missing).'
    Write-Error 'Not a Git repository'; exit 1
  }
  $branchRes = Run-Git 'branch --show-current'
  $branch = ($branchRes.Out).Trim()
  if ([string]::IsNullOrWhiteSpace($branch)) { Log 'ERROR: No current branch (detached HEAD).'; Write-Error 'Detached HEAD'; exit 1 }
  Log ("Branch: {0}" -f $branch)
  $originRes = Run-Git 'remote get-url origin'
  if ($originRes.Code -ne 0 -or -not $originRes.Out) { Log "ERROR: Remote 'origin' missing."; Write-Error "Missing origin"; exit 2 }
  $origin = ($originRes.Out).Trim()
  Log ("Origin: {0}" -f $origin)

  # Safety: HEAD disallowed scan (avoid pushing binaries/artifacts)
  $headList = Run-Git 'show --name-only --pretty="format:" HEAD'
  $headFiles = @(); if ($headList.Out) { $headFiles = ($headList.Out -split "`r`n|`n") | Where-Object { $_ -ne '' } }
  $blocked = @('^artifacts/','^BotG/bin/','^BotG/obj/','^Harness/bin/','^Harness/obj/','^Tests/bin/','^Tests/obj/')
  $blockedExt = '\\.(zip|7z|dll|exe|pdb)$'
  $sizeLimit = 5MB
  $off = @()
  foreach ($p in $headFiles) {
    $n = ($p -replace '\\','/'); if (-not $n) { continue }
    foreach ($rx in $blocked) { if ($n -match $rx) { $off += $p; break } }
    if ($n -match $blockedExt) { $off += $p }
    $full = Join-Path $RepoRoot $p
    if (Test-Path -LiteralPath $full) { try { if ((Get-Item -LiteralPath $full).Length -gt $sizeLimit) { $off += $p } } catch {} }
  }
  if ($off.Count -gt 0) {
    Log 'ERROR: HEAD contains disallowed/large files:'
    ($off | Select-Object -Unique) | ForEach-Object { Log (" - {0}" -f $_) }
    Log 'Remediation: git reset --soft HEAD~1; git add scripts/*.py scripts/*.ps1; git commit -m "feat(...)"'
    Write-Error 'Disallowed files in HEAD'; exit 4
  }

  # 2. Sanity: check default
  $null = Run-Git 'fetch origin --prune --tags'
  $remoteDefault = ''
  try {
    $show = Run-Git 'remote show origin'
    $line = ($show.Out -split "`r`n|`n") | Where-Object { $_ -match 'HEAD branch:' } | Select-Object -First 1
    if ($line) { $remoteDefault = ($line -split ':')[-1].Trim() }
  } catch {}
  if ([string]::IsNullOrWhiteSpace($remoteDefault)) { $remoteDefault = 'main' }
  Log ("Remote default: {0}" -f $remoteDefault)

  $needNoop = $false
  if ($remoteDefault -eq $branch) {
    $needNoop = $true
    Log 'Reason: remote default equals current branch.'
  } else {
    $cmp = Run-Git ("log --oneline origin/{0}..origin/{1}" -f $remoteDefault,$branch)
    if (-not $cmp.Out) { $needNoop = $true; Log 'Reason: no commits ahead of base on remote (empty diff).' }
  }

  # 3. Create noop trigger (idempotent)
  $noopCreated = $false
  $noopCommit = $null
  if ($needNoop) {
    $existsWorking = Test-Path -LiteralPath $TriggerPath
    $existsInHead = $false
    if (-not $existsWorking) {
      $grep = Run-Git 'ls-tree -r --name-only HEAD'
      if ($grep.Out) { $existsInHead = ($grep.Out -split "`r`n|`n") -contains ('scripts/.pr_trigger_for_pr.txt') }
    }
    if (-not $existsWorking -and -not $existsInHead) {
      $content = @(
        "Temporary PR trigger file created by automation on $((Get-Date).ToString('o')).",
        "Purpose: create a small safe commit so GitHub Compare shows a diff and allows PR creation.",
        "Delete this file after PR created/merged."
      ) -join "`r`n"
      [System.IO.File]::WriteAllText($TriggerPath, $content, [System.Text.Encoding]::UTF8)
      Log ("Created trigger file: {0}" -f $TriggerPath)
      $add = Run-Git 'add scripts/.pr_trigger_for_pr.txt'
      $cmt = Run-Git 'commit -m "chore: add PR trigger file (temporary) to enable PR creation"'
      if ($cmt.Code -eq 0) {
        $sha = Run-Git 'rev-parse HEAD'
        $noopCommit = ($sha.Out).Trim()
        $noopCreated = $true
        Log ("Noop commit: {0}" -f $noopCommit)
      } else { Log 'Commit failed or nothing to commit.' }
    } else {
      Log 'Trigger file already exists (working tree or HEAD); skipping creation.'
    }
  } else {
    Log 'No need for noop commit (diff already present).'
  }

  # 4. Push branch to origin (non-interactive)
  $env:GIT_TERMINAL_PROMPT = '0'
  $push = Run-Git ("push -u origin {0}" -f $branch)
  $pushed = $true
  if ($push.Code -ne 0) {
    $pushed = $false
    Log 'ERROR: git push failed. Likely authentication.'
    $guide = @(
      'Authentication guidance:',
      '- HTTPS + PAT:',
      '  1) Create a Personal Access Token (repo scope): https://github.com/settings/tokens',
      '  2) git push -u origin <branch> ; when prompted, use your GitHub username and PAT as password.',
      '- SSH:',
      '  1) ssh-keygen -t ed25519 -C "you@example.com"',
      '  2) Start ssh-agent and add key: eval $(ssh-agent) ; ssh-add ~/.ssh/id_ed25519',
      '  3) Add ~/.ssh/id_ed25519.pub to GitHub -> Settings -> SSH and GPG keys',
      '  4) git remote set-url origin git@github.com:<owner>/<repo>.git',
      '  5) git push -u origin <branch>'
    ) -join "`r`n"
    Write-Error ("git push failed. Exit 3`r`n{0}" -f $guide)
    exit 3
  }

  # 5. Compute Compare URL
  $candidates = @('main','master','develop')
  $base = ''
  foreach ($c in $candidates) {
    $chk = Run-Git ("ls-remote --exit-code --heads origin {0}" -f $c)
    if ($chk.Code -eq 0) { $base = $c; break }
  }
  if (-not $base) { $base = $remoteDefault }

  $owner = ''; $repo = ''
  if ($origin -match 'github\.com[/:]([^/]+)/([^/.]+)') { $owner = $Matches[1]; $repo = $Matches[2] }
  $compareUrl = if ($owner -and $repo) { "https://github.com/$owner/$repo/compare/$base...$branch" } else { '' }
  if ($compareUrl) { try { Start-Process $compareUrl } catch {} ; Log ("Compare URL: {0}" -f $compareUrl) } else { Log 'Could not parse origin for Compare URL.' }

  # 6. Report JSON
  $report = [ordered]@{
    branch = $branch
    remote = $origin
    pushed = $pushed
    pr_compare_url = $compareUrl
    noop_file_created = $noopCreated
    noop_commit = $noopCommit
    timestamp = (Get-Date).ToString('o')
  }
  [System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 6), [System.Text.Encoding]::UTF8)
  Log ("Report written: {0}" -f $ReportPath)

  # 7. Final instructions
  Log 'Open the Compare URL above, click Create pull request.'
  Log 'Paste PR body from scripts/pr_body_final.txt and include scripts/pr_review_checklist.md.'
  Log 'After PR is created, you may remove scripts/.pr_trigger_for_pr.txt in a follow-up commit.'

  # Print pretty JSON to stdout
  Write-Host (Get-Content -LiteralPath $ReportPath -Raw)
  exit 0
}
catch {
  Log ("Unexpected error: {0}" -f $_.Exception.Message)
  Write-Error $_.Exception.Message
  exit 4
}
