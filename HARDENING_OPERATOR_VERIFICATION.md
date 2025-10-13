# GATE24H HARDENING - OPERATOR VERIFICATION GUIDE

**Date:** 2025-10-03  
**PR:** #193 (https://github.com/baosang12/BotG/pull/193)  
**Branch:** ops/gate24h-hardening  
**Commit:** fa2e8b3

---

## CHANGES SUMMARY

### 3 Critical Blockers Fixed (B/C/E)

#### B) STRICT VALIDATOR (`validate_artifacts.py`)
- ✅ Enforces REQUEST/ACK/FILL actions in orders.csv
- ✅ Requires commission, spread_cost, slippage_pips columns
- ✅ Requires drawdown, R_used, exposure columns in risk_snapshots
- ✅ Validates >=1300 rows in risk_snapshots.csv
- ✅ Enforces mode=paper, simulation.enabled=false in run_metadata.json
- ✅ UTF-8-sig encoding for all CSV reads
- ✅ Returns exit code 1 on validation failure (CI ready)

#### C) RISK COLUMNS (`RiskSnapshotPersister.cs`)
- ✅ Added `drawdown` column (equity_peak - equity)
- ✅ Added `R_used` column (placeholder 0.0)
- ✅ Added `exposure` column (placeholder 0.0)
- ✅ Updated header: `timestamp,equity,balance,margin,free_margin,drawdown,R_used,exposure`
- ✅ Maintains 60s flush interval

#### E) POSTRUN UTF-8 HARDENING (`postrun_collect.ps1`)
- ✅ PYTHONIOENCODING=utf-8 enforced
- ✅ python -X utf8 for all calls
- ✅ Absolute path validation
- ✅ Direct Python calls (removed wrapper dependency)
- ✅ Safe ZIP creation with error handling

#### WORKFLOW INTEGRATION (`gate24h_main.yml`)
- ✅ PYTHONIOENCODING=utf-8 added to postrun step environment

---

## OPERATOR VERIFICATION CHECKLIST

### 1. Verify Files Changed
```powershell
git diff --name-only origin/main..HEAD
```

**Expected output:**
```
.github/workflows/gate24h_main.yml
BotG/Telemetry/RiskSnapshotPersister.cs
path_issues/validate_artifacts.py
scripts/postrun_collect.ps1
```

**Status:** 4 files modified ✅

---

### 2. Python Syntax Check
```powershell
python -m py_compile path_issues\validate_artifacts.py
```

**Expected:** No errors (silent success)

**Verification:**
```powershell
# Should complete without output
python -m py_compile path_issues\validate_artifacts.py
Write-Host "✓ Python syntax OK"
```

---

### 3. Validator Strict Checks Present
```powershell
Select-String -Path path_issues\validate_artifacts.py -Pattern "REQUEST|ACK|FILL"
Select-String -Path path_issues\validate_artifacts.py -Pattern "commission|spread_cost|slippage_pips"
Select-String -Path path_issues\validate_artifacts.py -Pattern "drawdown|R_used|exposure"
```

**Expected:** Multiple matches for each pattern

**Key lines to verify:**
- Line 39: `REQUIRED_ACTIONS = {"REQUEST", "ACK", "FILL"}`
- Line 30-31: `"commission", "spread_cost", "slippage_pips"`
- Line 35-36: `"drawdown", "R_used", "exposure"`

---

### 4. Risk Snapshot Header Check
```powershell
Select-String -Path BotG\Telemetry\RiskSnapshotPersister.cs -Pattern "timestamp,equity,balance,margin,free_margin,drawdown,R_used,exposure"
```

**Expected:** Match at line ~24 in `EnsureHeader()` method

---

### 5. Postrun UTF-8 Check
```powershell
Select-String -Path scripts\postrun_collect.ps1 -Pattern "PYTHONIOENCODING|utf8"
```

**Expected matches:**
- `$env:PYTHONIOENCODING = 'utf-8'`
- `python -X utf8 .\path_issues\reconstruct_fifo.py`
- `python -X utf8 .\path_issues\validate_artifacts.py`

---

### 6. Workflow UTF-8 Environment
```powershell
Select-String -Path .github\workflows\gate24h_main.yml -Pattern "PYTHONIOENCODING" -Context 2,2
```

**Expected:**
```yaml
    - name: Postrun FIFO reconstruction
      if: always()
      shell: powershell
      env:
        PYTHONIOENCODING: utf-8  # <-- This line
      run: |
```

---

### 7. YAML Syntax Validation
```powershell
python -c "import yaml; yaml.safe_load(open('.github/workflows/gate24h_main.yml', 'r', encoding='utf-8')); print('✓ YAML syntax valid')"
```

**Expected output:** `✓ YAML syntax valid`

---

### 8. Sanity Demo (Optional - If Time Permits)

