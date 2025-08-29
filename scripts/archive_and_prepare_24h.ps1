<#
Archive a completed realtime 1h artifact from TEMP to persistent storage, verify gates,
run Python telemetry analysis, report PVU/units, and start a supervised 24h paper run.

Usage (example):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\archive_and_prepare_24h.ps1 \
    -SourceZip "C:\Users\TechCare\AppData\Local\Temp\botg_artifacts\realtime_1h_20250825_160152\telemetry_run_20250825_160156\telemetry_run_20250825_160156.zip" \
    -DestRoot "D:\botg\runs" -PrNumber 14 -UseSimulation:$false -FillProbability 1.0 -Hours 24

This script is non-destructive: copies/expands but does not delete any temp files.
#>
[CmdletBinding()]
param(
  [string]$SourceZip = "C:\Users\TechCare\AppData\Local\Temp\botg_artifacts\realtime_1h_20250825_160152\telemetry_run_20250825_160156\telemetry_run_20250825_160156.zip",
  [string]$DestRoot = $env:BOTG_RUNS_ROOT,
  [string]$RepoRoot = $(Resolve-Path '.').Path,
  [int]$PrNumber = 14,
  [bool]$UseSimulation = $false,
  [double]$FillProbability = 1.0,
  [int]$Hours = 24,
  [int]$SecondsPerHour = 3600
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

function IsoNow() { (Get-Date).ToUniversalTime().ToString('o') }
function WriteUtf8([string]$p,[string]$t) { $enc=New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p,$t,$enc) }
function Q([string]$s){ '"' + $s + '"' }

# 1) Archive: copy zip to persistent dest and expand
if (-not (Test-Path -LiteralPath $SourceZip)) { Write-Error ("Source zip not found: " + $SourceZip); exit 1 }
if (-not (Test-Path -LiteralPath $DestRoot)) { New-Item -ItemType Directory -Path $DestRoot -Force | Out-Null }

$zipLeaf = Split-Path -Leaf $SourceZip
$m = [regex]::Match($zipLeaf, 'telemetry_run_(?<ts>\d{8}_\d{6})')
$ts = if ($m.Success) { $m.Groups['ts'].Value } else { (Get-Date).ToString('yyyyMMdd_HHmmss') }
$destFolder = Join-Path $DestRoot ("realtime_1h_" + $ts)
if (-not (Test-Path -LiteralPath $destFolder)) { New-Item -ItemType Directory -Path $destFolder -Force | Out-Null }

# Emit ACK for archive start (single line)
$iso = IsoNow
Write-Output ("ARCHIVE_STARTED: " + $iso + " - source: " + $SourceZip + " -> dest: " + $destFolder)

$destZip = Join-Path $destFolder $zipLeaf
try { Copy-Item -LiteralPath $SourceZip -Destination $destZip -Force } catch { Write-Error ("Copy zip failed: " + $_.Exception.Message); exit 1 }

# Expand
$expandDir = Join-Path $destFolder ("telemetry_run_" + $ts)
if (-not (Test-Path -LiteralPath $expandDir)) { New-Item -ItemType Directory -Path $expandDir -Force | Out-Null }
try {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  if (Get-ChildItem -LiteralPath $expandDir -Force | Measure-Object | Select-Object -ExpandProperty Count) { } # ensure exists
  # Use 2-arg overload for Windows PowerShell 5.1 compatibility
  [System.IO.Compression.ZipFile]::ExtractToDirectory($destZip, $expandDir)
} catch {
  $msg = $_.Exception.Message
  if ($msg -match 'already exists') {
    Write-Output ('NOTE: Extract skipped: ' + $msg)
  } else {
    Write-Error ("Extract failed: " + $msg)
    exit 1
  }
}

