$ErrorActionPreference = "Stop"
gh repo set-default baosang12/BotG | Out-Null

# ====== 0) Cấu hình đường dẫn output ======
$TS = Get-Date -Format "yyyyMMdd_HHmmss"
$OUT = "path_issues\gate2_24h_$TS"
$ART = Join-Path $OUT "artifacts"
New-Item -ItemType Directory -Force $OUT,$ART | Out-Null

# ====== 1) Xác minh preflight của A ======
$PREFLIGHT = "path_issues\preflight_20250929_111655\preflight_report.json"
if (!(Test-Path $PREFLIGHT)) { throw "Thiếu preflight_report.json: $PREFLIGHT" }
$pf = Get-Content $PREFLIGHT | ConvertFrom-Json
$need = "disk_ge_10GB","no_RUN_STOP","timeout_set","upload_always","runner_online"
$bad  = $need | Where-Object { -not $pf.pass_criteria.$_ }
if ($bad.Count) { throw "Preflight FAIL: $($bad -join ', ')" }
"[B] Preflight PASS" | Tee-Object -FilePath (Join-Path $OUT "run_kickoff.txt") -Append

# ====== 2) Kickoff Gate2 24h ======
gh workflow run gate24h.yml -f mode=paper -f hours=24 -f source=ops
Start-Sleep 5
$run = gh run list --workflow "gate24h.yml" -L 1 --json databaseId,createdAt | ConvertFrom-Json
$runId = $run[0].databaseId
"[RUN_ID] $runId" | Tee-Object -FilePath (Join-Path $OUT "run_kickoff.txt") -Append
$LOG = Join-Path $OUT "run_monitor.log"
"== MONITOR START $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==" | Tee-Object -FilePath $LOG -Append

# ====== 3) Monitor: 60s/lần, STOP ngay khi completed ======
# (Không còn ngủ cứng 30 phút)
$deadline = (Get-Date).AddHours(26)
do {
  $j = gh run view $runId --json status,conclusion,updatedAt | ConvertFrom-Json
  $hb = "[HB] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') status=$($j.status) concl=$($j.conclusion) updated=$($j.updatedAt)"
  $hb | Tee-Object -FilePath $LOG -Append | Out-Host
  if ($j.status -eq "completed") { break }
  Start-Sleep 60
} while ((Get-Date) -lt $deadline)

$final = gh run view $runId --json status,conclusion,createdAt,updatedAt,url | ConvertFrom-Json
"== MONITOR END $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') status=$($final.status) concl=$($final.conclusion) ==" | `
  Tee-Object -FilePath $LOG -Append | Out-Host

# ====== 4) Thu thập artifacts & kiểm tra “status.done/status.json” ======
gh run download $runId -D $ART | Tee-Object -FilePath $LOG -Append | Out-Null

$must = @(
  "orders.csv","telemetry.csv","risk_snapshots.csv",
  "trade_closes.log","run_metadata.json","closed_trades_fifo_reconstructed.csv",
  "status.done","status.json"
)
$missing = @()
foreach($m in $must){ if(-not (Get-ChildItem -Recurse $ART -Filter $m)){ $missing += $m } }

# Nén + SHA256
$ZIP = Join-Path $OUT "postrun_artifacts_$TS.zip"
Compress-Archive -Path (Join-Path $ART "*") -DestinationPath $ZIP -Force
$SHA = (Get-FileHash $ZIP -Algorithm SHA256).Hash
"SHA256  $SHA  $(Split-Path -Leaf $ZIP)" | Set-Content (Join-Path $OUT "SHA256.txt")

# ====== 5) (Tùy chọn) phân tích KPI sau-run nếu script có sẵn ======
$AN = "scripts\analyze_after_run.ps1"
$AN_OUT = Join-Path $OUT "postrun_report"
$analysisOk = $false
if (Test-Path $AN) {
  try { pwsh -File $AN -ArtifactsPath $ART -Out $AN_OUT; $analysisOk = $true } catch { Write-Warning $_.Exception.Message }
}

# ====== 6) One-pager & summary ======
$one = @()
$one += "# Gate2 — Báo cáo 24h (Agent B)"
$one += "*RunID:* $runId"
$one += "*Status:* $($final.status) / $($final.conclusion)"
$one += "*Run URL:* $($final.url)"
$one += "## Artifacts"
$one += "- Dir: $ART"
$one += "- ZIP: $(Split-Path -Leaf $ZIP)"
$one += "- SHA256: $SHA"
if ($missing.Count) {
  $one += "### ❌ Thiếu"
  foreach($m in $missing){ $one += "- $m" }
}
else {
  $one += "### ✅ Đủ 6 file + status.done + status.json"
}
$one += "## Phân tích"
if ($analysisOk) {
  $one += "- KPI generated at $AN_OUT"
} else {
  $one += "- Chưa có KPI (thiếu script hoặc lỗi)"
}
$one | Set-Content (Join-Path $OUT "one_pager.md") -Encoding utf8

$result = [ordered]@{
  run_id=$runId; status=$final.status; conclusion=$final.conclusion
  artifacts=$ART; zip=$ZIP; sha256=$SHA; missing=$missing; analysis_done=$analysisOk
  monitor_log=$LOG; out_dir=$OUT
}
$result | ConvertTo-Json -Depth 6 | Out-File (Join-Path $OUT "run_final_summary.json") -Encoding utf8

Write-Host ("[B][DONE] Báo cáo tại: {0}" -f $OUT)
if ($missing.Count) { exit 2 } else { exit 0 }
