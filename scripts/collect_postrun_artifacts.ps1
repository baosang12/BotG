<#
Collect and package postrun artifacts, ensure log ACLs, optional upload, and write a quick summary.

Inputs (via environment variables):
  - BOTG_ROOT (required)
  - BOTG_LOG_PATH (required)
  - UPLOAD_URL or UPLOAD_DEST (optional; if not set, upload is skipped)
  - UPLOAD_CREDENTIALS (optional)
  - ARTIFACTS_OUT (optional) default: $env:BOTG_ROOT\path_issues\postrun_artifacts_<ts>.zip

Behavior:
  - Creates path_issues\collected_artifacts under BOTG_ROOT and copies key files
  - Zips artifacts and writes path_issues\postrun_summary.txt
  - Grants read/list permissions on BOTG_LOG_PATH to TARGET_ACCOUNT (env TARGET_ACCOUNT or default 'Users')
  - Attempts upload if UPLOAD_URL is provided; errors are logged and do not delete the local ZIP
#>

param(
  [string]$BotgRoot = $env:BOTG_ROOT,
  [string]$LogPath = $env:BOTG_LOG_PATH,
  [string]$UploadUrl = $env:UPLOAD_URL,
  [string]$UploadDest = $env:UPLOAD_DEST,
  # Note: accept as string header value (e.g., 'Bearer <token>'); for stronger security, switch to PSCredential/SecureString.
  [string]$UploadCreds = $env:UPLOAD_CREDENTIALS,
  [string]$ArtifactsOut = $env:ARTIFACTS_OUT
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host ("[INFO] " + $msg) }
function Write-Warn($msg) { Write-Warning $msg }
function Write-Err($msg) { Write-Error $msg }

if (-not $BotgRoot) { throw "BOTG_ROOT not set (env:BOTG_ROOT)" }
if (-not (Test-Path -LiteralPath $BotgRoot)) { throw "BOTG_ROOT not found: $BotgRoot" }
if (-not $LogPath) { throw "BOTG_LOG_PATH not set (env:BOTG_LOG_PATH)" }
if (-not (Test-Path -LiteralPath $LogPath)) { New-Item -ItemType Directory -Path $LogPath -Force | Out-Null }

Write-Info "BOTG_ROOT=$BotgRoot"
Write-Info "BOTG_LOG_PATH=$LogPath"

# Ensure path_issues exists
$pathIssues = Join-Path $BotgRoot 'path_issues'
if (-not (Test-Path -LiteralPath $pathIssues)) { New-Item -ItemType Directory -Path $pathIssues -Force | Out-Null }

# 2) Collect artifacts
$t = Join-Path $pathIssues 'collected_artifacts'
Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $t -Force | Out-Null

# Copy known key files from repo
$repoCopy = @(
  'path_issues\build_and_test_output.txt',
  'path_issues\findings_paths_to_fix.txt',
  'scripts\write_path_summary.ps1',
  'scripts\smoke_collect_and_summarize.ps1'
)
foreach ($rel in $repoCopy) {
  $src = Join-Path $BotgRoot $rel
  if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $t (Split-Path -Leaf $rel)) -Force }
}

# Include any smoke_*.zip and smoke_* summaries from LogPath
Get-ChildItem -LiteralPath $LogPath -Filter 'smoke_*.zip' -File -ErrorAction SilentlyContinue | ForEach-Object {
  Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $t $_.Name) -Force
}
Get-ChildItem -LiteralPath $LogPath -Directory -Filter 'smoke_*' -ErrorAction SilentlyContinue | ForEach-Object {
  $sum = Join-Path $_.FullName 'summary_smoke.json'
  if (Test-Path -LiteralPath $sum) { Copy-Item -LiteralPath $sum -Destination (Join-Path $t (Split-Path -Leaf $sum)) -Force }
}

# Copy latest telemetry run core files from LogPath\artifacts\telemetry_run_*
function Get-LatestRunDir([string]$base) {
  $art = Join-Path $base 'artifacts'
  if (Test-Path -LiteralPath $art) {
    $runs = Get-ChildItem -LiteralPath $art -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Descending
    if ($runs -and $runs.Count -gt 0) { return $runs[0].FullName }
  }
  return $null
}
$latestRun = Get-LatestRunDir -base $LogPath
if ($latestRun) { Write-Info "Latest runDir: $latestRun" }

