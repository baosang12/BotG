# Smoke Test A-Review Generator
# Analyzes smoke test output and generates DoD compliance report

param(
    [Parameter(Mandatory=$true)]
    [string]$ArtifactPath,
    
    [string]$OutputDir = "D:\tmp\g2\18280282361\A_review_out"
)

$ErrorActionPreference = 'Stop'

Write-Host "`n=== A-Review Generator for Smoke Test ===" -ForegroundColor Cyan
Write-Host "Artifact: $ArtifactPath" -ForegroundColor Gray
Write-Host "Output: $OutputDir`n" -ForegroundColor Gray

# Ensure output directory
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# Find latest run directory
$runs = Get-ChildItem -Path $ArtifactPath -Filter "telemetry_run_*" -Directory | Sort-Object Name -Descending
if ($runs.Count -eq 0) {
    Write-Error "No telemetry_run_* directories found in $ArtifactPath"
    exit 1
}

$latestRun = $runs[0].FullName
Write-Host "Latest run: $latestRun" -ForegroundColor Green

# Required files
$requiredFiles = @(
    'orders.csv',
    'telemetry.csv', 
    'risk_snapshots.csv',
    'run_metadata.json'
)

# Check file presence
Write-Host "`nFile Presence Check:" -ForegroundColor Yellow
$presence = @{}
foreach ($f in $requiredFiles) {
    $path = Join-Path $latestRun $f
    $exists = Test-Path $path
    $size = if ($exists) { (Get-Item $path).Length } else { 0 }
    $lines = if ($exists -and $f -match '\.csv$') { 
        (Get-Content $path | Measure-Object -Line).Lines 
    } else { $null }
    
    $presence[$f] = @{
        exists = $exists
        size_bytes = $size
        line_count = $lines
    }
    
    $status = if ($exists) { "OK" } else { "MISSING" }
    $statusColor = if ($exists) { "Green" } else { "Red" }
    Write-Host "  [$status] $f ($size bytes, $lines lines)" -ForegroundColor $statusColor
}

# Analyze risk_snapshots density
Write-Host "`nRisk Snapshots Density:" -ForegroundColor Yellow
$riskPath = Join-Path $latestRun 'risk_snapshots.csv'
if (Test-Path $riskPath) {
    $riskRows = Import-Csv $riskPath
    $rowCount = $riskRows.Count
    
    if ($rowCount -gt 0) {
        # Get timestamp column (could be 'timestamp' or 'timestamp_utc')
        $tsCol = if ($riskRows[0].PSObject.Properties.Name -contains 'timestamp_utc') { 'timestamp_utc' } else { 'timestamp' }
        
        $first = [datetime]$riskRows[0].$tsCol
        $last = [datetime]$riskRows[-1].$tsCol
        $spanHours = ($last - $first).TotalHours
        $density = if ($spanHours -gt 0) { [math]::Round($rowCount / $spanHours, 2) } else { 0 }
        
        Write-Host "  Start: $first" -ForegroundColor Gray
        Write-Host "  End: $last" -ForegroundColor Gray
        Write-Host "  Span: $([math]::Round($spanHours, 2)) hours" -ForegroundColor Gray
        Write-Host "  Rows: $rowCount" -ForegroundColor Gray
        Write-Host "  Density: $density samples/hour" -ForegroundColor $(if ($density -ge 1) {"Green"} else {"Red"})
        
        $riskAnalysis = @{
            span_hours = [math]::Round($spanHours, 2)
            row_count = $rowCount
            density_per_hour = $density
            pass = $density -ge 1.0
        }
    } else {
        $riskAnalysis = @{ error = "No data rows" }
    }
} else {
    $riskAnalysis = @{ error = "File not found" }
}

