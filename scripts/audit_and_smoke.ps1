# PowerShell 5.1-compatible helper: audit log paths, run a short smoke, and summarize
[CmdletBinding()]
param(
  [int]$DurationSeconds = 180,
  [double]$FillProb = 1.0,
  [switch]$ForceRun
)

function Write-Info($msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Show-DirSummary([string]$path) {
  Write-Info ("--- {0} ---" -f $path)
  if (Test-Path $path) {
    try { $dir = Get-Item $path; Write-Host ("Exists; LastWrite=" + $dir.LastWriteTime.ToString('s')) } catch { }
    Get-ChildItem $path -File -ErrorAction SilentlyContinue |
      Sort-Object LastWriteTime -Descending |
      Select-Object -First 5 |
      ForEach-Object { Write-Host ("  {0}  {1}  {2}" -f $_.Name, $_.LastWriteTime.ToString('s'), $_.Length) }
  } else { Write-Host 'Missing' }
}

try {
  $ErrorActionPreference = 'Stop'
  Write-Info "PWD=$(Get-Location)"

  # 1) Audit env + config
  $envPath = $env:BOTG_LOG_PATH; if (-not $envPath) { $envPath = '<null>' }
  Write-Info ("ENV BOTG_LOG_PATH=$envPath")
  $cfgPath = Join-Path (Get-Location) 'config.runtime.json'
  $cfgLog = '<null>'
  if (Test-Path $cfgPath) {
    try { $cfgLog = (Get-Content -Raw $cfgPath | ConvertFrom-Json).LogPath } catch { $cfgLog = '<parse-error>' }
  }
  Write-Info ("CONFIG LogPath=$cfgLog")

  # 2) Inspect both roots
  Show-DirSummary 'C:\botg\logs'
  Show-DirSummary 'D:\botg\logs'

  # 3) Run harness via run_harness_and_collect.ps1 -> ensures BOTG_LOG_PATH points to the artifact dir
  $psArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/run_harness_and_collect.ps1','-DurationSeconds', [string]$DurationSeconds, '-FillProb', ([System.String]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:G}', $FillProb)))
  if ($ForceRun) { $psArgs += '-ForceRun' }
  Write-Info ("Starting run_harness_and_collect.ps1 with args: $($psArgs -join ' ')")
  $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $psArgs -NoNewWindow -PassThru
  $p.WaitForExit()
  Write-Info ("run_harness_and_collect exit=$($p.ExitCode)")
  if ($p.ExitCode -ne 0) { throw "run_harness_and_collect failed with exit $($p.ExitCode)" }

  # 4) Find newest artifact dir
  $art = Get-ChildItem '.\artifacts' -Directory -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $art) { throw 'No artifacts directory found' }
  Write-Info ("ARTIFACT: $($art.FullName)")

  # 5) Summarize into smoke_*
  .\scripts\smoke_collect_and_summarize.ps1 -LogDir $art.FullName
  $smoke = Get-ChildItem $art.FullName -Directory | Where-Object { $_.Name -like 'smoke_*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $smoke) { throw 'No smoke_* directory created' }
  Write-Info ("SMOKE: $($smoke.FullName)")

  # 6) Compute breakdowns
  .\scripts\compute_fill_breakdown.ps1 -OrdersCsv (Join-Path $smoke.FullName 'orders.csv') -OutDir $smoke.FullName

  # 7) Print A-D outputs
  Write-Host '--- A) summary_smoke.json ---' -ForegroundColor Green
  Get-Content (Join-Path $smoke.FullName 'summary_smoke.json')
  Write-Host '--- B) fill_rate_by_side.csv ---' -ForegroundColor Green
  Get-Content (Join-Path $smoke.FullName 'fill_rate_by_side.csv')
  Write-Host '--- C) fill_breakdown_by_hour.csv ---' -ForegroundColor Green
  Get-Content (Join-Path $smoke.FullName 'fill_breakdown_by_hour.csv')
  Write-Host '--- D) presence ---' -ForegroundColor Green
  Write-Host ('run_metadata.json: ' + (Test-Path (Join-Path $smoke.FullName 'run_metadata.json')))
  Write-Host ('trade_closes.log: ' + (Test-Path (Join-Path $smoke.FullName 'trade_closes.log')))

} catch {
  Write-Warn $_
  exit 1
}
