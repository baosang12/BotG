Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure scripts directory exists for logs/outputs
if (-not (Test-Path "$PSScriptRoot")) { New-Item -ItemType Directory -Path "$PSScriptRoot" -Force | Out-Null }

$logPath = Join-Path $PSScriptRoot 'run_push_pr_log.txt'
try { Stop-Transcript | Out-Null } catch {}
Start-Transcript -Path $logPath -Append | Out-Null

function Write-Json($obj) { $obj | ConvertTo-Json -Depth 8 }

try {
  Write-Host '=== 1) Preflight: repo and remote ==='
  if (-not (Test-Path (Join-Path $PWD '.git'))) {
    Write-Error "Not a Git repository (.git missing)."; Stop-Transcript | Out-Null; exit 1
  }

  $branch = (git branch --show-current).Trim()
  if ([string]::IsNullOrWhiteSpace($branch)) { Write-Error 'No current branch (detached HEAD).'; Stop-Transcript | Out-Null; exit 1 }
  Write-Host ("Current branch: {0}" -f $branch)

  $origin = ''
  try { $origin = (git remote get-url origin).Trim() } catch { $origin = '' }
  if (-not $origin) { Write-Error "Remote 'origin' missing. Please add it: git remote add origin <URL>"; Stop-Transcript | Out-Null; exit 2 }
  Write-Host ("Remote origin: {0}" -f $origin)

  Write-Host '=== 2) Safety check: HEAD contents ==='
  $headFiles = @()
  try { $headFiles = (git show --name-only --pretty="" HEAD) -split "`r?`n" | Where-Object { $_ -ne '' } } catch {}
  $blocked = @('^artifacts/','^BotG/bin/','^BotG/obj/','^Harness/bin/','^Harness/obj/','^Tests/bin/','^Tests/obj/')
  $sizeLimit = 5MB
  $offenders = @()
  foreach ($p in $headFiles) {
    $n = $p -replace '\\','/'
    if ($blocked | Where-Object { $n -match $_ }) { $offenders += $p; continue }
    $full = Join-Path (Get-Location).Path $p
    if (Test-Path -LiteralPath $full) {
      try { if ((Get-Item -LiteralPath $full).Length -gt $sizeLimit) { $offenders += $p } } catch {}
    }
  }
  if ($offenders.Count -gt 0) {
    Write-Error ("HEAD contains disallowed/large files; aborting. Offenders:`n  " + ($offenders -join "`n  "))
    Stop-Transcript | Out-Null; exit 4
  }
  Write-Host 'HEAD looks clean (scripts only; no artifacts/bin/obj/large files).'

  Write-Host '=== 3) Check if branch exists on origin ==='
  $existsRemote = $false
  $lsOut = ''
  try { $lsOut = git ls-remote --heads origin $branch } catch {}
  if ($lsOut -match '[0-9a-f]{7,}\s+refs/heads/') { $existsRemote = $true }
  Write-Host ("Remote has branch? {0}" -f $existsRemote)

  $pushed = $false
  if (-not $existsRemote) {
    Write-Host ("Pushing {0} to origin..." -f $branch)
    $pushOut = & git push -u origin $branch 2>&1
    $rc = $LASTEXITCODE
    if ($rc -ne 0) {
      Write-Warning 'git push failed. Authentication guidance:'
      Write-Host '- HTTPS + PAT: Create a Personal Access Token (repo scope) at https://github.com/settings/tokens' -ForegroundColor Yellow
      Write-Host '  Then run: git push -u origin ' $branch ' and use your GitHub username + PAT as password when prompted.' -ForegroundColor Yellow
      Write-Host '- SSH: Generate a key (ssh-keygen -t ed25519), add the public key to GitHub (Settings > SSH keys),' -ForegroundColor Yellow
    Write-Host '  start ssh-agent (Start-Process ssh-agent), ssh-add $env:USERPROFILE\\.ssh\\id_ed25519,' -ForegroundColor Yellow
      Write-Host '  then: git remote set-url origin git@github.com:<owner>/<repo>.git and git push -u origin ' $branch -ForegroundColor Yellow
      Write-Host '--- push output (first lines) ---'
      $pushOut | Select-Object -First 50 | ForEach-Object { Write-Host $_ }
      Stop-Transcript | Out-Null; exit 3
    } else {
      $pushed = $true
      Write-Host 'Push succeeded.'
    }
  } else {
    $pushed = $true
    Write-Host 'Branch already exists on origin; skipping push.'
  }

  Write-Host '=== 4) Backup tag ==='
  $ts = (Get-Date).ToString('yyyyMMdd_HHmmss')
  $tag = "pre_push_backup_$ts"
  try { git tag -f $tag | Out-Null } catch {}
  Write-Host ("Backup tag: {0}" -f $tag)

  Write-Host '=== 5) Draft PR or Compare URL ==='
  # Determine base branch from remote; default to main
  $base = ''
  try { $base = (git remote show origin | Select-String 'HEAD branch:' | ForEach-Object { $_.ToString().Split(':')[-1].Trim() }) } catch {}
  if ([string]::IsNullOrWhiteSpace($base) -or $base -eq $branch) { $base = 'main' }

  $prUrl = ''
  $compareUrl = ''
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  $ghLoggedIn = $false
  if ($gh) { try { gh auth status | Out-Null; if ($LASTEXITCODE -eq 0) { $ghLoggedIn = $true } } catch {} }

  $prBodyPath = Join-Path $PSScriptRoot 'pr_body_from_agent.txt'
  if (-not (Test-Path $prBodyPath)) {
    @'
Summary
Add robust automation for reconciling reconstructed closed trades and streaming computation of fill-breakdown across large orders.csv artifacts.

This PR includes:
- scripts/make_closes_from_reconstructed.py
  - Streams the reconstructed closed trades CSV, dedupes on key (open_time, close_time, open_price, close_price, pnl, side, volume, symbol), writes:
    - closed_trades_fifo_reconstructed_cleaned.csv
    - trade_closes_like_from_reconstructed.csv (CSV: trade_id,close_time,pnl)
    - trade_closes_like_from_reconstructed.jsonl (JSONL fallback)
- scripts/compute_fill_breakdown_stream.py
  - Streaming/chunked processing of orders.csv (default chunk size 100k), incremental aggregations, partial outputs and final merged CSVs:
    - fill_rate_by_side.csv
    - fill_breakdown_by_hour.csv
    - analysis_summary_stats.json
- scripts/run_reconcile_and_compute.ps1
  - Parameterized wrapper (-ArtifactPath, -ChunkSize), backups, reconcile with CSV/JSONL fallback, streaming compute, logs and summary JSON; exit codes 0/10/20/30.

Why
Previously the compute step could OOM or take unbounded time on large orders.csv (~483MB). This adds a streaming solution and a safe wrapper that is idempotent, logs progress, and can be reused across artifacts.

What I tested
- Ran wrapper on artifacts/telemetry_run_20250819_154459
  - closed_sum == closes_sum == 150016.07399960753 (diff 0)
  - total_orders_rows: 3,010,362; chunks_processed: 31; elapsed ~254.4s
  - produced artifacts and logs in artifact folder

Notes
- artifacts are NOT committed in this PR.
- Wrapper logs to run_full_stream.log and writes auto_reconcile_compute_summary.json.

Reviewer checklist
- Validate streaming aggregation vs previous outputs.
- Confirm reconcile CSV/JSONL compatibility.
- Ensure no artifacts are staged for commit.
- Optional: run wrapper on a small sample to verify.
'@ | Set-Content -NoNewline -Path $prBodyPath -Encoding UTF8
  }

  if ($gh -and $ghLoggedIn) {
    try {
      $out = gh pr create --draft --title 'feat(telemetry): add streaming compute + artifact reconcile wrapper (safe chunking + backup)' --body-file $prBodyPath --base $base --head $branch 2>&1
      $prUrl = ($out | Select-String -Pattern 'https?://\S+' -AllMatches).Matches.Value | Select-Object -Last 1
      if ($prUrl) { Write-Host ("PR created: {0}" -f $prUrl) } else { Write-Host $out }
    } catch {
      Write-Warning 'gh pr create failed; falling back to compare URL.'
    }
  }

  if (-not $prUrl) {
    if ($origin -match 'github\.com[/:]([^/]+)/([^/.]+)') {
      $owner = $Matches[1]; $repo = $Matches[2]
      $compareUrl = "https://github.com/$owner/$repo/compare/$base...$branch"
      Write-Host ("Compare URL: {0}" -f $compareUrl)
      try { Start-Process $compareUrl } catch {}
      Write-Host 'Open the URL and paste PR body from scripts/pr_body_from_agent.txt'
    } else {
      Write-Host 'Cannot parse owner/repo from origin. Open GitHub and create a PR manually from the pushed branch.' -ForegroundColor Yellow
    }
  }

  Write-Host '=== 6) Write summary JSON ==='
  $committed = (git show --name-only --pretty="" HEAD) -split "`r?`n" | Where-Object { $_ -match '^scripts/.*' }
  $prLink = $null
  if ($prUrl) { $prLink = $prUrl } else { $prLink = $compareUrl }
  $summary = [ordered]@{
    branch = $branch
    remote = $origin
    pushed = $pushed
    pr_url_or_compare_url = $prLink
    tag_backup = $tag
    summary_files_committed = $committed
    timestamp = (Get-Date).ToString('o')
  }
  $sumPath = Join-Path $PSScriptRoot 'auto_push_pr_summary.json'
  $json = Write-Json $summary
  [System.IO.File]::WriteAllText($sumPath, $json, [System.Text.Encoding]::UTF8)
  Write-Host 'Summary (JSON):'
  Write-Output $json

  Stop-Transcript | Out-Null

  Write-Host '=== Tail of log (last 10 lines) ==='
  Get-Content $logPath -Tail 10

  exit 0
}
catch {
  try { Stop-Transcript | Out-Null } catch {}
  Write-Error ("Unexpected error: {0}" -f $_.Exception.Message)
  if (Test-Path $logPath) { Get-Content $logPath -Tail 50 }
  exit 5
}
