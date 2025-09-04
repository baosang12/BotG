# Monitor snapshot script
param(
    [string]$SnapshotBase = ".\path_issues\monitor_snapshots"
)

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$snapshotDir = Join-Path $SnapshotBase $ts
New-Item -ItemType Directory -Path $snapshotDir -Force | Out-Null

Write-Output "Creating monitor snapshot at $snapshotDir"

# Find latest run artifacts
$latestRun = Get-ChildItem -LiteralPath ".\artifacts" -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Desc | Select-Object -First 1

$files = @()
if ($latestRun) {
    $runPath = $latestRun.FullName
    $nestedArtifacts = Join-Path $runPath 'artifacts'
    if (Test-Path -LiteralPath $nestedArtifacts) {
        $nestedRun = Get-ChildItem -LiteralPath $nestedArtifacts -Directory | Sort-Object LastWriteTime -Desc | Select-Object -First 1
        if ($nestedRun) {
            $targetFiles = @('orders.csv', 'telemetry.csv', 'trade_closes.log', 'closed_trades_fifo.csv')
            foreach ($f in $targetFiles) {
                $src = Join-Path $nestedRun.FullName $f
                if (Test-Path -LiteralPath $src) {
                    $dest = Join-Path $snapshotDir $f
                    Copy-Item -LiteralPath $src -Destination $dest
                    $item = Get-Item -LiteralPath $dest
                    $files += @{
                        name = $f
                        size = $item.Length
                        mtime = $item.LastWriteTimeUtc.ToString('o')
                        path = $dest
                    }
                }
            }
        }
    }
    
    # Copy run-level files
    $runLevelFiles = @('summary.json', 'build.log', 'risk_snapshots.csv')
    foreach ($f in $runLevelFiles) {
        $src = Join-Path $runPath $f
        if (Test-Path -LiteralPath $src) {
            $dest = Join-Path $snapshotDir $f
            Copy-Item -LiteralPath $src -Destination $dest
            $item = Get-Item -LiteralPath $dest
            $files += @{
                name = $f
                size = $item.Length
                mtime = $item.LastWriteTimeUtc.ToString('o')
                path = $dest
            }
        }
    }
}

# Create metadata
$metadata = @{
    timestamp = (Get-Date).ToUniversalTime().ToString('o')
    snapshot_dir = $snapshotDir
    source_run = if ($latestRun) { $latestRun.FullName } else { $null }
    files = $files
    total_files = $files.Count
    total_size = ($files | Measure-Object -Property size -Sum).Sum
}

$metadataPath = Join-Path $snapshotDir 'snapshot_metadata.json'
$metadata | ConvertTo-Json -Depth 6 | Out-File -FilePath $metadataPath -Encoding UTF8

Write-Output "Monitor snapshot completed:"
$metadata | ConvertTo-Json -Depth 6
