Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure UTF-8 for console and file outputs
try {
  [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
  [Console]::InputEncoding  = [System.Text.Encoding]::UTF8
} catch {}
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'

# Paths
$RepoRoot = (Get-Location).Path
$ScriptsDir = Join-Path $RepoRoot 'scripts'
New-Item -ItemType Directory -Path $ScriptsDir -Force -ErrorAction SilentlyContinue | Out-Null
$LogPath = Join-Path $ScriptsDir 'run_pr_push_log.txt'
$ReportPath = Join-Path $ScriptsDir 'pr_publish_report.json'
$TriggerPath = Join-Path $ScriptsDir '.pr_trigger_for_pr.txt'

function Log {
  param([string]$Message)
  $ts = (Get-Date).ToString('o')
  $line = "[$ts] $Message"
  Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
  Write-Host $line
}

function Run-Git {
  param([string]$gitArgs)
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
  # Preflight
  if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { Log 'ERROR: Not a Git repository (.git missing).'; throw 'Not a Git repository' }
  $branch = (Run-Git 'branch --show-current').Out.Trim()
  if ([string]::IsNullOrWhiteSpace($branch)) { Log 'ERROR: No current branch (detached HEAD).'; throw 'Detached HEAD' }
  $originRes = Run-Git 'remote get-url origin'
  if ($originRes.Code -ne 0 -or -not $originRes.Out) { Log "ERROR: Remote 'origin' missing."; throw 'Missing origin' }
  $origin = $originRes.Out.Trim()
  Log ("Append-noop: branch=$branch, origin=$origin")

  # Ensure trigger file exists then append a new line
  if (-not (Test-Path -LiteralPath $TriggerPath)) {
    [System.IO.File]::WriteAllText($TriggerPath, "Temporary PR trigger file created by automation.`r`n", [System.Text.Encoding]::UTF8)
    Log "Created trigger file: $TriggerPath"
  }
  $line = "touch at " + (Get-Date).ToString('o')
  Add-Content -LiteralPath $TriggerPath -Value $line -Encoding utf8
  Log "Appended to trigger file."

  # Commit and push only the trigger file
  $null = Run-Git 'add -- scripts/.pr_trigger_for_pr.txt'
  $cmt = Run-Git 'commit -m "chore: touch PR trigger to create diff vs main"'
  if ($cmt.Code -ne 0) { Log 'Nothing to commit (no changes).'; }

  $env:GIT_TERMINAL_PROMPT = '0'
  $push = Run-Git ("push -u origin {0}" -f $branch)
  if ($push.Code -ne 0) {
    $err = ($push.Err + ' ' + $push.Out)
    if ($err -match 'fetch first|non-fast-forward|failed to push some refs') {
      Log 'Push rejected (non-fast-forward). Preparing workspace and pulling with rebase (autoStash)...'
      # Move untracked helper files out of the way to avoid pull/rebase errors
      $tmpDir = Join-Path $ScriptsDir '.tmp_pr_backup'
      New-Item -ItemType Directory -Force -Path $tmpDir -ErrorAction SilentlyContinue | Out-Null
      $moved = @()
      foreach ($f in @('pr_body_final.txt','pr_review_checklist.md')) {
        $src = Join-Path $ScriptsDir $f
        if (Test-Path -LiteralPath $src) {
          $dst = Join-Path $tmpDir $f
          try { Move-Item -LiteralPath $src -Destination $dst -Force; $moved += $f; Log ("Temporarily moved: $src -> $dst") } catch {}
        }
      }

      $null = Run-Git 'fetch --prune origin'
      $pull = Run-Git ("-c rebase.autoStash=true pull --rebase origin {0}" -f $branch)
      if ($pull.Code -ne 0) {
        Log 'ERROR: Pull --rebase failed. Please resolve conflicts or run: git rebase --abort; git pull --rebase'
        throw 'Rebase failed'
      }
      # Optional: show divergence
      $div = Run-Git ("rev-list --left-right --count origin/{0}...HEAD" -f $branch)
      if ($div.Out) { Log ("Divergence (behind ahead): " + $div.Out.Trim()) }
      $push2 = Run-Git ("push -u origin {0}" -f $branch)
      if ($push2.Code -ne 0) {
        Log 'ERROR: git push failed after rebase.'
        throw 'git push failed after rebase'
      }
      # Restore moved files
      foreach ($f in $moved) {
        $dst = Join-Path $ScriptsDir $f
        $src = Join-Path $tmpDir $f
        try { Move-Item -LiteralPath $src -Destination $dst -Force; Log ("Restored: $src -> $dst") } catch {}
      }
    }
    elseif ($err -match 'Authentication failed|Permission denied|could not read Username|terminal prompts disabled') {
      Log 'ERROR: git push failed. Likely authentication required.'
      $guide = @(
        'Authentication guidance:',
        '- HTTPS + PAT:',
        '  1) Create a Personal Access Token (repo scope): https://github.com/settings/tokens',
        '  2) git push -u origin <branch> ; when prompted, use your GitHub username and PAT as password.',
        '- SSH:',
        '  1) ssh-keygen -t ed25519 -C "you@example.com"',
        '  2) Start ssh-agent and add key: start-ssh-agent ; ssh-add $HOME/.ssh/id_ed25519',
        '  3) Add $HOME/.ssh/id_ed25519.pub to GitHub -> Settings -> SSH and GPG keys',
        '  4) git remote set-url origin git@github.com:<owner>/<repo>.git',
        '  5) git push -u origin <branch>'
      ) -join "`r`n"
      Add-Content -LiteralPath $LogPath -Value $guide -Encoding utf8
      throw 'git push failed; auth needed'
    }
    else {
      Log 'ERROR: git push failed (unknown cause). Review logs above.'
      throw 'git push failed'
    }
  }

  # Compute base = main|master|develop|remote HEAD
  $base = ''
  foreach ($cand in @('main','master','develop')) { if ((Run-Git ("ls-remote --exit-code --heads origin {0}" -f $cand)).Code -eq 0) { $base = $cand; break } }
  if (-not $base) {
    $show = Run-Git 'remote show origin'
    $line2 = ($show.Out -split "`r`n|`n") | Where-Object { $_ -match 'HEAD branch:' } | Select-Object -First 1
    if ($line2) { $base = ($line2 -split ':')[-1].Trim() } else { $base = 'main' }
  }
  if ($base -eq $branch) { $base = 'main' }

  # Build Compare URL and open
  $owner = ''; $repo = ''
  if ($origin -match 'github\.com[/:]([^/]+)/([^/.]+)') { $owner = $Matches[1]; $repo = $Matches[2] }
  $compareUrl = if ($owner -and $repo) { "https://github.com/$owner/$repo/compare/$base...$branch" } else { '' }
  if ($compareUrl) { try { Start-Process $compareUrl } catch {} ; Log ("Compare URL: $compareUrl") }

  # Report
  $sha = (Run-Git 'rev-parse HEAD').Out.Trim()
  $report = [ordered]@{
    pushed = $true
    branch = $branch
    pr_compare_url = $compareUrl
    pr_created_via_gh = $false
    remote = $origin
    noop_commit = $sha
    timestamp = (Get-Date).ToString('o')
  }
  [System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 8), [System.Text.Encoding]::UTF8)
  Log ("Report written: $ReportPath")

  Write-Host $compareUrl
  Write-Host "Đã đẩy commit NOOP mới để tạo khác biệt với '$base'. Hãy nhấn Create pull request, dán nội dung từ scripts/pr_body_final.txt."
  Write-Host "Report: $ReportPath"
  Write-Host "Log: $LogPath"
  exit 0
}
catch {
  $msg = "Unexpected error: " + $_.Exception.Message
  Log $msg
  Write-Error $msg
  if (Test-Path -LiteralPath $LogPath) {
    Write-Host "--- Last 100 log lines ---"
    Get-Content -LiteralPath $LogPath -Tail 100 | ForEach-Object { Write-Host $_ }
  }
  exit 1
}
