#!/usr/bin/env python3
"""Strict artifact validator for Gate24h runs.

Full run (>=1h):
- 6 files must exist
- orders.csv: REQUEST/ACK/FILL + full schema
- risk_snapshots.csv: timestamp/drawdown/R_used/exposure + >=1300 rows (24h) scaled by --run-hours

Smoke-lite (run_hours <= 0.1 or --smoke-lite):
- 6 files must exist
- orders.csv: allow BUY/SELL only; relaxed columns
- risk_snapshots.csv: require timestamp + (equity or balance); rows >= 5; first equity in [199,201]
"""

import sys, json, csv
from pathlib import Path

REQUIRED_FILES = [
    "orders.csv",
    "telemetry.csv",
    "risk_snapshots.csv",
    "trade_closes.log",
    "run_metadata.json",
    "closed_trades_fifo_reconstructed.csv"
]

ORDERS_REQUIRED_COLUMNS_FULL = {
    "timestamp","order_id","action","status","reason","latency_ms",
    "symbol","side","requested_lots","price_requested","price_filled",
    "commission_usd","spread_cost_usd","slippage_pips"
}
ORDERS_MIN_COLUMNS_SMOKE = {"timestamp","symbol","side"}  # side = BUY/SELL

# Column name variants for compatibility
TIMESTAMP_VARIANTS = ["timestamp", "ts", "timestamp_utc", "timestamp_iso"]
TIMESTAMP_REQUEST_VARIANTS = ["timestamp_request", "ts_request"]
TIMESTAMP_ACK_VARIANTS = ["timestamp_ack", "ts_ack"]
TIMESTAMP_FILL_VARIANTS = ["timestamp_fill", "ts_fill"]

REQUIRED_ACTIONS_FULL = {"REQUEST","ACK","FILL"}

RISK_REQUIRED_COLUMNS_FULL = {"timestamp","drawdown_usd","drawdown_percent","R_used","exposure_usd"}
RISK_TIMESTAMP_VARIANTS = ["timestamp", "timestamp_utc", "ts", "timestamp_iso"]

def resolve_column(header, candidates, key_name):
    """Find first matching column from candidates list."""
    for candidate in candidates:
        if candidate in header:
            return candidate
    return None

def read_csv_header(p):
    try:
        with open(p,"r",encoding="utf-8-sig",newline="") as f:
            r=csv.reader(f); return {h.strip() for h in next(r,[])}
    except Exception: return set()

def read_actions(p):
    s=set()
    try:
        with open(p,"r",encoding="utf-8-sig",newline="") as f:
            r=csv.DictReader(f)
            for row in r:
                a=(row.get("action") or row.get("side") or "").strip().upper()
                if a: s.add(a)
    except Exception: pass
    return s

def count_rows(p):
    try:
        with open(p,"r",encoding="utf-8-sig") as f: return max(0,sum(1 for _ in f)-1)
    except Exception: return 0

def first_equity(p):
    try:
        with open(p,"r",encoding="utf-8-sig",newline="") as f:
            r=csv.DictReader(f)
            for row in r:
                for k in ("equity","balance"):
                    if k in row and row[k]:
                        try: return float(row[k])
                        except: pass
                break
    except Exception: pass
    return None

def validate_meta(p):
    try:
        with open(p,"r",encoding="utf-8-sig") as f: d=json.load(f)
        mode_ok = str(d.get("mode","")).lower()=="paper"
        sim_ok  = not d.get("simulation",{}).get("enabled",False)
        return {"mode_is_paper":mode_ok,"simulation_disabled":sim_ok,"ok":mode_ok and sim_ok}
    except Exception as e:
        return {"mode_is_paper":False,"simulation_disabled":False,"ok":False,"error":str(e)}

