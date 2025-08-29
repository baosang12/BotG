$ErrorActionPreference = 'Stop'

function Write-Tag($k,$v){ Write-Host ("$k=" + $v) }

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
$zipTxt = Join-Path $scriptDir 'latest_zip.txt'
if (!(Test-Path -LiteralPath $zipTxt)) { throw "latest_zip.txt not found: $zipTxt" }
$zipPath = (Get-Content -LiteralPath $zipTxt -Raw).Trim()
if (!(Test-Path -LiteralPath $zipPath)) { throw "ZIP not found at path_issues/latest_zip.txt -> $zipPath" }

$tmpRoot = Join-Path $env:TEMP ("botg_reconstruct_" + $ts)
New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null
$dest = Join-Path $tmpRoot 'unzipped'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Write-Host "Expanding: $zipPath -> $dest"
Expand-Archive -LiteralPath $zipPath -DestinationPath $dest -Force

$orders = Get-ChildItem -Path $dest -Recurse -Filter 'orders.csv' | Select-Object -First 1
if (-not $orders) { throw "orders.csv not found inside zip" }
$ordersPath = $orders.FullName

$py = Join-Path $repoRoot '.venv' | Join-Path -ChildPath 'Scripts' | Join-Path -ChildPath 'python.exe'
if (!(Test-Path -LiteralPath $py)) { $py = 'python' }
$reconPy = Join-Path $repoRoot 'reconstruct_closed_trades_sqlite.py'
if (!(Test-Path -LiteralPath $reconPy)) { throw "Reconstructor not found: $reconPy" }

$outCsv = Join-Path $scriptDir 'closed_trades_fifo_reconstructed.csv'
& $py $reconPy --orders $ordersPath --out $outCsv
Write-Tag 'RECON_OUT' $outCsv

# Validate: no close_time < open_time and PnL formatting
$rows = Import-Csv -LiteralPath $outCsv
$badTime = @(); $badPnl = @()
$regex = '^-?\d+\.\d{1,8}$'
foreach ($r in $rows) {
  $ot = $r.open_time; $ct = $r.close_time; $p = $r.pnl
  if (-not [string]::IsNullOrWhiteSpace($ot) -and -not [string]::IsNullOrWhiteSpace($ct)) {
    try {
      $od = [datetimeoffset]::Parse($ot)
      $cd = [datetimeoffset]::Parse($ct)
      if ($cd -lt $od) { $badTime += $r }
    } catch { $badTime += $r }
  }
  if ($p -notmatch $regex) { $badPnl += $r }
}

$summary = @{
  zip = $zipPath
  orders = $ordersPath
  outCsv = $outCsv
  total = $rows.Count
  badTimeCount = $badTime.Count
  badPnlCount = $badPnl.Count
}
$valPath = Join-Path $scriptDir ("reconstruct_validation_" + $ts + '.json')
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $valPath -Encoding UTF8
Write-Tag 'VALIDATION_SUMMARY_PATH' $valPath

if ($badTime.Count -gt 0 -or $badPnl.Count -gt 0) {
  $detail = Join-Path $scriptDir ("reconstruct_validation_" + $ts + '_details.csv')
  $badTime + $badPnl | Export-Csv -LiteralPath $detail -NoTypeInformation -Encoding UTF8
  Write-Tag 'VALIDATION_DETAILS_PATH' $detail
}
