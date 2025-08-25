<#
Given an OutBase directory containing telemetry_run_*/, compute a final JSON summary and optionally comment on PR.

Params:
  -OutBase  : Path to base dir holding telemetry_run_*
  -PRNumber : PR to comment (default 12)
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)] [string]$OutBase,
  [int]$PRNumber = 12
)

$ErrorActionPreference = 'Stop'

function Write-Text([string]$p,[string]$t) { $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p, $t, $enc) }
function Write-Json([string]$p,[object]$o,[int]$d=12) { Write-Text $p ($o | ConvertTo-Json -Depth $d) }

try {
  if (-not (Test-Path -LiteralPath $OutBase)) { throw "OutBase not found: $OutBase" }
  $runDir = Get-ChildItem -LiteralPath $OutBase -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $runDir) {
    $final = @{ STATUS='MISSING_ARTIFACTS'; runs=@(@{ name='realtime_1h'; outdir=$OutBase; status='FAILED'; notes='No telemetry_run_* present' }) }
    Write-Json (Join-Path $OutBase 'final_report.json') $final
    $final | ConvertTo-Json -Depth 12 | Write-Output
    exit 0
  }

  $dest = $runDir.FullName
  $paths = @{ meta = Join-Path $dest 'run_metadata.json'; rr = Join-Path $dest 'reconstruct_report.json'; rec = Join-Path $dest 'reconcile_report.json'; as = Join-Path $dest 'analysis_summary.json' }
  $meta=$null;$rr=$null;$rec=$null;$as=$null
  if (Test-Path -LiteralPath $paths.meta) { try { $meta = Get-Content -LiteralPath $paths.meta -Raw | ConvertFrom-Json } catch {} }
  if (Test-Path -LiteralPath $paths.rr) { try { $rr = Get-Content -LiteralPath $paths.rr -Raw | ConvertFrom-Json } catch {} }
  if (Test-Path -LiteralPath $paths.rec) { try { $rec = Get-Content -LiteralPath $paths.rec -Raw | ConvertFrom-Json } catch {} }
  if (Test-Path -LiteralPath $paths.as) { try { $as = Get-Content -LiteralPath $paths.as -Raw | ConvertFrom-Json } catch {} }

  $requests=$null;$fills=$null;$fillRate=$null;$closed=$null;$orphBefore=$null;$orphAfter=$null;$totalPnl=$null;$maxDD=$null
  if ($rec) { if ($rec.request_count) { $requests=[int]$rec.request_count }; if ($rec.fill_count) { $fills=[int]$rec.fill_count }; if ($rec.closed_trades_count) { $closed=[int]$rec.closed_trades_count }; if ($null -ne $rec.orphan_fills_count) { $orphBefore=[int]$rec.orphan_fills_count } }
  if ($null -ne $requests -and $null -ne $fills -and $requests -gt 0) { $fillRate=[math]::Round($fills/$requests,4) }
  if ($as) { if ($null -ne $as.trades) { $closed=[int]$as.trades }; if ($null -ne $as.total_pnl) { $totalPnl=[double]$as.total_pnl }; if ($null -ne $as.max_drawdown) { $maxDD=[double]$as.max_drawdown } }
  if ($rr -and $null -ne $rr.estimated_orphan_fills_after_reconstruct) { $orphAfter=[int]$rr.estimated_orphan_fills_after_reconstruct } elseif ($rec -and $null -ne $rec.orphan_fills_count) { $orphAfter=[int]$rec.orphan_fills_count }

  $status = if ($null -ne $orphAfter -and $orphAfter -eq 0) { 'SUCCESS' } else { 'FAILURE' }
  $runStatus = if ($status -eq 'SUCCESS') { 'PASSED' } else { 'FAILED' }

  $entry = [ordered]@{
    name = 'realtime_1h'
    outdir = $dest
    zip = (Get-ChildItem -LiteralPath $dest -Filter '*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $_.FullName })
    status = $runStatus
    requests = $requests
    fills = $fills
    fill_rate = $fillRate
    closed_trades_count = $closed
    orphan_before = $orphBefore
    orphan_after = $orphAfter
    total_pnl = $totalPnl
    max_drawdown = $maxDD
    run_metadata = $meta
    reconstruct_report = $rr
    notes = ''
  }
  $final = [ordered]@{
    STATUS = $status
    branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim()
    pr_url = "https://github.com/baosang12/BotG/pull/$PRNumber"
    runs = @($entry)
    notes = ''
  }
  # If sentinel missing, downgrade informationally
  try {
    $flag = Join-Path $dest 'copy_complete.flag'
    if (-not (Test-Path -LiteralPath $flag)) { $final.notes = 'copy_complete.flag not found; artifacts may have been copied by fallback.' }
  } catch {}
  Write-Json (Join-Path $OutBase 'final_report.json') $final

  try {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
      $tmp = Join-Path $OutBase 'pr_comment.md'
      $body = @()
      $body += "Automated realtime 1h smoke results (finalized):"; $body += ""; $body += ("OutDir: `"" + $dest + "`"")
      $zipPath = $entry.zip; if ($zipPath) { $body += ("Zip: `"" + $zipPath + "`"") }
      $body += ""; $body += "Final JSON:"; $body += '```json'; $body += ($final | ConvertTo-Json -Depth 12); $body += '```'
      Write-Text $tmp ($body -join "`n")
      & gh pr comment $PRNumber -F $tmp 2>$null | Out-Null
    }
  } catch {}

  $final | ConvertTo-Json -Depth 12 | Write-Output
} catch {
  @{ STATUS='ERROR'; message=$_.Exception.Message; outdir=$OutBase } | ConvertTo-Json -Depth 12 | Write-Output
  exit 0
}