# Analyze orders timestamp null rate
Write-Host "`nOrders Timestamp Null Rate:" -ForegroundColor Yellow
$ordersPath = Join-Path $latestRun 'orders.csv'
$nullRates = @{}
if (Test-Path $ordersPath) {
    $ordersRows = Import-Csv $ordersPath
    $totalOrders = $ordersRows.Count
    
    foreach ($tsField in @('timestamp_request', 'timestamp_ack', 'timestamp_fill')) {
        if ($ordersRows[0].PSObject.Properties.Name -contains $tsField) {
            $nullCount = ($ordersRows | Where-Object { -not $_.$tsField -or $_.$tsField -eq '' }).Count
            $nullPct = [math]::Round(($nullCount * 100.0 / [math]::Max(1, $totalOrders)), 2)
            $nullRates[$tsField] = @{
                null_count = $nullCount
                total = $totalOrders
                null_pct = $nullPct
            }
            
            $status = if ($nullPct -le 5) { "PASS" } else { "FAIL" }
            $color = if ($nullPct -le 5) { "Green" } else { "Red" }
            Write-Host "  [$status] $tsField : $nullPct% null ($nullCount/$totalOrders)" -ForegroundColor $color
        } else {
            Write-Host "  [WARN] $tsField : Column not found" -ForegroundColor Yellow
            $nullRates[$tsField] = @{ error = "Column not found" }
        }
    }
} else {
    Write-Host "  [ERROR] orders.csv not found" -ForegroundColor Red
}

# Analyze run_metadata
Write-Host "`nRun Metadata:" -ForegroundColor Yellow
$metaPath = Join-Path $latestRun 'run_metadata.json'
if (Test-Path $metaPath) {
    $meta = Get-Content $metaPath -Raw | ConvertFrom-Json
    
    $hoursOk = $meta.hours -eq 24
    $modeOk = $meta.mode -eq 'paper'
    $simOk = -not $meta.simulation.enabled
    $sphOk = $meta.seconds_per_hour -eq 3600
    
    Write-Host "  hours: $($meta.hours) $(if ($hoursOk) {'OK'} else {'FAIL'})" -ForegroundColor $(if ($hoursOk) {"Green"} else {"Red"})
    Write-Host "  mode: $($meta.mode) $(if ($modeOk) {'OK'} else {'FAIL'})" -ForegroundColor $(if ($modeOk) {"Green"} else {"Red"})
    Write-Host "  simulation.enabled: $($meta.simulation.enabled) $(if ($simOk) {'OK'} else {'FAIL'})" -ForegroundColor $(if ($simOk) {"Green"} else {"Red"})
    Write-Host "  seconds_per_hour: $($meta.seconds_per_hour) $(if ($sphOk) {'OK'} else {'FAIL'})" -ForegroundColor $(if ($sphOk) {"Green"} else {"Red"})
    
    $metaAnalysis = @{
        hours = $meta.hours
        mode = $meta.mode
        simulation_enabled = $meta.simulation.enabled
        seconds_per_hour = $meta.seconds_per_hour
        pass = $hoursOk -and $modeOk -and $simOk -and $sphOk
    }
} else {
    Write-Host "  [ERROR] run_metadata.json not found" -ForegroundColor Red
    $metaAnalysis = @{ error = "File not found" }
}

# Generate DoD verdict
Write-Host "`n=== DoD-A Verdict ===" -ForegroundColor Cyan
$dodPassed = @()
$dodFailed = @()

if ($riskAnalysis.pass) {
    $dodPassed += "Risk heartbeat density >= 1/h"
} else {
    $dodFailed += "Risk heartbeat density < 1/h"
}

$allTsPass = $true
foreach ($tsField in $nullRates.Keys) {
    if ($nullRates[$tsField].null_pct -gt 5) {
        $allTsPass = $false
        $dodFailed += "$tsField null rate > 5%"
    }
}
if ($allTsPass) {
    $dodPassed += "All timestamp fields <= 5% null"
}

if ($metaAnalysis.pass) {
    $dodPassed += "Metadata: hours=24, mode=paper, simulation=false, sph=3600"
} else {
    $dodFailed += "Metadata config incorrect"
}

$overallPass = $dodFailed.Count -eq 0

Write-Host "PASSED DoD items:" -ForegroundColor Green
foreach ($item in $dodPassed) {
    Write-Host "  ✓ $item" -ForegroundColor Green
}

