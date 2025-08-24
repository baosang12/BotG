param(
    [int]$ProcId = 5340,
    [string]$OutDir = "D:\OneDrive\Tài liệu\cAlgo\Sources\Robots\BotG\artifacts_ascii\paper_run_realtime_1h_20250824_183511",
    [int]$LogTail = 100
)

function Safe-Json {
    param($obj,$path)
    $json = $obj | ConvertTo-Json -Depth 6
    $json | Out-File -FilePath $path -Encoding utf8
    return $json
}

$ts = (Get-Date).ToString("yyyyMMdd_HHmmss")
$outReport = Join-Path -Path (Join-Path (Get-Location) "artifacts_ascii") -ChildPath "health_check_$ts.json"
$report = [ordered]@{
    STATUS = $null
    can_continue_running = $null
    summary = ""
    process = @{
        found = $false
    pid = $ProcId
        name = $null
        start_time = $null
        cpu_percent = $null
        memory_mb = $null
    }
    logs = @{
        path = $null
        tail = @()
    }
    disk = @{
        drive = (Split-Path -Qualifier $OutDir)
        free_gb = $null
    }
    temp_artifacts = @()
    outdir = @{
        path = $OutDir
        exists = $false
        files = @()
    }
    run_metadata = $null
    orders_header = $null
    reconstruct_tool = @{
        present = $false
        path = $null
    }
    host_resources = @{
        cpu_percent = $null
        free_memory_mb = $null
    }
    permissions = @{
        outdir_access = "unknown"
        files_locked = @()
    }
    issues = @()
    recommendations = @()
    notes = ""
}

