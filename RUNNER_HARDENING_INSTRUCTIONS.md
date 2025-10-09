# RUNNER HARDENING INSTRUCTIONS

**NEED_ADMIN_RUNNER**: The following commands require Administrator privileges on the self-hosted runner machine (botg-runner-01).

## Required PowerShell Commands (Run as Administrator):

```powershell
# 1. Set service to start automatically
Set-Service -Name "actions.runner.baosang12-BotG.botg-runner-01" -StartupType Automatic

# 2. Configure service failure recovery (restart on failure)
sc.exe failure "actions.runner.baosang12-BotG.botg-runner-01" reset= 0 actions= restart/60000/restart/120000/restart/300000

# 3. Set failure flag to enable recovery actions
sc.exe failureflag "actions.runner.baosang12-BotG.botg-runner-01" 1

# 4. Verify ExecutionPolicy is set to Bypass at LocalMachine scope
Get-ExecutionPolicy -List
# Should show: LocalMachine = Bypass
```

## Expected Results:
- Service startup type: Automatic
- Failure recovery: Restart after 1min, 2min, 5min delays
- ExecutionPolicy LocalMachine: Bypass (required for workflow scripts)

## Service Recovery Configuration:
- Reset failure count: 0 (never reset)
- First failure: Restart service after 60000ms (1 minute)
- Second failure: Restart service after 120000ms (2 minutes)  
- Subsequent failures: Restart service after 300000ms (5 minutes)

This ensures the GitHub Actions runner service automatically recovers from failures and maintains the correct execution policy for PowerShell scripts in workflows.