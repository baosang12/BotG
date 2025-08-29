<#
Runs a full 1-hour realtime smoke in the background, handles artifact copy, optional remediation (one retry),
zips artifacts, produces a final JSON, and attempts to comment on PR #12 using GitHub CLI if available.

Params:
  -OutBase    : repo-relative base output dir (will create telemetry_run_* inside)
  -Seconds    : defaults 3600
  -SecondsPerHour : defaults 3600
  -FillProb   : defaults 0.9
  -Drain      : defaults 60
  -Grace      : defaults 10
  -PRNumber   : defaults 12
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)] [string]$OutBase,
  [int]$Seconds = 3600,
  [int]$SecondsPerHour = 3600,
  [double]$FillProb = 0.9,
  [int]$Drain = 60,
  [int]$Grace = 10,
  [int]$PRNumber = 12
)

$ErrorActionPreference = 'Stop'

function TimestampNow { (Get-Date).ToString('yyyyMMdd_HHmmss') }
function Write-Text([string]$p,[string]$t) { $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p, $t, $enc) }
function Write-Json([string]$p,[object]$o,[int]$d=12) { Write-Text $p ($o | ConvertTo-Json -Depth $d) }

$repoRoot = (Resolve-Path '.').Path
$scriptsDir = Join-Path $repoRoot 'scripts'
$runSmoke = Join-Path $scriptsDir 'run_smoke.ps1'
if (-not (Test-Path -LiteralPath $runSmoke)) { throw "Missing scripts/run_smoke.ps1" }
New-Item -ItemType Directory -Path $OutBase -Force | Out-Null

# Record daemon metadata for operators
try {
  $pidFile = Join-Path $OutBase 'daemon.pid'
  $ackJson = Join-Path $OutBase 'daemon_ack.json'
  $ack = [pscustomobject]@{ started=(Get-Date).ToString('o'); seconds=$Seconds; seconds_per_hour=$SecondsPerHour; fill_prob=$FillProb; drain=$Drain; grace=$Grace; pid=$PID }
  $enc = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllText($pidFile, [string]$PID, $enc)
  [System.IO.File]::WriteAllText($ackJson, ($ack | ConvertTo-Json -Depth 6), $enc)
} catch {}

