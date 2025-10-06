# 24H RUN MONITORING SCRIPT
$runPid = 37288
$runTimestamp = '20250902_145432'
$snapshotsDir = 'path_issues\snapshots'
$logFile = 'path_issues\agent_steps_start_20250902_145432.log'
$lastOrdersSize = 0
$lastOrdersTime = Get-Date
$snapshotCount = 0

Write-Host "Starting 24h run monitoring for PID: $runPid" -ForegroundColor Green

while ($true) {
    $snapshotCount++
    $snapshotTime = Get-Date -Format 'yyyyMMdd_HHmmss'
    $snapshotFile = Join-Path $snapshotsDir "${snapshotTime}_${snapshotCount}.log"
    
    # Check if process is still running
    try {
        $process = Get-Process -Id $runPid -ErrorAction Stop
        $processAlive = $true
    } catch {
        $processAlive = $false
        Write-Host "Process PID $runPid terminated - ending monitoring" -ForegroundColor Red
        break
    }
    
    # Create snapshot
    $snapshot = @()
    $snapshot += "=== SNAPSHOT $snapshotCount at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
    $snapshot += "Process PID: $runPid - ALIVE"
    
    # Check disk space
    try {
        $disk = Get-WmiObject -Class Win32_LogicalDisk -Filter "DeviceID='D:'"
        $freeGB = [math]::Round($disk.FreeSpace / 1GB, 2)
        $snapshot += "Disk Free: $freeGB GB"
        
        if ($freeGB -lt 10) {
            $alertFile = "path_issues\alert_disk_low_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
            "ALERT: Low disk space at $(Get-Date)
Free space: $freeGB GB" | Out-File -FilePath $alertFile
            $snapshot += "ALERT: Low disk space - $alertFile created"
        }
    } catch {
        $snapshot += "ERROR: Could not check disk space"
    }
    
    # Find run output directory
    $runDirs = Get-ChildItem -Path "D:\botg\runs" -Directory -Filter "*realtime_24h*" | Sort-Object LastWriteTime -Descending
    if ($runDirs) {
        $runOutBase = $runDirs[0].FullName
        $snapshot += "Run Output: $runOutBase"
        
        # Check orders.csv growth
        $ordersFiles = Get-ChildItem -Path $runOutBase -Filter "orders*.csv" -ErrorAction SilentlyContinue
        if ($ordersFiles) {
            $ordersFile = $ordersFiles[0]
            $currentSize = $ordersFile.Length
            $currentTime = Get-Date
            
            $snapshot += "Orders CSV: $($ordersFile.Name) - $([math]::Round($currentSize / 1KB, 2))KB"
            
            if ($currentSize -eq $lastOrdersSize) {
                $timeDiff = ($currentTime - $lastOrdersTime).TotalMinutes
                if ($timeDiff -gt 10) {
                    $alertFile = "path_issues\alert_file_growth_stall_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
                    "ALERT: Orders CSV file growth stalled at $(Get-Date)
File: $($ordersFile.FullName)
Size: $currentSize bytes
Stall duration: $([math]::Round($timeDiff, 1)) minutes" | Out-File -FilePath $alertFile
                    $snapshot += "ALERT: File growth stall - $alertFile created"
                }
            } else {
                $lastOrdersSize = $currentSize
                $lastOrdersTime = $currentTime
            }
        }
        
        # Check reconstruct report
        $reconstructPath = Join-Path $runOutBase "reconstruct_report.json"
        if (Test-Path $reconstructPath) {
            try {
                $report = Get-Content $reconstructPath -Raw | ConvertFrom-Json
                $orphanAfter = if ($report.orphan_after) { $report.orphan_after } else { 0 }
                $snapshot += "Reconstruct: orphan_after=$orphanAfter"
                
                if ($orphanAfter -gt 0) {
                    Write-Host "CRITICAL: orphan_after > 0 detected - stopping run" -ForegroundColor Red
                    Set-Content -Path (Join-Path $runOutBase "RUN_STOP") -Value "Auto-stopped: orphan_after=$orphanAfter at $(Get-Date)"
                    $snapshot += "CRITICAL: RUN_STOP created due to orphan_after > 0"
                    break
                }
            } catch {
                $snapshot += "Reconstruct: ERROR reading report"
            }
        }
        
        # Check sentinel files
        $pauseFile = Join-Path $runOutBase "RUN_PAUSE"
        $stopFile = Join-Path $runOutBase "RUN_STOP"
        
        if (Test-Path $pauseFile) {
            $snapshot += "SENTINEL: RUN_PAUSE detected"
        }
        if (Test-Path $stopFile) {
            $snapshot += "SENTINEL: RUN_STOP detected - ending monitoring"
            break
        }
    }
    
    $snapshot += "=== END SNAPSHOT ==="
    $snapshot += ""
    
    # Write snapshot
    $snapshot -join "
" | Out-File -FilePath $snapshotFile -Encoding UTF8
    Write-Host "Snapshot $snapshotCount written: $snapshotFile"
    
    # Wait 15 minutes
    Start-Sleep -Seconds 900
}

Write-Host "Monitoring ended - starting postrun collection..." -ForegroundColor Yellow
