# Requires Windows PowerShell 5.1+; safe read-only status report
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'

# Ensure repo root
$RepoRoot = (Get-Location).Path
if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot '.git')) -or -not (Test-Path -LiteralPath (Join-Path $RepoRoot 'BotG.sln'))) {
  Write-Error 'ERROR: Must run at repo root (missing .git or BotG.sln).'
  exit 2
}

# Paths
$ScriptsDir = Join-Path $RepoRoot 'scripts'
New-Item -ItemType Directory -Path $ScriptsDir -Force -ErrorAction SilentlyContinue | Out-Null
$LogPath = Join-Path $ScriptsDir 'run_status_report_log.txt'
$ReportPath = Join-Path $ScriptsDir 'status_report.json'
function Log([string]$m){ $ts=(Get-Date).ToString('o'); Add-Content -LiteralPath $LogPath -Value ("[$ts] " + $m) -Encoding utf8 }
Log 'Start status report.'

# 1) General repo info
$originUrl = ''
try { $originUrl = (git remote get-url origin).Trim() } catch {}
$branch = ''
try { $branch = (git branch --show-current).Trim() } catch {}
$repoName = ''
if ($originUrl -and ($originUrl -match '[:/]([^:/]+)/([^/]+?)(?:\.git)?$')) { $repoName = $Matches[1] + '/' + $Matches[2] }
$tagsRaw = @(git for-each-ref --sort=-creatordate --format='%(refname:short)|%(creatordate:iso8601)' refs/tags/pre_push_backup_*)
$tags = @(); foreach($t in $tagsRaw){ if($t){ $p=$t.Split('|',2); $n=$null; $ts=$null; if($p.Length -ge 1){$n=$p[0]} if($p.Length -ge 2){$ts=$p[1]} $tags += ([pscustomobject]@{ name=$n; timestamp=$ts }) } }
$pushed = $false
try { $null = git ls-remote --exit-code --heads origin $branch 2>$null; if ($LASTEXITCODE -eq 0) { $pushed = $true } } catch {}

# 2) HEAD info
$headSha = ''; $headMsg = ''
try { $headSha = (git show -s --format='%H' HEAD).Trim(); $headMsg = (git show -s --format='%s' HEAD).Trim() } catch {}
$filesInHead = @()
try { $filesInHead = (git show --name-only --pretty='format:' HEAD) -split "`r`n|`n" | Where-Object { $_ -ne '' } } catch {}

# 3) Files/artifacts & scripts created
$checkFiles = @('scripts/pr_body_final.txt','scripts/pr_review_checklist.md','scripts/pr_publish_report.json','scripts/auto_push_pr_summary.json','scripts/run_push_pr_log.txt','scripts/run_pr_prep_log.txt','scripts/.pr_trigger_for_pr.txt')
function Read-FirstLast([string]$path, [int]$n=20){ $res = [pscustomobject]@{ first=@(); last=@(); total_lines=0 }; try { $lines = Get-Content -LiteralPath $path; $res.total_lines = if($lines){$lines.Count}else{0}; if ($lines -and $lines.Count -le ($n*2)) { $res.first = $lines; $res.last = @() } elseif ($lines) { $res.first = $lines[0..([Math]::Min($n-1,$lines.Count-1))]; $res.last = $lines[([Math]::Max(0,$lines.Count-$n))..($lines.Count-1)] } else { $res.first=@(); $res.last=@() } } catch { $res.first = @("<error reading file: " + $_.Exception.Message); $res.last = @() }; return $res }
$filesDetails = @{}
foreach($rel in $checkFiles){
  $full = Join-Path $RepoRoot $rel
  if (Test-Path -LiteralPath $full) {
    $fi = Get-Item -LiteralPath $full
    $snip = Read-FirstLast -path $full -n 20
    $filesDetails[$rel] = [pscustomobject]@{
      exists=$true; size_bytes=$fi.Length; last_modified=$fi.LastWriteTime.ToString('o');
      first_lines=$snip.first; last_lines=$snip.last; total_lines=$snip.total_lines
    }
  } else {
    $filesDetails[$rel] = [pscustomobject]@{ exists=$false }
  }
}

# 4) Build / Test (read-only)
$buildOut = ''; $buildOk = $false
try { $buildOut = (& dotnet build) 2>&1 | Out-String; $buildOk = $LASTEXITCODE -eq 0 } catch { $buildOut = $_.Exception.Message }
$buildTail = ($buildOut -split "`r`n|`n") | Select-Object -Last 50
$testOut = ''; $testOk = $false; $testsPassed = $null
try { $testOut = (& dotnet test) 2>&1 | Out-String; $testOk = $LASTEXITCODE -eq 0; $m = [regex]::Match($testOut, 'Total tests:\s*(\d+).*?Passed:\s*(\d+).*?Failed:\s*(\d+)', 'Singleline'); if ($m.Success) { $testsPassed = [int]$m.Groups[2].Value } } catch { $testOut = $_.Exception.Message }
$testTail = ($testOut -split "`r`n|`n") | Select-Object -Last 50

