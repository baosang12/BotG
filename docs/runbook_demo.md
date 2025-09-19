# BotG Demo Runbook

## Overview
This runbook covers running BotG in demo/live environments with proper safety controls.

## Environment Setup

### cTrader Demo Environment
```powershell
# Required environment variables
$env:CTRADER_API_BASEURI="https://demo-api.ctrader.com"
$env:CTRADER_API_KEY="your-demo-api-key"

# Optional log path (defaults to D:\botg\logs)
$env:BOTG_LOG_PATH="D:\botg\logs"
```

### Real Order Sending (2-Layer Safety)
To enable actual order sending in demo:

1. **Environment Variable**:
   ```powershell
   $env:SEND_REAL_ORDERS="true"
   ```

2. **Confirmation File**:
   ```powershell
   New-Item -ItemType File -Path ".\CONFIRM_SEND_ORDER" | Out-Null
   ```

**⚠️ Both layers required** - missing either will prevent order sending.

## Trading Commands

### Paper Trading (Strict Mode)
```powershell
.\BotG.Harness\bin\Release\net9.0\BotG.Harness.exe `
  --mode paper --trade-mode strict `
  --symbol XAUUSD --tf M15 --trend-tf H1 `
  --bars 100 --log-path "D:\botg\logs"
```

### Live Demo Trading (Strict Mode)
```powershell
.\BotG.Harness\bin\Release\net9.0\BotG.Harness.exe `
  --mode live --trade-mode strict `
  --symbol XAUUSD --tf M15 --trend-tf H1 `
  --bars 240 --log-path "D:\botg\logs"
```

### Automated Demo Runner
```powershell
# Uses run_live_strict_demo.ps1 with safety checks
powershell -File .\scripts\run_live_strict_demo.ps1
```

## Quick Results Check

After any run:
```powershell
$run = Get-ChildItem "D:\botg\logs" -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
"Run path: $($run.FullName)"
Get-Content (Join-Path $run.FullName "orders.csv") -TotalCount 1
"REQUEST=" + (Select-String -Path (Join-Path $run.FullName "orders.csv") -Pattern ",REQUEST,").Count
"FILL="    + (Select-String -Path (Join-Path $run.FullName "orders.csv") -Pattern ",FILL,").Count
```

## Safety Features

### Blocked Combinations
- **`--trade-mode test` + `--mode live`** → **FATAL ERROR + EXIT 2**
  - Test mode cannot be used with live trading
  - Use either: `--mode live --trade-mode Strict` OR `--mode paper --trade-mode Test`

### Test Features (Development Only)
- Mini-signals and market-mini orders are **Test mode only**
- Automatically disabled in Strict mode
- Require `ALLOW_TEST_MARKET=1` environment in release builds

### Default Safety
- **TradeMode defaults to "Strict"** for all production use
- Test features require explicit opt-in
- Live trading requires explicit environment setup

## Readiness Check

Before demo trading:
```powershell
powershell -File .\scripts\preflight_readiness.ps1
```

Expected outputs:
- `=== RUN_SIGNAL: READY_TO_RUN_DEMO ===` (live environment ready)
- `=== RUN_SIGNAL: READY_TO_RUN_REPLAY ===` (paper only)
- `CANNOT_RUN: <reason>` (missing requirements)

## Data Requirements

- **XAUUSD M15**: ≥2000 bars for meaningful analysis
- **H1 data**: Auto-aggregated from M15 if not present
- **Insufficient data**: System returns `CANNOT_RUN` instead of generating fake orders

## Support

- All runs create complete audit trails in `config_stamp.json`
- Orders logged with V3 format including TP columns
- No fake order generation - system reports actual capabilities