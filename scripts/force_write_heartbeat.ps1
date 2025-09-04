# Force write heartbeat script for testing file growth
param(
    [string]$TargetFile = ".\artifacts\heartbeat_test.csv",
    [int]$DurationSeconds = 120,
    [int]$IntervalSeconds = 5
)

$startTime = Get-Date
$endTime = $startTime.AddSeconds($DurationSeconds)

Write-Output "Starting heartbeat to $TargetFile for $DurationSeconds seconds"

# Ensure target directory exists
$dir = Split-Path -Parent $TargetFile
if (-not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

# Initialize file with header
"timestamp,heartbeat_id,message" | Out-File -FilePath $TargetFile -Encoding UTF8

$heartbeatId = 0
while ((Get-Date) -lt $endTime) {
    $heartbeatId++
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $line = "$timestamp,$heartbeatId,heartbeat_active"
    
    # Append with immediate flush
    $line | Out-File -FilePath $TargetFile -Append -Encoding UTF8
    
    Write-Output "Heartbeat $heartbeatId at $timestamp"
    Start-Sleep -Seconds $IntervalSeconds
}

Write-Output "Heartbeat completed. Final file size: $((Get-Item $TargetFile).Length) bytes"
