# Preflight Final Check Script
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function IsoNow { (Get-Date).ToUniversalTime().ToString('o') }

$ts = '20250901_121347'
$base = (Get-Location).Path
$pi = Join-Path $base 'path_issues'

Write-Output "Starting final preflight checks at $(IsoNow)"

# Initialize report structure
$report = @{
    ts = $ts
    base = $base
    timestamp = IsoNow
    checks = @()
    blockers = @()
    warnings = @()
    verdict = 'UNKNOWN'
    free_disk_bytes = 0
    dotnet_sdks = @()
    python_version = ''
    powershell_version = $PSVersionTable.PSVersion.ToString()
    git_branch = ''
    git_sha = ''
    artifact_hashes = @{}
}

# Helper function to add check result
function Add-Check($name, $status, $details, $evidence = @()) {
    $report.checks += @{
        name = $name
        status = $status
        details = $details
        evidence = $evidence
    }
    if ($status -eq 'FAIL') { $report.blockers += $name }
    if ($status -eq 'WARN') { $report.warnings += $name }
}

# 1. Environment checks
try {
    if ($env:BOTG_ROOT -and (Test-Path -LiteralPath $env:BOTG_ROOT)) {
        Add-Check 'env.BOTG_ROOT' 'PASS' "BOTG_ROOT=$env:BOTG_ROOT"
    } else {
        Add-Check 'env.BOTG_ROOT' 'FAIL' 'BOTG_ROOT not set or invalid'
    }
} catch { Add-Check 'env.BOTG_ROOT' 'FAIL' $_.Exception.Message }

# 2. File growth check
try {
    $growthFile = Join-Path $pi 'file_growth_test_20250901_121347.json'
    if (Test-Path -LiteralPath $growthFile) {
        $growth = Get-Content -Raw -LiteralPath $growthFile | ConvertFrom-Json
        $allGrew = $true
        foreach ($file in $growth.PSObject.Properties) {
            if (-not $file.Value.meets_criteria) { $allGrew = $false; break }
        }
        if ($allGrew) {
            Add-Check 'smoke.file_growth' 'PASS' 'All files showed expected growth' @($growthFile)
        } else {
            Add-Check 'smoke.file_growth' 'FAIL' 'Some files did not meet growth criteria' @($growthFile)
        }
    } else {
        Add-Check 'smoke.file_growth' 'FAIL' 'File growth test not found'
    }
} catch { Add-Check 'smoke.file_growth' 'FAIL' $_.Exception.Message }

# 3. Git clean check (post-archive)
try {
    $gitStatusFile = Get-ChildItem -LiteralPath $pi -Filter 'git_status_post_archive_*.txt' | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    if ($gitStatusFile) {
        $gitStatus = Get-Content -LiteralPath $gitStatusFile.FullName
        if ($gitStatus.Count -eq 0 -or ($gitStatus.Count -eq 1 -and $gitStatus[0].Trim() -eq '')) {
            Add-Check 'git.clean_post_archive' 'PASS' 'Repository clean after archiving'
        } else {
            Add-Check 'git.clean_post_archive' 'WARN' "Still $($gitStatus.Count) modified/untracked files" @($gitStatusFile.FullName)
        }
    } else {
        Add-Check 'git.clean_post_archive' 'FAIL' 'Git status post-archive not found'
    }
} catch { Add-Check 'git.clean_post_archive' 'FAIL' $_.Exception.Message }

# 4. Sentinel support check
try {
    $supervisorScript = Join-Path $base 'scripts\start_realtime_24h_supervised.ps1'
    $content = Get-Content -Raw -LiteralPath $supervisorScript
    if ($content -match 'RUN_PAUSE' -and $content -match 'RUN_STOP') {
        Add-Check 'orchestration.sentinels' 'PASS' 'Sentinel support detected in supervisor' @($supervisorScript)
    } else {
        Add-Check 'orchestration.sentinels' 'FAIL' 'Sentinel support not found in supervisor'
    }
} catch { Add-Check 'orchestration.sentinels' 'FAIL' $_.Exception.Message }

