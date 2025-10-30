# BotG OPS Kit (P0) — Operator Guide

## Overview

**Purpose:** Standardize pre-Gate2 evidence collection for BotG.algo on Windows (local operation).

**Scope:** Preflight → Canary → Postrun validation. **No strategy changes, no automated Gate2 dispatch.**

**Artifact:** `BotG.algo` (unchanged name, manual attach in cTrader).

**Default Log Path:** `D:\botg\logs`

---

## Prerequisites

- Windows 10/11 with PowerShell 7+
- cTrader installed and configured
- BotG.algo built and available
- Network connectivity to broker (live market data)

---

## Workflow

### Phase 1: Preflight

**Purpose:** Verify live L1 data feed quality before bot attach.

```powershell
# Step 1: Attach BotG.algo in cTrader (paper mode, EURUSD)
# Wait for telemetry.csv to start writing

# Step 2: Run connection test (180s window)
.\scripts\preflight\ctrader_connect.ps1 -Seconds 180 -LogPath "D:\botg\logs" -Symbol "EURUSD"

# Expected output:
# - preflight/connection_ok.json (ok=true)
# - preflight/l1_sample.csv (last 50 ticks)
```

**PASS CRITERIA:**
- `connection_ok.json` → `ok: true`
- `last_age_now_sec ≤ 5.0`
- `active_ratio ≥ 0.7` (70% of seconds had new ticks)
- `tick_rate_avg ≥ 0.5` (at least 0.5 ticks/sec)

---

### Phase 2: Canary Trade

**Purpose:** Verify order pipeline (REQUEST→ACK→FILL→CLOSE) with paper-only test trade.

```powershell
# Step 1: Enable canary in config
.\scripts\preflight\run_canary.ps1 -TimeoutSec 300 -LogPath "D:\botg\logs"

# Step 2: Script will prompt: "RE-ATTACH BotG.algo..."
# MANUALLY detach and re-attach bot in cTrader

# Step 3: Script polls orders.csv for canary lifecycle
# Expected output:
# - preflight/canary_proof.json (ok=true, all phases present)
# - preflight/orders_tail_canary.txt (last 120 orders)
```

**PASS CRITERIA:**
- `canary_proof.json` → `ok: true`
- All phases present: `requested: true, ack: true, fill: true, close: true`
- Label: `BotG_CANARY`
- Timeout: 300 seconds (5 minutes)

**NOTE:** Script does NOT attach bot. Operator must manually attach/detach.

---

### Phase 3: Postrun Validation

**Purpose:** Verify schema compliance and calculate basic KPIs after bot run.

```powershell
# Run after bot session ends (detach bot first)
.\scripts\postrun\collect_and_validate.ps1 -LogPath "D:\botg\logs"

# Expected output:
# - postrun/validator_report.json (schema validation results)
# - postrun/postrun_summary.json (KPIs: fill_rate, row counts)
```

**PASS CRITERIA:**
- All required files exist: `orders.csv`, `telemetry.csv`, `risk_snapshots.csv`
- All headers match exact schema
- `fill_rate ≥ 0.95` (95% of requests filled)
- No missing columns

---

## File Locations

### Input Files (bot-generated)
```
D:\botg\logs\
  ├─ config.runtime.json      (bot config, modified by run_canary.ps1)
  ├─ orders.csv               (order lifecycle log)
  ├─ telemetry.csv            (L1 ticks: timestamp_iso, symbol, bid, ask)
  └─ risk_snapshots.csv       (account state snapshots)
```

### Output Files (script-generated)
```
D:\botg\logs\preflight\
  ├─ connection_ok.json       (L1 feed quality metrics)
  ├─ l1_sample.csv            (last 50 ticks sample)
  ├─ preflight_canary.json    (bot-written canary status)
  ├─ canary_proof.json        (script-verified canary lifecycle)
  └─ orders_tail_canary.txt   (last 120 orders during canary)

D:\botg\logs\postrun\
  ├─ validator_report.json    (schema validation results)
  └─ postrun_summary.json     (KPIs: fill_rate, row counts)
```

---

## Schema Reference

### orders.csv (exact header)
```
timestamp_iso,action,symbol,qty,price,status,reason,latency_ms,price_requested,price_filled
```

### telemetry.csv (exact header)
```
timestamp_iso,symbol,bid,ask
```

### risk_snapshots.csv (exact header)
```
timestamp_iso,equity,balance,floating
```

---

## Troubleshooting

### Preflight Fails (connection_ok.json → ok=false)

**Symptoms:**
- `last_age_now_sec > 5.0` → Data feed stale
- `active_ratio < 0.7` → Tick gaps
- `tick_rate_avg < 0.5` → Low tick frequency

**Solutions:**
1. Check cTrader connection (broker status)
2. Verify bot is attached and running
3. Check network connectivity
4. Verify `telemetry.csv` is being written (new rows appearing)
5. Try different symbol (e.g., GBPUSD if EURUSD fails)

### Canary Fails (canary_proof.json → ok=false)

**Symptoms:**
- `requested: false` → Canary config not enabled
- `ack: false` → Order not acknowledged by broker
- `fill: false` → Order not filled (timeout or rejection)
- `close: false` → Position not closed

**Solutions:**
1. Verify `config.runtime.json` has `Preflight.Canary.Enabled=true`
2. Check bot was re-attached after config change
3. Verify account has sufficient balance (paper mode)
4. Check `orders.csv` for error messages in `reason` column
5. Increase timeout: `run_canary.ps1 -TimeoutSec 600`

### Postrun Fails (validator_report.json → errors)

**Symptoms:**
- `exists: false` → Missing required file
- `headersOk: false` → Schema mismatch
- `missing: [...]` → Missing columns

**Solutions:**
1. Ensure bot ran for sufficient time (>5 minutes)
2. Check file permissions on `D:\botg\logs`
3. Verify bot version matches OPS Kit (BotG.algo from current build)
4. Check for file corruption (open CSV in text editor)

---

## P0 Checklist (Operator)

- [ ] **Preflight:** `connection_ok.json` → `ok=true`
- [ ] **Canary:** `canary_proof.json` → `ok=true` with all phases
- [ ] **Postrun:** `validator_report.json` → all `headersOk=true`
- [ ] **KPIs:** `fill_rate ≥ 0.95` in `postrun_summary.json`
- [ ] All evidence files present (see `ops/manifest.json`)
- [ ] No runtime artifacts committed to repo

---

## Notes

- Scripts are **read-only observers** — they do not modify bot behavior
- Bot must be **manually attached/detached** by operator
- All files UTF-8 without BOM
- Scripts require PowerShell 7+ (no external modules)
- Runtime logs (`D:\botg\logs\**`) are ignored by git

---

## Manifest

See `ops/manifest.json` for complete list of:
- Bot artifact name
- Default paths
- Evidence file list (preflight, canary, postrun)
- Module inventory

---

## Support

For issues or questions, check:
1. This README (troubleshooting section)
2. Script inline comments
3. Bot logs: `D:\botg\logs\*.log`
4. Repository issues: https://github.com/baosang12/BotG/issues
