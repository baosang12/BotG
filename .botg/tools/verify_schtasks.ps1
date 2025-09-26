# .botg/tools/verify_schtasks.ps1
# Quick verification for scheduled tasks hardening status

Write-Host "BotG Scheduled Tasks Status Check" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan

$TaskNames = @('BotG-Server','BotG-Tunnel','BotG-Watchdog','BotG-WebhookRepair')

$results = foreach($name in $TaskNames) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($task) {
        $isHardened = (
            ($task.Principal.UserId -eq 'SYSTEM') -and
            ($task.Principal.RunLevel -eq 'Highest') -and
            ($task.Settings.Hidden -eq $true) -and
            ($task.Actions | Where-Object { $_.Arguments -match '-WindowStyle Hidden' })
        )
        
        [PSCustomObject]@{
            TaskName = $name
            Exists = "Yes"
            User = $task.Principal.UserId
            RunLevel = $task.Principal.RunLevel  
            Hidden = $task.Settings.Hidden
            WindowStyle = if($task.Actions.Arguments -match '-WindowStyle Hidden') {"Hidden"} else {"Visible"}
            Hardened = if($isHardened) {"Yes"} else {"No"}
            State = $task.State
        }
    } else {
        [PSCustomObject]@{
            TaskName = $name
            Exists = "No"
            User = "N/A"
            RunLevel = "N/A"
            Hidden = "N/A" 
            WindowStyle = "N/A"
            Hardened = "No"
            State = "N/A"
        }
    }
}

$results | Format-Table -AutoSize

$hardenedCount = ($results | Where-Object {$_.Hardened -eq "Yes"}).Count
$totalCount = $results.Count

Write-Host "Summary:" -ForegroundColor Yellow
Write-Host "  Hardened Tasks: $hardenedCount/$totalCount" -ForegroundColor $(if($hardenedCount -eq $totalCount){'Green'}else{'Red'})
Write-Host "  Expected: SYSTEM + Highest + Hidden + WindowStyle=Hidden" -ForegroundColor Gray

if ($hardenedCount -eq $totalCount) {
    Write-Host "`nAll tasks are properly hardened!" -ForegroundColor Green
} else {
    Write-Host "`nSome tasks need hardening. Run: .\harden_schtasks.ps1" -ForegroundColor Yellow
}
