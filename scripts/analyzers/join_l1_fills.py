import argparse
import json
import os
from typing import Optional, Tuple

import pandas as pd
import numpy as np

# === L1 SCALE HELPERS (PR#283, enhanced PR#285) ===
def _load_symbol_specs():
    path = os.path.join(os.path.dirname(__file__), "symbol_specs.json")
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}

_SYMBOL_SPECS = _load_symbol_specs()

def get_point_size_for_symbol(sym: str, prices: list) -> float:
    """Get point_size for symbol from mapping or infer from price decimals."""
    sym = (sym or "").upper()
    if sym in _SYMBOL_SPECS and "point_size" in _SYMBOL_SPECS[sym]:
        return float(_SYMBOL_SPECS[sym]["point_size"])
    
    # Fallback: infer from decimals of observed prices
    decs = 0
    for p in prices:
        s = str(p)
        if "." in s:
            decs = max(decs, len(s.split(".")[-1]))
    if decs <= 0:
        # conservative fallback
        return 0.0001
    return 10 ** (-decs)

def rel_gap(a: float, b: float) -> float:
    """Calculate relative gap between two prices."""
    try:
        na = float(a)
        nb = float(b)
        den = max(abs(na), abs(nb), 1e-9)
        return abs(na - nb) / den
    except (ValueError, TypeError):
        return float('inf')  # Invalid comparison

def get_rel_gap_threshold(symbol: str) -> float:
    """Get relative gap threshold for symbol validation."""
    sym_upper = (symbol or "").upper()
    # Stricter for metals
    if any(metal in sym_upper for metal in ["XAU", "XAG", "XPT", "XPD", "GOLD", "SILVER"]):
        return 0.03  # 3% for metals
    return 0.05  # 5% default
# === /L1 SCALE HELPERS ===

POINT = float(os.getenv("POINT_VALUE", "0.0001"))
POINT_VALUE_PER_LOT = float(os.getenv("POINT_VALUE_PER_LOT", "10.0"))
MAX_MATCH_MILLIS = int(os.getenv("L1_MATCH_MAX_MS", "500"))
REQUIRED_ORDER_COLUMNS = {
    "order_id",
    "symbol",
    "side",
    "lots",
    "timestamp_submit",
    "timestamp_fill",
    "price_filled",
}
REQUIRED_L1_COLUMNS = {"timestamp", "bid", "ask"}


def _nearest_index(ts: pd.Timestamp, series: pd.Series, max_ms: int) -> Optional[int]:
    """Return index of nearest timestamp within the allowed window."""
    if series.empty:
        return None

    position = series.searchsorted(ts)
    candidates = []
    if position > 0:
        candidates.append(position - 1)
    if position < len(series):
        candidates.append(position)
    if not candidates:
        return None

    best_idx = min(
        candidates,
        key=lambda idx: abs((series.iloc[idx] - ts).total_seconds()),
    )
    delta_ms = abs((series.iloc[best_idx] - ts).total_seconds() * 1000.0)
    return best_idx if delta_ms <= max_ms else None


def _percentile(values: pd.Series, percentile: float) -> float:
    if values.empty:
        return 0.0
    return float(values.quantile(percentile / 100.0))


