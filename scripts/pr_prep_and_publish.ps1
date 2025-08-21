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
if (-not (Test-Path $ScriptsDir)) { New-Item -ItemType Directory -Path $ScriptsDir -Force | Out-Null }
$LogPath = Join-Path $ScriptsDir 'run_pr_prep_log.txt'

try { Stop-Transcript | Out-Null } catch {}
Start-Transcript -Path $LogPath -Append | Out-Null

function Write-Json($obj) { $obj | ConvertTo-Json -Depth 8 }

try {
  Write-Host '=== A) Preflight & Safety ==='
  if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { Write-Error 'Not a Git repository (.git missing).'; Stop-Transcript | Out-Null; exit 1 }
  $branch = (git branch --show-current).Trim()
  if ([string]::IsNullOrWhiteSpace($branch)) { Write-Error 'No current branch (detached HEAD).'; Stop-Transcript | Out-Null; exit 1 }
  $origin = ''
  try { $origin = (git remote get-url origin).Trim() } catch { $origin = '' }
  if (-not $origin) { Write-Error "Remote 'origin' missing. Add it first: git remote add origin <URL>"; Stop-Transcript | Out-Null; exit 2 }
  Write-Host ("Branch: {0}" -f $branch)
  Write-Host ("Origin: {0}" -f $origin)

  # HEAD safety
  $headFiles = @(); try { $headFiles = (git show --name-only --pretty="" HEAD) -split "`r`n|`n" | Where-Object { $_ -ne '' } } catch {}
  $blocked = @('^artifacts/','^BotG/bin/','^BotG/obj/','^Harness/bin/','^Harness/obj/','^Tests/bin/','^Tests/obj/')
  $sizeLimit = 5MB
  $offenders = @()
  foreach ($p in $headFiles) {
    $n = $p -replace '\\','/'
    if ($blocked | Where-Object { $n -match $_ }) { $offenders += $p; continue }
    $full = Join-Path $RepoRoot $p
    if (Test-Path -LiteralPath $full) {
      try { if ((Get-Item -LiteralPath $full).Length -gt $sizeLimit) { $offenders += $p } } catch {}
    }
  }
  if ($offenders.Count -gt 0) { Write-Error ("HEAD contains disallowed/large files:`n  " + ($offenders -join "`n  ")); Stop-Transcript | Out-Null; exit 4 }
  Write-Host 'HEAD OK (scripts only; no large/bin/obj/artifacts).'

  Write-Host '=== B) Create PR body & checklist ==='
  $bodyFromAgent = Join-Path $ScriptsDir 'pr_body_from_agent.txt'
  $bodyFinal = Join-Path $ScriptsDir 'pr_body_final.txt'
  $checklistPath = Join-Path $ScriptsDir 'pr_review_checklist.md'

  $baseTitle = 'feat(telemetry): add streaming compute + artifact reconcile wrapper (safe chunking + backup)'
  $bodyAgent = ''; if (Test-Path $bodyFromAgent) { $bodyAgent = Get-Content $bodyFromAgent -Raw }

  $bodyFinalContent = @"
Title
$baseTitle

Summary (Tiếng Việt + EN one-liner)
- VN: Thêm luồng tính toán streaming cho orders.csv và wrapper đối soát/reconcile an toàn (chunking + backup).
- EN: Add streaming compute for orders.csv and a safe artifact reconcile wrapper (chunking + backup).

What changed
- scripts/compute_fill_breakdown_stream.py — Tính toán tỉ lệ fill/slippage theo từng side/giờ theo dạng streaming/chunked; xuất CSV + JSON stats.
- scripts/make_closes_from_reconstructed.py — Làm sạch (dedupe) closed trades tái dựng; tạo CSV/JSONL trade closes tương thích.
- scripts/reconcile.py — Đối soát PnL: hỗ trợ CSV/JSONL cho closes; gom tổng robust; xuất JSON.
- scripts/run_reconcile_and_compute.ps1 — Wrapper tham số hóa (-ArtifactPath, -ChunkSize), backup, đối soát CSV/JSONL fallback, compute streaming, logs + summary JSON; mã thoát 0/10/20/30.

Why
- Tránh OOM/treo khi xử lý orders.csv lớn (~483MB). Cần quy trình streaming và wrapper tái sử dụng, có log/backup, dễ lặp lại.

How I tested
- Artifact: .\artifacts\telemetry_run_20250819_154459
- Reconcile: closed_sum == closes_sum == 150016.07399960753 (diff 0)
- Compute streaming: total_orders_rows: 3,010,362; chunks_processed: 31; elapsed ~254.4s
- Sản phẩm: fill_rate_by_side.csv, fill_breakdown_by_hour.csv, analysis_summary_stats.json, reconcile_fix_report.json, trade_closes_like_from_reconstructed.* , closed_trades_fifo_reconstructed_cleaned.csv

How to validate (commands)
1) Build/Test (nếu có solution/tests):
   - dotnet build
   - dotnet test
