# CHANGE-002: Preflight Soft-Pass - Demonstration Script
# This script demonstrates the core concepts of CHANGE-002
# Full implementation requires modifying the existing 300+ line ctrader_connect.ps1

param(
    [ValidateSet('gate2', 'gate3')]
    [string]$Mode = 'gate2',
    [int]$NumProbes = 3,
    [int]$IntervalSec = 10,
    [string]$OutDir = "demo_output"
)

Write-Host "`n=== CHANGE-002 Demonstration ===" -ForegroundColor Cyan
Write-Host "Mode: $Mode" -ForegroundColor Yellow
Write-Host "Probes: $NumProbes" -ForegroundColor Yellow
Write-Host "Interval: ${IntervalSec}s`n" -ForegroundColor Yellow

# Simulate probe collection
$probes = @()
Write-Host "Collecting $NumProbes probe(s)..." -ForegroundColor Cyan

for ($i = 1; $i -le $NumProbes; $i++) {
    if ($i -gt 1) {
        Write-Host "Waiting ${IntervalSec}s..." -ForegroundColor Gray
        Start-Sleep -Seconds 2  # Shortened for demo
    }
    
    # Simulate metrics (in real implementation, these come from telemetry.csv)
    $simAge = 4.5 + (Get-Random -Minimum 0 -Maximum 10) / 10.0
    $simRatio = 0.75 + (Get-Random -Minimum 0 -Maximum 15) / 100.0
    $simTick = 1.0 + (Get-Random -Minimum 0 -Maximum 50) / 100.0
    
    $probe = [PSCustomObject]@{
        ts_utc = (Get-Date).ToUniversalTime().ToString('o')
        last_age_sec = [math]::Round($simAge, 1)
        active_ratio = [math]::Round($simRatio, 3)
        tick_rate = [math]::Round($simTick, 2)
    }
    $probes += $probe
    
    Write-Host "Probe $i`: last_age=$($probe.last_age_sec)s, ratio=$($probe.active_ratio), tick=$($probe.tick_rate)" -ForegroundColor Gray
}

# Calculate min_last_age_sec
$minAge = ($probes | ForEach-Object { $_.last_age_sec } | Measure-Object -Minimum).Minimum

# Check metrics
$allRatiosOk = ($probes | Where-Object { $_.active_ratio -lt 0.7 }).Count -eq 0
$allTicksOk = ($probes | Where-Object { $_.tick_rate -lt 0.5 }).Count -eq 0

# Determine result based on Mode
$ok = $false
$note = ""

if (-not $allRatiosOk) {
    $ok = $false
    $note = "fail: active_ratio violation"
}
elseif (-not $allTicksOk) {
    $ok = $false
    $note = "fail: tick_rate violation"
}
elseif ($Mode -eq 'gate2') {
    if ($minAge -le 5.0) {
        $ok = $true
        $note = "pass"
    }
    elseif ($minAge -le 5.5) {
        $ok = $true  # SOFT-PASS!
        $note = "warn: borderline freshness"
    }
    else {
        $ok = $false
        $note = "fail: min_last_age_sec=$minAge > 5.5"
    }
}
elseif ($Mode -eq 'gate3') {
    if ($minAge -le 5.0) {
        $ok = $true
        $note = "pass"
    }
    else {
        $ok = $false
        $note = "fail: min_last_age_sec=$minAge > 5.0 (gate3 hard)"
    }
}

# Output
Write-Host "`n=== Result ===" -ForegroundColor Cyan
Write-Host "min_last_age_sec: $minAge" -ForegroundColor Yellow
Write-Host "ok: $ok" -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
Write-Host "note: $note" -ForegroundColor $(if ($ok -and $note -match 'warn') { 'Yellow' } elseif ($ok) { 'Green' } else { 'Red' })

# Write output JSON
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$output = [PSCustomObject]@{
    ok = $ok
    mode = $Mode
    min_last_age_sec = $minAge
    probes = $probes
    note = $note
    generated_at_iso = (Get-Date).ToUniversalTime().ToString('o')
}

$outputPath = Join-Path $OutDir "connection_ok_demo.json"
$output | ConvertTo-Json -Depth 5 | Out-File $outputPath -Encoding UTF8

Write-Host "`nOutput written to: $outputPath" -ForegroundColor Cyan

if ($ok -and $note -match 'warn') {
    Write-Host "`n[WARN] Soft-pass (exit 0)" -ForegroundColor Yellow
    exit 0
}
elseif ($ok) {
    Write-Host "`n[PASS] (exit 0)" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "`n[FAIL] (exit 1)" -ForegroundColor Red
    exit 1
}