# Also copy final_report.json from outbase (two levels up from source zip)
$sourceOutBase = Split-Path -Parent (Split-Path -Parent $SourceZip)
$srcFinal = Join-Path $sourceOutBase 'final_report.json'
if (Test-Path -LiteralPath $srcFinal) {
  try { Copy-Item -LiteralPath $srcFinal -Destination (Join-Path $destFolder 'final_report.json') -Force } catch {}
}

# 2) Verify final_report.json and reconstruct gate
$finalPath = Join-Path $destFolder 'final_report.json'
if (-not (Test-Path -LiteralPath $finalPath)) { Write-Error "final_report.json not found in destination; cannot verify"; exit 1 }
$finalObj = $null
try { $finalObj = Get-Content -LiteralPath $finalPath -Raw | ConvertFrom-Json } catch { Write-Error "Invalid final_report.json"; exit 1 }
if ($finalObj.STATUS -ne 'SUCCESS') { Write-Error ("Final.status is not SUCCESS: " + $finalObj.STATUS); exit 1 }

# Determine orphan_after via final JSON or reconstruct_report.json in expanded dir
$orphanAfter = $null
if ($finalObj.runs -and $finalObj.runs.Count -gt 0 -and $finalObj.runs[0].reconstruct_report) {
  $rr = $finalObj.runs[0].reconstruct_report
  if ($null -ne $rr.orphan_after) { $orphanAfter = [int]$rr.orphan_after }
  elseif ($null -ne $rr.estimated_orphan_fills_after_reconstruct) { $orphanAfter = [int]$rr.estimated_orphan_fills_after_reconstruct }
}
if ($null -eq $orphanAfter) {
  $rrFile = Join-Path $expandDir 'reconstruct_report.json'
  if (Test-Path -LiteralPath $rrFile) {
    try {
      $rrObj = Get-Content -LiteralPath $rrFile -Raw | ConvertFrom-Json
      if ($null -ne $rrObj.orphan_after) { $orphanAfter = [int]$rrObj.orphan_after }
      elseif ($null -ne $rrObj.estimated_orphan_fills_after_reconstruct) { $orphanAfter = [int]$rrObj.estimated_orphan_fills_after_reconstruct }
    } catch {}
  }
}
if ($null -eq $orphanAfter) { Write-Error "Could not determine orphan_after from reports"; exit 1 }
if ($orphanAfter -ne 0) { Write-Error ("Gate failed: orphan_after = " + $orphanAfter); exit 1 }
Write-Output 'VERIFY_PASSED: STATUS=SUCCESS, orphan_after=0'

# 3) Python analysis from closed_trades_fifo_reconstructed.csv
$reconCsv = Join-Path $expandDir 'closed_trades_fifo_reconstructed.csv'
if (-not (Test-Path -LiteralPath $reconCsv)) {
  # Auto-reconstruct using Tools/ReconstructClosedTrades if available
  $orders = Join-Path $expandDir 'orders.csv'
  $toolProj = Join-Path $RepoRoot 'Tools\ReconstructClosedTrades\ReconstructClosedTrades.csproj'
  if (-not (Test-Path -LiteralPath $orders)) {
    Write-Error ("Missing closed_trades_fifo_reconstructed.csv and orders.csv not found at " + $orders)
    Write-Output "Manual reconstruct command (orders required):"
    $cmdMissing = '  dotnet run --project "{0}" -- --orders "{1}" --out "{2}"' -f $toolProj, '<orders.csv>', $reconCsv
    Write-Output $cmdMissing
    exit 1
  }
  if (-not (Test-Path -LiteralPath $toolProj)) {
    Write-Error ("Reconstruct tool project not found: " + $toolProj)
    Write-Output "Manual reconstruct commands (install .NET SDK if needed):"
    Write-Output ("  dotnet run --project `"" + $toolProj + "`" -- --orders `"" + $orders + "`" --out `"" + $reconCsv + "`"")
    exit 1
  }
  try {
    # Build once for clarity
    & dotnet build $toolProj -c Release 2>&1 | Out-Null
  } catch {}
  try {
    $reconOut = & dotnet run --project $toolProj -- --orders $orders --out $reconCsv 2>&1
  } catch {}
  if (-not (Test-Path -LiteralPath $reconCsv)) {
    Write-Error "Auto-reconstruct failed (closed_trades_fifo_reconstructed.csv not created)."
    Write-Output "Manual reconstruct commands (run in repo root):"
    $cmdManual = '  dotnet run --project "{0}" -- --orders "{1}" --out "{2}"' -f $toolProj, $orders, $reconCsv
    Write-Output $cmdManual
    exit 1
  }
}
$analysisDir = Join-Path $destFolder 'analysis'
if (-not (Test-Path -LiteralPath $analysisDir)) { New-Item -ItemType Directory -Path $analysisDir -Force | Out-Null }

