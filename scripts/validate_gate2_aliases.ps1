# Gate2 Alias Compliance Validator (CHANGE-001)
# Purpose: Verify CSV headers contain required Gate2 alias columns
# Usage: .\scripts\validate_gate2_aliases.ps1 -ArtifactsDir <path>

param(
    [Parameter(Mandatory=$true)]
    [string]$ArtifactsDir
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Gate2 Alias Compliance Validator" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Define required aliases per file
$requirements = @{
    "orders.csv" = @{
        Description = "Order lifecycle CSV"
        RequiredAliases = @("latency", "request_id", "ts_request", "ts_ack", "ts_fill")
    }
    "risk_snapshots.csv" = @{
        Description = "Risk snapshots CSV"
        RequiredAliases = @("ts")
    }
    "telemetry.csv" = @{
        Description = "Telemetry CSV"
        RequiredAliases = @("timestamp")
    }
}

$overallPass = $true
$results = @()

foreach ($fileName in $requirements.Keys) {
    $filePath = Join-Path $ArtifactsDir $fileName
    $requirement = $requirements[$fileName]
    
    Write-Host "Checking $fileName ..." -ForegroundColor Yellow
    Write-Host "  Description: $($requirement.Description)" -ForegroundColor Gray
    Write-Host "  Required aliases: $($requirement.RequiredAliases -join ', ')" -ForegroundColor Gray
    
    if (-not (Test-Path $filePath)) {
        Write-Host "  ✗ FAIL: File not found at $filePath" -ForegroundColor Red
        $overallPass = $false
        $results += @{
            File = $fileName
            Status = "FAIL"
            Reason = "File not found"
            MissingAliases = $null
        }
        continue
    }
    
    # Read header line
    try {
        $header = Get-Content $filePath -First 1 -ErrorAction Stop
        $headerColumns = $header -split ','
        
        # Check for required aliases
        $missingAliases = @()
        foreach ($alias in $requirement.RequiredAliases) {
            if ($headerColumns -notcontains $alias) {
                $missingAliases += $alias
            }
        }
        
        if ($missingAliases.Count -gt 0) {
            Write-Host "  ✗ FAIL: Missing aliases: $($missingAliases -join ', ')" -ForegroundColor Red
            $overallPass = $false
            $results += @{
                File = $fileName
                Status = "FAIL"
                Reason = "Missing aliases"
                MissingAliases = $missingAliases
            }
        } else {
            Write-Host "  ✓ PASS: All required aliases present" -ForegroundColor Green
            $results += @{
                File = $fileName
                Status = "PASS"
                Reason = $null
                MissingAliases = $null
            }
        }
    }
    catch {
        Write-Host "  ✗ FAIL: Error reading file: $_" -ForegroundColor Red
        $overallPass = $false
        $results += @{
            File = $fileName
            Status = "FAIL"
            Reason = "Error reading file: $_"
            MissingAliases = $null
        }
    }
    
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Cyan
if ($overallPass) {
    Write-Host " VALIDATION RESULT: PASS ✓" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "All Gate2 alias columns are present." -ForegroundColor Green
    exit 0
} else {
    Write-Host " VALIDATION RESULT: FAIL ✗" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "One or more required aliases are missing." -ForegroundColor Red
    Write-Host ""
    Write-Host "Failures:" -ForegroundColor Yellow
    foreach ($result in $results) {
        if ($result.Status -eq "FAIL") {
            Write-Host "  - $($result.File): $($result.Reason)" -ForegroundColor Red
            if ($result.MissingAliases) {
                Write-Host "    Missing: $($result.MissingAliases -join ', ')" -ForegroundColor Red
            }
        }
    }
    exit 1
}
