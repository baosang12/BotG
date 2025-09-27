# C:\BotG\run.ps1
# Runtime script for scheduled tasks - runs hidden with logging

param([string]$TaskName = $env:TASK_NAME)

$LogDir = 'C:\BotG\logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$Log = Join-Path $LogDir ("run_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))

Start-Transcript -Path $Log -Append | Out-Null
try {
  $ErrorActionPreference = 'Stop'
  Set-StrictMode -Version Latest

  Write-Output "=== BotG Scheduled Task Runtime ==="
  Write-Output "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
  Write-Output "Task Name: $TaskName"
  Write-Output "User Context: $env:USERNAME"

  # Task-specific logic
  switch ($TaskName) {
    'BotG-Server' {
      Write-Output "Starting BotG Server..."
      # Add server startup logic here
    }
    'BotG-Tunnel' {
      Write-Output "Starting BotG Tunnel..."
      # Add tunnel startup logic here
    }
    'BotG-Watchdog' {
      Write-Output "Running BotG Watchdog..."
      # Add watchdog logic here
    }
    'BotG-WebhookRepair' {
      Write-Output "Running BotG Webhook Repair..."
      # Add webhook repair logic here
    }
    default {
      Write-Output "Unknown task: $TaskName"
    }
  }

  Write-Output "Task completed successfully at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

} catch {
  Write-Output "ERROR: $_"
  Write-Output "Stack Trace: $($_.ScriptStackTrace)"
  exit 1
} finally {
  Write-Output "=== End Runtime Log ==="
  Stop-Transcript | Out-Null
}