$pyPath = Join-Path $analysisDir 'embedded_analysis.py'
$pyCode = @'
import sys, json, argparse
import pandas as pd
import math
from pathlib import Path

def find_col(cols, candidates):
  s = {c.lower(): c for c in cols}
  for cand in candidates:
    if cand.lower() in s:
      return s[cand.lower()]
  return None

ap = argparse.ArgumentParser()
ap.add_argument('--input', required=True)
ap.add_argument('--outdir', required=True)
args = ap.parse_args()
out = Path(args.outdir)
out.mkdir(parents=True, exist_ok=True)

df = pd.read_csv(args.input)
cols = list(df.columns)
ts_col = find_col(cols, ['timestamp','time','close_time','closed_time','close_timestamp'])
pnl_col = find_col(cols, ['pnl','profit','pnl_value','net_pnl'])
side_col = find_col(cols, ['side','direction','buy_sell','is_buy'])
price_f = find_col(cols, ['price_filled','filled_price','execution_price'])
price_r = find_col(cols, ['price_requested','requested_price','request_price'])

if pnl_col is None:
  # fallback to zero PnL if missing
  pnl_col = 'pnl_fallback'
  df[pnl_col] = 0.0

# Build a timestamp column
if ts_col is None:
  # synthesize timestamps if missing: 1-minute cadence ending now UTC
  n = len(df)
  if n > 0:
    try:
      end = pd.Timestamp.utcnow().tz_localize('UTC')
    except Exception:
      end = pd.Timestamp.utcnow()
    df['_ts'] = pd.date_range(end=end, periods=n, freq='T')
  else:
    df['_ts'] = pd.to_datetime([])
else:
  df['_ts'] = pd.to_datetime(df[ts_col], errors='coerce', utc=True)
df = df.dropna(subset=['_ts']).sort_values('_ts')

# Equity series
equity = df[['_ts', pnl_col]].copy()
equity['equity'] = equity[pnl_col].astype(float).cumsum()
equity.rename(columns={'_ts':'timestamp'}, inplace=True)
equity[['timestamp','equity']].to_csv(out / 'analysis_equity_series.csv', index=False)

# Per-hour aggregates
perh = df.copy()
perh['hour'] = perh['_ts'].dt.floor('H')
agg = perh.groupby('hour').agg(trades=('hour','size'), pnl=(pnl_col,'sum')).reset_index()
agg['requests'] = math.nan
agg['fills'] = math.nan
agg['fill_rate'] = math.nan
agg.rename(columns={'hour':'timestamp'}, inplace=True)
agg.to_csv(out / 'analysis_per_hour.csv', index=False)

# Summary stats
total_pnl = float(df[pnl_col].sum())
max_dd = 0.0
peak = float('-inf')
for v in equity['equity'].values:
  if v > peak:
    peak = v
  dd = v - peak
  if dd < max_dd:
    max_dd = dd
summary = {
  'total_pnl': total_pnl,
  'max_drawdown': max_dd,
  'closed_trades_count': int(len(df))
}
with open(out / 'analysis_summary_stats.json','w',encoding='utf-8') as f:
  json.dump(summary, f, ensure_ascii=False)