def main(
    orders_csv: str,
    l1_csv: str,
    out_fees_csv: str,
    out_kpi_json: str,
    commission_per_lot_side_usd: float = 0.0,
    swap_long: float = 0.0,
    swap_short: float = 0.0,
) -> None:
    orders = pd.read_csv(
        orders_csv,
        parse_dates=["timestamp_submit", "timestamp_fill"],
    )
    missing_order_cols = REQUIRED_ORDER_COLUMNS.difference(orders.columns)
    if missing_order_cols:
        raise ValueError(f"orders.csv missing columns: {sorted(missing_order_cols)}")

    l1 = pd.read_csv(l1_csv, parse_dates=["timestamp"])
    missing_l1_cols = REQUIRED_L1_COLUMNS.difference(l1.columns)
    if missing_l1_cols:
        raise ValueError(f"l1_stream.csv missing columns: {sorted(missing_l1_cols)}")
    l1 = l1.sort_values("timestamp").reset_index(drop=True)

    l1_ts = l1["timestamp"]

    # Track debug stats per symbol
    symbol_stats = {}
    
    rows = []
    for _, order in orders.iterrows():
        side = str(order.get("side", "")).upper()
        symbol = str(order.get("symbol", ""))
        lots = float(order.get("lots", 0))
        px_fill = float(order.get("price_filled", 0))
        ts_submit = order.get("timestamp_submit")
        ts_fill = order.get("timestamp_fill")

        # Initialize symbol stats
        if symbol not in symbol_stats:
            symbol_stats[symbol] = {
                "total_rows": 0,
                "missing_ref": 0,
                "invalid_ref": 0,
                "fallback_requested": 0,
                "skipped_no_ref": 0,
                "point_used": set()
            }
        
        symbol_stats[symbol]["total_rows"] += 1

        if pd.isna(ts_submit):
            continue

        # Get L1 reference price
        px_ref_l1 = None
        px_ref_side = None
        idx = _nearest_index(ts_submit, l1_ts, MAX_MATCH_MILLIS)
        if idx is not None:
            tick_row = l1.iloc[idx]
            bid_submit = float(tick_row.get("bid", 0))
            ask_submit = float(tick_row.get("ask", 0))
            px_ref_l1 = ask_submit if side == "BUY" else bid_submit
            px_ref_side = "ASK" if side == "BUY" else "BID"

        # Get price_requested as fallback
        price_requested = order.get("price_requested")
        px_ref_requested = None
        if not pd.isna(price_requested):
            try:
                px_ref_requested = float(price_requested)
            except (ValueError, TypeError):
                pass

        # Validate L1 ref and choose final ref
        px_ref = None
        ref_source = "NONE"
        
        if px_ref_l1 is not None and px_ref_l1 > 0:
            # Check if L1 ref is valid (within threshold of px_fill)
            gap = rel_gap(px_ref_l1, px_fill)
            threshold = get_rel_gap_threshold(symbol)
            
            if gap <= threshold:
                px_ref = px_ref_l1
                ref_source = "L1"
            else:
                # L1 ref invalid, try fallback
                symbol_stats[symbol]["invalid_ref"] += 1
                if px_ref_requested is not None and px_ref_requested > 0:
                    px_ref = px_ref_requested
                    ref_source = "REQUESTED"
                    symbol_stats[symbol]["fallback_requested"] += 1
        else:
            # No L1 ref, try price_requested
            symbol_stats[symbol]["missing_ref"] += 1
            if px_ref_requested is not None and px_ref_requested > 0:
                px_ref = px_ref_requested
                ref_source = "REQUESTED"
                symbol_stats[symbol]["fallback_requested"] += 1

        if px_ref is None or px_ref <= 0:
            # Skip row - no valid reference
            symbol_stats[symbol]["skipped_no_ref"] += 1
            continue

        s = 1 if side == "BUY" else -1

        # SLIPPAGE_COMPUTE_START (PR#285)
        # Get point_size for this symbol
        point_size = get_point_size_for_symbol(symbol, [px_fill, px_ref])
        if point_size <= 0:
            point_size = 0.0001  # Safety fallback
        
        symbol_stats[symbol]["point_used"].add(point_size)
        
        slip_pts = s * (px_fill - px_ref) / point_size
        slip_cost = max(slip_pts, 0.0) * POINT_VALUE_PER_LOT * lots
        # SLIPPAGE_COMPUTE_END

        commission = commission_per_lot_side_usd * lots
        swap = (swap_long if side == "BUY" else swap_short) * lots * 0

        rows.append(
            {
                "order_id": order.get("order_id", ""),
                "symbol": symbol,
                "side": side,
                "lots": lots,
                "timestamp_submit": ts_submit,
                "timestamp_fill": ts_fill,
                "px_ref": px_ref,
                "px_ref_side": px_ref_side or "",
                "ref_source": ref_source,
                "px_fill": px_fill,
                "point_used": point_size,
                "slip_pts": slip_pts,
                "slip_cost": slip_cost,
                "commission": commission,
                "swap": swap,
            }
        )

    columns = [
        "order_id",
        "symbol",
        "side",
        "lots",
        "timestamp_submit",
        "timestamp_fill",
        "px_ref",
        "px_ref_side",
        "ref_source",
        "px_fill",
        "point_used",
        "slip_pts",
        "slip_cost",
        "commission",
        "swap",
    ]
    fees_df = pd.DataFrame(rows, columns=columns)
    coverage = len(fees_df) / max(len(orders), 1)
    tick_stale_rate = 1.0 - coverage if len(orders) else 0.0

    kpi = {
        "coverage": float(coverage),
        "l1_coverage": float(coverage),
        "tick_stale_rate": float(tick_stale_rate),
    }

    for side in ("BUY", "SELL"):
        side_values = fees_df.loc[fees_df["side"] == side, "slip_pts"] if not fees_df.empty else pd.Series(dtype=float)
        kpi[f"{side}_median_slip_pts"] = _percentile(side_values, 50)
        kpi[f"{side}_p95_slip_pts"] = _percentile(side_values, 95)

    # Generate scale_debug.json with enhanced stats
    debug_stats = {}
    for sym, stats in symbol_stats.items():
        debug_stats[sym] = {
            "total_rows": stats["total_rows"],
            "missing_ref": stats["missing_ref"],
            "invalid_ref": stats["invalid_ref"],
            "fallback_requested": stats["fallback_requested"],
            "skipped_no_ref": stats["skipped_no_ref"],
            "point_used": sorted(list(stats["point_used"])) if stats["point_used"] else []
        }
    
    # Ensure output directory exists for l1 subdirectory
    out_dir = os.path.dirname(out_fees_csv)
    if out_dir and not os.path.exists(out_dir):
        os.makedirs(out_dir, exist_ok=True)
    
    scale_debug_path = os.path.join(out_dir, "scale_debug.json")
    with open(scale_debug_path, "w", encoding="utf-8") as handle:
        json.dump(debug_stats, handle, indent=2)

    fees_df.to_csv(out_fees_csv, index=False)
    with open(out_kpi_json, "w", encoding="utf-8") as handle:
        json.dump(kpi, handle, indent=2)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--orders", required=True)
    parser.add_argument("--l1", required=True)
    parser.add_argument("--out-fees", required=True)
    parser.add_argument("--out-kpi", required=True)
    parser.add_argument(
        "--commission",
        type=float,
        default=float(os.getenv("COMMISSION_PER_LOT_SIDE_USD", 0.0)),
    )
    parser.add_argument(
        "--swap-long",
        type=float,
        default=float(os.getenv("SWAP_LONG", 0.0)),
    )
    parser.add_argument(
        "--swap-short",
        type=float,
        default=float(os.getenv("SWAP_SHORT", 0.0)),
    )
    args = parser.parse_args()
    main(
        orders_csv=args.orders,
        l1_csv=args.l1,
        out_fees_csv=args.out_fees,
        out_kpi_json=args.out_kpi,
        commission_per_lot_side_usd=args.commission,
        swap_long=args.swap_long,
        swap_short=args.swap_short,
    )