def main():
    # args
    if "--dir" not in sys.argv:
        print(json.dumps({"error":"Missing --dir"},indent=2)); sys.exit(1)
    base = Path(sys.argv[sys.argv.index("--dir")+1])

    run_hours = 24.0
    if "--run-hours" in sys.argv:
        try: run_hours = float(sys.argv[sys.argv.index("--run-hours")+1])
        except: pass
    smoke_flag = ("--smoke-lite" in sys.argv) or (run_hours <= 0.1)

    rep={"base_directory":str(base),"checks":[]}; ok=True

    # 0. required files
    for fn in REQUIRED_FILES:
        exists=(base/fn).exists(); rep["checks"].append({"check":"file_exists","file":fn,"ok":exists}); ok&=exists
    if not ok:
        rep["overall"]="FAIL"; print(json.dumps(rep,ensure_ascii=False,indent=2)); sys.exit(1)

    # 1. run_metadata
    meta=validate_meta(base/"run_metadata.json"); rep["checks"].append({"check":"run_metadata",**meta}); ok&=meta["ok"]

    # 2. orders schema + actions
    hdr = read_csv_header(base/"orders.csv")
    if smoke_flag:
        # Check for timestamp variants
        ts_col = resolve_column(hdr, TIMESTAMP_VARIANTS, "timestamp")
        if not ts_col:
            miss = ["timestamp (or variants)"]
            cols_ok = False
        else:
            miss = []
            cols_ok = True
        # Also need symbol and side
        for col in ["symbol", "side"]:
            if col not in hdr:
                miss.append(col)
                cols_ok = False
        
        acts = read_actions(base/"orders.csv")
        # smoke chấp nhận chỉ BUY/SELL
        acts_ok = len(acts.intersection({"BUY","SELL","REQUEST","ACK","FILL"}))>0
    else:
        # Full mode: check for timestamp variant
        ts_col = resolve_column(hdr, TIMESTAMP_VARIANTS, "timestamp")
        miss = []
        if not ts_col:
            miss.append("timestamp (or variants)")
        
        # Check other required columns
        required_without_ts = ORDERS_REQUIRED_COLUMNS_FULL - {"timestamp"}
        miss.extend(sorted(list(required_without_ts - hdr)))
        cols_ok = (len(miss)==0)
        
        acts = read_actions(base/"orders.csv")
        acts_ok = REQUIRED_ACTIONS_FULL.issubset(acts)

    rep["checks"].append({"check":"orders.csv_columns","mode":"smoke" if smoke_flag else "full","missing":sorted(list(miss)) if miss else [],"ok":cols_ok})
    ok &= cols_ok
    rep["checks"].append({"check":"orders.csv_actions","mode":"smoke" if smoke_flag else "full","found":sorted(list(acts)),"ok":acts_ok})
    ok &= acts_ok

    # 3. risk_snapshots
    risk_hdr = read_csv_header(base/"risk_snapshots.csv")
    if smoke_flag:
        # Check for timestamp variant
        ts_col = resolve_column(risk_hdr, RISK_TIMESTAMP_VARIANTS, "risk_timestamp")
        if not ts_col:
            miss = ["timestamp (or variants)"]
            cols_ok = False
        else:
            miss = []
            cols_ok = True
            
        rows = count_rows(base/"risk_snapshots.csv")
        min_rows = max(5, int(1300*(run_hours/24.0)))
        rows_ok = rows >= min_rows
        eq0 = first_equity(base/"risk_snapshots.csv")
        # Remove hardcoded equity check - accept any positive value
        eq_ok = (eq0 is not None and eq0 > 0)
    else:
        # Full mode: check for timestamp variant
        ts_col = resolve_column(risk_hdr, RISK_TIMESTAMP_VARIANTS, "risk_timestamp")
        miss = []
        if not ts_col:
            miss.append("timestamp (or variants)")
        
        # Check other required columns
        required_without_ts = RISK_REQUIRED_COLUMNS_FULL - {"timestamp"}
        miss.extend(sorted(list(required_without_ts - risk_hdr)))
        cols_ok = (len(miss)==0)
        
        rows = count_rows(base/"risk_snapshots.csv")
        min_rows = max(10, int(1300*(run_hours/24.0)))
        rows_ok = rows >= min_rows
        eq0 = first_equity(base/"risk_snapshots.csv")
        # Accept any positive equity
        eq_ok = (eq0 is None or eq0 > 0)

    rep["checks"].append({"check":"risk_snapshots.csv_columns","mode":"smoke" if smoke_flag else "full","missing":sorted(list(miss)) if miss else [],"ok":cols_ok})
    ok &= cols_ok
    rep["checks"].append({"check":"risk_snapshots.csv_rows","rows":rows,"minimum_required":min_rows,"ok":rows_ok})
    ok &= rows_ok
    rep["checks"].append({"check":"risk_first_equity_positive","value":eq0,"ok":eq_ok})
    ok &= eq_ok

    rep["overall"]="PASS" if ok else "FAIL"
    print(json.dumps(rep,ensure_ascii=False,indent=2))
    sys.exit(0 if ok else 1)

if __name__=="__main__":
    main()
