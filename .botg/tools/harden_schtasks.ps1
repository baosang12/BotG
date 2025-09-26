# .botg/tools/harden_schtasks.ps1
# Purpose: Re-register BotG scheduled tasks to run hidden, non-interactive, SYSTEM, with backups & rollback.

param([switch]$Rollback)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Config ---
$TaskNames = @('BotG-Server','BotG-Tunnel','BotG-Watchdog','BotG-WebhookRepair')
$ScriptPath = 'C:\BotG\run.ps1'
$LogDir = Join-Path $PSScriptRoot '..\logs'
$BackupDir = Join-Path $PSScriptRoot '..\schtask_backup'

# --- Ensure dirs ---
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null

# --- Guard: check script exists ---
if (-not (Test-Path $ScriptPath)) {
  throw "Script target does not exist: $ScriptPath. Please update ScriptPath variable."
}

# --- Helper: backup & restore ---
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
  Write-Host "Rollback complete." -ForegroundColor Green
}

# Handle rollback parameter
if ($Rollback) { 
  Restore-All
  exit 0 
}

# --- Check current state ---
Write-Host "Current scheduled tasks state:" -ForegroundColor Cyan
$before = foreach($n in $TaskNames){
  $t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue
  if($t){
    [pscustomobject]@{
      TaskName = $n
      Exists = $true
      User = $t.Principal.UserId
      RunLevel = $t.Principal.RunLevel
      Hidden = $t.Settings.Hidden
      Actions = ($t.Actions | ForEach-Object { $_.Execute + ' ' + $_.Arguments }) -join ' | '
    }
  } else {
    [pscustomobject]@{ 
      TaskName = $n
      Exists = $false
      User = ''
      RunLevel = ''
      Hidden = ''
      Actions = '' 
    }
  }
}
$before | Format-Table -AutoSize

# --- Re-register each task (idempotent) ---
$psArgs = @(
  '-NoLogo','-NoProfile','-NonInteractive',
  '-ExecutionPolicy','Bypass','-WindowStyle','Hidden',
  '-File',"`"$ScriptPath`""
) -join ' '

$tasksChanged = 0
foreach($n in $TaskNames){
  $t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue
  if (-not $t) { 
    Write-Host "Task not found, skipping: $n" -ForegroundColor DarkYellow
    continue 
  }

  # Backup once (if not already done)
  $backup = Join-Path $BackupDir "$n.xml"
  if (-not (Test-Path $backup)) { 
    Write-Host "Backing up task: $n" -ForegroundColor DarkGray
    Backup-TaskXml $n | Out-Null 
  }

  # Preserve existing triggers
  $triggers = $t.Triggers

  # Check if already in correct state
  $alreadyOk = (
    ($t.Principal.UserId -eq 'SYSTEM') -and
    ($t.Principal.RunLevel -eq 'Highest') -and
    ($t.Settings.Hidden -eq $true) -and
    ($t.Actions.Count -ge 1) -and
    ($t.Actions | Where-Object { $_.Execute -match 'powershell\.exe' -and $_.Arguments -match '\-WindowStyle Hidden' })
  )

  if ($alreadyOk) {
    Write-Host "Already hardened, skipping: $n" -ForegroundColor Green
    continue
  }

  # Create new hardened configuration
  $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $psArgs
  $settings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest

  # Re-register task atomically
  Write-Host "Hardening task: $n (SYSTEM + Hidden + NonInteractive)" -ForegroundColor Blue
  Register-ScheduledTask -TaskName $n -Action $action -Trigger $triggers -Settings $settings -Principal $principal -Force | Out-Null
  $tasksChanged++
}

# --- Show final state ---
Write-Host "`nFinal scheduled tasks state:" -ForegroundColor Cyan
$after = Get-ScheduledTask -TaskName $TaskNames -ErrorAction SilentlyContinue | ForEach-Object {
  [pscustomobject]@{
    TaskName = $_.TaskName
    User = $_.Principal.UserId
    RunLevel = $_.Principal.RunLevel
    Hidden = $_.Settings.Hidden
    Actions = ($_.Actions | ForEach-Object { $_.Execute + ' ' + $_.Arguments }) -join ' | '
  }
}
$after | Format-Table -AutoSize

if ($tasksChanged -gt 0) {
  Write-Host "Hardening completed ($tasksChanged tasks updated)." -ForegroundColor Green
  Write-Host "Backup files saved to: $BackupDir" -ForegroundColor DarkGray
} else {
  Write-Host "No changes needed (already idempotent & correctly configured)." -ForegroundColor Green
}

Write-Host "`nUse -Rollback parameter to restore from backup if needed." -ForegroundColor DarkGray
Write-Host "Example: .\harden_schtasks.ps1 -Rollback" -ForegroundColor DarkGray