2) Chạy wrapper trên sample artifact:
   - .\scripts\run_reconcile_and_compute.ps1 -ArtifactPath .\artifacts\telemetry_run_20250819_154459 -ChunkSize 10000
   - Kỳ vọng: mã thoát 0; file auto_reconcile_compute_summary.json có closed_sum == closes_sum
3) Kiểm tra output nhanh:
   - type fill_rate_by_side.csv (vài dòng đầu)
   - type fill_breakdown_by_hour.csv (vài dòng đầu)

Risks & rollback
- Rủi ro thấp; thay đổi ở thư mục scripts. Có tạo tag backup dạng pre_push_backup_YYYYMMDD_HHMMSS. Cần rollback: git reset --hard <tag> hoặc git checkout <tag>.

Files changed
- scripts/compute_fill_breakdown_stream.py
- scripts/make_closes_from_reconstructed.py
- scripts/reconcile.py
- scripts/run_reconcile_and_compute.ps1

Reviewer checklist (rút gọn)
- Build/Test pass
- Wrapper chạy thành công trên sample artifact
- Reconcile diff == 0
- Không commit artifacts/bin/obj/nhị phân
- Đủ log và summary JSON

Suggested reviewers
- @team-lead @owner

Notes
- Đề xuất thêm CI job cho artifact sample và tham số hóa artifact path nếu cần.

Appendix (source draft)
$bodyAgent
"@
  [System.IO.File]::WriteAllText($bodyFinal, $bodyFinalContent, [System.Text.Encoding]::UTF8)

  $checklistContent = @'
# PR Reviewer Checklist

## Quick summary
Run build/tests and a small streaming compute on a known artifact. Validate reconcile and outputs.

## Commands

### Build
```powershell
dotnet build
```
Expected: Build Succeeded.

### Tests (if present)
```powershell
dotnet test
```
Expected: All tests pass.

### Sample wrapper run (10k chunk)
```powershell
.\scripts\run_reconcile_and_compute.ps1 -ArtifactPath .\artifacts\telemetry_run_20250819_154459 -ChunkSize 10000
```
Expected:
- Exit code 0
- File auto_reconcile_compute_summary.json created under the artifact
- JSON shows closed_sum == closes_sum

### Inspect outputs
```powershell
Get-Content .\artifacts\telemetry_run_20250819_154459\fill_rate_by_side.csv -Head 10
Get-Content .\artifacts\telemetry_run_20250819_154459\fill_breakdown_by_hour.csv -Head 10
Get-Content .\artifacts\telemetry_run_20250819_154459\analysis_summary_stats.json
Get-Content .\artifacts\telemetry_run_20250819_154459\reconcile_fix_report.json
```

