# .botg/tools/harden_schtasks.ps1
# Purpose: Re-register BotG scheduled tasks to run hidden, non-interactive, SYSTEM

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Config
$TaskNames = @('BotG-Server','BotG-Tunnel','BotG-Watchdog','BotG-WebhookRepair')
$ScriptPath = 'C:\BotG\run.ps1'
$LogDir = Join-Path $PSScriptRoot '..\logs'
$BackupDir = Join-Path $PSScriptRoot '..\schtask_backup'

# Ensure directories
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null

# Guard: check script exists
if (-not (Test-Path $ScriptPath)) {
  throw "Script target does not exist: $ScriptPath"
}

# Backup function
function Backup-TaskXml {
  param([string]$Name)
  $xmlPath = Join-Path $BackupDir "$Name.xml"
  try {
    schtasks /Query /TN $Name /XML > $xmlPath 2>$null
    return $xmlPath
  } catch {
    return $null
  }
}

# Rollback function
function Restore-All {
  Write-Host "Rolling back from backups..." -ForegroundColor Yellow
  foreach($n in $TaskNames){
    $xml = Join-Path $BackupDir "$n.xml"
    if (Test-Path $xml) {
      schtasks /Delete /TN $n /F | Out-Null
      schtasks /Create /TN $n /XML $xml /RU SYSTEM | Out-Null
      Write-Host "  Restored: $n"
    }
  }
  Write-Host "Rollback complete."
}

# Handle rollback parameter
param([switch]$Rollback)
if ($Rollback) { Restore-All; exit 0 }

# Check current state
Write-Host "Current task state:" -ForegroundColor Cyan
$before = foreach($n in $TaskNames){
  $t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue
  if($t){
    [pscustomobject]@{
      TaskName = $n
      User = $t.Principal.UserId
      RunLevel = $t.Principal.RunLevel
      Hidden = $t.Settings.Hidden
    }
  }
}
$before | Format-Table -AutoSize

# Process each task
$psArgs = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $ScriptPath + '"'
$tasksChanged = 0

foreach($n in $TaskNames){
  $t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue
  if (-not $t) { 
    Write-Host "Task not found: $n" -ForegroundColor Yellow
    continue 
  }

  # Backup if needed
  $backup = Join-Path $BackupDir "$n.xml"
  if (-not (Test-Path $backup)) { 
    Write-Host "Backing up: $n"
    Backup-TaskXml $n | Out-Null 
  }

  # Keep existing triggers
  $triggers = $t.Triggers

  # Check if already hardened
  $alreadyOk = (
    ($t.Principal.UserId -eq 'SYSTEM') -and
    ($t.Principal.RunLevel -eq 'Highest') -and
    ($t.Settings.Hidden -eq $true) -and
    ($t.Actions | Where-Object { $_.Arguments -match '-WindowStyle Hidden' })
  )

  if ($alreadyOk) {
    Write-Host "Already hardened: $n" -ForegroundColor Green
    continue
  }

  # Create new hardened configuration
  $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $psArgs
  $settings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest

  # Re-register task
  Write-Host "Hardening: $n (SYSTEM + Hidden + NonInteractive)" -ForegroundColor Blue
  Register-ScheduledTask -TaskName $n -Action $action -Trigger $triggers -Settings $settings -Principal $principal -Force | Out-Null
  $tasksChanged++
}

# Show final state
Write-Host "`nFinal task state:" -ForegroundColor Cyan
$after = Get-ScheduledTask -TaskName $TaskNames -ErrorAction SilentlyContinue | ForEach-Object {
  [pscustomobject]@{
    TaskName = $_.TaskName
    User = $_.Principal.UserId
    RunLevel = $_.Principal.RunLevel
    Hidden = $_.Settings.Hidden
  }
}
$after | Format-Table -AutoSize

if ($tasksChanged -gt 0) {
  Write-Host "Hardening complete ($tasksChanged tasks updated)." -ForegroundColor Green
} else {
  Write-Host "No changes needed (already hardened)." -ForegroundColor Green
}

Write-Host "`nUse -Rollback parameter to restore from backup if needed." -ForegroundColor Gray