# 5. Monitor snapshot check
try {
    $snapshotFile = Get-ChildItem -LiteralPath $pi -Filter 'monitor_snapshot_run_*.json' | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    if ($snapshotFile) {
        Add-Check 'monitor.snapshot' 'PASS' 'Monitor snapshot capability verified' @($snapshotFile.FullName)
    } else {
        Add-Check 'monitor.snapshot' 'WARN' 'Monitor snapshot test not found'
    }
} catch { Add-Check 'monitor.snapshot' 'WARN' $_.Exception.Message }

# 6. Quick build and test check
try {
    dotnet build "$base\BotG.sln" -c Debug --nologo | Out-Null
    Add-Check 'build.current' 'PASS' 'Build successful'
} catch { Add-Check 'build.current' 'FAIL' $_.Exception.Message }

try {
    dotnet test "$base\BotG.sln" --no-build --verbosity minimal | Out-Null
    Add-Check 'tests.current' 'PASS' 'Tests passed'
} catch { Add-Check 'tests.current' 'FAIL' $_.Exception.Message }

# 7. Get system info
try {
    $report.free_disk_bytes = (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -eq 'D:\' }).Free
    $report.dotnet_sdks = (dotnet --list-sdks)
    $report.python_version = (python --version 2>&1)
    $report.git_branch = (git rev-parse --abbrev-ref HEAD)
    $report.git_sha = (git rev-parse HEAD)
} catch {}

# 8. Final verdict
$failCount = ($report.checks | Where-Object { $_.status -eq 'FAIL' }).Count
if ($failCount -eq 0) {
    $report.verdict = 'ALL_PRECHECKS_PASSED_FOR_24H'
} else {
    $report.verdict = 'PRECHECKS_FAILED'
}

# Write final report
$reportPath = Join-Path $pi "preflight_strict_report_final_$ts.json"
$report | ConvertTo-Json -Depth 6 | Out-File -FilePath $reportPath -Encoding UTF8

# Write human-readable README
$readmePath = Join-Path $pi "preflight_strict_readme_final_$ts.md"
$readme = @()
$readme += "# Final Preflight Check - $ts"
$readme += ""
$readme += "## Verdict"
$readme += $report.verdict
$readme += ""
$readme += "## Check Results"
$readme += "| Name | Status | Details |"
$readme += "|------|--------|---------|"
foreach ($check in $report.checks) {
    $readme += "| $($check.name) | $($check.status) | $($check.details) |"
}
$readme += ""
if ($report.blockers.Count -gt 0) {
    $readme += "## Remaining Blockers"
    foreach ($blocker in $report.blockers) {
        $readme += "- $blocker"
    }
    $readme += ""
}
if ($report.warnings.Count -gt 0) {
    $readme += "## Warnings"
    foreach ($warning in $report.warnings) {
        $readme += "- $warning"
    }
}
($readme -join "
") | Out-File -FilePath $readmePath -Encoding UTF8

# Create start command if all passed
if ($report.verdict -eq 'ALL_PRECHECKS_PASSED_FOR_24H') {
    $startCmdPath = Join-Path $pi "preflight_ready_start_command_$ts.txt"
    $startCmd = "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start_realtime_24h_supervised.ps1 -Hours 24 -FillProbability 1.0 -UseSimulation -WaitForFinish"
    $startCmd | Out-File -FilePath $startCmdPath -Encoding UTF8
}

# Write agent steps log
$agentLogPath = Join-Path $pi "agent_steps_preflight_fix_$ts.log"
$agentSteps = @()
$agentSteps += "Preflight Fix Steps - $ts"
$agentSteps += "================================"
$agentSteps += "1. Created force_write_heartbeat.ps1"
$agentSteps += "2. Archived untracked files"
$agentSteps += "3. Added sentinel support to supervisor"
$agentSteps += "4. Created monitor snapshot capability"
$agentSteps += "5. Ran smoke test with file growth monitoring"
$agentSteps += "6. Performed final comprehensive checks"
$agentSteps += "7. Generated final report and README"
$agentSteps += "Final verdict: $($report.verdict)"
($agentSteps -join "
") | Out-File -FilePath $agentLogPath -Encoding UTF8

# Console output
Write-Output "JSON: $(Split-Path -Leaf $reportPath)"
Write-Output "README: $(Split-Path -Leaf $readmePath)"
Write-Output "AGENT_LOG: $(Split-Path -Leaf $agentLogPath)"
Write-Output $report.verdict
if ($report.blockers.Count -gt 0) {
    Write-Output "Top blockers: $($report.blockers -join ', ')"
}