### Validate reconcile
```powershell
($s = Get-Content .\artifacts\telemetry_run_20250819_154459\auto_reconcile_compute_summary.json -Raw | ConvertFrom-Json) | Out-Null
"closed_sum=$($s.closed_sum)  closes_sum=$($s.closes_sum)  diff=$([math]::Abs($s.closed_sum - $s.closes_sum))"
```
Expect: diff == 0.

## Performance
- Tune chunk size via -ChunkSize (e.g., 10000, 50000, 100000) for your machine.

## Security
- Ensure artifacts/ are not tracked (git status should not show artifacts/)
- Ensure no files > 5MB are committed.

## Merge readiness
- Build/tests pass
- Wrapper success on sample
- Reviewer sign-off
- Optional: 24h paper run before merge
'@
  [System.IO.File]::WriteAllText($checklistPath, $checklistContent, [System.Text.Encoding]::UTF8)

  Write-Host ("PR body: {0}" -f $bodyFinal)
  Write-Host ("Checklist: {0}" -f $checklistPath)

  Write-Host '=== C) Sanity checks: build & tests ==='
  $testsPassed = $true
  $solution = Join-Path $RepoRoot 'BotG.sln'
  if (Test-Path $solution) {
    Write-Host 'dotnet build...'
    & dotnet build $solution | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Error 'dotnet build failed.'; $testsPassed=$false }
    if ($testsPassed) {
      # Attempt tests if a Tests project directory exists
      if (Test-Path (Join-Path $RepoRoot 'Tests')) {
        Write-Host 'dotnet test...'
        & dotnet test $solution --no-build | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Error 'dotnet test failed.'; $testsPassed=$false }
      } else {
        Write-Host 'No Tests/ directory; skipping dotnet test.'
      }
    }
  } else {
    Write-Host 'No solution file found; skipping build/tests.'
  }
  if (-not $testsPassed) { Stop-Transcript | Out-Null; exit 4 }

  Write-Host '=== D) Sample artifact preparation ==='
  $artifact = Join-Path $RepoRoot 'artifacts/telemetry_run_20250819_154459'
  if (-not (Test-Path $artifact)) { Write-Error "Sample artifact not found: $artifact"; Stop-Transcript | Out-Null; exit 4 }
  $ordersCsv = Join-Path $artifact 'orders.csv'
  $ordersSample = Join-Path $artifact 'orders_sample_10k.csv'
  if ((Test-Path $ordersCsv -PathType Leaf) -and -not (Test-Path $ordersSample)) {
    Write-Host 'Creating orders_sample_10k.csv (header + 10k rows)...'
    $lines = Get-Content $ordersCsv -ReadCount 0
    if ($lines.Length -gt 0) {
      $take = [Math]::Min($lines.Length, 10001)
      $lines[0..($take-1)] | Set-Content -Path $ordersSample -Encoding UTF8
    }
  }

  Write-Host '=== E) Run wrapper on sample (ChunkSize=10000) ==='
  $summaryPath = Join-Path $artifact 'auto_reconcile_compute_summary.json'
  if (-not (Test-Path $summaryPath)) {
    $wrapExit = 0
    $wrapOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ScriptsDir 'run_reconcile_and_compute.ps1') -ArtifactPath $artifact -ChunkSize 10000 2>&1
    $wrapExit = $LASTEXITCODE
    if ($wrapExit -ne 0) {
      Write-Error ("Wrapper failed with exit code {0}" -f $wrapExit)
      $wrapOut | Select-Object -First 200 | ForEach-Object { Write-Host $_ }
      Stop-Transcript | Out-Null; exit 4
    }
    if (-not (Test-Path $summaryPath)) { Write-Error 'Summary JSON not produced by wrapper.'; Stop-Transcript | Out-Null; exit 4 }
  } else {
    Write-Host 'Summary JSON already exists; reusing it.'
  }
  $sum = Get-Content $summaryPath -Raw | ConvertFrom-Json
  $diff = [math]::Abs(($sum.closed_sum) - ($sum.closes_sum))
  if ($diff -gt 1e-6) { Write-Error ("Reconcile diff not zero: {0}" -f $diff); Stop-Transcript | Out-Null; exit 4 }
  $sampleRun = @{ rows = $sum.total_orders_rows; chunks = $sum.chunks_processed; elapsed_seconds = $sum.elapsed_seconds }

  Write-Host '=== F) PR creation (gh or compare) ==='
  # Determine base branch
  $base = ''
  try { $base = (git remote show origin | Select-String 'HEAD branch:' | ForEach-Object { $_.ToString().Split(':')[-1].Trim() }) } catch {}
  if ([string]::IsNullOrWhiteSpace($base) -or $base -eq $branch) { $base = 'main' }

  $prUrl = ''
  $compareUrl = ''
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  $ghLoggedIn = $false
  if ($gh) { try { gh auth status | Out-Null; if ($LASTEXITCODE -eq 0) { $ghLoggedIn = $true } } catch {} }

  if (($gh) -and $ghLoggedIn) {
    try {
      $out = gh pr create --draft --title $baseTitle --body-file $bodyFinal --base $base --head $branch 2>&1
      $prUrl = ($out | Select-String -Pattern 'https?://\S+' -AllMatches).Matches.Value | Select-Object -Last 1
      if ($prUrl) { Write-Host ("PR created: {0}" -f $prUrl) } else { Write-Host $out }
    } catch {
      Write-Warning 'gh pr create failed; fallback to compare URL.'
    }
  }

  if (-not $prUrl) {
    if ($origin -match 'github\.com[/:]([^/]+)/([^/.]+)') {
      $owner = $Matches[1]; $repo = $Matches[2]
      $compareUrl = "https://github.com/$owner/$repo/compare/$base...$branch"
      Write-Host ("Compare URL: {0}" -f $compareUrl)
      try { Start-Process $compareUrl } catch {}
      Write-Host 'Open the URL and paste PR body from scripts/pr_body_final.txt'
    } else {
      Write-Host 'Cannot parse owner/repo from origin; create PR manually.' -ForegroundColor Yellow
    }
  }

  Write-Host '=== G) Publish report ==='
  $tagBackup = (git tag --list 'pre_push_backup_*' | Sort-Object | Select-Object -Last 1)
  $prOrCompare = $prUrl
  if (-not $prOrCompare) { $prOrCompare = $compareUrl }

  $report = [ordered]@{
    branch = $branch
    remote = $origin
    pushed = $true
    pr_url_or_compare_url = $prOrCompare
    tests_passed = $true
    sample_run_summary = $sampleRun
    tag_backup = $tagBackup
    timestamp = (Get-Date).ToString('o')
  }
  $reportPath = Join-Path $ScriptsDir 'pr_publish_report.json'
  [System.IO.File]::WriteAllText($reportPath, (Write-Json $report), [System.Text.Encoding]::UTF8)
  Write-Host ("PR body: {0}" -f $bodyFinal)
  Write-Host ("Checklist: {0}" -f $checklistPath)
  Write-Host ("Report: {0}" -f $reportPath)
  Write-Host ("PR/Compare: {0}" -f $prOrCompare)

  Stop-Transcript | Out-Null

  # Print short outputs
  Write-Host '--- pr_body_final.txt (first 40 lines) ---'
  Get-Content $bodyFinal -TotalCount 40 | ForEach-Object { Write-Host $_ }
  Write-Host '--- pr_review_checklist.md (first 40 lines) ---'
  Get-Content $checklistPath -TotalCount 40 | ForEach-Object { Write-Host $_ }
  Write-Host '--- pr_publish_report.json ---'
  Get-Content $reportPath -Raw

  exit 0
}
catch {
  try { Stop-Transcript | Out-Null } catch {}
  Write-Error ("Unexpected error: {0}" -f $_.Exception.Message)
  if (Test-Path $LogPath) { Get-Content $LogPath -Tail 50 }
  exit 5
}
