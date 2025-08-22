param(
  [int]$PollSeconds = 30,
  [int]$QuietAfterSeconds = 120,
  [switch]$Analyze
)

function Write-Info($m){ Write-Host "[WATCH] $m" -ForegroundColor Cyan }
function Write-Warn($m){ Write-Host "[WARN]  $m" -ForegroundColor Yellow }
function Latest-ArtifactsDir(){
  $arts = Get-ChildItem -Path (Join-Path $PSScriptRoot '..') -LiteralPath | Out-Null
}

# Resolve repo root and artifacts folder
$repoRoot = Split-Path -Parent $PSScriptRoot
$artRoot = Join-Path $repoRoot 'artifacts'
if (-not (Test-Path $artRoot)) { New-Item -ItemType Directory -Path $artRoot -Force | Out-Null }

Write-Info "Monitoring artifacts in: $artRoot"
$lastDir = $null
$lastWrite = Get-Date
$stableSince = $null

while ($true) {
  $dir = Get-ChildItem -Path $artRoot -Directory -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -like 'telemetry_run_*' } |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1

  if (-not $dir) { Write-Info 'No telemetry_run_* folder yet...'; Start-Sleep -Seconds $PollSeconds; continue }

  if (-not $lastDir -or $dir.FullName -ne $lastDir.FullName) {
    Write-Info "Active dir: $($dir.FullName)"
    $lastDir = $dir
    $stableSince = $null
  }

  $lw = $dir.LastWriteTimeUtc
  if (-not $stableSince) {
    $stableSince = $lw
  } elseif ($lw -gt $stableSince) {
    $stableSince = $lw
  }

  $idleSec = [int]([DateTime]::UtcNow - $stableSince).TotalSeconds
  Write-Info ("LastWrite {0}s ago" -f $idleSec)

  $summaryJson = Join-Path $dir.FullName 'summary.json'
  $zip = Join-Path $dir.FullName ('{0}.zip' -f $dir.Name)

  if ($idleSec -ge $QuietAfterSeconds -and (Test-Path $summaryJson -PathType Leaf)) {
    Write-Info "Run appears finished. Found summary.json."
    try {
      $sum = Get-Content -Raw -Path $summaryJson | ConvertFrom-Json
      Write-Output ($sum | ConvertTo-Json -Depth 6)
    } catch { Write-Warn "Could not parse summary.json: $_" }

    if ($Analyze) {
      $py = $null
      foreach ($cand in @('py','python','python3')) {
        try { & $cand --version *> $null; if ($LASTEXITCODE -eq 0) { $py = $cand; break } } catch {}
      }
      if ($py) {
        Write-Info "Running analyze_smoke.py on $($dir.FullName)"
        & $py (Join-Path $repoRoot 'scripts/analyze_smoke.py') --base $dir.FullName
      } else {
        Write-Warn 'Python not found; skipping analyze_smoke.py'
      }
    }
    break
  }

  Start-Sleep -Seconds $PollSeconds
}

Write-Info 'Watcher completed.'
