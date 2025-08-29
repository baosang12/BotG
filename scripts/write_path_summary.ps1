[CmdletBinding()]
param(
  [string]$IssuesDir = 'path_issues',
  [string]$LogDir = $env:BOTG_LOG_PATH
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

function Ensure-Dir([string]$p) { if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }
function Read-TextSafe([string]$p) { if (Test-Path -LiteralPath $p) { try { return Get-Content -LiteralPath $p -Raw -ErrorAction Stop } catch { return '' } } else { return '' } }
function Tail-Text([string]$p,[int]$n=200) { if (Test-Path -LiteralPath $p) { try { return (Get-Content -LiteralPath $p -Tail $n -ErrorAction Stop) -join "`n" } catch { return '' } } else { return '' } }
function Head-Text([string]$p,[int]$n=50) { if (Test-Path -LiteralPath $p) { try { return (Get-Content -LiteralPath $p -TotalCount $n -ErrorAction Stop) -join "`n" } catch { return '' } } else { return '' } }
function WriteUtf8([string]$p,[string]$t){ $enc=New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p,$t,$enc) }

$repo = try { (Resolve-Path '.').Path } catch { (Get-Location).Path }
$issues = Join-Path $repo $IssuesDir
Ensure-Dir $issues

if (-not $LogDir) { $LogDir = ($env:BOTG_LOG_PATH ? $env:BOTG_LOG_PATH : 'D:\botg\logs') }

# 1) Path scan findings
$scanPath = Join-Path $issues 'findings_paths_to_fix.txt'
$scanText = Read-TextSafe $scanPath
$scanCount = 0
if ($scanText) {
  $scanCount = ($scanText -split "`n").Where({ $_.Trim().Length -gt 0 }).Count
}

# 2) Build/test output quick verdict
$buildLog = Join-Path $issues 'build_and_test_output.txt'
$buildText = Read-TextSafe $buildLog
$buildVerdict = 'UNKNOWN'
if ($buildText) {
  if ($buildText -match '(?im)^\s*Build succeeded\.?') { $buildVerdict = 'PASS' }
  elseif ($buildText -match '(?im)^\s*Build FAILED\.?') { $buildVerdict = 'FAIL' }
}

# 3) Latest smoke summary_smoke.json under LogDir/smoke_*
$smokeDir = $null
try {
  $smokeDir = Get-ChildItem -LiteralPath $LogDir -Directory -Filter 'smoke_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
} catch {}
$summaryObj = $null
if ($smokeDir) {
  $sumPath = Join-Path $smokeDir.FullName 'summary_smoke.json'
  if (Test-Path -LiteralPath $sumPath) {
    try { $summaryObj = Get-Content -LiteralPath $sumPath -Raw | ConvertFrom-Json } catch { $summaryObj = $null }
    if ($summaryObj) {
      Copy-Item -LiteralPath $sumPath -Destination (Join-Path $issues 'smoke_analysis_summary.json') -Force -ErrorAction SilentlyContinue
    }
  }
}

# 4) Orders warnings tail
$warnPath = Join-Path $LogDir 'orders_warnings.log'
$warnTail = Tail-Text $warnPath 200
if ($warnTail) { WriteUtf8 (Join-Path $issues 'orders_warnings_tail.txt') $warnTail }

# 5) Optional diagnostics from realtime runs if present
$diagPreview = ''
$closedHead = ''
try {
  $diag = Get-ChildItem -LiteralPath $repo -Recurse -Filter 'diagnostic_pnl_preview.csv' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($diag) { $diagPreview = Head-Text $diag.FullName 50; WriteUtf8 (Join-Path $issues 'diagnostic_preview_head.csv') $diagPreview }
} catch {}
try {
  $closed = Get-ChildItem -LiteralPath $repo -Recurse -Filter 'closed_trades_fifo*.csv' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($closed) { $closedHead = Head-Text $closed.FullName 50; WriteUtf8 (Join-Path $issues 'closed_trades_head.csv') $closedHead }
} catch {}

# 6) Summary report
$lines = @()
$lines += '# Path hardening verification summary'
$lines += ''
$lines += '## Scan results'
$lines += ('- findings_paths_to_fix.txt lines: ' + $scanCount)
$lines += ('- file: ' + $scanPath)
$lines += ''
$lines += '## Build/Test'
$lines += ('- verdict: ' + $buildVerdict)
$lines += ('- log: ' + $buildLog)
$lines += ''
$lines += '## Smoke run'
if ($summaryObj) {
  $counts = $summaryObj.counts
  $fillRate = $summaryObj.fill_rate
  $lines += ('- run_dir: ' + $summaryObj.run_dir)
  if ($counts) { $lines += ('- counts: REQUEST=' + $counts.REQUEST + ', ACK=' + $counts.ACK + ', FILL=' + $counts.FILL) }
  if ($null -ne $fillRate) { $lines += ('- fill_rate: ' + $fillRate) }
} else {
  $lines += '- summary_smoke.json not found.'
}
$lines += ('- log root: ' + $LogDir)
$lines += ''
$lines += '## Warnings'
if ($warnTail) { $lines += '- orders_warnings_tail.txt included'; } else { $lines += '- no warnings file found' }
$lines += ''
$lines += '## Optional diagnostics'
if ($diagPreview) { $lines += '- diagnostic_preview_head.csv included' } else { $lines += '- diagnostic preview not found' }
if ($closedHead) { $lines += '- closed_trades_head.csv included' } else { $lines += '- closed trades CSV not found' }

WriteUtf8 (Join-Path $issues 'summary_report.md') ($lines -join "`n")
Write-Host ('Wrote: ' + (Join-Path $issues 'summary_report.md')) -ForegroundColor Cyan
