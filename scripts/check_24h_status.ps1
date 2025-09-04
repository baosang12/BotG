# 24H Run Final Status Reporter
param([switch]$WaitForCompletion, [int]$CheckIntervalSeconds = 30)

$runPid = 25328
$runOutBase = "D:\botg\runs\realtime_24h_20250901_122453"
$healthLog = "path_issues\run_health_20250901_122744.log"

function Get-RunStatus {
    $status = @{}
    
    # Check process
    try {
        $proc = Get-Process -Id $runPid -ErrorAction Stop
        $status.process_alive = $true
        $status.process_runtime = (Get-Date) - $proc.StartTime
    }
    catch {
        $status.process_alive = $false
        $status.process_runtime = "TERMINATED"
    }
    
    # Check sentinel files
    $pauseFile = Join-Path $runOutBase "RUN_PAUSE"
    $stopFile = Join-Path $runOutBase "RUN_STOP"
    $status.pause_sentinel = Test-Path $pauseFile
    $status.stop_sentinel = Test-Path $stopFile
    
    # Check output files
    if (Test-Path $runOutBase) {
        $files = Get-ChildItem $runOutBase
        $status.output_files = $files.Count
        $status.latest_file = ($files | Sort-Object LastWriteTime -Desc | Select-Object -First 1).LastWriteTime
    }
    
    # Check for alerts
    $alerts = Get-ChildItem -Path "path_issues" -Filter "run_alert_*.txt" -ErrorAction SilentlyContinue
    $status.alert_count = $alerts.Count
    
    return $status
}

if ($WaitForCompletion) {
    Write-Host "Waiting for 24H run completion..."
    
    while ($true) {
        $status = Get-RunStatus
        
        Write-Host "$(Get-Date -Format 'HH:mm:ss') - Process: $($status.process_alive) | Files: $($status.output_files) | Alerts: $($status.alert_count)"
        
        if (-not $status.process_alive -or $status.stop_sentinel) {
            Write-Host "Run completed or stopped!"
            break
        }
        
        Start-Sleep -Seconds $CheckIntervalSeconds
    }
    
    # Final collection
    Write-Host "Running post-run collection..."
    $result = & ".\scripts\collect_postrun_artifacts.ps1"
    
    $finalStatus = Get-RunStatus
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    
    if ($finalStatus.process_alive) {
        Write-Host "RUN_COMPLETED_OK $timestamp $($result.artifacts_path)"
    } else {
        $reason = if ($finalStatus.alert_count -gt 0) { "ALERTS" } else { "PROCESS_TERMINATED" }
        $alertPath = if ($finalStatus.alert_count -gt 0) { (Get-ChildItem "path_issues\run_alert_*.txt" | Sort-Object LastWriteTime -Desc | Select-Object -First 1).FullName } else { "N/A" }
        Write-Host "RUN_ABORTED $timestamp $reason $alertPath"
    }
} else {
    # Just show current status
    $status = Get-RunStatus
    Write-Host "=== CURRENT 24H RUN STATUS ==="
    Write-Host "Process Alive: $($status.process_alive)"
    Write-Host "Runtime: $($status.process_runtime)"
    Write-Host "Output Files: $($status.output_files)"
    Write-Host "Pause Sentinel: $($status.pause_sentinel)"
    Write-Host "Stop Sentinel: $($status.stop_sentinel)"
    Write-Host "Alert Count: $($status.alert_count)"
    Write-Host "Latest File: $($status.latest_file)"
}
