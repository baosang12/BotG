param(
    [string]$Timestamp = (Get-Date -Format 'yyyyMMdd_HHmmss'),
    [string]$BotgRoot = (Resolve-Path '.').Path,
    [string]$LogDir = $null,
    [string[]]$RequiredFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    if (-not $LogDir -or $LogDir -eq '') { $LogDir = $env:BOTG_LOG_PATH }
    if (-not $LogDir -or $LogDir -eq '') { $LogDir = 'D:\botg\logs' }

    $issues = Join-Path -Path $BotgRoot -ChildPath 'path_issues'
    if (-not (Test-Path -LiteralPath $issues)) {
        New-Item -ItemType Directory -Path $issues -Force | Out-Null
    }

    if (-not (Test-Path -LiteralPath $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }

    # Probe disk free space for the drive containing LogDir
    $freeMB = $null
    $driveLetter = $null
    if ($LogDir -match '^[A-Za-z]:') { $driveLetter = $LogDir.Substring(0,1) }
    if ($driveLetter) {
        $d = Get-PSDrive -Name $driveLetter -ErrorAction SilentlyContinue
        if ($d) { $freeMB = [math]::Round($d.Free / 1MB, 0) }
    }

    # Check writability of LogDir
    $writable = $false
    $tmp = Join-Path $LogDir ("agent_write_{0}.tmp" -f $Timestamp)
    try {
        'ok' | Out-File -FilePath $tmp -Encoding ascii -ErrorAction Stop
        $writable = Test-Path -LiteralPath $tmp
    } catch {
        $writable = $false
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }

    if (-not $RequiredFiles -or $RequiredFiles.Count -eq 0) {
        $RequiredFiles = @(
            'scripts\start_realtime_24h_supervised.ps1',
            'path_issues\start_24h_command.txt',
            'path_issues\reconstruct_report.json',
            'path_issues\closed_trades_fifo_reconstructed.csv',
            'path_issues\slip_latency_percentiles.json',
            'path_issues\slippage_hist.png',
            'path_issues\latency_percentiles.png',
            'path_issues\latest_zip.txt'
        )
    }

    $files = foreach ($p in $RequiredFiles) {
        $exists = Test-Path -LiteralPath (Join-Path $BotgRoot $p)
        [pscustomobject]@{ path = $p; exists = $exists }
    }
    $missing = @($files | Where-Object { -not $_.exists } | ForEach-Object { $_.path })

    $status = if (($missing.Count -eq 0) -and $writable) { 'PASS' } else { 'FAIL' }

    $obj = [pscustomobject]@{
        ts            = $Timestamp
        botg_root     = $BotgRoot
        botg_log_path = $LogDir
        disk_free_mb  = $freeMB
        files_present = $files
        writable      = $writable
        status        = $status
        missing       = $missing
    }

    $json = $obj | ConvertTo-Json -Depth 6

    $ready = Join-Path $issues ("pre_run_readiness_{0}.json" -f $Timestamp)
    Set-Content -LiteralPath $ready -Value $json -Encoding UTF8

    $logf = Join-Path $issues ("agent_steps_{0}.log" -f $Timestamp)
    Add-Content -LiteralPath $logf -Value ("[INIT] ts={0}" -f $Timestamp)
    Add-Content -LiteralPath $logf -Value ($json)

    # Emit JSON to STDOUT for the caller to capture
    Write-Output $json
} catch {
    $err = $_
    try {
        $issues = if (-not $issues) { Join-Path -Path (Resolve-Path '.').Path -ChildPath 'path_issues' } else { $issues }
        if (-not (Test-Path -LiteralPath $issues)) {
            New-Item -ItemType Directory -Path $issues -Force | Out-Null
        }
        $logf = Join-Path $issues ("agent_steps_{0}.log" -f $Timestamp)
        Add-Content -LiteralPath $logf -Value ("[ERROR] {0}" -f $err)
    } catch { }
    throw
}