try {
    # 1) Process status
    $p = $null
    try { $p = Get-Process -Id $ProcId -ErrorAction Stop } catch { $p = $null }
    if ($p) {
        $report.process.found = $true
        $report.process.name = $p.ProcessName
        $report.process.start_time = $p.StartTime.ToString("o")
        $memMB = [math]::Round($p.WorkingSet64 / 1MB,1)
        $report.process.memory_mb = $memMB
        # CPU sample over 500ms
        $t1 = $p.TotalProcessorTime
        Start-Sleep -Milliseconds 500
    try { $p2 = Get-Process -Id $ProcId -ErrorAction Stop } catch { $p2 = $null }
        if ($p2) {
            $t2 = $p2.TotalProcessorTime
            $cpuPct = [math]::Round((($t2 - $t1).TotalMilliseconds / 500) * 100 / $env:NUMBER_OF_PROCESSORS,1)
            $report.process.cpu_percent = $cpuPct
        }
    } else {
        $report.process.found = $false
    $report.issues += "Process with PID $ProcId not found or already exited."
        $report.can_continue_running = $false
    }

    # 2) Logs tail: try OUTDIR then TEMP
    $logFound = $false
    $possibleRunDirs = @()
    if (Test-Path -LiteralPath $OutDir) {
        $report.outdir.exists = $true
        $report.outdir.files = (Get-ChildItem -LiteralPath $OutDir -Force -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        # find telemetry subdir
        $dirs = Get-ChildItem -LiteralPath $OutDir -Directory -Filter "telemetry_run_*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
        if ($dirs.Count -gt 0) { $possibleRunDirs += $dirs[0].FullName }
    } else {
        $report.outdir.exists = $false
    }
    # fallback: find recent telemetry_run_* in TEMP
    $tempMatches = @()
    try {
        $temp = $env:TEMP
        if ($temp) {
            $tempMatches = Get-ChildItem -Path (Join-Path $temp "telemetry_run_*") -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
            if ($tempMatches.Count -gt 0) { $possibleRunDirs += $tempMatches[0].FullName }
        }
    } catch {}

    foreach ($dir in $possibleRunDirs | Select-Object -Unique) {
        $candidates = @(
            Join-Path $dir "harness_stdout.log",
            Join-Path $dir "harness.log",
            Join-Path $dir "logs\harness.log",
            Join-Path $dir "harness_stderr.log"
        )
        foreach ($c in $candidates) {
            if (Test-Path -LiteralPath $c) {
                $report.logs.path = $c
                $report.logs.tail = Get-Content -LiteralPath $c -Tail $LogTail -ErrorAction SilentlyContinue
                $logFound = $true
                break
            }
        }
        if ($logFound) { break }
    }
    if (-not $logFound) {
        $report.logs.path = $null
        $report.logs.tail = @()
        $report.issues += "No harness log found in OUTDIR or recent TEMP telemetry run dir."
    }

    # 3) Disk free
    try {
        $drive = (Split-Path -Qualifier $OutDir) -replace "\\$",""
        $ld = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$drive'"
        if ($ld) { $report.disk.free_gb = [math]::Round($ld.FreeSpace/1GB,2) }
        if ($report.disk.free_gb -lt 5 -and $report.disk.free_gb -ne $null) {
            $report.issues += "Low disk space on ${drive}: $($report.disk.free_gb) GB free (recommend >= 5GB)."
            $report.recommendations += "Free up disk space on ${drive} or move OUTDIR to a drive with more space."
        }
    } catch { $report.disk.free_gb = $null }

    # 4) TEMP artifacts (newest 5 in last 3h)
    try {
        $win = (Get-Date).AddHours(-3)
        $tempList = @()
        if ($env:TEMP) {
            $tempList += Get-ChildItem -Path (Join-Path $env:TEMP "telemetry_run_*") -Directory -ErrorAction SilentlyContinue
            $tempList += Get-ChildItem -Path (Join-Path $env:TEMP "botg_artifacts*") -Directory -ErrorAction SilentlyContinue
            $tempList = $tempList | Where-Object { $_.LastWriteTime -ge $win } | Sort-Object LastWriteTime -Descending | Select-Object -First 5
            foreach ($it in $tempList) {
                $sz = 0
                try { $sz = (Get-ChildItem -LiteralPath $it.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Sum Length).Sum } catch {}
                $report.temp_artifacts += [pscustomobject]@{ path = $it.FullName; last_write = $it.LastWriteTime.ToString("o"); size_bytes = [int64]$sz }
            }
        }
        if ($report.temp_artifacts.Count -eq 0) {
            $report.issues += "No recent telemetry_run_* or botg_artifacts* found in TEMP within last 3 hours."
        }
    } catch { }

    # 5) outdir files already captured above

    # 6) run_metadata.json (most recent)
    try {
        $runMetaFound = $false
        $searchDirs = @()
        if ($report.outdir.exists) {
            $dirs = Get-ChildItem -LiteralPath $OutDir -Directory -Filter "telemetry_run_*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
            if ($dirs.Count -gt 0) { $searchDirs += $dirs[0].FullName }
        }
        if ($env:TEMP) {
            $dirsTemp = Get-ChildItem -Path (Join-Path $env:TEMP "telemetry_run_*") -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
            if ($dirsTemp.Count -gt 0) { $searchDirs += $dirsTemp[0].FullName }
        }
        foreach ($d in $searchDirs | Select-Object -Unique) {
            $rm = Join-Path $d "run_metadata.json"
            if (Test-Path -LiteralPath $rm) {
                $content = Get-Content -LiteralPath $rm -Raw -ErrorAction SilentlyContinue
                try { $obj = $content | ConvertFrom-Json; $report.run_metadata = $obj; $runMetaFound = $true; break } catch {}
            }
        }
        if (-not $runMetaFound) { $report.issues += "run_metadata.json not found in OUTDIR or recent TEMP telemetry dir." }
    } catch {}

    # 7) orders.csv header
    try {
        $ordersHeader = $null
        foreach ($d in $searchDirs | Select-Object -Unique) {
            $f = Join-Path $d "orders.csv"
            if (Test-Path -LiteralPath $f) {
                $header = Get-Content -LiteralPath $f -TotalCount 1 -ErrorAction SilentlyContinue
                if ($header) {
                    $report.orders_header = ($header -split ",") | ForEach-Object { $_.Trim() }
                    break
                }
            }
        }
        if (-not $report.orders_header) { $report.issues += "orders.csv not found or header missing in run dirs." }
    } catch {}

    # 8) reconstruct tool presence
    $reconPaths = @("tools\reconstruct.py","reconstruct_closed_trades_sqlite.py","Tools\ReconstructClosedTrades")
    foreach ($rp in $reconPaths) {
        if (Test-Path -LiteralPath (Join-Path (Get-Location) $rp)) {
            $report.reconstruct_tool.present = $true
            $report.reconstruct_tool.path = (Join-Path (Get-Location) $rp)
            break
        }
    }
    if (-not $report.reconstruct_tool.present) { $report.reconstruct_tool.present = $false; $report.issues += "Reconstruct tool not found in expected paths." }

    # 9) host resources
    try {
        $cpu = [math]::Round((Get-Counter '\\Processor(_Total)\\% Processor Time').CounterSamples.CookedValue,1)
        $mem = Get-CimInstance Win32_OperatingSystem
        $freeMB = [math]::Round($mem.FreePhysicalMemory/1024,0)
        $report.host_resources.cpu_percent = $cpu
        $report.host_resources.free_memory_mb = $freeMB
        if ($cpu -gt 90) { $report.issues += "High CPU usage: $cpu%"; $report.recommendations += "Consider pausing long runs if CPU > 90%." }
        if ($freeMB -lt 500) { $report.issues += "Low free memory: ${freeMB}MB"; $report.recommendations += "Free memory <500MB; consider stopping run to avoid OOM." }
    } catch {}

    # 10) permissions - try listing OUTDIR
    try {
        if (Test-Path -LiteralPath $OutDir) {
            try { Get-ChildItem -LiteralPath $OutDir -ErrorAction Stop | Out-Null; $report.permissions.outdir_access = "ok" } catch { $report.permissions.outdir_access = "denied"; $report.issues += "No permission to list OUTDIR." }
        } else {
            $report.permissions.outdir_access = "unknown"
        }
    } catch {}

    # set summary & STATUS heuristics
    $status = "OK"
    if ($report.issues.Count -gt 0) {
        # determine severity
        $crit = $false
        foreach ($i in $report.issues) {
            if ($i -match "not found" -or $i -match "No recent telemetry" -or $i -match "not found or already exited") { $crit = $true; break }
        }
        if ($crit) { $status = "CRITICAL"; $report.can_continue_running = $false } else { $status = "WARNING"; $report.can_continue_running = "unknown" }
    } else {
        $status = "OK"; $report.can_continue_running = $true
    }
    $report.STATUS = $status
    $report.summary = if ($status -eq "OK") { "All quick checks passed: no immediate action required." } elseif ($status -eq "WARNING") { "Non-critical issues found; review recommendations." } else { "Critical issues found; consider stopping run and performing recovery." }

} catch {
    $report.STATUS = "CANNOT_CHECK"
    $report.notes = "Exception during health check: " + $_.Exception.Message
    $report.issues += "Health-check script encountered an error."
    $report.can_continue_running = "unknown"
}

# write report
$reportJson = Safe-Json $report $outReport
Write-Output $reportJson
Write-Host "Health check JSON saved to: $outReport"

