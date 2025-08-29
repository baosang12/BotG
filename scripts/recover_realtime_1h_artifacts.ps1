<#
Scans %TEMP% for botg_artifacts_orchestrator_realtime_1h_* and copies the latest telemetry_run_* into OutBase.

Params:
  -OutBase : Repo-relative or absolute output directory (e.g., artifacts_ascii\paper_run_realtime_1h_YYYYMMDD_HHMMSS)
  -Pattern : TEMP folder glob to search (default: botg_artifacts_orchestrator_realtime_1h_*)
  -MaxScan : Max number of candidate TEMP folders to inspect (default: 30)
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)] [string]$OutBase,
  [string]$Pattern = 'botg_artifacts_orchestrator_realtime_1h_*',
  [int]$MaxScan = 30,
  [int]$FallbackScanHours = 12
)

$ErrorActionPreference = 'Stop'

function TimestampNow { (Get-Date).ToString('yyyyMMdd_HHmmss') }
function Write-Text([string]$p,[string]$t) { $enc = New-Object System.Text.UTF8Encoding $false; [System.IO.File]::WriteAllText($p, $t, $enc) }
function Write-Json([string]$p,[object]$o,[int]$d=12) { Write-Text $p ($o | ConvertTo-Json -Depth $d) }

try {
  $repoRoot = (Resolve-Path '.').Path
  if (-not (Test-Path -LiteralPath $OutBase)) { New-Item -ItemType Directory -Path $OutBase -Force | Out-Null }

  $temp = $env:TEMP
  if (-not $temp) { throw 'TEMP environment variable not set' }
  $glob = Join-Path $temp $Pattern
  $cands = Get-ChildItem -Path $glob -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First $MaxScan
  if (-not $cands -or $cands.Count -eq 0) {
    # broaden search: any botg_artifacts*
    $glob2 = Join-Path $temp 'botg_artifacts*'
    $cands = Get-ChildItem -Path $glob2 -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First ($MaxScan*2)
  }

  $allRuns = @()
  foreach ($c in $cands) {
    $runs = Get-ChildItem -LiteralPath $c.FullName -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue
    if ($runs) { $allRuns += $runs }
  }
  if (-not $allRuns -or $allRuns.Count -eq 0) {
    # as last resort, search TEMP directly for telemetry_run_* dirs by time
    $cut = (Get-Date).AddHours(-[math]::Abs($FallbackScanHours))
    $allRuns = Get-ChildItem -Path (Join-Path $temp 'telemetry_run_*') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $cut } | Sort-Object LastWriteTime -Descending
    if (-not $allRuns -or $allRuns.Count -eq 0) {
      $res = @{ status='NOT_FOUND'; note=('No telemetry_run_* found in TEMP range; searched patterns: ' + $glob + ' and botg_artifacts*'); outdir=$OutBase }
      $res | ConvertTo-Json -Depth 6 | Write-Output
      exit 0
    }
  }

  $pick = $allRuns | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  $src = $pick.FullName
  $leaf = Split-Path -Leaf $src
  $dest = Join-Path $OutBase $leaf
  if (-not (Test-Path -LiteralPath $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
  Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue

  # Write atomic sentinel to mark copy finished
  try {
    $tmp = Join-Path $dest 'copy_complete.tmp'
    $flag = Join-Path $dest 'copy_complete.flag'
    $enc = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($tmp, (Get-Date).ToString('o'), $enc)
    if (Test-Path -LiteralPath $flag) { Remove-Item -LiteralPath $flag -Force -ErrorAction SilentlyContinue }
    Move-Item -LiteralPath $tmp -Destination $flag -Force
  } catch {}

  # Ensure preflight record
  $preflight = Join-Path $dest 'preflight_report.txt'
  if (-not (Test-Path -LiteralPath $preflight)) {
    $lines = @()
    $lines += ('SMOKE_ARTIFACT_PATH=' + $src)
    $lines += ('RECOVERED_AT=' + (Get-Date).ToString('s'))
    $lines += ('SOURCE_TEMP_DIR=' + (Split-Path -Parent $src))
    $lines -join "`n" | Set-Content -LiteralPath $preflight -Encoding utf8
  }

  # Zip if needed
  try {
    $zip = Get-ChildItem -LiteralPath $dest -Filter '*.zip' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $zip) {
      Add-Type -AssemblyName System.IO.Compression.FileSystem
      $z = Join-Path $dest ($leaf + '.zip')
      if (Test-Path -LiteralPath $z) { Remove-Item -LiteralPath $z -Force }
      [System.IO.Compression.ZipFile]::CreateFromDirectory($dest, $z)
      $zip = Get-Item -LiteralPath $z
    }
  } catch {}

  $result = @{ status='RECOVERED'; source=$src; dest=$dest; zip=($zip?.FullName) }
  $result | ConvertTo-Json -Depth 6 | Write-Output
} catch {
  $err = $_.Exception.Message
  @{ status='ERROR'; message=$err; outdir=$OutBase } | ConvertTo-Json -Depth 6 | Write-Output
  exit 0
}
