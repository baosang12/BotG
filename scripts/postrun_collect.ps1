[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$RunDir,
  [Parameter(Mandatory=$false)][double]$RunHours = 24,
  [switch]$SmokeLite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'

# --- Normalize path
$rd = (Resolve-Path -LiteralPath $RunDir -ErrorAction Stop).Path
$rd = $rd.TrimEnd('\','/')

# --- Required files (đủ cho reconstruct & validate)
$required = @('orders.csv','trade_closes.log','run_metadata.json','telemetry.csv','risk_snapshots.csv')
$missing = $required | Where-Object { -not (Test-Path (Join-Path $rd $_)) }
if ($missing) { throw "Missing required files: $($missing -join ', ')" }

# --- Python UTF-8
$env:PYTHONIOENCODING = 'utf-8'
$py = 'python'

Write-Host "[1/4] Reconstruct FIFO..." -ForegroundColor Cyan
& $py -X utf8 .\path_issues\reconstruct_fifo.py `
  --orders "$rd\orders.csv" `
  --closes "$rd\trade_closes.log" `
  --meta   "$rd\run_metadata.json" `
  --out    "$rd\closed_trades_fifo_reconstructed.csv"

Write-Host "[2/4] Validate artifacts..." -ForegroundColor Cyan
$pyArgs = @("-X","utf8",".\path_issues\validate_artifacts.py","--dir",$rd,"--run-hours",$RunHours)
if ($SmokeLite) { $pyArgs += "--smoke-lite" }
& python @pyArgs
if ($LASTEXITCODE -ne 0) { throw "Artifact validation failed (exit $LASTEXITCODE)" }

Write-Host "[3/4] Ensure analysis_summary_stats.json..." -ForegroundColor Cyan
$stats = Join-Path $rd 'analysis_summary_stats.json'
if (-not (Test-Path $stats)) {
  $trades = (Get-Content "$rd\closed_trades_fifo_reconstructed.csv").Count - 1
  @{ trades_count = $trades } | ConvertTo-Json | Out-File $stats -Encoding utf8 -Force
}

Write-Host "[4/4] Zip artifacts..." -ForegroundColor Cyan
$zip = Join-Path (Split-Path $rd -Parent) ("artifacts_" + (Split-Path $rd -Leaf) + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$rd\*" -DestinationPath $zip -Force
Write-Host "ARTIFACT_ZIP=$zip" -ForegroundColor Green
