param(
  [string]$ArtifactRoot = ".\artifacts_ascii",
  [int]$PreviewLines = 10,
  [switch]$RunReconstructIfMissing
)

$ErrorActionPreference = 'Continue'

function Head([string]$f,[int]$n) {
  if (-not (Test-Path -LiteralPath $f)) { Write-Host ("MISSING: " + $f) -ForegroundColor Yellow; return }
  Write-Host ("---- HEAD " + $f + " ----") -ForegroundColor Cyan
  try { Get-Content -LiteralPath $f -TotalCount $n | ForEach-Object { Write-Host $_ } } catch {}
}

# discover latest telemetry_run_*
$dirs = Get-ChildItem -Path $ArtifactRoot -Directory -Filter 'telemetry_run_*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
if (-not $dirs -or $dirs.Count -eq 0) {
  Write-Host ("No telemetry_run_* folders found under " + $ArtifactRoot) -ForegroundColor Yellow
  exit 2
}
$ART = $dirs[0].FullName
Write-Host ("Using artifact folder: " + $ART) -ForegroundColor Green

$ordersPath = Join-Path $ART 'orders.csv'
$ctPath = Join-Path $ART 'closed_trades_fifo.csv'

Write-Host "\nFiles present in artifact folder:" -ForegroundColor Gray
try { Get-ChildItem -Path $ART | Select-Object Name,Length | Format-Table -AutoSize | Out-String | Write-Host } catch {}

Head $ordersPath $PreviewLines
Head $ctPath $PreviewLines

# Counts
$fills = 0; $req = 0
if (Test-Path -LiteralPath $ordersPath) {
  try {
    $csv = Import-Csv -LiteralPath $ordersPath
    $req = ($csv | Where-Object { $_.status -eq 'REQUEST' }).Count
    $fills = ($csv | Where-Object { $_.status -eq 'FILL' }).Count
    Write-Host ("orders.csv: REQUEST = $req ; FILL = $fills") -ForegroundColor DarkCyan
  } catch { Write-Host ("Failed to parse orders.csv: " + $_.Exception.Message) -ForegroundColor Yellow }
} else { Write-Host "orders.csv NOT FOUND" -ForegroundColor Red }

# closed_trades existence
$ctCount = 0
if (Test-Path -LiteralPath $ctPath) { $ctCount = (Get-Content -LiteralPath $ctPath | Measure-Object).Count; Write-Host ("closed_trades_fifo.csv line count: $ctCount") }
else { Write-Host "closed_trades_fifo.csv NOT FOUND" -ForegroundColor Red }

if ((-not (Test-Path -LiteralPath $ctPath) -or $ctCount -le 1) -and (Test-Path -LiteralPath $ordersPath) -and $fills -gt 0) {
  Write-Host "\nDetected FILLs but closed_trades empty/only header." -ForegroundColor Yellow
  if ($RunReconstructIfMissing) {
    $out = Join-Path $ART 'closed_trades_fifo_reconstructed.csv'
    Write-Host "Running ReconstructClosedTrades tool..." -ForegroundColor Yellow
    $tool = Join-Path (Resolve-Path '.') 'Tools\ReconstructClosedTrades\bin\Debug\net6.0\ReconstructClosedTrades.exe'
    if (-not (Test-Path -LiteralPath $tool)) { $tool = Join-Path (Resolve-Path '.') 'Tools\ReconstructClosedTrades.exe' }
    $cliArgs = @('--orders', '"' + $ordersPath + '"', '--out', '"' + $out + '"')
    try {
      Start-Process -FilePath $tool -ArgumentList $cliArgs -NoNewWindow -Wait
    } catch { Write-Host ("Failed to start reconstruct tool: " + $_.Exception.Message) -ForegroundColor Red }
    if (Test-Path -LiteralPath $out) { Write-Host ("Reconstruction created: " + $out) -ForegroundColor Green; Head $out $PreviewLines }
    else { Write-Host "Reconstruction failed or did not produce file." -ForegroundColor Red }
  } else {
    Write-Host "Run with -RunReconstructIfMissing to auto-run reconstruction." -ForegroundColor Cyan
  }
}

Write-Host "\nDone."
