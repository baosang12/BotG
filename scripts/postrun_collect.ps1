[CmdletBinding()]
param(
  [string]$RunDir,
  [switch]$WithL1,
  [string]$ArtifactsDir = "$env:RUNNER_TEMP\postrun",
  [double]$RunHours = 24,
  [switch]$SmokeLite,
  [ValidateSet('strict','smoke')]
  [string]$ValidateMode = 'strict'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'

if (-not $RunDir) {
  if ($env:RUN_DIR) {
    $RunDir = $env:RUN_DIR
  } else {
    throw "RunDir not provided and RUN_DIR env is empty"
  }
}

# === Normalize RUN_DIR safely ===
$rd = (Resolve-Path -LiteralPath $RunDir -ErrorAction Stop).Path
# Trim trailing separators and spaces using char[]
$rd = $rd.TrimEnd([char[]]@(
    [IO.Path]::DirectorySeparatorChar,    # '\'
    [IO.Path]::AltDirectorySeparatorChar, # '/'
    ' '                                    # trailing spaces
))

$artifactsDirInfo = New-Item -ItemType Directory -Force -Path $ArtifactsDir
$artifactsPath = (Resolve-Path -LiteralPath $artifactsDirInfo.FullName).Path
# Normalize artifacts path too
$artifactsPath = $artifactsPath.TrimEnd([char[]]@(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar,
    ' '
))

Write-Host "[postrun] RUN_DIR=$rd -> ArtifactsDir=$artifactsPath"

function Get-RequiredFile([string]$basePath, [string]$name) {
  $candidate = Join-Path $basePath $name
  if (-not (Test-Path -LiteralPath $candidate)) {
    throw "Missing required file: $name"
  }
  return (Get-Item -LiteralPath $candidate)
}

function Resolve-ArtifactPath([string]$ArtifactsDir, [string]$Name) {
  # Try l1 subdirectory first (priority after PR#285)
  $p1 = Join-Path $ArtifactsDir "l1\$Name"
  if (Test-Path -LiteralPath $p1) { return $p1 }
  
  # Fallback to root for backward compatibility
  $p2 = Join-Path $ArtifactsDir $Name
  if (Test-Path -LiteralPath $p2) { return $p2 }
  
  return $null
}

$orders = Get-RequiredFile $rd 'orders.csv'
$closes = Get-RequiredFile $rd 'trade_closes.log'
$meta   = Get-RequiredFile $rd 'run_metadata.json'
$tele   = Get-RequiredFile $rd 'telemetry.csv'
$risk   = Get-RequiredFile $rd 'risk_snapshots.csv'
$l1Path = $null
if ($WithL1) {
  $l1Candidate = Join-Path $rd 'l1_stream.csv'
  if (-not (Test-Path -LiteralPath $l1Candidate)) {
    throw "Missing required file: l1_stream.csv"
  }
  $l1Path = (Get-Item -LiteralPath $l1Candidate)
}

function Copy-ToArtifacts([IO.FileInfo]$file, [string]$targetName) {
  Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $artifactsPath $targetName) -Force
}

Copy-ToArtifacts $orders 'orders.csv'
Copy-ToArtifacts $closes 'trade_closes.log'
Copy-ToArtifacts $meta 'run_metadata.json'
Copy-ToArtifacts $tele 'telemetry.csv'
Copy-ToArtifacts $risk 'risk_snapshots.csv'
if ($l1Path) {
  Copy-ToArtifacts $l1Path 'l1_stream.csv'
}

$env:PYTHONIOENCODING = 'utf-8'

function Invoke-PythonCommand([string[]]$arguments, [string]$errorMessage) {
  & python @arguments
  if ($LASTEXITCODE -ne 0) {
    throw "$errorMessage (exit $LASTEXITCODE)"
  }
}

Write-Host "[1/5] Reconstruct FIFO..." -ForegroundColor Cyan
$fifoOut = Join-Path $artifactsPath 'closed_trades_fifo_reconstructed.csv'
Invoke-PythonCommand @('-X','utf8','./path_issues/reconstruct_fifo.py','--orders',(Join-Path $artifactsPath 'orders.csv'),'--closes',(Join-Path $artifactsPath 'trade_closes.log'),'--meta',(Join-Path $artifactsPath 'run_metadata.json'),'--out',$fifoOut) "FIFO reconstruction failed"
Copy-Item -LiteralPath $fifoOut -Destination (Join-Path $rd 'closed_trades_fifo_reconstructed.csv') -Force

