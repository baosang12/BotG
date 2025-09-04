# Test sentinel file detection
param(
    [string]$OutBase = ".\path_issues\test_sentinel_outbase"
)

Write-Output "Testing sentinel support at $(Get-Date)"

# Ensure outbase exists
if (-not (Test-Path -LiteralPath $OutBase)) {
    New-Item -ItemType Directory -Path $OutBase -Force | Out-Null
}

$pauseFile = Join-Path $OutBase 'RUN_PAUSE'
$stopFile = Join-Path $OutBase 'RUN_STOP'
$logFile = Join-Path $OutBase 'sentinel_test.log'

# Clean up any existing sentinel files
Remove-Item -LiteralPath $pauseFile -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue

"Sentinel test started at $(Get-Date -Format o)" | Out-File -FilePath $logFile -Encoding UTF8

# Test 1: Create RUN_PAUSE
Write-Output "Creating RUN_PAUSE"
"pause_test" | Out-File -FilePath $pauseFile -Encoding UTF8
"Created RUN_PAUSE at $(Get-Date -Format o)" | Out-File -FilePath $logFile -Append -Encoding UTF8

Start-Sleep -Seconds 2

# Test 2: Remove RUN_PAUSE
Write-Output "Removing RUN_PAUSE"
Remove-Item -LiteralPath $pauseFile -ErrorAction SilentlyContinue
"Removed RUN_PAUSE at $(Get-Date -Format o)" | Out-File -FilePath $logFile -Append -Encoding UTF8

Start-Sleep -Seconds 1

# Test 3: Create RUN_STOP
Write-Output "Creating RUN_STOP"
"stop_test" | Out-File -FilePath $stopFile -Encoding UTF8
"Created RUN_STOP at $(Get-Date -Format o)" | Out-File -FilePath $logFile -Append -Encoding UTF8

Start-Sleep -Seconds 1

# Clean up
Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
"Test completed at $(Get-Date -Format o)" | Out-File -FilePath $logFile -Append -Encoding UTF8

Write-Output "Sentinel test completed. Log at: $logFile"
Get-Content -LiteralPath $logFile