# Fill breakdowns if side exists
if side_col is not None:
  try:
    fb = df.groupby(side_col).size().reset_index(name='count')
    with open(out / 'analysis_fill_breakdown.json','w',encoding='utf-8') as f:
      json.dump(fb.to_dict(orient='records'), f, ensure_ascii=False)
  except Exception:
    with open(out / 'analysis_fill_breakdown.json','w',encoding='utf-8') as f:
      json.dump([], f)
  try:
    perh2 = df.copy(); perh2['hour'] = perh2['_ts'].dt.floor('H')
    fb2 = perh2.groupby(['hour', side_col]).size().reset_index(name='count')
    fb2.rename(columns={'hour':'timestamp'}, inplace=True)
    fb2.to_csv(out / 'fill_breakdown_by_hour.csv', index=False)
  except Exception:
    pd.DataFrame(columns=['timestamp','side','count']).to_csv(out / 'fill_breakdown_by_hour.csv', index=False)
else:
  with open(out / 'analysis_fill_breakdown.json','w',encoding='utf-8') as f:
    json.dump([], f)
  pd.DataFrame(columns=['timestamp','side','count']).to_csv(out / 'fill_breakdown_by_hour.csv', index=False)

# Fill rate by side placeholder (without requests we report counts)
if side_col is not None:
  fr = df.groupby(side_col).size().reset_index(name='fills')
  fr['requests'] = math.nan
  fr['fill_rate'] = math.nan
  fr.to_csv(out / 'fill_rate_by_side.csv', index=False)
else:
  pd.DataFrame(columns=['side','fills','requests','fill_rate']).to_csv(out / 'fill_rate_by_side.csv', index=False)

# Raw slips if columns exist
if price_f is not None and price_r is not None:
  slips = df[[ts_col, price_f, price_r]].copy()
  slips['slip'] = slips[price_f].astype(float) - slips[price_r].astype(float)
  slips.rename(columns={ts_col:'timestamp'}, inplace=True)
  slips.to_csv(out / 'raw_slips.csv', index=False)
else:
  pd.DataFrame(columns=['timestamp','price_filled','price_requested','slip']).to_csv(out / 'raw_slips.csv', index=False)
'@
WriteUtf8 $pyPath $pyCode

# If analysis outputs already exist, skip rerun to keep idempotent
$expected = @('analysis_equity_series.csv','analysis_per_hour.csv','analysis_summary_stats.json','analysis_fill_breakdown.json','fill_rate_by_side.csv','fill_breakdown_by_hour.csv','raw_slips.csv')
$already = $true
foreach($f in $expected){ if (-not (Test-Path -LiteralPath (Join-Path $analysisDir $f))) { $already = $false; break } }
if ($already) {
  Write-Output ("ANALYSIS_WRITTEN: " + $analysisDir)
  foreach($f in $expected){ $p = Join-Path $analysisDir $f; if (Test-Path -LiteralPath $p) { Write-Output (" - " + $p) } }
} else {

# Ensure pandas is available; attempt install if missing
$havePandas = $true
try {
  $null = & python -c "import pandas,sys; sys.stdout.write(pandas.__version__)" 2>$null
  if ($LASTEXITCODE -ne 0) { $havePandas = $false }
} catch { $havePandas = $false }
if (-not $havePandas) {
  try {
  $null = & python -m pip install --disable-pip-version-check --no-warn-script-location pandas 2>&1
  if ($LASTEXITCODE -ne 0) { throw "pip install pandas failed" }
  } catch {
  Write-Error "pandas not installed and auto-install failed."
  Write-Output "Manual commands:"
  Write-Output "  python -m pip install pandas"
  Write-Output "  python -u `"$pyPath`" --input `"$reconCsv`" --outdir `"$analysisDir`""
  exit 1
  }
}

# Run python analysis
$pyOk = $false
$pyOutput = ""
try {
  $pyOutput = & python -u $pyPath --input $reconCsv --outdir $analysisDir 2>&1
  if ($LASTEXITCODE -eq 0) { $pyOk = $true }
} catch { $pyOk = $false }
if (-not $pyOk) {
  Write-Error "Python analysis failed. See output below:"
  if ($pyOutput) { $pyOutput | Write-Output }
  Write-Output "Manual command: python -u `"$pyPath`" --input `"$reconCsv`" --outdir `"$analysisDir`""
  exit 1
} else {
  Write-Output ("ANALYSIS_WRITTEN: " + $analysisDir)
  foreach($f in 'analysis_equity_series.csv','analysis_per_hour.csv','analysis_summary_stats.json','analysis_fill_breakdown.json','fill_rate_by_side.csv','fill_breakdown_by_hour.csv','raw_slips.csv'){
  $p = Join-Path $analysisDir $f; if (Test-Path -LiteralPath $p) { Write-Output (" - " + $p) }
  }
}

}

