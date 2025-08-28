param(
  [Parameter(Mandatory=$true)][string]$ArtifactsRoot,
  [Parameter(Mandatory=$false)][string]$OutDir = "out"
)

$ErrorActionPreference = 'Stop'

Write-Host "PWD=$(Get-Location)"
Write-Host "ArtifactsRoot=$ArtifactsRoot"
if (-not (Test-Path -LiteralPath $ArtifactsRoot)) { throw "Artifacts root not found: $ArtifactsRoot" }

$resolvedOut = Resolve-Path -LiteralPath (New-Item -ItemType Directory -Path $OutDir -Force).FullName
Write-Host "OutDir=$resolvedOut"

# Emit a quick directory listing for diagnostics
try {
  $listing = Join-Path $resolvedOut 'artifact_listing.txt'
  Get-ChildItem -LiteralPath $ArtifactsRoot -Recurse | Select-Object FullName, Length, LastWriteTime | Out-File -FilePath $listing -Encoding UTF8
  Write-Host "Wrote listing -> $listing"
} catch { Write-Warning $_ }

# Find an orders.csv inside the smoke artifact
$orders = Get-ChildItem -Path $ArtifactsRoot -Recurse -Filter 'orders.csv' -ErrorAction SilentlyContinue | Select-Object -First 1
# If not found, try expanding telemetry_run_*.zip within artifacts root and search again
if (-not $orders) {
  $zip = Get-ChildItem -Path $ArtifactsRoot -Recurse -Filter 'telemetry_run_*.zip' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($zip) {
    $unzipDir = Join-Path $resolvedOut 'unzipped'
    New-Item -ItemType Directory -Path $unzipDir -Force | Out-Null
    Write-Host "Expanding zip: $($zip.FullName) -> $unzipDir"
    try { Expand-Archive -LiteralPath $zip.FullName -DestinationPath $unzipDir -Force } catch { Write-Warning $_ }
    $orders = Get-ChildItem -Path $unzipDir -Recurse -Filter 'orders.csv' -ErrorAction SilentlyContinue | Select-Object -First 1
  }
}
if (-not $orders) { throw "orders.csv not found under $ArtifactsRoot (and none inside any telemetry_run_*.zip)" }
$ordersPath = $orders.FullName
Write-Host "orders.csv=$ordersPath"

# Determine python robustly (python, py -3, py, python3)
$pyExe = $null; $pyArgs = @()
foreach ($cand in @('python','py -3','py','python3')) {
  try {
    $parts = $cand.Split(' ')
    $exe = $parts[0]
    $args = if ($parts.Length -gt 1) { $parts[1..($parts.Length-1)] } else { @() }
    $ver = & $exe @args --version 2>$null
    if ($LASTEXITCODE -eq 0 -or $ver) { $pyExe = $exe; $pyArgs = $args; Write-Host "Using Python: $exe $($args -join ' ') ($ver)"; break }
  } catch { }
}
if (-not $pyExe) { throw 'Python interpreter not found (tried: python, py -3, py, python3)' }

# Path to reconstructor
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$reconPy = Join-Path $repoRoot 'reconstruct_closed_trades_sqlite.py'
Write-Host "RepoRoot=$repoRoot"
Write-Host "Reconstructor=$reconPy"
if (-not (Test-Path -LiteralPath $reconPy)) { throw "Reconstructor not found: $reconPy" }

# Run reconstruction
$outCsv = Join-Path $resolvedOut 'closed_trades_fifo_reconstructed.csv'
Write-Host "Running: $pyExe $($pyArgs -join ' ') $reconPy --orders <orders> --out $outCsv"
try {
  $reconLog = Join-Path $resolvedOut 'reconstruction_stdout.txt'
  & $pyExe @pyArgs $reconPy --orders $ordersPath --out $outCsv *>&1 | Tee-Object -FilePath $reconLog | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Reconstruction script exited with code $LASTEXITCODE" }
} catch {
  $errLog = Join-Path $resolvedOut 'reconstruction_error.txt'
  "Error running reconstruction: $($_.Exception.Message)" | Out-File -FilePath $errLog -Encoding UTF8
  throw
}
Write-Host "Reconstruction complete -> $outCsv"

if (-not (Test-Path -LiteralPath $outCsv)) {
  throw "Expected output CSV not found: $outCsv"
}

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
