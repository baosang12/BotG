<#
Runs a 15-minute realtime-like sample, validates orphan_after, optionally starts a 1-hour run in background,
archives artifacts under artifacts_ascii, and emits the final JSON per spec.

Usage examples:
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_realtime_pipeline.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run_realtime_pipeline.ps1 -RunFullIfPass:$false
#>
[CmdletBinding()]
param(
  [switch]$RunFullIfPass
)

$ErrorActionPreference = 'Stop'

function TimestampNow { (Get-Date).ToString('yyyyMMdd_HHmmss') }
function Utf8NoBom([string]$s) { [System.Text.Encoding]::UTF8.GetString(([System.Text.Encoding]::UTF8.GetBytes($s))) }
function Write-Text([string]$path,[string]$text) { $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($path, $text, $enc) }
function Write-JsonObj([string]$path,[object]$obj,[int]$depth=10) { Write-Text $path (($obj | ConvertTo-Json -Depth $depth)) }

# Resolve repo
$repoRoot = (Resolve-Path '.').Path
$scriptsDir = Join-Path $repoRoot 'scripts'
$runSmoke = Join-Path $scriptsDir 'run_smoke.ps1'
if (-not (Test-Path -LiteralPath $runSmoke)) { throw "Missing scripts/run_smoke.ps1" }
$artifactsBase = Join-Path $repoRoot 'artifacts_ascii'
if (-not (Test-Path -LiteralPath $artifactsBase)) { New-Item -ItemType Directory -Path $artifactsBase -Force | Out-Null }

# A) Branch and PR
$branch = 'unknown'
try { $branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim() } catch {}
$prUrl = 'https://github.com/baosang12/BotG/pull/12'

# B) Build & tests
try {
  & dotnet build (Join-Path $repoRoot 'BotG.sln') -c Release | Out-Null
  $testOut = & dotnet test (Join-Path $repoRoot 'BotG.sln') --no-build --verbosity minimal 2>&1
  if ($LASTEXITCODE -ne 0) {
    $tail = ($testOut | Select-Object -Last 200) -join "`n"
    $obj = @{ STATUS = 'UNIT_TESTS_FAILED'; branch = $branch; pr_url = $prUrl; notes = $tail }
    $obj | ConvertTo-Json -Depth 6 | Write-Output
    exit 0
  }
} catch {
  $obj = @{ STATUS = 'UNIT_TESTS_FAILED'; branch = $branch; pr_url = $prUrl; notes = $_.Exception.Message }
  $obj | ConvertTo-Json -Depth 6 | Write-Output
  exit 0
}