# 4) PVU/units from run_metadata and risk_snapshots
$metaPath = Join-Path $expandDir 'run_metadata.json'
$asumPath = Join-Path $expandDir 'analysis_summary.json'
$totalPnl = $null
try {
  # prefer final report total_pnl
  if ($finalObj.runs -and $finalObj.runs.Count -gt 0) {
    if ($null -ne $finalObj.runs[0].total_pnl) { $totalPnl = [double]$finalObj.runs[0].total_pnl }
  }
} catch {}
if ($null -eq $totalPnl -and (Test-Path -LiteralPath $asumPath)) { try { $a = Get-Content -LiteralPath $asumPath -Raw | ConvertFrom-Json; if ($null -ne $a.total_pnl) { $totalPnl = [double]$a.total_pnl } } catch {} }

$pvl = $null; $pvu = $null
if (Test-Path -LiteralPath $metaPath) {
  try {
    $m = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json
    # common places
    if ($m.pointValuePerLot) { $pvl = [double]$m.pointValuePerLot }
    elseif ($m.extra -and $m.extra.pointValuePerLot) { $pvl = [double]$m.extra.pointValuePerLot }
    elseif ($m.config_snapshot -and $m.config_snapshot.execution -and $m.config_snapshot.execution.pointValuePerLot) { $pvl = [double]$m.config_snapshot.execution.pointValuePerLot }
    if ($m.pvu_used) { $pvu = [double]$m.pvu_used }
  } catch {}
}
$riskCsv = Join-Path $expandDir 'risk_snapshots.csv'
if (Test-Path -LiteralPath $riskCsv) {
  try {
    $rows = Import-Csv -LiteralPath $riskCsv
    if ($rows -and $rows.Count -gt 0) {
      $last = $rows[-1]
      foreach($k in $last.PSObject.Properties.Name){ if ($k -match 'pointValuePerLot|point_value_per_lot|pvu') { $v=$last.$k; if ($v -and -not $pvl -and $k -match 'point') { [double]$pvl = $v }; if ($v -and -not $pvu -and $k -match 'pvu') { [double]$pvu = $v } } }
    }
  } catch {}
}
if ($pvu -and -not $pvl) { $pvl = $pvu }
if ($pvl) { Write-Output ("PVU: pointValuePerLot=" + $pvl) } else { Write-Output "PVU: N/A" }
if ($null -ne $totalPnl -and $pvl) { $converted = [double]($totalPnl * $pvl); Write-Output ("total_pnl_in_currency = total_pnl * pointValuePerLot = " + $totalPnl + " * " + $pvl + " = " + $converted) }

