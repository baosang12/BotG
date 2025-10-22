import argparse
import json
import os
from typing import Optional

import pandas as pd

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
        lots = float(order.get("lots", 0))
        px_fill = float(order.get("price_filled", 0))
        ts_submit = order.get("timestamp_submit")
        ts_fill = order.get("timestamp_fill")

        if pd.isna(ts_submit):
            continue

        idx = _nearest_index(ts_submit, l1_ts, MAX_MATCH_MILLIS)
        if idx is None:
            continue

        tick_row = l1.iloc[idx]
        bid_submit = float(tick_row.get("bid", 0))
        ask_submit = float(tick_row.get("ask", 0))

        px_ref = ask_submit if side == "BUY" else bid_submit
        s = 1 if side == "BUY" else -1

        slip_pts = s * (px_fill - px_ref) / POINT if POINT else 0.0
        slip_cost = max(slip_pts, 0.0) * POINT_VALUE_PER_LOT * lots

        commission = commission_per_lot_side_usd * lots
        swap = (swap_long if side == "BUY" else swap_short) * lots * 0

        rows.append(
            {
                "order_id": order.get("order_id", ""),
                "symbol": order.get("symbol", ""),
                "side": side,
                "lots": lots,
                "timestamp_submit": ts_submit,
                "timestamp_fill": ts_fill,
                "px_ref": px_ref,
                "px_fill": px_fill,
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
        "px_fill",
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
