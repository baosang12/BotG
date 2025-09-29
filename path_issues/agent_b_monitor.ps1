param(
  [Parameter(Mandatory=$true)][string]$RunId,
  [int]$PollSec = 30
)
$ErrorActionPreference = "Stop"
$TS  = Get-Date -Format "yyyyMMdd_HHmmss"
$OUT = "path_issues\agent_b_monitor_$TS"
$ART = Join-Path $OUT "artifacts"
New-Item -ItemType Directory -Force $OUT,$ART | Out-Null
$LOG = Join-Path $OUT "monitor.log"

"== MONITOR START $(Get-Date -Format o) runId=$RunId ==" | Tee-Object -FilePath $LOG -Append

$deadline = (Get-Date).AddHours(26)
do {
    # 1) Ưu tiên kiểm tra trạng thái job trên GitHub
    $s = gh run view $RunId --json status,conclusion,updatedAt,url 2>$null | ConvertFrom-Json
    "[HB] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') status=$($s.status) concl=$($s.conclusion)" |
      Tee-Object -FilePath $LOG -Append | Out-Host
    if ($s -and $s.status -eq "completed") { break }

    # 2) Fallback: watcher status.done/status.json trong artifact (hotfix #157)
    $statusDone = "artifacts\gate24h-artifacts\status.done"
    $statusJson = "artifacts\gate24h-artifacts\status.json"
    if (Test-Path $statusDone -and (Test-Path $statusJson)) {
        try {
            $j = Get-Content $statusJson -Raw | ConvertFrom-Json
            if ($j.status -eq "completed") { break }
        } catch {}
    }

    Start-Sleep -Seconds $PollSec
} while ((Get-Date) -lt $deadline)

# 3) Thu thập artifacts, xác minh đủ files
gh run download $RunId -D $ART | Tee-Object -FilePath $LOG -Append | Out-Null
$must = @(
  "orders.csv","telemetry.csv","risk_snapshots.csv",
  "trade_closes.log","run_metadata.json","closed_trades_fifo_reconstructed.csv",
  "status.done","status.json"
)
$missing=@(); foreach($m in $must){ if(-not (Get-ChildItem -Recurse $ART -Filter $m)){ $missing += $m } }

# 4) Đóng gói + SHA256 + one pager
$ZIP = Join-Path $OUT "postrun_$TS.zip"
Compress-Archive -Path (Join-Path $ART "*") -DestinationPath $ZIP -Force
$SHA = (Get-FileHash $ZIP -Algorithm SHA256).Hash
"SHA256  $SHA  $(Split-Path -Leaf $ZIP)" | Set-Content (Join-Path $OUT "SHA256.txt")

$one = @()
$one += "# Agent B Monitor Result"
$one += "*RunID:* $RunId"
$one += "*Ended:* $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$one += "*ZIP:* $(Split-Path -Leaf $ZIP)"
$one += "*SHA256:* $SHA"
if ($missing.Count){ $one += "##  Missing"; $missing | ForEach-Object { $one += "- $_" } }
else { $one += "##  Đủ 6 file + status.done + status.json" }
$one | Set-Content (Join-Path $OUT "one_pager.md") -Encoding utf8

Write-Host ("[B][DONE] Report dir: {0}" -f $OUT)
if($missing.Count){ exit 2 } else { exit 0 }