# NOTE if previous run used simulation
try {
  if ($finalObj.runs -and $finalObj.runs.Count -gt 0 -and $finalObj.runs[0].run_metadata -and $finalObj.runs[0].run_metadata.config_snapshot -and $finalObj.runs[0].run_metadata.config_snapshot.simulation) {
    $fp = $finalObj.runs[0].run_metadata.config_snapshot.simulation.fill_probability
    if ($fp -gt 0) { Write-Output ("NOTE: previous run used simulation (fill_probability = " + $fp + "). For deterministic full-fill results we will set use_simulation=false / fill_probability=1.0 for the 24h run.") }
  }
} catch {}

# 5) Prepare and start 24h run (paper)
$starter = Join-Path $RepoRoot 'scripts/start_realtime_24h_supervised.ps1'
if (-not (Test-Path -LiteralPath $starter)) {
  Write-Output "Creating start_realtime_24h_supervised.ps1..."
  $starterBody = @'
param(
  [int]$Hours = 24,
  [int]$SecondsPerHour = 3600,
  [double]$FillProbability = 1.0,
  [switch]$UseSimulation,
  [int]$PRNumber = 0,
  [string]$OutBase = '',
  [switch]$WaitForFinish
)

$ErrorActionPreference = 'Stop'
function IsoNow(){ (Get-Date).ToUniversalTime().ToString("o") }
function Q([string]$s){ '"' + $s + '"' }
function WriteUtf8([string]$p,[string]$t){ $enc=New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p,$t,$enc) }

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')
$runSmoke = Join-Path $repoRoot 'scripts/run_smoke.ps1'
if (-not (Test-Path -LiteralPath $runSmoke)) { Write-Error 'Missing scripts/run_smoke.ps1'; exit 1 }

if (-not $OutBase -or $OutBase.Trim().Length -eq 0) {
  $runsRoot = if ($DestRoot) { $DestRoot } elseif ($env:BOTG_RUNS_ROOT) { $env:BOTG_RUNS_ROOT } else { 'D:\botg\runs' }
  $OutBase = Join-Path $runsRoot ("realtime_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss'))
}
New-Item -ItemType Directory -Path $OutBase -Force | Out-Null

$asciiBase = Join-Path $env:TEMP ("botg_artifacts_orchestrator_realtime_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $asciiBase -Force | Out-Null

$sec = [int]($Hours * $SecondsPerHour)
$log = Join-Path $OutBase ("run_smoke_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss') + '.log')
$err = Join-Path $OutBase ("run_smoke_24h_" + (Get-Date).ToString('yyyyMMdd_HHmmss') + '.err.log')

# Optional config override to disable simulation
$cfgPath = ''
if (-not $UseSimulation.IsPresent) {
  $cfgPath = Join-Path $asciiBase 'config.override.json'
  $cfg = @{ simulation = @{ enabled = $false; fill_probability = $FillProbability }; execution = @{ fee_per_trade = 0; fee_percent = 0; spread_pips = 0 }; seconds_per_hour = $SecondsPerHour; drain_seconds = 120 }
  $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 6), $enc)
}

$args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Q $runSmoke), '-Seconds',[string]$sec, '-ArtifactPath',(Q $asciiBase), '-FillProbability',[string]$FillProbability, '-DrainSeconds','120','-SecondsPerHour',[string]$SecondsPerHour, '-GracefulShutdownWaitSeconds','15')
if ($UseSimulation.IsPresent) { $args += '-UseSimulation' }
if ($cfgPath) { $args += @('-ConfigPath',(Q $cfgPath)) }

$p = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -RedirectStandardOutput $log -RedirectStandardError $err -PassThru -WorkingDirectory $repoRoot -WindowStyle Hidden

$ack = @{ STATUS='STARTED'; started= (IsoNow()); pid=$p.Id; outbase=$OutBase; ascii_temp=$asciiBase; hours=$Hours; seconds_per_hour=$SecondsPerHour; use_simulation=$UseSimulation.IsPresent; fill_probability=$FillProbability }
WriteUtf8 (Join-Path $OutBase 'final_report.json') ($ack | ConvertTo-Json -Depth 6)
Write-Output ("RUN24_STARTED: " + (IsoNow()) + " - pid: " + $p.Id + " - outbase: " + $OutBase)
($ack | ConvertTo-Json -Depth 6) | Write-Output