if ($dodFailed.Count -gt 0) {
    Write-Host "`nFAILED DoD items:" -ForegroundColor Red
    foreach ($item in $dodFailed) {
        Write-Host "  ✗ $item" -ForegroundColor Red
    }
}

Write-Host "`nOverall DoD-A: $(if ($overallPass) {'PASS'} else {'FAIL'})" -ForegroundColor $(if ($overallPass) {"Green"} else {"Red"})

# Generate JSON output
$reviewJson = @{
    review_id = "A_review_smoke_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
    run_path = $latestRun
    timestamp_utc = (Get-Date).ToUniversalTime().ToString('o')
    file_presence = $presence
    risk_analysis = $riskAnalysis
    timestamp_null_rates = $nullRates
    metadata_analysis = $metaAnalysis
    dod_verdict = @{
        passed = $dodPassed
        failed = $dodFailed
        overall_pass = $overallPass
    }
} | ConvertTo-Json -Depth 10

$jsonPath = Join-Path $OutputDir "A_review_smoke_$(Get-Date -Format 'yyyyMMdd_HHmmss').json"
$reviewJson | Out-File -FilePath $jsonPath -Encoding UTF8
Write-Host "`n✓ JSON report: $jsonPath" -ForegroundColor Green

# Generate Markdown output
$md = @"
# A-Review: Smoke Test DoD Compliance

**Generated:** $(Get-Date -Format 'o')
**Run Path:** ``$latestRun``

## DoD-A Verdict: **$(if ($overallPass) {'PASS'} else {'FAIL'})**

### Passed Items
$(foreach ($item in $dodPassed) { "- ✅ $item`n" })

$(if ($dodFailed.Count -gt 0) {
"### Failed Items
$(foreach ($item in $dodFailed) { "- ❌ $item`n" })"
})

---

## Detailed Analysis

### File Presence
$(foreach ($f in $presence.Keys) {
    $p = $presence[$f]
    "- ``$f``: $(if ($p.exists) {'✅ Exists'} else {'❌ Missing'}) ($($p.size_bytes) bytes, $($p.line_count) lines)"
})

### Risk Snapshots
- Span: $($riskAnalysis.span_hours) hours
- Rows: $($riskAnalysis.row_count)
- **Density: $($riskAnalysis.density_per_hour) samples/hour** $(if ($riskAnalysis.pass) {'✅'} else {'❌'})

### Timestamp Null Rates
$(foreach ($tsField in $nullRates.Keys) {
    $nr = $nullRates[$tsField]
    if ($nr.null_pct) {
        "- ``$tsField``: **$($nr.null_pct)%** null ($($nr.null_count)/$($nr.total)) $(if ($nr.null_pct -le 5) {'✅'} else {'❌'})"
    } else {
        "- ``$tsField``: ⚠️ $($nr.error)"
    }
})

### Metadata
- hours: ``$($metaAnalysis.hours)`` $(if ($metaAnalysis.hours -eq 24) {'✅'} else {'❌'})
- mode: ``$($metaAnalysis.mode)`` $(if ($metaAnalysis.mode -eq 'paper') {'✅'} else {'❌'})
- simulation.enabled: ``$($metaAnalysis.simulation_enabled)`` $(if (-not $metaAnalysis.simulation_enabled) {'✅'} else {'❌'})
- seconds_per_hour: ``$($metaAnalysis.seconds_per_hour)`` $(if ($metaAnalysis.seconds_per_hour -eq 3600) {'✅'} else {'❌'})

---

## Next Steps

$(if ($overallPass) {
"✅ **DoD-A PASSED** - Ready for Agent B cross-validation and PR creation"
} else {
"❌ **DoD-A FAILED** - Fix issues above and re-run smoke test"
})

"@

$mdPath = Join-Path $OutputDir "A_review_smoke_$(Get-Date -Format 'yyyyMMdd_HHmmss').md"
$md | Out-File -FilePath $mdPath -Encoding UTF8
Write-Host "✓ Markdown report: $mdPath" -ForegroundColor Green

Write-Host "`n=== A-Review Complete ===" -ForegroundColor Cyan

if (-not $overallPass) {
    exit 1
}
