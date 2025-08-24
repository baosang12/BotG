# Check running harness paper-run processes and show elapsed/remaining
$procs = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and ($_.CommandLine -match 'run_harness_and_collect.ps1' -or $_.CommandLine -match 'run_harness_and_collect') }
if (-not $procs) {
    Write-Output 'NO_RUN_FOUND'
    exit 0
}

foreach ($p in $procs) {
    $cmd = $p.CommandLine -replace "`r","" -replace "`n"," "
    $procId = $p.ProcessId
    $start = [Management.ManagementDateTimeConverter]::ToDateTime($p.CreationDate)
    $startUtc = $start.ToUniversalTime().ToString("s")
    $m = [regex]::Match($cmd, '-DurationSeconds\\s+([0-9]+)')
    $dur = if ($m.Success) { [int]$m.Groups[1].Value } else { 86400 }
    $elapsed = (Get-Date) - $start
    $rem = [TimeSpan]::FromSeconds($dur) - $elapsed
    if ($rem.TotalSeconds -lt 0) { $rem = [TimeSpan]::Zero }

    Write-Output ("PID: $procId")
    Write-Output (" CommandLine: $cmd")
    Write-Output (" StartTime (UTC): $startUtc")
    Write-Output (" Duration (s): $dur")
    Write-Output (" Elapsed: $($elapsed.ToString('hh\\:mm\\:ss'))")
    Write-Output (" Remaining: $($rem.ToString('hh\\:mm\\:ss'))")
    Write-Output '---'
}
