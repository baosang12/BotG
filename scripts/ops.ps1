#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

function Resolve-Defaults {
    param(
        [string]$OutRoot = "D:\botg\runs",
        [string]$LogPath = "D:\botg\logs"
    )
    if (!(Test-Path -LiteralPath $OutRoot)) { New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null }
    if (!(Test-Path -LiteralPath $LogPath)) { New-Item -ItemType Directory -Force -Path $LogPath | Out-Null }
    return @($OutRoot, $LogPath)
}

function Resolve-LatestRun {
    param([string]$OutRoot = "D:\botg\runs")
    $latest = Get-ChildItem -LiteralPath $OutRoot -Directory -Filter "paper_smoke_60m_v2_*" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return $null
}

function Invoke-Smoke60mMergeLatest {
    param([string]$OutRoot = "D:\botg\runs", [string]$LogPath = "D:\botg\logs")
    $r = Resolve-Defaults -OutRoot $OutRoot -LogPath $LogPath
    & ".\scripts\run_smoke_60m_wrapper_v2.ps1" -MergeOnly -OutRoot $r[0] -LogPath $r[1]
}

function Invoke-Smoke60mMergeExisting {
    param(
        [Parameter(Mandatory)][string]$ExistingRoot,
        [string]$LogPath = "D:\botg\logs"
    )
    if (!(Test-Path -LiteralPath $ExistingRoot)) { throw "ExistingRoot not found: $ExistingRoot" }
    $null = Resolve-Defaults -OutRoot (Split-Path -Parent $ExistingRoot) -LogPath $LogPath
    & ".\scripts\run_smoke_60m_wrapper_v2.ps1" -MergeOnly -ExistingRoot $ExistingRoot -LogPath $LogPath
}

function Invoke-Smoke60mRun {
    param([int]$Segments = 4, [string]$OutRoot = "D:\botg\runs", [string]$LogPath = "D:\botg\logs")
    $r = Resolve-Defaults -OutRoot $OutRoot -LogPath $LogPath
    & ".\scripts\run_smoke_60m_wrapper_v2.ps1" -Segments $Segments -OutRoot $r[0] -LogPath $r[1]
}

function Show-Smoke60mReport {
    param([string]$OutRoot = "D:\botg\runs")
    $root = Resolve-LatestRun -OutRoot $OutRoot
    if (!$root) { throw "No paper_smoke_60m_v2_* found under $OutRoot" }
    $report = Join-Path -Path $root -ChildPath "report_60m.md"
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
    else { Write-Warning "report_60m.md not found in $root" }
}

function Show-PhaseStats {
    param([string]$OutRoot = "D:\botg\runs")
    $root = Resolve-LatestRun -OutRoot $OutRoot
    if (!$root) { throw "No paper_smoke_60m_v2_* found under $OutRoot" }
    $merged = Join-Path -Path $root -ChildPath "orders_merged.csv"
    if (!(Test-Path -LiteralPath $merged)) { throw "Missing merged CSV: $merged" }
    $csv = Import-Csv -LiteralPath $merged
    $ph = ($csv | Group-Object phase | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join '; '
    $fills = $csv | Where-Object { $_.phase -eq 'FILL' }
    if ($fills) {
        $m = $fills | Measure-Object latency_ms -Average -Minimum -Maximum
        Write-Host "PHASES: $ph"
        Write-Host ("FILL: {0} | latency avg={1} min={2} max={3}" -f $fills.Count, [int]$m.Average, [int]$m.Minimum, [int]$m.Maximum)
    } else {
        Write-Host "PHASES: $ph"
        Write-Host "FILL: 0"
    }
}

function Run-UnitTests {
    if (-not (Get-Module -ListAvailable -Name Pester)) {
        try { Install-Module Pester -Scope CurrentUser -Force -ErrorAction Stop } catch { Write-Warning "Install-Module Pester failed: $_" }
    }
    if (Test-Path -LiteralPath ".\tests") { Invoke-Pester -Path ".\tests" -Output Detailed } else { Write-Warning "tests folder not found." }
}

function Invoke-Precommit {
    if (Test-Path -LiteralPath ".\scripts\precommit_check.ps1") {
        & ".\scripts\precommit_check.ps1"
    } else {
        Write-Warning "scripts\precommit_check.ps1 not found."
    }
}

function Invoke-Rollback {
    $pairs = @(
        @{bak="scripts\lib_csv_merge.ps1.backup";  dst="scripts\lib_csv_merge.ps1"},
        @{bak="scripts\run_smoke_60m_wrapper_v2.ps1.backup"; dst="scripts\run_smoke_60m_wrapper_v2.ps1"},
        @{bak="reconstruct_closed_trades_sqlite.py.backup";  dst="reconstruct_closed_trades_sqlite.py"}
    )
    foreach ($p in $pairs) {
        if (Test-Path -LiteralPath $p.bak) {
            Copy-Item -LiteralPath $p.bak -Destination $p.dst -Force
            Write-Host "Rolled back: $($p.dst)"
        }
    }
}

function Clean-OldRuns {
    param([int]$Days = 14, [string]$OutRoot = "D:\botg\runs")
    if (!(Test-Path -LiteralPath $OutRoot)) { 
        Write-Warning "OutRoot not found: $OutRoot"
        return
    }
    $cut = (Get-Date).AddDays(-$Days)
    $removed = 0
    Get-ChildItem -LiteralPath $OutRoot -Directory -ErrorAction SilentlyContinue |
      Where-Object { $_.LastWriteTime -lt $cut -and $_.Name -like "paper_smoke_60m_*" } |
      ForEach-Object { 
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
        Write-Host "Removed old run: $($_.Name)"
      }
    Write-Host "Clean-OldRuns: removed $removed runs, kept last $Days days under $OutRoot"
}

function Open-SmokeFolder {
    param([string]$OutRoot = "D:\botg\runs")
    if (!(Test-Path -LiteralPath $OutRoot)) { 
        Write-Warning "OutRoot not found: $OutRoot"
        return
    }
    $latest = Get-ChildItem -LiteralPath $OutRoot -Directory -Filter "paper_smoke_60m_v2_*" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { 
        Write-Host "Opening: $($latest.FullName)"
        Start-Process explorer.exe $latest.FullName 
    } else { 
        Write-Warning "No v2 runs found under $OutRoot" 
    }
}

Write-Host "ops.ps1 loaded. Available functions:"
" - Invoke-Smoke60mRun [-Segments 4] [-OutRoot ...] [-LogPath ...]"
" - Invoke-Smoke60mMergeLatest [-OutRoot ...] [-LogPath ...]"
" - Invoke-Smoke60mMergeExisting -ExistingRoot <path> [-LogPath ...]"
" - Show-Smoke60mReport [-OutRoot ...]"
" - Show-PhaseStats [-OutRoot ...]"
" - Run-UnitTests"
" - Invoke-Precommit"
" - Invoke-Rollback"
" - Clean-OldRuns [-Days 14] [-OutRoot ...]"
" - Open-SmokeFolder [-OutRoot ...]"