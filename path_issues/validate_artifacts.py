#!/usr/bin/env python3
"""Strict artifact validator for Gate24h runs.

ENFORCES:
- 6 required files exist
- orders.csv: REQUEST/ACK/FILL actions + commission/spread_cost/slippage_pips columns
- risk_snapshots.csv: drawdown/R_used/exposure columns + >=1300 rows
- run_metadata.json: mode=paper, simulation.enabled=false

Exit code 0: All validations passed
Exit code 1: One or more validation failures
"""

import sys
import json
import csv
from pathlib import Path

REQUIRED_FILES = [
    "orders.csv",
    "telemetry.csv",
    "risk_snapshots.csv",
    "trade_closes.log",
    "run_metadata.json",
    "closed_trades_fifo_reconstructed.csv"
]

ORDERS_REQUIRED_COLUMNS = {
    "timestamp", "order_id", "action", "status", "reason", "latency_ms",
    "symbol", "side", "requested_lots", "price_requested", "price_filled",
    "commission", "spread_cost", "slippage_pips"
}

RISK_REQUIRED_COLUMNS = {
    "timestamp", "equity", "balance", "margin", "free_margin",
    "drawdown", "R_used", "exposure"
}

REQUIRED_ACTIONS = {"REQUEST", "ACK", "FILL"}


def read_csv_header(filepath):
    """Read CSV header with UTF-8-sig encoding."""
    try:
        with open(filepath, "r", encoding="utf-8-sig", newline="") as f:
            reader = csv.reader(f)
            header = next(reader, [])
        return {h.strip() for h in header}
    except Exception as e:
        return set()


def read_actions_from_orders(filepath):
    """Extract all unique action values from orders.csv."""
    actions = set()
    try:
        with open(filepath, "r", encoding="utf-8-sig", newline="") as f:
            reader = csv.DictReader(f)
            for row in reader:
                action = (row.get("action") or "").strip().upper()
                if action:
                    actions.add(action)
    except Exception as e:
        pass
    return actions


def count_csv_rows(filepath):
    """Fast row count for CSV file."""
    try:
        with open(filepath, "r", encoding="utf-8-sig") as f:
            return sum(1 for _ in f) - 1  # Exclude header
    except Exception:
        return 0


def validate_run_metadata(filepath):
    """Validate run_metadata.json for mode=paper and simulation.enabled=false."""
    try:
        with open(filepath, "r", encoding="utf-8-sig") as f:
            meta = json.load(f)
        
        mode_ok = str(meta.get("mode", "")).lower() == "paper"
        sim_disabled = not bool(meta.get("simulation", {}).get("enabled", False))
        
        return {
            "mode_is_paper": mode_ok,
            "simulation_disabled": sim_disabled,
            "ok": mode_ok and sim_disabled
        }
    except Exception as e:
        return {
            "mode_is_paper": False,
            "simulation_disabled": False,
            "ok": False,
            "error": str(e)
        }


def main():
    """Main validation entry point."""
    # Parse --dir argument
    if "--dir" not in sys.argv:
        print(json.dumps({"error": "Missing --dir argument"}, indent=2))
        sys.exit(1)
    
    dir_index = sys.argv.index("--dir") + 1
    if dir_index >= len(sys.argv):
        print(json.dumps({"error": "No directory specified after --dir"}, indent=2))
        sys.exit(1)
    
    base_dir = Path(sys.argv[dir_index])
    
    if not base_dir.exists():
        print(json.dumps({"error": f"Directory not found: {base_dir}"}, indent=2))
        sys.exit(1)
    
    report = {
        "base_directory": str(base_dir),
        "checks": []
    }
    
    all_ok = True
    
    # 1. Check all required files exist
    for filename in REQUIRED_FILES:
        filepath = base_dir / filename
        exists = filepath.exists()
        report["checks"].append({
            "check": "file_exists",
            "file": filename,
            "ok": exists
        })
        all_ok &= exists
    
    if not all_ok:
        report["overall"] = "FAIL"
        print(json.dumps(report, ensure_ascii=False, indent=2))
        sys.exit(1)
    
    # 2. Validate run_metadata.json
    meta_result = validate_run_metadata(base_dir / "run_metadata.json")
    report["checks"].append({
        "check": "run_metadata",
        **meta_result
    })
    all_ok &= meta_result["ok"]
    
    # 3. Validate orders.csv schema
    orders_header = read_csv_header(base_dir / "orders.csv")
    orders_missing = ORDERS_REQUIRED_COLUMNS - orders_header
    orders_columns_ok = len(orders_missing) == 0
    
    report["checks"].append({
        "check": "orders.csv_columns",
        "required": sorted(list(ORDERS_REQUIRED_COLUMNS)),
        "missing": sorted(list(orders_missing)),
        "ok": orders_columns_ok
    })
    all_ok &= orders_columns_ok
    
    # 4. Validate orders.csv actions (REQUEST, ACK, FILL)
    actions_found = read_actions_from_orders(base_dir / "orders.csv")
    actions_missing = REQUIRED_ACTIONS - actions_found
    actions_ok = len(actions_missing) == 0
    
    report["checks"].append({
        "check": "orders.csv_actions",
        "required": sorted(list(REQUIRED_ACTIONS)),
        "found": sorted(list(actions_found)),
        "missing": sorted(list(actions_missing)),
        "ok": actions_ok
    })
    all_ok &= actions_ok
    
    # 5. Validate risk_snapshots.csv schema
    risk_header = read_csv_header(base_dir / "risk_snapshots.csv")
    risk_missing = RISK_REQUIRED_COLUMNS - risk_header
    risk_columns_ok = len(risk_missing) == 0
    
    report["checks"].append({
        "check": "risk_snapshots.csv_columns",
        "required": sorted(list(RISK_REQUIRED_COLUMNS)),
        "missing": sorted(list(risk_missing)),
        "ok": risk_columns_ok
    })
    all_ok &= risk_columns_ok
    
    # 6. Validate risk_snapshots.csv row count
    risk_rows = count_csv_rows(base_dir / "risk_snapshots.csv")
    risk_rows_ok = risk_rows >= 1300
    
    report["checks"].append({
        "check": "risk_snapshots.csv_rows",
        "rows": risk_rows,
        "minimum_required": 1300,
        "ok": risk_rows_ok
    })
    all_ok &= risk_rows_ok
    
    # Final report
    report["overall"] = "PASS" if all_ok else "FAIL"
    print(json.dumps(report, ensure_ascii=False, indent=2))
    
    sys.exit(0 if all_ok else 1)


if __name__ == "__main__":
    main()
