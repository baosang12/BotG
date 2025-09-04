param(
    [string]$OutBase = "D:\botg\runs\realtime_24h_20250901_122453",
    [string]$Reason = "COMPLETED"
)

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$artifactsZip = "path_issues\postrun_artifacts_$timestamp.zip"
$summaryJson = "path_issues\postrun_summary_$timestamp.json"

Write-Host "POST-RUN COLLECTOR STARTED"
Write-Host "OutBase: $OutBase"
Write-Host "Reason: $Reason"

# Create collection
$files = @()
$errors = @()

# Collect key files from run output
if (Test-Path $OutBase) {
    $runFiles = Get-ChildItem -Path $OutBase -File
    foreach ($file in $runFiles) {
        try {
            $relPath = "run_output\$($file.Name)"
            $files += @{
                source = $file.FullName
                archive_path = $relPath
                size = $file.Length
                hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
            }
        }
        catch {
            $errors += "Failed to process $($file.FullName): $($_.Exception.Message)"
        }
    }
}

# Collect health logs
$healthLogs = Get-ChildItem -Path "path_issues" -Filter "run_health_*.log" -ErrorAction SilentlyContinue
foreach ($log in $healthLogs) {
    try {
        $files += @{
            source = $log.FullName
            archive_path = "health_logs\$($log.Name)"
            size = $log.Length
            hash = (Get-FileHash $log.FullName -Algorithm SHA256).Hash
        }
    }
    catch {
        $errors += "Failed to process health log $($log.FullName): $($_.Exception.Message)"
    }
}

# Collect alert files
$alerts = Get-ChildItem -Path "path_issues" -Filter "run_alert_*.txt" -ErrorAction SilentlyContinue
foreach ($alert in $alerts) {
    try {
        $files += @{
            source = $alert.FullName
            archive_path = "alerts\$($alert.Name)"
            size = $alert.Length
            hash = (Get-FileHash $alert.FullName -Algorithm SHA256).Hash
        }
    }
    catch {
        $errors += "Failed to process alert $($alert.FullName): $($_.Exception.Message)"
    }
}

# Create summary object
$summary = @{
    timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ")
    reason = $Reason
    outbase = $OutBase
    files_collected = $files.Count
    total_size = ($files | ForEach-Object { $_.size } | Measure-Object -Sum).Sum
    files = $files
    errors = $errors
    collector_version = "24h_supervised_v1"
}

# Create artifacts directory
$artifactsDir = "postrun_temp_$timestamp"
New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null

try {
    # Copy files to temp directory
    foreach ($file in $files) {
        $destDir = Join-Path $artifactsDir (Split-Path $file.archive_path -Parent)
        if (-not (Test-Path $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        Copy-Item -Path $file.source -Destination (Join-Path $artifactsDir $file.archive_path) -Force
    }
    
    # Create ZIP archive
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($artifactsDir, $artifactsZip)
    
    Write-Host "Created archive: $artifactsZip"
}
catch {
    $errors += "Failed to create archive: $($_.Exception.Message)"
    Write-Host "ERROR: Failed to create archive - $($_.Exception.Message)"
}
finally {
    # Cleanup temp directory
    if (Test-Path $artifactsDir) {
        Remove-Item -Path $artifactsDir -Recurse -Force
    }
}

# Write summary JSON
$summary | ConvertTo-Json -Depth 10 | Set-Content -Path $summaryJson -Encoding UTF8

Write-Host "POST-RUN COLLECTION COMPLETED"
Write-Host "Artifacts: $artifactsZip"
Write-Host "Summary: $summaryJson"
Write-Host "Files collected: $($files.Count)"
Write-Host "Errors: $($errors.Count)"

return @{
    artifacts_path = $artifactsZip
    summary_path = $summaryJson
    files_count = $files.Count
    errors_count = $errors.Count
}
