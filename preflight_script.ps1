$ErrorActionPreference = "Stop"
$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$OUT = "path_issues\preflight_$ts"
New-Item -ItemType Directory -Force $OUT | Out-Null
$LOG = Join-Path $OUT "preflight_stdout.log"
$JSON = Join-Path $OUT "preflight_report.json"

# == 0) Banner start (1 dòng) ==
Write-Host ("[PRE-FLIGHT START] {0} (UTC+7)" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))

# Helper: capture to log
function Tee($msg){ $msg | Tee-Object -FilePath $LOG -Append | Out-Host }

# == 1) Repo & CI ==
gh repo set-default baosang12/BotG | Out-Null
Tee "== Repo HEAD =="
$headRaw = gh api repos/baosang12/BotG/commits/main
$headObj = $headRaw | ConvertFrom-Json
$head = @{
  sha = $headObj.sha
  msg = $headObj.commit.message
  date = $headObj.commit.author.date
} | ConvertTo-Json -Compress
Tee $head

Tee "== CI status (required checks) =="
$checksRaw = gh api repos/baosang12/BotG/commits/main/check-runs
$checksObj = ($checksRaw | ConvertFrom-Json).check_runs | Select-Object name,status,conclusion
$checks = $checksObj | ConvertTo-Json -Compress
Tee $checks

# == 2) gate24h.yml sanity ==
Tee "== gate24h.yml snapshot =="
$wfPath = ".github/workflows/gate24h.yml"
Copy-Item $wfPath (Join-Path $OUT "workflow_snap.yml") -Force
$yaml = Get-Content $wfPath -Raw

$hasTimeout = $yaml -match 'timeout-minutes:\s*1500'
$hasAlways  = $yaml -match 'uses:\s*actions/upload-artifact@v4[\s\S]*?if:\s*\$\{\{\s*always\(\)\s*\}\}'
$hasSelfcheck = $yaml -match 'artifact-selfcheck'
Tee "timeout-minutes:1500? -> $hasTimeout"
Tee "upload-artifact has always()? -> $hasAlways"
Tee "artifact-selfcheck job present? -> $hasSelfcheck"

# == 3) Runner & môi trường ==
Tee "== Self-hosted runners (repo level) =="
$runnersRaw = gh api repos/baosang12/BotG/actions/runners
$runnersObj = $runnersRaw | ConvertFrom-Json
$runners = @{
  total_count = $runnersObj.total_count
  names = $runnersObj.runners | ForEach-Object { $_.name }
  statuses = $runnersObj.runners | ForEach-Object { $_.status }
  os = $runnersObj.runners | ForEach-Object { $_.os }
} | ConvertTo-Json -Compress
Tee $runners

Tee "== Disk free (GB) =="
$diskC = [math]::Round((Get-PSDrive C).Free/1GB,2)
$diskD = [math]::Round((Get-PSDrive D).Free/1GB,2)
Tee ("C: {0} GB free; D: {1} GB free" -f $diskC, $diskD)

Tee "== Sentinels =="
$stop  = Test-Path D:\botg\logs\RUN_STOP
$pause = Test-Path D:\botg\logs\RUN_PAUSE
Tee ("RUN_STOP={0}; RUN_PAUSE={1}" -f $stop, $pause)
if (!(Test-Path D:\botg\logs)) { New-Item -ItemType Directory -Force D:\botg\logs | Out-Null }
# Test quyền ghi
$tmp = "D:\botg\logs\_preflight_write_test.txt"
"ok" | Out-File -FilePath $tmp -Encoding utf8
Remove-Item $tmp -Force

# == 4) (Tuỳ chọn) chạy selfcheck nhanh để xác thực glob upload ==
# Chỉ chạy nếu artifact-selfcheck tồn tại
$didSelfcheck = $false
$runId = $null
if ($hasSelfcheck) {
  Tee "== Trigger artifact-selfcheck (workflow_dispatch) =="
  gh workflow run gate24h.yml -f mode=paper -f hours=0 -f source=artifact-selfcheck | Tee-Object -FilePath $LOG -Append | Out-Null
  Start-Sleep -Seconds 8
  $runs = gh run list --workflow "gate24h.yml" -L 5 --json databaseId,status,displayTitle,createdAt
  Tee $runs
  $runId = ($runs | ConvertFrom-Json | Select-Object -First 1).databaseId
  if ($runId) {
    Tee "Selfcheck runId: $runId"
    # chờ tối đa ~2 phút
    $limit = (Get-Date).AddMinutes(2)
    do {
      Start-Sleep -Seconds 5
      $v = gh run view $runId --json status,conclusion,updatedAt
      Tee $v
      $st = ($v | ConvertFrom-Json).status
    } while ($st -ne "completed" -and (Get-Date) -lt $limit)

    # tải artifact và liệt kê 6 file
    $selfDst = Join-Path $OUT "selfcheck_artifacts"
    New-Item -ItemType Directory -Force $selfDst | Out-Null
    gh run download $runId -D $selfDst | Tee-Object -FilePath $LOG -Append | Out-Null
    Get-ChildItem -Recurse $selfDst | Where-Object {!$_.PSIsContainer} | Select-Object FullName |
      ForEach-Object { $_.FullName } | Set-Content (Join-Path $OUT "selfcheck_artifacts_list.txt")
    $didSelfcheck = $true
  }
}

# == 5) Tổng hợp JSON báo cáo ==
$report = [ordered]@{
  timestamp_local = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
  repo_head       = $headObj | Select-Object sha,@{n='msg';e={$_.commit.message}},@{n='date';e={$_.commit.author.date}}
  ci_checks       = $checksObj
  workflow        = [ordered]@{
    timeout_1500      = $hasTimeout
    upload_always     = $hasAlways
    has_artifact_selfcheck = $hasSelfcheck
  }
  runners         = $runnersObj | Select-Object total_count,@{n='names';e={$_.runners.name}},@{n='statuses';e={$_.runners.status}},@{n='os';e={$_.runners.os}}
  disk_free_gb    = @{ C = $diskC; D = $diskD }
  sentinels       = @{ RUN_STOP = $stop; RUN_PAUSE = $pause }
  paths           = @{ logs_root = "D:\botg\logs"; out_dir = $OUT }
  selfcheck       = @{ executed = $didSelfcheck; run_id = $runId }
  pass_criteria   = [ordered]@{
    disk_ge_10GB   = ($diskD -ge 10 -or $diskC -ge 10)
    no_RUN_STOP    = (-not $stop)
    timeout_set    = $hasTimeout
    upload_always  = $hasAlways
    runner_online  = ($runnersObj.runners.status -contains "online")
  }
}
$report | ConvertTo-Json -Depth 6 | Out-File -FilePath $JSON -Encoding utf8

# == 6) Banner end (1 dòng) ==
Write-Host ("[PRE-FLIGHT DONE] {0} (UTC+7) Report: {1}" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"), $JSON)