param(
  [Parameter(Mandatory=$false)] [string] $ArtifactPath = ".\artifacts\telemetry_run_20250819_154459",
  [Parameter(Mandatory=$false)] [int] $ChunkSize = 100000
)

$ErrorActionPreference = 'Stop'

# Ensure UTF-8 for console and file outputs
try {
  [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
  [Console]::InputEncoding  = [System.Text.Encoding]::UTF8
} catch {}
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'

function Write-Log {
  param([string]$Message,[string]$Level='INFO')
  $ts = (Get-Date).ToUniversalTime().ToString("s") + 'Z'
  $line = "[$ts][$Level] $Message"
  Write-Host $line
  if ($global:LogPath) { Add-Content -Path $global:LogPath -Value $line -Encoding utf8 }
}

function Fail {
  param([int]$Code,[string]$Message,[string]$Step)
  Write-Log $Message 'ERROR'
  if ($Step -and $global:LogDir) { "$Message" | Out-File -FilePath (Join-Path $global:LogDir ("run_error_" + $Step + ".log")) -Encoding utf8 }
  # Print first 200 lines of relevant logs to console on failure
  try {
    if ($Step -eq 'compute') {
      $clog = Join-Path $global:LogDir 'run_compute_stream.log'
      if (Test-Path $clog) {
        Write-Host "--- compute log (first 200 lines) ---"
        Get-Content -LiteralPath $clog -TotalCount 200 | Write-Host
      }
    }
    elseif ($Step -eq 'reconcile') {
      $rlog = Join-Path $global:LogDir 'reconcile_fix_report.json'
      if (Test-Path $rlog) { Write-Host (Get-Content -LiteralPath $rlog -Raw) }
    }
    else {
      if (Test-Path $global:LogPath) {
        Write-Host "--- run_full_stream.log (first 200 lines) ---"
        Get-Content -LiteralPath $global:LogPath -TotalCount 200 | Write-Host
      }
    }
  } catch {}
  exit $Code
}

# Preserve the original (possibly relative, ASCII-safe) path for child processes.
$ArtifactArg = $ArtifactPath
try {
  $ArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
} catch {}

$global:LogDir = $ArtifactPath
$global:LogPath = Join-Path $ArtifactPath 'run_full_stream.log'
Remove-Item -LiteralPath $global:LogPath -ErrorAction SilentlyContinue
Write-Log "Artifact: $ArtifactPath"

# Preflight
try {
  $scriptsOk = @('.\\scripts\\make_closes_from_reconstructed.py','.\\scripts\\reconcile.py','.\\scripts\\compute_fill_breakdown_stream.py') | ForEach-Object { Test-Path $_ }
  if ($scriptsOk -contains $false) { Fail -Code 30 -Message 'Missing required scripts in scripts/ folder.' -Step 'preflight' }
  $orders = Join-Path $ArtifactPath 'orders.csv'
  if (-not (Test-Path -LiteralPath $orders)) { Fail -Code 30 -Message "orders.csv not found: $orders" -Step 'preflight' }
  if ((Get-Item -LiteralPath $orders).Length -le 0) { Fail -Code 30 -Message "orders.csv is empty: $orders" -Step 'preflight' }
} catch {
  Fail -Code 30 -Message ("Preflight error: " + $_.Exception.Message) -Step 'preflight'
}

# Backup
try {
  $Backup = "${ArtifactPath}_backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
  Write-Log "Backup -> $Backup"
  Copy-Item -LiteralPath $ArtifactPath -Destination $Backup -Recurse -Force
} catch {
  Fail -Code 30 -Message ("Backup failed: " + $_.Exception.Message) -Step 'backup'
}

$py = 'python'

# Step 1: make closes from reconstructed
try {
  Write-Log 'Run make_closes_from_reconstructed.py'
  & $py .\scripts\make_closes_from_reconstructed.py --artifact $ArtifactArg 2>&1 | Tee-Object -FilePath (Join-Path $ArtifactPath 'make_closes_run.log')
} catch {
  Fail -Code 30 -Message ("make_closes_from_reconstructed failed: " + $_.Exception.Message) -Step 'make_closes'
}

# Step 2: reconcile
$closedCsv = Join-Path $ArtifactPath 'closed_trades_fifo_reconstructed_cleaned.csv'
$closesCsv = Join-Path $ArtifactPath 'trade_closes_like_from_reconstructed.csv'
$closesJsonl = Join-Path $ArtifactPath 'trade_closes_like_from_reconstructed.jsonl'
$reconOut = Join-Path $ArtifactPath 'reconcile_fix_report.json'
$reconOutJsonl = Join-Path $ArtifactPath 'reconcile_fix_report_jsonl.json'

try {
  Write-Log 'Run reconcile (CSV)'
  & $py .\scripts\reconcile.py --closed $closedCsv --closes $closesCsv --risk (Join-Path $ArtifactPath 'risk_snapshots.csv') 2>&1 | Tee-Object -FilePath $reconOut
} catch {
  Write-Log ("reconcile CSV failed: " + $_.Exception.Message) 'WARN'
}

$closed_sum = $null; $closes_sum = $null
try {
  $r = Get-Content -LiteralPath $reconOut -Raw | ConvertFrom-Json
  $closed_sum = $r.closed_sum; $closes_sum = $r.closes_sum
} catch {}

if (-not $closes_sum -or [string]$closes_sum -eq '0' -or [string]$closes_sum -eq '0.0') {
  try {
    Write-Log 'Run reconcile (JSONL fallback)'
    & $py .\scripts\reconcile.py --closed $closedCsv --closes $closesJsonl --risk (Join-Path $ArtifactPath 'risk_snapshots.csv') 2>&1 | Tee-Object -FilePath $reconOutJsonl
    $r = Get-Content -LiteralPath $reconOutJsonl -Raw | ConvertFrom-Json
    $closed_sum = $r.closed_sum; $closes_sum = $r.closes_sum
  } catch {
    Write-Log ("reconcile JSONL failed: " + $_.Exception.Message) 'WARN'
  }
}

if ($null -eq $closed_sum -or $null -eq $closes_sum) {
  Fail -Code 10 -Message 'Reconcile did not produce both sums.' -Step 'reconcile'
}

$diff = [double]$closed_sum - [double]$closes_sum
if ($diff -ne 0) {
  Write-Log ("Reconcile mismatch: diff=" + $diff) 'ERROR'
  # Continue to compute but exit with 10 at end
}

# Step 3: streaming compute
$computeLog = Join-Path $ArtifactPath 'run_compute_stream.log'
$summaryCompute = Join-Path $ArtifactPath 'analysis_summary_stats.json'
Write-Log "Run compute_fill_breakdown_stream.py chunksize=$ChunkSize"
& $py .\scripts\compute_fill_breakdown_stream.py --orders $orders --outdir $ArtifactArg --chunksize $ChunkSize 2>&1 | Tee-Object -FilePath $computeLog
$computeExit = $LASTEXITCODE
if ($computeExit -ne 0) { Fail -Code 20 -Message 'Compute failed.' -Step 'compute' }

# Step 4: Compose final summary JSON
$cleaned_rows = 0
try { $cleaned_rows = (Import-Csv -LiteralPath $closedCsv).Count } catch {}
$dup_groups = 0
try { if (Test-Path (Join-Path $ArtifactPath 'duplicate_groups_from_reconstructed.csv')) { $dup_groups = (Import-Csv -LiteralPath (Join-Path $ArtifactPath 'duplicate_groups_from_reconstructed.csv')).Count } } catch {}

$chunks_processed = $null; $total_rows = $null; $elapsed_seconds = $null
try {
  $cobj = Get-Content -LiteralPath $summaryCompute -Raw | ConvertFrom-Json
  $chunks_processed = $cobj.chunks_processed
  $total_rows = $cobj.rows
  $elapsed_seconds = $cobj.elapsed_seconds
} catch {}

$produced = @()
foreach ($f in 'fill_rate_by_side.csv','fill_breakdown_by_hour.csv','analysis_summary_stats.json','reconcile_fix_report.json','reconcile_fix_report_jsonl.json','trade_closes_like_from_reconstructed.csv','trade_closes_like_from_reconstructed.jsonl','closed_trades_fifo_reconstructed_cleaned.csv') {
  $p = Join-Path $ArtifactPath $f
  if (Test-Path $p) { $produced += $p }
}

$logs = @()
foreach ($f in 'run_full_stream.log','make_closes_run.log','run_compute_stream.log') { $p = Join-Path $ArtifactPath $f; if (Test-Path $p) { $logs += $p } }

$summary = [ordered]@{
  closed_sum = [double]$closed_sum
  closes_sum = [double]$closes_sum
  diff = [double]$diff
  cleaned_rows = [int]$cleaned_rows
  duplicate_groups = [int]$dup_groups
  chunks_processed = $chunks_processed
  total_orders_rows = $total_rows
  elapsed_seconds = $elapsed_seconds
  produced_files = $produced
  logs = $logs
}

$summaryPath = Join-Path $ArtifactPath 'auto_reconcile_compute_summary.json'
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Write-Log (Get-Content -LiteralPath $summaryPath -Raw)

if ($diff -ne 0) { exit 10 }
exit 0
$orders = Join-Path $art 'orders.csv'
$computeLog = Join-Path $art 'compute_stream_run.log'
$chunksProcessed = 0; $totalRows = 0
$computeStatus = 0
$start = Get-Date
try {
  $cmd = "-u .\scripts\compute_fill_breakdown_stream.py --orders `"$orders`" --outdir `"$art`" --chunksize $ChunkSize"
  & $py $cmd 2>&1 | Tee-Object -FilePath $computeLog
  # Read summary JSON produced
  if (Test-Path (Join-Path $art 'analysis_summary_stats.json')) {
    $st = Get-Content (Join-Path $art 'analysis_summary_stats.json') -Raw | ConvertFrom-Json
    $chunksProcessed = [int]$st.chunks_processed
    $totalRows = [int]$st.total_orders_rows
  }
} catch {
  $computeStatus = 20
}
$elapsed = [int]((Get-Date) - $start).TotalSeconds

# 5) summary and exit code
$diff = $null
if (($null -ne $closedSum) -and ($null -ne $closesSum)) { $diff = [double]$closedSum - [double]$closesSum }
$exitCode = 0
if (($null -ne $diff) -and ([math]::Abs($diff) -gt 1e-9)) { $exitCode = 10 }
if ($computeStatus -ne 0) { $exitCode = 20 }

$cleanedRows = 0; $dupGroups = 0
if (Test-Path (Join-Path $art 'closed_trades_fifo_reconstructed_cleaned.csv')) { try { $cleanedRows = (Import-Csv (Join-Path $art 'closed_trades_fifo_reconstructed_cleaned.csv')).Count } catch {} }
if (Test-Path (Join-Path $art 'duplicate_groups_from_reconstructed.csv')) { try { $dupGroups = (Import-Csv (Join-Path $art 'duplicate_groups_from_reconstructed.csv')).Count } catch {} }

$produced = @()
foreach ($f in 'fill_rate_by_side.csv','fill_breakdown_by_hour.csv','analysis_summary_stats.json','reconcile_fix_report.json','reconcile_fix_report_jsonl.json','make_closes_run.log','compute_stream_run.log','compute_stream_log.jsonl') {
  $p = Join-Path $art $f
  if (Test-Path $p) { $produced += $p }
}

$summary = [ordered]@{
  artifact = $art
  closed_sum = $closedSum
  closes_sum = $closesSum
  closed_vs_closes_diff = $diff
  cleaned_rows = $cleanedRows
  duplicate_groups = $dupGroups
  chunks_processed = $chunksProcessed
  total_orders_rows = $totalRows
  elapsed_seconds = $elapsed
  produced_files = $produced
  logs = @($logFile,$makeLog,$computeLog)
}
$summary | ConvertTo-Json -Depth 6 | Tee-Object -FilePath $summaryFile
"[{0}] DONE exit={1} summary={2}" -f (Get-Date), $exitCode, ($summary | ConvertTo-Json -Depth 3 -Compress) | TeeLog
exit $exitCode
