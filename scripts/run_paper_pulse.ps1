[CmdletBinding()]
param(
  [int]$Hours = 24,
  [string]$ArtifactPath = $(Join-Path $env:TEMP ("botg_paper_runs")),
  [double]$FillProb = 0.9,
  [double]$FeePerTrade = 0.02,
  [switch]$GeneratePlots
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')

$ts = Get-Date -Format yyyyMMdd_HHmmss
$art = Join-Path $ArtifactPath ("paper_run_" + $ts)
New-Item -ItemType Directory -Path $art -Force | Out-Null

# metadata
$meta = @{
  start_time_iso = (Get-Date).ToUniversalTime().ToString('o')
  hours = $Hours
  config = @{ fill_prob = $FillProb; fee_per_trade = $FeePerTrade }
  artifact = $art
}
$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Join-Path $art 'run_metadata.json'), ($meta | ConvertTo-Json -Depth 6), $utf8)

$hourly = Join-Path $art 'hourly_summaries'
New-Item -ItemType Directory -Path $hourly -Force | Out-Null

for ($h = 0; $h -lt $Hours; $h++) {
  Write-Host ("[pulse] Hour $h / $Hours") -ForegroundColor Cyan
  $tmp = Join-Path $art ("h_" + ("{0:D2}" -f $h))
  New-Item -ItemType Directory -Path $tmp -Force | Out-Null
  # 55 minutes per hour to reduce overlap
  $sec = 55 * 60
  pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\run_smoke.ps1') -Seconds $sec -ArtifactPath $tmp -FillProb $FillProb -FeePerTrade $FeePerTrade -GeneratePlots:$GeneratePlots | Tee-Object -FilePath (Join-Path $tmp 'pulse_hour.log')
  # Copy summaries into hourly
  $sum = Join-Path $tmp (Get-ChildItem -Path $tmp -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).Name
  if ($sum) {
    $run = Join-Path $tmp $sum
    foreach ($f in 'analysis_summary.json','reconcile_report.json','monitoring_summary.json') {
      if (Test-Path (Join-Path $run $f)) { Copy-Item (Join-Path $run $f) -Destination (Join-Path $hourly ("${h}_" + $f)) -Force }
    }
  }
}

try {
  $zip = Join-Path $art ("paper_run_" + $ts + '.zip')
  if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
  Compress-Archive -Path $art -DestinationPath $zip -Force
  Write-Host ("[pulse] Zipped to: " + $zip)
} catch {}

Write-Host "[pulse] Done."
