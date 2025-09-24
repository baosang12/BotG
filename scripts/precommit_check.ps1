#requires -Modules Pester
#requires -Version 5.1

<#
.SYNOPSIS
Pre-commit validation for BotG Smoke 60m system

.DESCRIPTION
Runs Pester tests and basic lint checks to ensure code quality before commits.
Use this script in your git pre-commit hook or manual validation workflow.

.EXAMPLE
.\scripts\precommit_check.ps1

.NOTES
Requires Pester module installed:
Install-Module Pester -Force -Scope CurrentUser
#>

param(
    [switch]$SkipTests,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$ScriptRoot = $PSScriptRoot
$RepoRoot = Split-Path $ScriptRoot -Parent

Write-Host "🔍 Pre-commit validation starting..." -ForegroundColor Cyan
Write-Host "Repository: $RepoRoot" -ForegroundColor Gray

# Check required files exist
$RequiredFiles = @(
    "scripts\lib_csv_merge.ps1",
    "scripts\run_smoke_60m_wrapper_v2.ps1", 
    "reconstruct_closed_trades_sqlite.py",
    "docs\RUNBOOK_smoke60m.md"
)

Write-Host "`n📁 Checking required files..." -ForegroundColor Yellow
$MissingFiles = @()
foreach ($file in $RequiredFiles) {
    $fullPath = Join-Path $RepoRoot $file
    if (-not (Test-Path -LiteralPath $fullPath)) {
        $MissingFiles += $file
        Write-Warning "❌ Missing: $file"
    } else {
        if ($Verbose) { Write-Host "✅ Found: $file" -ForegroundColor Green }
    }
}

if ($MissingFiles.Count -gt 0) {
    Write-Error "❌ Missing required files: $($MissingFiles -join ', ')"
    exit 1
}

# Basic syntax checks
Write-Host "`n🔧 PowerShell syntax validation..." -ForegroundColor Yellow
$PowerShellFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter "*.ps1" -File |
    Where-Object { $_.FullName -notlike "*\tests\*" -and $_.FullName -notlike "*\.git\*" }

foreach ($psFile in $PowerShellFiles) {
    try {
        $null = [System.Management.Automation.PSParser]::Tokenize((Get-Content -LiteralPath $psFile.FullName -Raw), [ref]$null)
        if ($Verbose) { Write-Host "✅ Syntax OK: $($psFile.Name)" -ForegroundColor Green }
    } catch {
        Write-Error "❌ Syntax error in $($psFile.Name): $_"
        exit 1
    }
}

# Python syntax check
Write-Host "`n🐍 Python syntax validation..." -ForegroundColor Yellow
$PythonFile = Join-Path $RepoRoot "reconstruct_closed_trades_sqlite.py"
if (Test-Path -LiteralPath $PythonFile) {
    try {
        $result = & python -m py_compile $PythonFile 2>&1
        if ($LASTEXITCODE -eq 0) {
            if ($Verbose) { Write-Host "✅ Python syntax OK" -ForegroundColor Green }
        } else {
            Write-Error "❌ Python syntax error: $result"
            exit 1
        }
    } catch {
        Write-Warning "⚠️ Could not validate Python syntax (python not in PATH?)"
    }
}

# Run Pester tests
if (-not $SkipTests) {
    Write-Host "`n🧪 Running Pester tests..." -ForegroundColor Yellow
    
    $TestsPath = Join-Path $RepoRoot "tests"
    if (-not (Test-Path -LiteralPath $TestsPath)) {
        Write-Warning "⚠️ Tests directory not found, skipping..."
    } else {
        try {
            $TestResults = Invoke-Pester -Path $TestsPath -PassThru -Output Minimal
            
            if ($TestResults.FailedCount -gt 0) {
                Write-Error "❌ $($TestResults.FailedCount) test(s) failed out of $($TestResults.TotalCount)"
                exit 1
            } else {
                Write-Host "✅ All $($TestResults.PassedCount) tests passed!" -ForegroundColor Green
            }
        } catch {
            Write-Error "❌ Test execution failed: $_"
            exit 1
        }
    }
} else {
    Write-Host "⏭️ Skipping tests (-SkipTests specified)" -ForegroundColor Gray
}

# Final validation
Write-Host "`n✅ Pre-commit validation PASSED!" -ForegroundColor Green
Write-Host "Ready for commit! 🚀" -ForegroundColor Cyan
exit 0