**Create minimal demo directory:**
```powershell
$demo = "D:\tmp\g24_demo"
if (!(Test-Path $demo)) { 
    New-Item -ItemType Directory -Path $demo | Out-Null 
}

# Create minimal run_metadata.json
@{
    mode = "paper"
    simulation = @{ enabled = $false }
} | ConvertTo-Json | Out-File "$demo\run_metadata.json" -Encoding utf8

# Create minimal orders.csv with required columns and actions
@"
timestamp,order_id,action,status,reason,latency_ms,symbol,side,requested_lots,price_requested,price_filled,commission,spread_cost,slippage_pips
2025-10-03T10:00:00Z,O1,REQUEST,PENDING,Init,0,EURUSD,BUY,1.0,1.10000,0,0,0,0
2025-10-03T10:00:01Z,O1,ACK,ACCEPTED,Broker,50,EURUSD,BUY,1.0,1.10000,0,0,0,0
2025-10-03T10:00:02Z,O1,FILL,FILLED,Execution,100,EURUSD,BUY,1.0,1.10000,1.10005,2.5,1.0,0.5
"@ | Out-File "$demo\orders.csv" -Encoding utf8

# Create minimal risk_snapshots.csv with 1300+ rows
$header = "timestamp,equity,balance,margin,free_margin,drawdown,R_used,exposure"
$header | Out-File "$demo\risk_snapshots.csv" -Encoding utf8
for ($i = 0; $i -lt 1300; $i++) {
    "2025-10-03T10:$($i.ToString('00:00'))Z,10000,10000,100,9900,0,0,0" | Out-File "$demo\risk_snapshots.csv" -Append -Encoding utf8
}

# Create placeholder files
"" | Out-File "$demo\telemetry.csv" -Encoding utf8
"" | Out-File "$demo\trade_closes.log" -Encoding utf8
"" | Out-File "$demo\closed_trades_fifo_reconstructed.csv" -Encoding utf8

Write-Host "✓ Demo directory created: $demo"
```

**Run validator on demo:**
```powershell
python .\path_issues\validate_artifacts.py --dir $demo
Write-Host "Validator exit code: $LASTEXITCODE"
```

**Expected output:**
```json
{
  "base_directory": "D:\\tmp\\g24_demo",
  "checks": [
    {"check": "file_exists", "file": "orders.csv", "ok": true},
    {"check": "file_exists", "file": "telemetry.csv", "ok": true},
    ...
    {"check": "run_metadata", "mode_is_paper": true, "simulation_disabled": true, "ok": true},
    {"check": "orders.csv_columns", "ok": true, "missing": []},
    {"check": "orders.csv_actions", "ok": true, "missing": [], "found": ["ACK", "FILL", "REQUEST"]},
    {"check": "risk_snapshots.csv_columns", "ok": true, "missing": []},
    {"check": "risk_snapshots.csv_rows", "rows": 1300, "ok": true}
  ],
  "overall": "PASS"
}
```

**Validator exit code:** `0` (PASS)

---

## PASS CRITERIA FOR PREFLIGHT ENABLEMENT

### Required (All Must Pass)

1. ✅ **Python compilation:** `py_compile` succeeds for `validate_artifacts.py`
2. ✅ **Strict checks present:** REQUEST/ACK/FILL, cost columns, risk columns found in validator
3. ✅ **Risk header updated:** `drawdown,R_used,exposure` present in `RiskSnapshotPersister.cs`
4. ✅ **UTF-8 enforcement:** `PYTHONIOENCODING` set in both script and workflow
5. ✅ **YAML syntax valid:** No parsing errors in `gate24h_main.yml`
6. ✅ **CI green:** All 6 CI checks pass on PR #193

### Optional (Nice to Have)

- ⭕ **Demo validation:** Sanity test passes with minimal data
- ⭕ **Manual review:** Code inspection confirms logic correctness

---

## POST-MERGE ACTIONS

### 1. Verify Merge
```powershell
git checkout main
git pull origin main
git log --oneline -1
# Should show: "ops(gate2): strict validator + risk columns..."
```

### 2. Preflight Test Run
```bash
# Dispatch Gate24h workflow with:
# - mode: paper
# - hours: 0.1 (6 minutes)
# - Observe run creates risk_snapshots.csv with new columns
# - Confirm validator accepts new schema
# - Check UTF-8 handling works on CI runner
```

### 3. Monitor First Full Run
```bash
# After preflight PASS, dispatch full 24h run:
# - mode: paper
# - hours: 24
# - Verify >=1300 rows in risk_snapshots
# - Confirm drawdown column populated correctly
# - Check postrun validation succeeds
```

---

## TROUBLESHOOTING

### Validator Fails on Demo
**Symptom:** Exit code 1, JSON shows missing columns  
**Fix:** Check CSV header matches exact column names (case-sensitive)

### UTF-8 Encoding Issues
**Symptom:** Unicode errors in Python output  
**Fix:** Verify `PYTHONIOENCODING=utf-8` is set before Python calls

### Risk Snapshots Row Count Fail
**Symptom:** Validator reports rows < 1300  
**Fix:** Confirm bot runs for full duration (24h = 1440 rows @ 60s interval)

### CI Build Fails
**Symptom:** C# compilation errors  
**Fix:** Check `RiskSnapshotPersister.cs` syntax, verify UTF-8 encoding saved correctly

---

## DECISION MATRIX

| Condition | Action |
|-----------|--------|
| All 6 CI checks PASS | ✅ **MERGE PR** → Enable preflight |
| 1+ CI check FAIL | ❌ Review failure logs, fix issues, push updates |
| Validator demo FAIL | ⚠️ Review schema requirements, update demo data |
| UTF-8 errors persist | ⚠️ Check environment variables, verify Python version |

---

## COMPLETION PROOF

**When all checks pass:**

1. Screenshot of PR #193 with all CI checks green ✅
2. Output of `git diff --name-only origin/main..HEAD` showing 4 files
3. Output of `python -m py_compile path_issues\validate_artifacts.py` (no errors)
4. Output of validator demo showing `"overall": "PASS"` and exit code 0

**Status File:**
```powershell
@{
    timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
    pr_number = 193
    commit = "fa2e8b3"
    ci_status = "GREEN"  # or "PENDING" / "FAILED"
    ready_for_preflight = $true  # or $false
    blockers_fixed = @("B", "C", "E")
    verification_passed = $true
} | ConvertTo-Json | Out-File "HARDENING_VERIFICATION_$(Get-Date -Format 'yyyyMMdd_HHmmss').json" -Encoding utf8
```

---

**STATUS:** READY FOR OPERATOR VERIFICATION → (after green CI) → READY FOR PREFLIGHT ✅
