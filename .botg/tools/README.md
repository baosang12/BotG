# Scheduled Tasks Hardening Tools

Tools for hardening BotG scheduled tasks to run completely hidden in the background.

## Files

- **harden_schtasks.ps1** - Main hardening script (requires Admin)
- **verify_schtasks.ps1** - Quick status verification script  
- **run_template.ps1** - Template for C:\BotG\run.ps1

## Usage

### Harden Tasks (Admin Required)
```powershell
powershell -ExecutionPolicy Bypass -File .\.botg\tools\harden_schtasks.ps1
```

### Verify Status
```powershell
powershell -ExecutionPolicy Bypass -File .\.botg\tools\verify_schtasks.ps1
```

### Rollback
```powershell  
powershell -ExecutionPolicy Bypass -File .\.botg\tools\harden_schtasks.ps1 -Rollback
```

## What Gets Hardened

- User: SYSTEM
- RunLevel: Highest  
- Hidden: True
- WindowStyle: Hidden
- Profile: NonInteractive, NoProfile

## Safety Features

- Idempotent execution
- XML backups before changes
- Quick rollback capability
- Preserves existing triggers
- Runtime logging to files
