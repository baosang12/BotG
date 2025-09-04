
Write-Host "ArtifactsRoot=$ArtifactsRoot"
if (-not (Test-Path -LiteralPath $ArtifactsRoot)) { throw "Artifacts root not found: $ArtifactsRoot" }

$resolvedOut = Resolve-Path -LiteralPath (New-Item -ItemType Directory -Path $OutDir -Force).FullName
Write-Host "OutDir=$resolvedOut"



# Path to reconstructor
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$reconPy = Join-Path $repoRoot 'reconstruct_closed_trades_sqlite.py'


# Validate: no close_time < open_time and PnL with 1-8 decimals
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

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$summary = @{
  orders = $ordersPath
  outCsv = $outCsv
  total = $rows.Count
  badTimeCount = $badTime.Count
  badPnlCount = $badPnl.Count
}
$valPath = Join-Path $resolvedOut ("reconstruct_validation_" + $ts + '.json')
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $valPath -Encoding UTF8
Write-Host "Summary -> $valPath"

if ($badTime.Count -gt 0 -or $badPnl.Count -gt 0) {
  $detail = Join-Path $resolvedOut ("reconstruct_validation_" + $ts + '_details.csv')
  $badTime + $badPnl | Export-Csv -LiteralPath $detail -NoTypeInformation -Encoding UTF8
  Write-Host "Details -> $detail"
  throw ("Validation failed: badTimeCount={0}, badPnlCount={1}" -f $badTime.Count, $badPnl.Count)
}

Write-Host "Validation OK"