if ($WaitForFinish.IsPresent) {
  $deadline = (Get-Date).AddHours([double]$Hours + 1)
  while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 10 }
  # Copy back and synthesize a final report similar to 1h daemon
  $cand = Get-ChildItem -LiteralPath $asciiBase -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($cand) {
    $runDir = $cand.FullName
    $leaf = Split-Path -Leaf $runDir
    $dest = Join-Path $OutBase $leaf
    try { $robo = Get-Command robocopy -ErrorAction SilentlyContinue; if ($robo) { $null = & robocopy $runDir $dest *.* /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null } else { Copy-Item -Path (Join-Path $runDir '*') -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue } } catch {}
    # Gather metrics
    $rrp = Join-Path $dest 'reconstruct_report.json'
    $rr=null; $orphAfter=null
    if (Test-Path -LiteralPath $rrp) { try { $rr = Get-Content -LiteralPath $rrp -Raw | ConvertFrom-Json; if ($rr.orphan_after -ne $null) { $orphAfter = [int]$rr.orphan_after } elseif ($rr.estimated_orphan_fills_after_reconstruct -ne $null) { $orphAfter = [int]$rr.estimated_orphan_fills_after_reconstruct } } catch {} }
    $status = if ($orphAfter -eq 0) { 'SUCCESS' } else { 'FAILURE' }
    $final = @{ STATUS=$status; runs=@(@{ name='realtime_24h'; outdir=$dest; status=(if($status -eq 'SUCCESS'){'PASSED'}else{'FAILED'}); reconstruct_report=$rr }) }
    $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText((Join-Path $OutBase 'final_report.json'), ($final | ConvertTo-Json -Depth 8), $enc)
    ($final | ConvertTo-Json -Depth 8) | Write-Output
  }
}
'@
  WriteUtf8 $starter $starterBody
}

# Print configuration and the exact command to be executed
$cfgLine = "CONFIG: use_simulation=" + ($UseSimulation) + ", fill_probability=" + $FillProbability + ", SecondsPerHour=" + $SecondsPerHour + ", Hours=" + $Hours
Write-Output $cfgLine

# Build command args safely
$psArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $starter, '-Hours', [string]$Hours, '-SecondsPerHour', [string]$SecondsPerHour, '-FillProbability', [string]$FillProbability, '-PRNumber', [string]$PrNumber)
if ($UseSimulation) { $psArgs += '-UseSimulation' }
$cmdPrintable = 'powershell ' + ($psArgs | ForEach-Object { if ($_ -match '^[A-Za-z0-9_\-\.]+$') { $_ } else { '"' + $_ + '"' } } | Out-String).Trim()
Write-Output ("COMMAND: " + $cmdPrintable)

# Start 24h run
try {
  $out = & powershell @psArgs
  $out | Write-Output
} catch {
  Write-Error ("Failed to launch 24h run: " + $_.Exception.Message)
  Write-Output ("Manual run:")
  Write-Output $cmdPrintable
  exit 1
}

# Optional PR comment
try {
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  if ($gh) {
    $body = @()
    $body += "Archived artifact and started 24h paper run."
    $body += "Source zip: `"$SourceZip`""
    $body += "Dest: `"$destFolder`""
    $body += "Analysis: `"$analysisDir`""
    $tmp = Join-Path $destFolder 'archive_and_prepare_comment.md'
    WriteUtf8 $tmp ($body -join "`n")
    & gh pr comment $PrNumber -F $tmp 2>$null | Out-Null
  } else {
    Write-Output "gh CLI not available; comment body:"; Write-Output "Archived artifact and started 24h paper run. Source: $SourceZip Dest: $destFolder"
  }
} catch {}