function Invoke-OneRun([string]$pretty,[int]$sec,[int]$sph,[double]$fp,[int]$dr,[int]$gr,[string]$baseOut) {
  $asciiBase = Join-Path $env:TEMP ("botg_artifacts_orchestrator_" + $pretty + '_' + (TimestampNow))
  New-Item -ItemType Directory -Path $asciiBase -Force | Out-Null
  $log = Join-Path $baseOut ("run_smoke_" + $pretty + '_' + (TimestampNow) + '.log')
  $err = Join-Path $baseOut ("run_smoke_" + $pretty + '_' + (TimestampNow) + '.err.log')

  $deadline = (Get-Date).AddHours(2)
  while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
  if (-not $p.HasExited) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}; return @{ status='TIMEOUT'; note='Process exceeded 2h window'; outdir=$null; zip=$null } }
  $cand = Get-ChildItem -LiteralPath $asciiBase -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $cand) { return @{ status='MISSING'; note='No telemetry_run_* under temp'; outdir=$null; zip=$null } }
  $runDir = $cand.FullName
  $leaf = Split-Path -Leaf $runDir
  $dest = Join-Path $baseOut $leaf
  if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
  # Robust copy-back: prefer robocopy on Windows for Unicode-safety and resilience
  try {
    $robo = Get-Command robocopy -ErrorAction SilentlyContinue
    if ($robo) {
      $null = & robocopy $runDir $dest *.* /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    } else {
      Copy-Item -Path (Join-Path $runDir '*') -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue
    }
  } catch {
    # last resort fallback
    Copy-Item -Path (Join-Path $runDir '*') -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue
  }
  # Atomic sentinel to signal copy completion
  try {
    $tmpFlag = Join-Path $dest 'copy_complete.tmp'
    $finalFlag = Join-Path $dest 'copy_complete.flag'
    $enc = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($tmpFlag, (Get-Date).ToString('o'), $enc)
    if (Test-Path -LiteralPath $finalFlag) { Remove-Item -LiteralPath $finalFlag -Force -ErrorAction SilentlyContinue }
    Move-Item -LiteralPath $tmpFlag -Destination $finalFlag -Force
  } catch {}
  # ensure zip
  $zip = Get-ChildItem -LiteralPath $dest -Filter '*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $zip) { try { Add-Type -AssemblyName System.IO.Compression.FileSystem; $z = Join-Path $dest ((Split-Path -Leaf $dest) + '.zip'); if (Test-Path -LiteralPath $z) { Remove-Item -LiteralPath $z -Force }; [System.IO.Compression.ZipFile]::CreateFromDirectory($dest, $z); $zip = Get-Item -LiteralPath $z } catch {} }
  # load metrics
  $paths = @{ meta = Join-Path $dest 'run_metadata.json'; rr = Join-Path $dest 'reconstruct_report.json'; rec = Join-Path $dest 'reconcile_report.json'; as = Join-Path $dest 'analysis_summary.json' }
  $missing = @(); foreach ($p2 in @($paths.meta, (Join-Path $dest 'orders.csv'))) { if (-not (Test-Path -LiteralPath $p2)) { $missing += $p2 } }
  if ($missing.Count -gt 0) {
    $zipPath = $null; if ($zip) { $zipPath = $zip.FullName }
    return @{ status='MISSING_ARTIFACTS'; outdir=$dest; zip=$zipPath; missing=$missing }
  }
  $meta=$null;$rr=$null;$rec=$null;$as=$null
  try { $meta = Get-Content -LiteralPath $paths.meta -Raw | ConvertFrom-Json } catch {}
  if (Test-Path -LiteralPath $paths.rr) { try { $rr = Get-Content -LiteralPath $paths.rr -Raw | ConvertFrom-Json } catch {} }
  if (Test-Path -LiteralPath $paths.rec) { try { $rec = Get-Content -LiteralPath $paths.rec -Raw | ConvertFrom-Json } catch {} }
  if (Test-Path -LiteralPath $paths.as) { try { $as = Get-Content -LiteralPath $paths.as -Raw | ConvertFrom-Json } catch {} }
  # derive
  $requests=$null;$fills=$null;$fillRate=$null;$closed=$null;$orphBefore=$null;$orphAfter=$null;$totalPnl=$null;$maxDD=$null
  if ($rec) { if ($rec.request_count) { $requests=[int]$rec.request_count }; if ($rec.fill_count) { $fills=[int]$rec.fill_count }; if ($rec.closed_trades_count) { $closed=[int]$rec.closed_trades_count }; if ($null -ne $rec.orphan_fills_count) { $orphBefore=[int]$rec.orphan_fills_count } }
  if ($null -ne $requests -and $null -ne $fills -and $requests -gt 0) { $fillRate=[math]::Round($fills/$requests,4) }
  if ($as) { if ($null -ne $as.trades) { $closed=[int]$as.trades }; if ($null -ne $as.total_pnl) { $totalPnl=[double]$as.total_pnl }; if ($null -ne $as.max_drawdown) { $maxDD=[double]$as.max_drawdown } }
  if ($rr -and $null -ne $rr.estimated_orphan_fills_after_reconstruct) { $orphAfter=[int]$rr.estimated_orphan_fills_after_reconstruct } elseif ($rec -and $null -ne $rec.orphan_fills_count) { $orphAfter=[int]$rec.orphan_fills_count }
  $zipPath2 = $null; if ($zip) { $zipPath2 = $zip.FullName }
  return @{ status='OK'; outdir=$dest; zip=$zipPath2; run_metadata=$meta; reconstruct_report=$rr; reconcile_report=$rec; analysis_summary=$as; requests=$requests; fills=$fills; fill_rate=$fillRate; closed_trades_count=$closed; orphan_before=$orphBefore; orphan_after=$orphAfter; total_pnl=$totalPnl; max_drawdown=$maxDD }
}