Write-Host "[2/5] Ensure analysis_summary_stats.json..." -ForegroundColor Cyan
$stats = Join-Path $artifactsPath 'analysis_summary_stats.json'
if (-not (Test-Path -LiteralPath $stats)) {
  $tradeLines = (Get-Content -LiteralPath $fifoOut | Measure-Object -Line).Lines
  $trades = [math]::Max($tradeLines - 1, 0)
  @{ trades_count = $trades } | ConvertTo-Json | Out-File -FilePath $stats -Encoding utf8 -Force
}
Copy-Item -LiteralPath $stats -Destination (Join-Path $rd 'analysis_summary_stats.json') -Force

if ($WithL1) {
  Write-Host "[3/5] Compute fees & slippage..." -ForegroundColor Cyan
  $l1Dir = Join-Path $artifactsPath 'l1'
  if (-not (Test-Path -LiteralPath $l1Dir)) {
    New-Item -ItemType Directory -Path $l1Dir -Force | Out-Null
  }
  $feesOut = Join-Path $l1Dir 'fees_slippage.csv'
  $kpiOut = Join-Path $l1Dir 'kpi_slippage.json'
  Invoke-PythonCommand @('-X','utf8','scripts/analyzers/join_l1_fills.py','--orders',(Join-Path $artifactsPath 'orders.csv'),'--l1',(Join-Path $artifactsPath 'l1_stream.csv'),'--out-fees',$feesOut,'--out-kpi',$kpiOut) "Slippage analyzer failed"
  Copy-Item -LiteralPath $feesOut -Destination (Join-Path $rd 'fees_slippage.csv') -Force
  Copy-Item -LiteralPath $kpiOut -Destination (Join-Path $rd 'kpi_slippage.json') -Force
  # Copy scale_debug.json if it exists
  $scaleDebugPath = Join-Path $l1Dir 'scale_debug.json'
  if (Test-Path -LiteralPath $scaleDebugPath) {
    Copy-Item -LiteralPath $scaleDebugPath -Destination (Join-Path $rd 'scale_debug.json') -Force
  }
}

Write-Host "[4/5] Validate artifacts..." -ForegroundColor Cyan
$validateArgs = @('-X','utf8','./path_issues/validate_artifacts.py','--dir',$artifactsPath,'--run-hours',$RunHours)
if ($SmokeLite) { $validateArgs += '--smoke-lite' }
try {
  Invoke-PythonCommand $validateArgs "Artifact validation failed"
} catch {
  if ($ValidateMode -eq 'strict') {
    throw
  } else {
    Write-Warning "[validator] failed in smoke mode; continuing - $_"
  }
}

Write-Host "[5/5] Zip artifacts..." -ForegroundColor Cyan
$requiredOutputs = @(
  'orders.csv',
  'telemetry.csv',
  'risk_snapshots.csv',
  'trade_closes.log',
  'run_metadata.json',
  'closed_trades_fifo_reconstructed.csv',
  'analysis_summary_stats.json'
)

# Validate L1 outputs using Resolve-ArtifactPath (supports both l1\ and root)
if ($WithL1) {
  $feesPath = Resolve-ArtifactPath $artifactsPath 'fees_slippage.csv'
  $kpiPath = Resolve-ArtifactPath $artifactsPath 'kpi_slippage.json'
  
  if (-not $feesPath -or -not $kpiPath) {
    throw "Postrun missing L1 outputs (searched in root and l1\): fees=$feesPath; kpi=$kpiPath"
  }
  
  # Optional: scale_debug.json (not required, just info)
  $scaleDebugPath = Resolve-ArtifactPath $artifactsPath 'scale_debug.json'
  if ($scaleDebugPath) {
    Write-Host "[info] Found scale_debug.json at: $scaleDebugPath" -ForegroundColor Gray
  }
}

# Validate other required outputs in root
$missingOutputs = $requiredOutputs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $artifactsPath $_)) }
if ($missingOutputs) {
  throw "Postrun missing outputs: $($missingOutputs -join ', ')"
}

$zip = Join-Path (Split-Path $artifactsPath -Parent) ("artifacts_" + (Split-Path $rd -Leaf) + ".zip")
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

# Zip includes both root and l1 subdirectory
$zipSrc = @()
$zipSrc += (Join-Path $artifactsPath '*')
$l1SubDir = Join-Path $artifactsPath 'l1'
if (Test-Path -LiteralPath $l1SubDir) {
  $zipSrc += $l1SubDir
}

Compress-Archive -Path $zipSrc -DestinationPath $zip -Force
Write-Host "ARTIFACT_ZIP=$zip" -ForegroundColor Green
