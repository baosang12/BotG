$healthLogFile = "path_issues\run_health_20250901_122744.log"
$runOutBase = "D:\botg\runs\realtime_24h_20250901_122453"
$runPid = 25328
$intervalMinutes = 15

function Write-HealthSnapshot {
    param([string]$OutBase, [string]$LogFile)
    
    $timestamp = Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ"
    $logEntry = @()
    
    $logEntry += "=== HEALTH_SNAPSHOT: $timestamp ==="
    
    # Check if main process is alive
    try {
        $process = Get-Process -Id $runPid -ErrorAction Stop
        $logEntry += "PROCESS: ALIVE (PID $runPid)"
    }
    catch {
        $logEntry += "PROCESS: NOT_FOUND (PID $runPid) - CRITICAL!"
        $alertFile = "path_issues\run_alert_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
        "ALERT: Main run process $runPid terminated at $timestamp" | Out-File -FilePath $alertFile
        Write-Host "CRITICAL ALERT: $alertFile - Process terminated!"
        return $false
    }
    
    # Check run directory files
    if (Test-Path $OutBase) {
        $checkFiles = @("orders*.csv", "telemetry*.csv", "trade_closes.log")
        foreach ($pattern in $checkFiles) {
            $files = Get-ChildItem -Path $OutBase -Filter $pattern -ErrorAction SilentlyContinue
            foreach ($file in $files) {
                $sizeKB = [math]::Round($file.Length / 1KB, 2)
                $mtime = $file.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                $logEntry += "FILE: $($file.Name) | ${sizeKB}KB | $mtime"
            }
        }
        
        # Check reconstruct report
        $reconstructPath = Join-Path $OutBase "reconstruct_report.json"
        if (Test-Path $reconstructPath) {
            try {
                $report = Get-Content $reconstructPath -Raw | ConvertFrom-Json
                $orphan = if ($report.orphan_after) { $report.orphan_after } else { 0 }
                $unmatched = if ($report.unmatched_orders_count) { $report.unmatched_orders_count } else { 0 }
                $logEntry += "RECONSTRUCT: orphan_after=$orphan, unmatched_orders_count=$unmatched"
                
                if ($orphan -gt 0 -or $unmatched -gt 0) {
                    $alertFile = "path_issues\run_alert_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
                    "ALERT: Reconstruct issues at $timestamp`norphan_after: $orphan`nunmatched_orders_count: $unmatched" | Out-File -FilePath $alertFile
                    Set-Content -Path (Join-Path $OutBase "RUN_PAUSE") -Value "Auto-paused: orphan_after=$orphan, unmatched_orders_count=$unmatched"
                    $logEntry += "ALERT_CREATED: $alertFile - RUN_PAUSE created"
                    Write-Host "ALERT: $alertFile - Reconstruct issues detected"
                }
            }
            catch {
                $logEntry += "RECONSTRUCT: ERROR reading report"
            }
        }
    } else {
        $logEntry += "ERROR: Run output directory not found: $OutBase"
    }
    
    # Check disk space
    $freeGB = [math]::Round((Get-WmiObject -Class Win32_LogicalDisk -Filter "DeviceID='D:'").FreeSpace / 1GB, 2)
    $logEntry += "DISK_FREE: ${freeGB}GB"
    
    if ($freeGB -lt 5) {
        $alertFile = "path_issues\run_alert_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
        "ALERT: Low disk space at $timestamp`nFree space: ${freeGB}GB" | Out-File -FilePath $alertFile
        Set-Content -Path (Join-Path $OutBase "RUN_PAUSE") -Value "Auto-paused: Low disk space ${freeGB}GB"
        $logEntry += "ALERT_CREATED: $alertFile - RUN_PAUSE created"
        Write-Host "ALERT: $alertFile - Low disk space: ${freeGB}GB"
    }
    
    # Check sentinel files
    $pauseFile = Join-Path $OutBase "RUN_PAUSE"
    $stopFile = Join-Path $OutBase "RUN_STOP"
    
    if (Test-Path $pauseFile) {
        $logEntry += "SENTINEL: RUN_PAUSE detected"
    }
    if (Test-Path $stopFile) {
        $logEntry += "SENTINEL: RUN_STOP detected"
        return $false
    }
    
    $logEntry += "=== END SNAPSHOT ==="
    $logEntry += ""
    
    # Append to log
    Add-Content -Path $LogFile -Value ($logEntry -join "`n")
    Write-Host "Health snapshot written: $timestamp"
    return $true
}

Write-Host "Starting continuous health monitoring..."
Write-Host "Health Log: $healthLogFile"
Write-Host "Monitor Interval: $intervalMinutes minutes"

while ($true) {
    $continue = Write-HealthSnapshot -OutBase $runOutBase -LogFile $healthLogFile
    if (-not $continue) {
        Write-Host "Monitoring stopped due to critical condition"
        break
    }
    
    Write-Host "Next check in $intervalMinutes minutes..."
    Start-Sleep -Seconds ($intervalMinutes * 60)
}
