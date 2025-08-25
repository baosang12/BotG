param(
  [Parameter(Mandatory=$true)] [string] $OrdersCsv,
  [Parameter(Mandatory=$true)] [string] $ClosedCsv,
  [Parameter(Mandatory=$true)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

function Write-File([string]$path,[string]$text) {
  $utf8 = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllText($path, $text, $utf8)
}

try {
  $orders = Import-Csv -LiteralPath $OrdersCsv
  $closed = Import-Csv -LiteralPath $ClosedCsv
} catch {
  Write-Error $_; exit 2
}

$fills = $orders | Where-Object { $_.phase -eq 'FILL' -or $_.status -eq 'FILL' }
$fillIds = @{}
foreach ($f in $fills) { $fillIds[$f.orderId] = $true }

$closedIds = @{}
foreach ($c in $closed) {
  if ($c.entry_order_id) { $closedIds[$c.entry_order_id] = $true }
  if ($c.exit_order_id) { $closedIds[$c.exit_order_id] = $true }
}

$orphanFills = @()
foreach ($key in $fillIds.Keys) { if (-not $closedIds.ContainsKey($key)) { $orphanFills += $key } }

$orphanCloses = @()
foreach ($c in $closed) {
  if ($c.entry_order_id -and -not $fillIds.ContainsKey($c.entry_order_id)) { $orphanCloses += ($c.entry_order_id) }
  if ($c.exit_order_id -and -not $fillIds.ContainsKey($c.exit_order_id)) { $orphanCloses += ($c.exit_order_id) }
}

$report = [pscustomobject]@{
  request_count = ($orders | Where-Object { $_.phase -eq 'REQUEST' -or $_.status -eq 'REQUEST' }).Count
  fill_count = $fills.Count
  closed_trades_count = ($closed | Measure-Object).Count
  orphan_fills_count = $orphanFills.Count
  orphan_closes_count = $orphanCloses.Count
  sample_orphan_fills = ($orphanFills | Select-Object -First 50)
  sample_orphan_closes = ($orphanCloses | Select-Object -First 50)
}

$json = $report | ConvertTo-Json -Depth 6
Write-File (Join-Path $OutDir 'reconcile_report.json') $json

$txt = @()
$txt += 'Reconcile Report'
$txt += '----------------'
$txt += "request_count       : $($report.request_count)"
$txt += "fill_count          : $($report.fill_count)"
$txt += "closed_trades_count : $($report.closed_trades_count)"
$txt += "orphan_fills_count  : $($report.orphan_fills_count)"
$txt += "orphan_closes_count : $($report.orphan_closes_count)"
if ($report.sample_orphan_fills.Count -gt 0) {
  $txt += ''
  $txt += 'Sample orphan fills:'
  $txt += ($report.sample_orphan_fills -join [Environment]::NewLine)
}
if ($report.sample_orphan_closes.Count -gt 0) {
  $txt += ''
  $txt += 'Sample orphan closes:'
  $txt += ($report.sample_orphan_closes -join [Environment]::NewLine)
}
Write-File (Join-Path $OutDir 'reconcile_report.txt') ($txt -join [Environment]::NewLine)

if ($report.orphan_fills_count -gt 0 -or $report.orphan_closes_count -gt 0) { exit 3 }
exit 0
