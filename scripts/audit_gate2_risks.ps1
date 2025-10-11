<#
.SYNOPSIS
    Gate2 Risk Audit - PowerShell wrapper for Python audit script

.DESCRIPTION
    Analyzes Gate2 artifacts for 12 risk categories (R1-R12)
    Generates comprehensive CSV/JSON/MD reports

.PARAMETER InputDir
    Path to extracted artifacts directory

.PARAMETER OutDir
    Path to output reports directory

.EXAMPLE
    .\audit_gate2_risks.ps1 -InputDir "C:\artifacts\gate2_run" -OutDir ".\reports"
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$InputDir,
    
    [Parameter(Mandatory=$true)]
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

# Get script directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PythonScript = Join-Path $ScriptDir "audit_gate2_risks.py"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "GATE2 RISK AUDIT - PowerShell Wrapper" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Validate paths
if (-not (Test-Path $InputDir)) {
    Write-Host "ERROR: Input directory not found: $InputDir" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $PythonScript)) {
    Write-Host "ERROR: Python script not found: $PythonScript" -ForegroundColor Red
    exit 1
}

# Ensure output directory exists
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Write-Host "Input Directory : $InputDir" -ForegroundColor White
Write-Host "Output Directory: $OutDir" -ForegroundColor White
Write-Host "Python Script   : $PythonScript" -ForegroundColor White
Write-Host ""

# Check Python availability
$PythonCmd = $null
foreach ($cmd in @('python', 'python3', 'py')) {
    try {
        $version = & $cmd --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            $PythonCmd = $cmd
            Write-Host "✓ Found Python: $version" -ForegroundColor Green
            break
        }
    } catch {
        continue
    }
}

if (-not $PythonCmd) {
    Write-Host "ERROR: Python not found. Install Python 3.7+ and ensure it's in PATH" -ForegroundColor Red
    exit 1
}

# Check required Python packages
Write-Host "`nChecking Python dependencies..." -ForegroundColor Yellow
$RequiredPackages = @('pandas', 'numpy')

foreach ($pkg in $RequiredPackages) {
    try {
        $result = & $PythonCmd -c "import $pkg" 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ $pkg installed" -ForegroundColor Green
        } else {
            throw "Not installed"
        }
    } catch {
        Write-Host "  ✗ $pkg not installed" -ForegroundColor Red
        Write-Host "`nInstalling $pkg..." -ForegroundColor Yellow
        & $PythonCmd -m pip install $pkg --quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Failed to install $pkg" -ForegroundColor Red
            exit 1
        }
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Running Risk Audit (R1-R12)..." -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Run Python audit script
& $PythonCmd $PythonScript -InputDir $InputDir -OutDir $OutDir

$ExitCode = $LASTEXITCODE

if ($ExitCode -eq 0) {
    Write-Host "`n✅ AUDIT PASSED - All risks OK" -ForegroundColor Green
} else {
    Write-Host "`n⚠️ AUDIT FOUND ISSUES - Review reports in $OutDir" -ForegroundColor Yellow
}

Write-Host "`nGenerated Reports:" -ForegroundColor Cyan
Get-ChildItem $OutDir -File | ForEach-Object {
    $size = if ($_.Length -gt 0) { "$([math]::Round($_.Length/1KB,2)) KB" } else { "0 KB" }
    Write-Host "  • $($_.Name) ($size)" -ForegroundColor White
}

Write-Host ""
exit $ExitCode