# 4b) Sample wrapper summary
$summaryPath = $null; $rows=$null; $chunks=$null; $elapsed=$null; $diff=$null
try { $sumFiles = Get-ChildItem -Path (Join-Path $RepoRoot 'artifacts') -Filter 'auto_reconcile_compute_summary.json' -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending; if ($sumFiles -and $sumFiles.Count -gt 0) { $summaryPath = $sumFiles[0].FullName } } catch {}
if (-not $summaryPath) { $prReport = Join-Path $ScriptsDir 'pr_publish_report.json'; if (Test-Path -LiteralPath $prReport) { try { $pr = Get-Content -LiteralPath $prReport -Raw | ConvertFrom-Json; if ($pr.summary_path) { $summaryPath = $pr.summary_path } } catch {} } }
if ($summaryPath -and (Test-Path -LiteralPath $summaryPath)) {
  try {
    $s = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $rows = $s.total_orders_rows -as [int]; if (-not $rows) { $rows = $s.rows }
    $chunks = $s.chunks_processed -as [int]; if (-not $chunks) { $chunks = $s.chunks }
    $elapsed = $s.elapsed_seconds -as [double]
    $cs = $s.closed_sum; $cls = $s.closes_sum
    if ($null -ne $cs -and $null -ne $cls) { $diff = [double]$cs - [double]$cls } else { $diff = $s.diff }
  } catch {}
}

# 5) PR / Compare status
$compareUrl = $null; $prUrl = $null
$prReportPath = Join-Path $ScriptsDir 'pr_publish_report.json'
if (Test-Path -LiteralPath $prReportPath) { try { $prr = Get-Content -LiteralPath $prReportPath -Raw | ConvertFrom-Json; $compareUrl = $prr.pr_compare_url; $prUrl = $prr.pr_url } catch {} }
if (-not $compareUrl -and $originUrl -and $branch) { if ($originUrl -match 'github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?$') { $owner=$Matches[1]; $repo=$Matches[2]; $compareUrl = 'https://github.com/' + $owner + '/' + $repo + '/compare/main...' + $branch } }
$originShow = (& git remote show origin) 2>&1 | Out-String

# 8) JSON
$now = (Get-Date).ToString('o')
$noop = $filesDetails['scripts/.pr_trigger_for_pr.txt']
$json = [pscustomobject]@{
  repo = $repoName
  remote = $originUrl
  branch = $branch
  head_sha = $headSha
  head_message = $headMsg
  files_in_head = $filesInHead
  tags = ($tags | ForEach-Object { $_.name })
  pushed = $pushed
  pr_compare_url = $compareUrl
  pr_url = $prUrl
  noop_file = @{ exists = ($noop -and $noop.exists); path = 'scripts/.pr_trigger_for_pr.txt'; commit_sha = $null }
  sample_run = @{ rows=$rows; chunks=$chunks; elapsed_seconds=$elapsed; diff=$diff; summary_path=$summaryPath }
  build = @{ dotnet_build_passed=$buildOk; dotnet_test_passed=$testOk; tests_passed=$testsPassed; build_tail=$buildTail; test_tail=$testTail }
  artifacts = @('scripts/pr_body_final.txt','scripts/pr_review_checklist.md','scripts/pr_publish_report.json','scripts/auto_push_pr_summary.json')
  logs = @{ run_push_pr_log = 'scripts/run_push_pr_log.txt'; run_pr_prep_log = 'scripts/run_pr_prep_log.txt'; status_log = 'scripts/run_status_report_log.txt' }
  origin_show = $originShow
  files_details = $filesDetails
  timestamp = $now
}
$json | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $ReportPath -Encoding utf8
Log 'Report written.'

# Print JSON
$json | ConvertTo-Json -Depth 8 | Write-Output

# Human-readable summary
Write-Output "`n--- SUMMARY (VI) ---"
$state = if ($buildOk -and $testOk) { 'Sẵn sàng' } else { 'Cần kiểm tra' }
$headShort = if ([string]::IsNullOrWhiteSpace($headSha)) { '<none>' } else { $headSha.Substring(0,[Math]::Min(7,$headSha.Length)) }
Write-Output ('Trạng thái: ' + $state + '. Branch: ' + $branch + '. HEAD: ' + $headShort)
$compareShow = if ([string]::IsNullOrWhiteSpace($compareUrl)) { '<none>' } else { $compareUrl }
Write-Output ('- Pushed to origin: ' + $pushed + '; Compare: ' + $compareShow)
if ($rows) { Write-Output ('- Sample run: rows=' + $rows + ', chunks=' + $chunks + ', elapsed~' + $elapsed + 's, diff=' + $diff) }
if ($buildOk) { $buildStat = 'PASS' } else { $buildStat = 'FAIL' }
if ($testOk) { $testStat = 'PASS' } else { $testStat = 'FAIL' }
Write-Output ('- Build: ' + $buildStat + '; Test: ' + $testStat)
Write-Output ('- Report JSON: ' + $ReportPath)
Write-Output 'Next: mở Compare URL và tạo PR nếu cần; hoặc bật branch protection cho main.'

# Actions
Write-Output "`n--- ACTIONS ---"
$idx=1
$reproCompare = $compareUrl; if ([string]::IsNullOrWhiteSpace($reproCompare) -and $repoName -and $branch) { $reproCompare = 'https://github.com/' + $repoName + '/compare/main...' + $branch }
if ([string]::IsNullOrWhiteSpace($reproCompare)) { $reproCompare = '<none>' }
Write-Output ($idx.ToString() + ') Open Compare: ' + $reproCompare); $idx++
Write-Output ($idx.ToString() + ') Build/test: dotnet build ; dotnet test'); $idx++
Write-Output ($idx.ToString() + ') Run wrapper sample: .\\scripts\\run_reconcile_and_compute.ps1 -ArtifactPath .\\artifacts\\telemetry_run_20250819_154459 -ChunkSize 10000'); $idx++
Write-Output ($idx.ToString() + ') Tail logs: Get-Content .\\scripts\\run_push_pr_log.txt -Tail 50'); $idx++