function Invoke-SmokeRun([string]$pretty,[int]$seconds,[int]$secondsPerHour,[double]$fillProb,[int]$drain,[int]$grace,[string]$baseOut,[string]$topupsCsv='') {
  New-Item -ItemType Directory -Path $baseOut -Force | Out-Null
  $asciiBase = Join-Path $env:TEMP ("botg_artifacts_orchestrator_" + $pretty + '_' + (TimestampNow))
  New-Item -ItemType Directory -Path $asciiBase -Force | Out-Null
  $log = Join-Path $baseOut ("run_smoke_" + $pretty + '_' + (TimestampNow) + '.log')
  $err = Join-Path $baseOut ("run_smoke_" + $pretty + '_' + (TimestampNow) + '.err.log')
  $qRun = '"' + $runSmoke + '"'
  $psArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$qRun,
    '-Seconds',[string]$seconds,
    '-ArtifactPath',$asciiBase,
    '-FillProbability',[string]$fillProb,
    '-DrainSeconds',[string]$drain,
    '-SecondsPerHour',[string]$secondsPerHour,
    '-GracefulShutdownWaitSeconds',[string]$grace,
    '-UseSimulation')
  if ($topupsCsv -and $topupsCsv.Trim().Length -gt 0) { $psArgs += @('-TopUpSeconds',$topupsCsv) }
  $p = Start-Process -FilePath 'powershell' -ArgumentList $psArgs -RedirectStandardOutput $log -RedirectStandardError $err -PassThru -WindowStyle Hidden
  $p.WaitForExit()
  $runDir = $null
  try {
    $cand = Get-ChildItem -LiteralPath $asciiBase -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($cand) { $runDir = $cand.FullName }
  } catch {}
  if (-not $runDir) { throw "Run output directory not found under $asciiBase" }
  # Copy back
  $leaf = Split-Path -Leaf $runDir
  $dest = Join-Path $baseOut $leaf
  if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
  Copy-Item -Path (Join-Path $runDir '*') -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue
  # Ensure zip exists
  $zip = Get-ChildItem -LiteralPath $dest -Filter '*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $zip) {
    try { Add-Type -AssemblyName System.IO.Compression.FileSystem; $z = Join-Path $dest ((Split-Path -Leaf $dest) + '.zip'); if (Test-Path -LiteralPath $z) { Remove-Item -LiteralPath $z -Force }; [System.IO.Compression.ZipFile]::CreateFromDirectory($dest, $z); $zip = Get-Item -LiteralPath $z } catch {}
  }
  # Load metrics
  $paths = @{ meta = Join-Path $dest 'run_metadata.json'; rr = Join-Path $dest 'reconstruct_report.json'; rec = Join-Path $dest 'reconcile_report.json'; as = Join-Path $dest 'analysis_summary.json' }
  $missing = @(); foreach ($p in @($paths.meta, (Join-Path $dest 'orders.csv'))) { if (-not (Test-Path -LiteralPath $p)) { $missing += $p } }
  if ($missing.Count -gt 0) { return @{ status='MISSING'; outdir=$dest; zip=($zip?.FullName); missing=$missing; run_metadata=$null; reconstruct_report=$null; reconcile_report=$null; analysis_summary=$null } }
  $meta=$null;$rr=$null;$rec=$null;$as=$null
  try { $meta = Get-Content -LiteralPath $paths.meta -Raw | ConvertFrom-Json } catch {}
  if (Test-Path -LiteralPath $paths.rr) { try { $rr = Get-Content -LiteralPath $paths.rr -Raw | ConvertFrom-Json } catch {} }
  if (Test-Path -LiteralPath $paths.rec) { try { $rec = Get-Content -LiteralPath $paths.rec -Raw | ConvertFrom-Json } catch {} }
  if (Test-Path -LiteralPath $paths.as) { try { $as = Get-Content -LiteralPath $paths.as -Raw | ConvertFrom-Json } catch {} }
  # Derive
  $requests=$null;$fills=$null;$fillRate=$null;$closed=$null;$orphBefore=$null;$orphAfter=$null;$totalPnl=$null;$maxDD=$null
  if ($rec) { if ($rec.request_count) { $requests=[int]$rec.request_count }; if ($rec.fill_count) { $fills=[int]$rec.fill_count }; if ($rec.closed_trades_count) { $closed=[int]$rec.closed_trades_count }; if ($rec.orphan_fills_count -ne $null) { $orphBefore=[int]$rec.orphan_fills_count } }
  if ($null -ne $requests -and $null -ne $fills -and $requests -gt 0) { $fillRate=[math]::Round($fills/$requests,4) }
  if ($as) { if ($null -ne $as.trades) { $closed=[int]$as.trades }; if ($null -ne $as.total_pnl) { $totalPnl=[double]$as.total_pnl }; if ($null -ne $as.max_drawdown) { $maxDD=[double]$as.max_drawdown } }
  if ($rr -and $null -ne $rr.estimated_orphan_fills_after_reconstruct) { $orphAfter=[int]$rr.estimated_orphan_fills_after_reconstruct } elseif ($rec -and $null -ne $rec.orphan_fills_count) { $orphAfter=[int]$rec.orphan_fills_count }
  return @{ status='OK'; outdir=$dest; zip=($zip?.FullName); run_metadata=$meta; reconstruct_report=$rr; reconcile_report=$rec; analysis_summary=$as; requests=$requests; fills=$fills; fill_rate=$fillRate; closed_trades_count=$closed; orphan_before=$orphBefore; orphan_after=$orphAfter; total_pnl=$totalPnl; max_drawdown=$maxDD }
}

# C) Settings
$ts = TimestampNow
$sampleOut = Join-Path $artifactsBase ("paper_run_realtime_sample_15m_" + $ts)
$fullOut = Join-Path $artifactsBase ("paper_run_realtime_1h_" + $ts)

