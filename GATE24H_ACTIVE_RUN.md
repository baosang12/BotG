# GATE24H ACTIVE RUN - 24H SUPERVISED PAPER

**Launch Time:** 2025-10-03 17:07:03 (UTC+7)  
**Run ID:** 18219356473  
**Timestamp:** 20251003_170703  
**Mode:** paper  
**Duration:** 24 hours  
**Simulation:** DISABLED (live paper trading)

---

## RUN STATUS

**GitHub Actions:** https://github.com/baosang12/BotG/actions/runs/18219356473

**Jobs:**
- ✅ artifact-selfcheck (ID 51875683209)
- ⏳ gate24h (ID 51875683246) - **24h supervised run**

---

## MONITORING COMMANDS

### Check Current Status
```powershell
gh run view 18219356473
```

### Open in Browser
```powershell
gh run view 18219356473 --web
```

### Watch Live Logs (Job: gate24h)
```powershell
gh run watch 18219356473 --job=51875683246
```

---

## EXPECTED TIMELINE

| Time (UTC+7) | Event | Status |
|--------------|-------|--------|
| 17:07 | Workflow triggered | ✅ DONE |
| 17:08 | Jobs queued | ✅ DONE |
| 17:09-17:15 | Build & setup | ⏳ IN PROGRESS |
| 17:15-41:15 | **24h supervised run** | ⏳ PENDING (next day 17:15) |
| 41:15-41:20 | Postrun collection | ⏳ PENDING |
| 41:20-41:25 | Artifact upload | ⏳ PENDING |
| 41:25 | **Run complete** | ⏳ PENDING |

**Expected Completion:** 2025-10-04 ~17:25 (UTC+7)

---

## RISK PARAMETERS (LV0 - REMINDER)

- **R per trade:** $10
- **Daily stop:** -3R (-$30)
- **Weekly stop:** -6R (-$60)
- **NO martingale/grid**
- **Mode:** paper (no real money)
- **Simulation:** disabled (live paper broker feed)

---

## POST-RUN ARTIFACT COLLECTION

### 1. Download Artifacts
```powershell
$TS = Get-Content GATE24H_TIMESTAMP.txt
$DL = "D:\tmp\g24_$TS"
mkdir $DL
gh run download 18219356473 -D $DL
```

### 2. Extract ZIP
```powershell
Expand-Archive (Get-ChildItem $DL\*.zip | Select -First 1).FullName -DestinationPath "$DL\extracted" -Force
```

### 3. Strict Validation (MUST PASS for Gate3)
```powershell
python .\path_issues\validate_artifacts.py --dir "$DL\extracted"
```

**Expected Output:**
```json
{
  "overall": "PASS",
  "checks": [
    {"check": "file_exists", "file": "orders.csv", "ok": true},
    {"check": "file_exists", "file": "risk_snapshots.csv", "ok": true},
    {"check": "orders.csv_actions", "ok": true, "found": ["REQUEST", "ACK", "FILL"]},
    {"check": "orders.csv_columns", "ok": true, "missing": []},
    {"check": "risk_snapshots.csv_columns", "ok": true, "missing": []},
    {"check": "risk_snapshots.csv_rows", "rows": 1440, "ok": true}
  ]
}
```

**Exit Code:** 0 (PASS required for Gate3 promotion)

---

## REQUIRED FILES (6 artifacts)

1. **orders.csv** - All trade requests/acks/fills with commission/spread/slippage
2. **telemetry.csv** - System metrics
3. **risk_snapshots.csv** - >=1300 rows (24h @ 60s = ~1440 rows), columns: timestamp,equity,balance,margin,free_margin,**drawdown,R_used,exposure**
4. **trade_closes.log** - Trade closure events
5. **run_metadata.json** - mode="paper", simulation.enabled=false
6. **closed_trades_fifo_reconstructed.csv** - FIFO P&L breakdown

---

## VALIDATION CRITERIA (PR #193 STRICT ENFORCER)

### run_metadata.json
- ✅ `mode` = "paper"
- ✅ `simulation.enabled` = false

### orders.csv
- ✅ Columns: timestamp, order_id, action, status, reason, latency_ms, symbol, side, requested_lots, price_requested, price_filled, **commission, spread_cost, slippage_pips**
- ✅ Actions present: **REQUEST, ACK, FILL**

### risk_snapshots.csv
- ✅ Columns: timestamp, equity, balance, margin, free_margin, **drawdown, R_used, exposure**
- ✅ Row count: **>=1300** (1440 expected for 24h @ 60s interval)

---

## COMPLETION NOTIFICATION

**When run finishes (≈24h from now), report to operator:**

```
AFTER_RUN artifacts=D:\tmp\g24_20251003_170703\extracted
```

---

## TROUBLESHOOTING

### If Run Fails Early (<1h)
- Check workflow logs: `gh run view 18219356473 --log`
- Review job logs: `gh run view --job=51875683246 --log`
- Common issues: broker connection, config errors, insufficient margin

### If Postrun Validation Fails
- Check artifact contents: `ls "$DL\extracted"`
- Review validator output for specific failures
- Verify UTF-8 encoding: `file "$DL\extracted\*.csv"`

### If Artifacts Missing
- Check workflow upload step logs
- Verify postrun_collect.ps1 completed successfully
- Re-download: `gh run download 18219356473 -D <new_dir>`

---

**STATUS:** ⏳ **RUN IN PROGRESS** - Check back in ~24 hours (2025-10-04 17:25 UTC+7)
