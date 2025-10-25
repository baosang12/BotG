import argparse
import json
import os
from typing import Optional, Tuple

import pandas as pd

# === L1 SCALE HELPERS (PR#283, hardened in PR#284) ===
def _load_symbol_specs():
    path = os.path.join(os.path.dirname(__file__), "symbol_specs.json")
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}

_SYMBOL_SPECS = _load_symbol_specs()

def normalize_symbol(sym: str) -> str:
    """Normalize symbol: uppercase, strip broker suffixes."""
    s = (sym or "").upper().strip()
    for suffix in [".I", ".R", ".PRO", ".M", ".ECN"]:
        if s.endswith(suffix):
            s = s[:-len(suffix)]
    return s

def resolve_point_size(sym: str, prices: list) -> Tuple[float, str]:
    """
    Get point_size for symbol with strict mapping priority.
    Returns (point_size, source) where source is "mapping" or "fallback".
    """
    s = normalize_symbol(sym)
    
    # Priority 1: Use mapping if available
    if s in _SYMBOL_SPECS and "point_size" in _SYMBOL_SPECS[s]:
        return float(_SYMBOL_SPECS[s]["point_size"]), "mapping"
    
    # Priority 2: Fallback with clamping for unknown symbols
    decs = 0
    for p in prices:
        try:
            # Format with high precision to capture all decimals
            sp = f"{float(p):.15f}".rstrip("0").rstrip(".")
            if "." in sp:
                decs = max(decs, len(sp.split(".")[-1]))
        except (ValueError, TypeError):
            pass
    
    if decs <= 0:
        ps = 1e-4  # Conservative default
    else:
        ps = 10 ** (-decs)
    
    # Clamp based on symbol type
    is_metal = any(metal in s for metal in ["XAU", "XAG", "XPT", "XPD", "GOLD", "SILVER"])
    if is_metal:
        ps = min(max(ps, 0.01), 0.1)  # Metals: [0.01, 0.1]
    else:
        ps = min(max(ps, 1e-4), 0.1)  # Forex/others: [0.0001, 0.1]
    
    return ps, "fallback"