# D/E/F) Run sample 15m and auto-remediate once if needed
$res = Invoke-SmokeRun -pretty 'realtime_sample' -seconds 900 -secondsPerHour 3600 -fillProb 0.9 -drain 60 -grace 10 -baseOut $sampleOut
if ($res.status -eq 'MISSING') {
  $obj = @{ STATUS='MISSING_ARTIFACTS'; branch=$branch; pr_url=$prUrl; runs=@(@{ name='realtime_sample_15m'; outdir=$res.outdir; zip=$res.zip; status='FAILED'; notes=("Missing: " + ($res.missing -join ', ')) }); notes='Artifacts missing; aborting.' }
  $obj | ConvertTo-Json -Depth 10 | Write-Output; exit 0
}

$sample = $res
if ($null -ne $sample.orphan_after -and $sample.orphan_after -gt 0) {
  # remediation: re-run with greater drain/grace and top-ups
  $res2 = Invoke-SmokeRun -pretty 'realtime_sample_retry' -seconds 900 -secondsPerHour 3600 -fillProb 0.9 -drain 120 -grace 15 -baseOut $sampleOut -topupsCsv '7,9,11,15,20'
  if ($res2.status -ne 'MISSING') { $sample = $res2 }
}

$sampleStatus = if ($sample.orphan_after -eq 0) { 'PASSED' } else { 'FAILED' }

# H) Optionally start full 1h in background only if sample passed
  $fullNote = ''
if ($RunFullIfPass -and $sampleStatus -eq 'PASSED') {
  try {
    New-Item -ItemType Directory -Path $fullOut -Force | Out-Null
    $asciiBase = Join-Path $env:TEMP ("botg_artifacts_orchestrator_realtime_1h_" + (TimestampNow))
    New-Item -ItemType Directory -Path $asciiBase -Force | Out-Null
    $log = Join-Path $fullOut ("run_smoke_realtime1h_" + (TimestampNow) + '.log')
    $err = Join-Path $fullOut ("run_smoke_realtime1h_" + (TimestampNow) + '.err.log')
    $qRun = '"' + $runSmoke + '"'
  $psArgs2 = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$qRun,
      '-Seconds','3600','-ArtifactPath',$asciiBase,'-FillProbability','0.9','-DrainSeconds','60','-SecondsPerHour','3600','-GracefulShutdownWaitSeconds','10','-UseSimulation')
  $null = Start-Process -FilePath 'powershell' -ArgumentList $psArgs2 -RedirectStandardOutput $log -RedirectStandardError $err -PassThru -WindowStyle Hidden
    $fullNote = "Full 1h run started in background. Monitor logs: $log"
  } catch { $fullNote = 'Failed to start full run: ' + $_.Exception.Message }
}

# I) Zip (sample dir already zipped by run_smoke; ensure base out zip)
try {
  $rd = $sample.outdir; $zip = Join-Path (Split-Path -Parent $rd) ((Split-Path -Leaf $rd) + '.zip');
  Add-Type -AssemblyName System.IO.Compression.FileSystem; if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }; [System.IO.Compression.ZipFile]::CreateFromDirectory($rd, $zip)
} catch {}

# Prepare sample run JSON entry
$sampleEntry = [ordered]@{
  name = 'realtime_sample_15m'
  outdir = $sample.outdir
  zip = $sample.zip
  status = $sampleStatus
  requests = $sample.requests
  fills = $sample.fills
  fill_rate = $sample.fill_rate
  closed_trades_count = $sample.closed_trades_count
  orphan_before = $sample.orphan_before
  orphan_after = $sample.orphan_after
  total_pnl = $sample.total_pnl
  max_drawdown = $sample.max_drawdown
  run_metadata = $sample.run_metadata
  reconstruct_report = $sample.reconstruct_report
  notes = $fullNote
}

# J) Final JSON
$status = if ($sampleStatus -eq 'PASSED') { 'SUCCESS' } else { 'FAILURE' }
$final = [ordered]@{
  STATUS = $status
  branch = $branch
  pr_url = $prUrl
  runs = @($sampleEntry)
  notes = if ($fullNote) { $fullNote } else { '' }
}
$final | ConvertTo-Json -Depth 12 | Write-Output