$coreFiles = 'orders.csv','closed_trades_fifo.csv','telemetry.csv','run_metadata.json','trade_closes.log','analysis_summary.json','reconcile_report.json','monitoring_summary.json','size_comparison.json'
foreach ($fn in $coreFiles) {
  $src = if ($latestRun) { Join-Path $latestRun $fn } else { Join-Path $LogPath $fn }
  if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $t $fn) -Force }
}

# Always include risk_snapshots from base log path
$risk = Join-Path $LogPath 'risk_snapshots.csv'
if (Test-Path -LiteralPath $risk) { Copy-Item -LiteralPath $risk -Destination (Join-Path $t 'risk_snapshots.csv') -Force }

# Optionally copy entire logs tree (can be large)
$logCopyDest = Join-Path $t 'logs'
try { Copy-Item -LiteralPath $LogPath -Destination $logCopyDest -Recurse -Force -ErrorAction SilentlyContinue } catch {}

# 3) Check required presence
$required = @('findings_paths_to_fix.txt','build_and_test_output.txt')
$foundMissing = @()
foreach ($r in $required) {
  $p = Join-Path $t $r
  if (-not (Test-Path -LiteralPath $p)) { $foundMissing += $r }
}
if ($foundMissing.Count -gt 0) { Write-Warn ("Missing required artifacts: " + ($foundMissing -join ', ')) } else { Write-Info "All required artifacts present." }

# 4) Zip with timestamp
$ts = (Get-Date).ToString('yyyyMMdd_HHmmss')
if (-not $ArtifactsOut) { $ArtifactsOut = Join-Path $pathIssues ("postrun_artifacts_" + $ts + ".zip") }
if (Test-Path -LiteralPath $ArtifactsOut) { Remove-Item -LiteralPath $ArtifactsOut -Force }
Compress-Archive -Path (Join-Path $t '*') -DestinationPath $ArtifactsOut -Force
Write-Info ("Created zip: " + $ArtifactsOut)

# 5) Ensure ACLs
$target = if ($env:TARGET_ACCOUNT) { $env:TARGET_ACCOUNT } else { 'Users' }
try {
  icacls $LogPath /grant `"$target:(R,WDAC)`" /T | Out-Null
  Write-Info ("Applied ACL: $target -> $LogPath")
} catch {
  Write-Warn ("ACL change failed: " + $_.Exception.Message)
}

# 6) Small patches (optional) — no-op by default; ensure write_path_summary fallback already present
# Reserved for future minimal content tweaks.

# 7) Optional upload
if ($UploadUrl) {
  try {
    $headers = @{}
    if ($UploadCreds) { $headers['Authorization'] = $UploadCreds }
    Invoke-RestMethod -Uri $UploadUrl -Method Put -InFile $ArtifactsOut -ContentType 'application/zip' -TimeoutSec 300 -Headers $headers | Out-Null
    Write-Info "Upload complete to $UploadUrl"
  } catch {
    Write-Warn ("Upload failed: " + $_.Exception.Message)
  }
} elseif ($UploadDest) {
  try {
    $destDir = Split-Path -Parent $UploadDest
    if ($destDir -and -not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Copy-Item -LiteralPath $ArtifactsOut -Destination $UploadDest -Force
    Write-Info ("Copied ZIP to $UploadDest")
  } catch {
    Write-Warn ("Copy to UPLOAD_DEST failed: " + $_.Exception.Message)
  }
} else {
  Write-Info "No UPLOAD_URL/UPLOAD_DEST provided; skipping upload."
}

# 8) Integrity hash
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ArtifactsOut).Hash
Write-Info ("SHA256: $hash")

# 9) Write summary file
$summaryReport = Join-Path $pathIssues 'postrun_summary.txt'
@(
  "ZIP: $ArtifactsOut",
  "SHA256: $hash",
  "Collected files count: $((Get-ChildItem -Path $t -Recurse -File | Measure-Object).Count)",
  ("Missing required: " + ($foundMissing -join ', '))
) | Out-File -FilePath $summaryReport -Encoding UTF8
Write-Info ("Wrote summary: " + $summaryReport)

return 0