# First attempt
$res = Invoke-OneRun -pretty 'realtime_1h' -sec $Seconds -sph $SecondsPerHour -fp $FillProb -dr $Drain -gr $Grace -baseOut $OutBase
if ($res.status -eq 'TIMEOUT') {
  $final = @{ STATUS='RUN_TIMEOUT'; branch=(git rev-parse --abbrev-ref HEAD 2>$null).Trim(); pr_url=("https://github.com/baosang12/BotG/pull/" + $PRNumber); runs=@(@{ name='realtime_1h'; outdir=$res.outdir; zip=$res.zip; status='FAILED'; notes=$res.note }) }
  Write-Json (Join-Path $OutBase 'final_report.json') $final
  $final | ConvertTo-Json -Depth 12 | Write-Output
  exit 0
}
if ($res.status -eq 'MISSING_ARTIFACTS') {
  $final = @{ STATUS='MISSING_ARTIFACTS'; branch=(git rev-parse --abbrev-ref HEAD 2>$null).Trim(); pr_url=("https://github.com/baosang12/BotG/pull/" + $PRNumber); runs=@(@{ name='realtime_1h'; outdir=$res.outdir; zip=$res.zip; status='FAILED'; notes=("Missing: " + ($res.missing -join ', ')) }) }
  Write-Json (Join-Path $OutBase 'final_report.json') $final
  $final | ConvertTo-Json -Depth 12 | Write-Output
  exit 0
}

$run = $res
if ($null -ne $run.orphan_after -and $run.orphan_after -gt 0) {
  # One remediation retry
  $res2 = Invoke-OneRun -pretty 'realtime_1h_retry' -sec $Seconds -sph $SecondsPerHour -fp $FillProb -dr ([int]($Drain+30)) -gr ([int]($Grace+5)) -baseOut $OutBase
  if ($res2.status -ne 'MISSING_ARTIFACTS' -and $res2.status -ne 'TIMEOUT') { $run = $res2 }
}

$status = if ($null -ne $run.orphan_after -and $run.orphan_after -eq 0) { 'SUCCESS' } else { 'FAILURE' }
$runStatus = if ($status -eq 'SUCCESS') { 'PASSED' } else { 'FAILED' }

$entry = [ordered]@{
  name = 'realtime_1h'
  outdir = $run.outdir
  zip = $run.zip
  status = $runStatus
  requests = $run.requests
  fills = $run.fills
  fill_rate = $run.fill_rate
  closed_trades_count = $run.closed_trades_count
  orphan_before = $run.orphan_before
  orphan_after = $run.orphan_after
  total_pnl = $run.total_pnl
  max_drawdown = $run.max_drawdown
  run_metadata = $run.run_metadata
  reconstruct_report = $run.reconstruct_report
  notes = ''
}
$final = [ordered]@{
  STATUS = $status
  branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim()
  pr_url = "https://github.com/baosang12/BotG/pull/$PRNumber"
  runs = @($entry)
  notes = ''
}
Write-Json (Join-Path $OutBase 'final_report.json') $final

# Attempt to comment on PR using gh if available
try {
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  if ($gh) {
    $tmp = Join-Path $OutBase 'pr_comment.md'
    $body = @()
    $body += "Automated realtime 1h smoke results:"; $body += ""; $body += ("OutDir: `"" + $run.outdir + "`"")
    if ($run.zip) { $body += ("Zip: `"" + $run.zip + "`"") }
    $body += ""; $body += "Final JSON:"; $body += '```json'; $body += ($final | ConvertTo-Json -Depth 12); $body += '```'
    Write-Text $tmp ($body -join "`n")
    & gh pr comment $PRNumber -F $tmp 2>$null | Out-Null
  }
} catch {}

# Also print to stdout for logs
$final | ConvertTo-Json -Depth 12 | Write-Output
