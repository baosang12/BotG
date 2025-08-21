Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure UTF-8 for console and file outputs to avoid mojibake in PR bodies/logs
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
$PrBodyPath = Join-Path $ScriptsDir 'pr_body_final.txt'
$PrChecklistPath = Join-Path $ScriptsDir 'pr_review_checklist.md'
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

function Write-Report {
  param([hashtable]$data)
  [System.IO.File]::WriteAllText($ReportPath, ($data | ConvertTo-Json -Depth 8), [System.Text.Encoding]::UTF8)
  Log ("Report written: $ReportPath")
}

try {
  # 1) Preflight
  if (-not (Test-Path (Join-Path $RepoRoot '.git'))) {
    Log 'ERROR: Not a Git repository (.git missing).'
    throw 'Not a Git repository'
  }

  $branchRes = Run-Git 'branch --show-current'
  $branch = ($branchRes.Out).Trim()
  if ([string]::IsNullOrWhiteSpace($branch)) { Log 'ERROR: No current branch (detached HEAD).'; throw 'Detached HEAD' }
  Log ("Preflight: Branch = $branch")

  $originRes = Run-Git 'remote get-url origin'
  if ($originRes.Code -ne 0 -or -not $originRes.Out) { Log "ERROR: Remote 'origin' missing."; throw 'Missing origin' }
  $origin = ($originRes.Out).Trim()
  Log ("Preflight: Origin = $origin")

  if (Test-Path -LiteralPath $TriggerPath) { Log "Note: NOOP trigger file exists: $TriggerPath" }

  # 2) Ensure branch pushed
  $existsRemote = Run-Git ("ls-remote --exit-code --heads origin {0}" -f $branch)
  if ($existsRemote.Code -ne 0) {
    Log "Remote branch not found. Attempting to push '-u origin $branch' (non-interactive)."
    $env:GIT_TERMINAL_PROMPT = '0'
    $pushRes = Run-Git ("push -u origin {0}" -f $branch)
    if ($pushRes.Code -ne 0) {
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
  } else {
    Log 'Remote branch exists.'
  }

  # 3) Detect preferred base branch
  $base = ''
  foreach ($cand in @('main','master','develop')) {
    $chk = Run-Git ("ls-remote --exit-code --heads origin {0}" -f $cand)
    if ($chk.Code -eq 0) { $base = $cand; break }
  }
  if (-not $base) {
    $remoteDefault = ''
    $show = Run-Git 'remote show origin'
    $line = ($show.Out -split "`r`n|`n") | Where-Object { $_ -match 'HEAD branch:' } | Select-Object -First 1
    if ($line) { $remoteDefault = ($line -split ':')[-1].Trim() }
    if ([string]::IsNullOrWhiteSpace($remoteDefault)) { $remoteDefault = 'main' }
    $base = $remoteDefault
  }
  # If base resolved to current feature branch (origin/HEAD points to feature), prefer main/master/develop
  if ($base -eq $branch) {
    Log 'Remote HEAD points to current branch; preferring a stable base (main/master/develop).'
    foreach ($cand in @('main','master','develop')) {
      if ($cand -eq $branch) { continue }
      $chk2 = Run-Git ("ls-remote --exit-code --heads origin {0}" -f $cand)
      if ($chk2.Code -eq 0) { $base = $cand; break }
    }
    if ($base -eq $branch) { $base = 'main' }
  }
  # Ensure chosen base actually exists; if not, pick the first other remote branch
  $existsBase = (Run-Git ("ls-remote --exit-code --heads origin {0}" -f $base)).Code -eq 0
  if (-not $existsBase) {
    Log ("Chosen base '$base' not found on origin; enumerating remote heads.")
    $heads = Run-Git 'ls-remote --heads origin'
    $base = ''
    if ($heads.Out) {
      $lines = $heads.Out -split "`r`n|`n" | Where-Object { $_ -ne '' }
      foreach ($ln in $lines) {
        if ($ln -match 'refs/heads/(.+)$') {
          $name = $Matches[1]
          if ($name -ne $branch) { $base = $name; break }
        }
      }
    }
    if (-not $base) { Log 'No alternative base found; using current branch (will show no diff).'; $base = $branch }
  }
  Log ("Base branch selected: $base")

  # 4) Try automatic PR creation via gh
  $prUrl = ''
  $createdViaGh = $false
  $gh = $null
  try { $gh = Get-Command gh -ErrorAction Stop } catch {}
  if ($gh) {
    $auth = $null
    try { $auth = & gh auth status 2>$null; $authOk = $LASTEXITCODE -eq 0 } catch { $authOk = $false }
    if ($authOk) {
      Log 'gh detected and authenticated; creating Draft PR.'
      $title = 'feat(telemetry): add streaming compute + artifact reconcile wrapper (safe chunking + backup)'
      $cmd = @(
        'pr','create','--draft',
        '--title', $title,
        '--body-file', $PrBodyPath,
        '--base', $base,
        '--head', $branch
      )
      $psi = New-Object System.Diagnostics.ProcessStartInfo
      $psi.FileName = 'gh'
      $psi.RedirectStandardOutput = $true
      $psi.RedirectStandardError = $true
      $psi.UseShellExecute = $false
      $psi.CreateNoWindow = $true
      $psi.ArgumentList.AddRange($cmd)
      $proc = New-Object System.Diagnostics.Process
      $proc.StartInfo = $psi
      $null = $proc.Start()
      $ghOut = $proc.StandardOutput.ReadToEnd()
      $ghErr = $proc.StandardError.ReadToEnd()
      $proc.WaitForExit()
      if ($ghOut) { Log ("gh pr create | stdout: " + ($ghOut.Trim())) }
      if ($ghErr) { Log ("gh pr create | stderr: " + ($ghErr.Trim())) }
      if ($proc.ExitCode -eq 0) {
        $match = [regex]::Match($ghOut + "`n" + $ghErr, 'https?://github\.com/[^\s]+/pull/\d+')
        if ($match.Success) { $prUrl = $match.Value }
        $createdViaGh = $true
        Log ("Draft PR created: $prUrl")
      } else {
        Log 'gh pr create failed; will fallback to Compare URL.'
      }
    } else {
      Log 'gh found but not authenticated; skipping auto PR create.'
    }
  } else {
    Log 'gh not found; skipping auto PR create.'
  }

  if ($createdViaGh -and $prUrl) {
    $report = [ordered]@{
      branch = $branch
      remote = $origin
      pushed = $true
      pr_url = $prUrl
      pr_created_via_gh = $true
      timestamp = (Get-Date).ToString('o')
    }
    Write-Report -data $report
    Write-Host "Draft PR created: $prUrl"
    Write-Host "Report: $ReportPath"
    Write-Host "Log: $LogPath"
    exit 0
  }

  # 5) Fallback: open Compare URL with base preselected
  $owner = ''; $repo = ''
  if ($origin -match 'github\.com[/:]([^/]+)/([^/.]+)') { $owner = $Matches[1]; $repo = $Matches[2] }
  $compareUrl = if ($owner -and $repo) { "https://github.com/$owner/$repo/compare/$base...$branch" } else { '' }
  if ($compareUrl) { try { Start-Process $compareUrl } catch {} ; Log ("Compare URL: $compareUrl") } else { Log 'Could not parse origin for Compare URL.' }

  $report2 = [ordered]@{
    branch = $branch
    remote = $origin
    pushed = $true
    pr_compare_url = $compareUrl
    pr_created_via_gh = $false
    timestamp = (Get-Date).ToString('o')
  }
  Write-Report -data $report2

  Write-Host $compareUrl
  Write-Host "Copilot đã mở Compare URL. Hãy đổi base sang '$base' trong dropdown, click \"Create pull request\", dán nội dung từ scripts/pr_body_final.txt vào Description, và đính kèm scripts/pr_review_checklist.md nếu muốn."
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