# Legacy function kept for compatibility (delegates to resolve_point_size)
def get_point_size_for_symbol(sym: str, prices: list) -> float:
    """Get point_size for symbol from mapping or infer from price decimals."""
    ps, _ = resolve_point_size(sym, prices)
    return ps
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

    rows = []
    for _, order in orders.iterrows():
        side = str(order.get("side", "")).upper()
        symbol = str(order.get("symbol", ""))
        lots = float(order.get("lots", 0))
        px_fill = order.get("price_filled")
        ts_submit = order.get("timestamp_submit")
        ts_fill = order.get("timestamp_fill")

        if pd.isna(ts_submit):
            continue

        idx = _nearest_index(ts_submit, l1_ts, MAX_MATCH_MILLIS)
        if idx is None:
            # No L1 tick found - store with NaN ref
            rows.append({
                "order_id": order.get("order_id", ""),
                "symbol": symbol,
                "side": side,
                "lots": lots,
                "timestamp_submit": ts_submit,
                "timestamp_fill": ts_fill,
                "px_ref": None,
                "px_fill": px_fill,
                "px_ref_side": None,
                "point_used": None,
                "slip_pts": None,
                "slip_cost": 0.0,
                "commission": commission_per_lot_side_usd * lots,
                "swap": 0.0,
            })
            continue

        tick_row = l1.iloc[idx]
        bid_submit = tick_row.get("bid")
        ask_submit = tick_row.get("ask")

        # Determine reference price based on side
        px_ref_side = "ASK" if side == "BUY" else "BID"
        px_ref = ask_submit if side == "BUY" else bid_submit

        rows.append({
            "order_id": order.get("order_id", ""),
            "symbol": symbol,
            "side": side,
            "lots": lots,
            "timestamp_submit": ts_submit,
            "timestamp_fill": ts_fill,
            "px_ref": px_ref,
            "px_fill": px_fill,
            "px_ref_side": px_ref_side,
            "point_used": None,  # Will be computed after DataFrame creation
            "slip_pts": None,
            "slip_cost": None,
            "commission": commission_per_lot_side_usd * lots,
            "swap": (swap_long if side == "BUY" else swap_short) * lots * 0,
        })

    # Create DataFrame
    fees_df = pd.DataFrame(rows)
    
    if fees_df.empty:
        # Empty case - create with proper schema
        fees_df = pd.DataFrame(columns=[
            "order_id", "symbol", "side", "lots", "timestamp_submit", "timestamp_fill",
            "px_ref", "px_fill", "px_ref_side", "point_used", "slip_pts", "slip_cost",
            "commission", "swap"
        ])
    else:
        # SLIPPAGE_COMPUTE_START (PR#284: hardened with missing ref handling)
        # Convert to numeric, coercing errors to NaN
        fees_df["px_ref"] = pd.to_numeric(fees_df["px_ref"], errors="coerce")
        fees_df["px_fill"] = pd.to_numeric(fees_df["px_fill"], errors="coerce")
        
        # Only compute slippage for rows with valid ref and fill prices
        mask_valid = fees_df["px_ref"].notna() & fees_df["px_fill"].notna()
        
        # Compute point_used and slip_pts for valid rows
        for idx in fees_df[mask_valid].index:
            row = fees_df.loc[idx]
            symbol = row["symbol"]
            px_ref = row["px_ref"]
            px_fill = row["px_fill"]
            side = row["side"]
            lots = row["lots"]
            
            # Resolve point size with source tracking
            point_size, source = resolve_point_size(symbol, [px_ref, px_fill])
            fees_df.at[idx, "point_used"] = point_size
            
            # Compute slippage: sign * (fill - ref) / point_size
            sign = 1 if side == "BUY" else -1
            slip_pts = sign * (px_fill - px_ref) / point_size
            fees_df.at[idx, "slip_pts"] = slip_pts
            fees_df.at[idx, "slip_cost"] = max(slip_pts, 0.0) * POINT_VALUE_PER_LOT * lots
        
        # Fill missing slip_cost with 0
        fees_df["slip_cost"] = fees_df["slip_cost"].fillna(0.0)
        # SLIPPAGE_COMPUTE_END
    
    # Generate debug statistics
    out_dir = os.path.dirname(out_fees_csv)
    debug_path = os.path.join(out_dir, "scale_debug.json")
    
    symbol_stats = {}
    if not fees_df.empty:
        for symbol in fees_df["symbol"].unique():
            sym_df = fees_df[fees_df["symbol"] == symbol]
            missing_ref = int(sym_df["px_ref"].isna().sum())
            point_used = float(sym_df["point_used"].max()) if sym_df["point_used"].notna().any() else None
            
            symbol_stats[symbol] = {
                "rows": int(len(sym_df)),
                "missing_ref": missing_ref,
                "point_used": point_used,
            }
    
    with open(debug_path, "w", encoding="utf-8") as f:
        json.dump({"symbol_stats": symbol_stats}, f, indent=2)
    
    # Compute KPIs
    coverage = len(fees_df) / max(len(orders), 1)
    tick_stale_rate = 1.0 - coverage if len(orders) else 0.0

    kpi = {
        "coverage": float(coverage),
        "l1_coverage": float(coverage),
        "tick_stale_rate": float(tick_stale_rate),
    }

    for side in ("BUY", "SELL"):
        side_values = fees_df.loc[fees_df["side"] == side, "slip_pts"] if not fees_df.empty else pd.Series(dtype=float)
        # Only consider non-NaN values for statistics
        side_values = side_values.dropna()
        kpi[f"{side}_median_slip_pts"] = _percentile(side_values, 50)
        kpi[f"{side}_p95_slip_pts"] = _percentile(side_values, 95)

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
