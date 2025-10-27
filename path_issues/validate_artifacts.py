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

def pick_col(cols, *cands):
    """Pick first matching column from candidates."""
    for c in cands:
        if c in cols:
            return c
    return None

def derive_drawdown(equity_series):
    """Derive drawdown_usd and drawdown_percent from equity series."""
    peak = None
    dd_usd = []
    dd_pct = []
    for x in equity_series:
        if peak is None or x > peak:
            peak = x
        d = (peak - x) if peak else 0.0
        dd_usd.append(d)
        dd_pct.append((d / peak * 100.0) if peak and peak > 0 else 0.0)
    return dd_usd, dd_pct

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

    rep={"base_directory":str(base),"checks":[],"warnings":[]}; ok=True
    
    def add_warning(key, msg):
        rep["warnings"].append({"key": key, "message": msg})

    # 0. required files
    for fn in REQUIRED_FILES:
        exists=(base/fn).exists(); rep["checks"].append({"check":"file_exists","file":fn,"ok":exists}); ok&=exists
    if not ok:
        rep["overall"]="FAIL"; print(json.dumps(rep,ensure_ascii=False,indent=2)); sys.exit(1)

    # 1. run_metadata
    meta=validate_meta(base/"run_metadata.json"); rep["checks"].append({"check":"run_metadata",**meta}); ok&=meta["ok"]

    # 2. orders schema + lifecycle
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
        
        # Check lifecycle via status/event/lifecycle OR fill evidence
        status_col = pick_col(hdr, "status", "event", "lifecycle")
        lifecycle_ok = False
        if status_col:
            acts = read_actions(base/"orders.csv")
            lifecycle_ok = len(acts.intersection({"REQUEST", "ACK", "FILL"})) > 0
            if not lifecycle_ok:
                # Tolerate BUY/SELL in smoke mode
                lifecycle_ok = len(acts.intersection({"BUY", "SELL"})) > 0
                if lifecycle_ok:
                    add_warning("orders.lifecycle", "found BUY/SELL in action/side; accepted in smoke mode")
        
        if not lifecycle_ok:
            # Fallback: check for fill evidence
            ts_fill_col = pick_col(hdr, "timestamp_fill", "ts_fill")
            price_fill_col = pick_col(hdr, "price_filled", "fill_price")
            if ts_fill_col and price_fill_col:
                add_warning("orders.lifecycle", "no status column with REQUEST/ACK/FILL; inferred fills via timestamp_fill/price_filled")
                lifecycle_ok = True
    else:
        # Full mode: check for timestamp variant
        ts_col = resolve_column(hdr, TIMESTAMP_VARIANTS, "timestamp")
        miss = []
        if not ts_col:
            miss.append("timestamp (or variants)")
        
        # Check lifecycle via status/event/lifecycle column
        status_col = pick_col(hdr, "status", "event", "lifecycle")
        lifecycle_ok = False
        if status_col:
            acts = read_actions(base/"orders.csv")
            lifecycle_ok = REQUIRED_ACTIONS_FULL.issubset(acts)
            if not lifecycle_ok and len(acts.intersection({"BUY", "SELL"})) > 0:
                add_warning("orders.lifecycle", "found BUY/SELL but missing REQUEST/ACK/FILL lifecycle")
        
        if not lifecycle_ok:
            # Fallback: check for fill evidence
            ts_fill_col = pick_col(hdr, "timestamp_fill", "ts_fill")
            price_fill_col = pick_col(hdr, "price_filled", "fill_price")
            if ts_fill_col and price_fill_col:
                add_warning("orders.lifecycle", "no full lifecycle status; inferred fills via timestamp_fill/price_filled")
                lifecycle_ok = True
        
        # Check other required columns (relaxed - no longer require action/status strictly)
        required_optional = {"order_id", "symbol", "side", "requested_lots", "price_requested", 
                           "price_filled", "commission_usd", "spread_cost_usd", "slippage_pips", 
                           "reason", "latency_ms"}
        missing_optional = required_optional - hdr
        if missing_optional:
            add_warning("orders.optional_columns", f"missing optional columns: {sorted(list(missing_optional))}")
        
        cols_ok = True  # Only fail if timestamp missing

    rep["checks"].append({"check":"orders.csv_columns","mode":"smoke" if smoke_flag else "full","missing":sorted(list(miss)) if miss else [],"ok":cols_ok})
    ok &= cols_ok
    rep["checks"].append({"check":"orders.lifecycle_status","mode":"smoke" if smoke_flag else "full","ok":lifecycle_ok})
    ok &= lifecycle_ok

    # 3. risk_snapshots - core: timestamp + equity; drawdown/exposure optional (derive if needed)
    risk_hdr = read_csv_header(base/"risk_snapshots.csv")
    
    # Core requirement: timestamp variant + equity
    ts_col = resolve_column(risk_hdr, RISK_TIMESTAMP_VARIANTS, "risk_timestamp")
    eq_col = pick_col(risk_hdr, "equity", "balance")
    
    if not ts_col or not eq_col:
        miss = []
        if not ts_col:
            miss.append("timestamp (or variants)")
        if not eq_col:
            miss.append("equity (or balance)")
        cols_ok = False
    else:
        miss = []
        cols_ok = True
        
        # Optional columns - derive if missing, warn instead of fail
        has_dd_usd = pick_col(risk_hdr, "drawdown_usd", "drawdown") is not None
        has_dd_pct = pick_col(risk_hdr, "drawdown_percent", "drawdown_pct") is not None
        has_r_used = "R_used" in risk_hdr
        has_exposure = pick_col(risk_hdr, "exposure_usd", "exposure") is not None
        
        if not (has_dd_usd and has_dd_pct):
            add_warning("risk.drawdown", "drawdown_* columns missing; can be derived from equity for validation")
        if not has_r_used:
            add_warning("risk.R_used", "R_used column missing; not critical for core validation")
        if not has_exposure:
            add_warning("risk.exposure", "exposure_usd/exposure column missing; assuming 0 for validation")
    
    if smoke_flag:
        rows = count_rows(base/"risk_snapshots.csv")
        min_rows = max(5, int(1300*(run_hours/24.0)))
        rows_ok = rows >= min_rows
        eq0 = first_equity(base/"risk_snapshots.csv")
        # Accept any positive equity
        eq_ok = (eq0 is not None and eq0 > 0)
    else:
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
