#!/usr/bin/env python3
"""
Reconstruct closed trades (FIFO) from orders.csv fills.

Usage:
  python reconstruct_closed_trades_sqlite.py --orders path\\to\\orders.csv --out path\\to\\closed_trades_fifo_reconstructed.csv

Notes:
- Fixes csv shadowing: do not use a local variable named `csv`; `import csv` stays at top.
- Outputs columns: trade_id,open_time,close_time,symbol,side,volume,open_price,close_price,pnl,status
"""

import argparse
import csv
import os
from datetime import datetime, timezone
from collections import defaultdict, deque
from typing import Dict, Deque, List, Tuple, Optional
from decimal import Decimal, ROUND_HALF_UP, InvalidOperation


def parse_args():
    p = argparse.ArgumentParser(description="Reconstruct closed trades from orders.csv (FIFO)")
    p.add_argument("--orders", required=True, help="Path to orders.csv")
    p.add_argument("--out", required=True, help="Output CSV path for reconstructed closed trades")
    p.add_argument("--fill_phase", default="FILL", help="Phase value indicating fill rows (default: FILL)")
    return p.parse_args()


def to_epoch_ms(val: Optional[str]) -> Optional[int]:
    if val is None or str(val).strip() == "":
        return None
    s = str(val).strip()
    # numeric path (avoid float rounding by using Decimal)
    try:
        # Heuristics: if integer-like
        if s.isdigit():
            v = int(s)
            if v > 10_000_000_000:  # very likely ms
                return v
            # seconds -> ms
            return v * 1000
        d = Decimal(s)
        # If looks like seconds (<= 1e11), convert precisely
        if d > Decimal(10_000_000_000):  # already ms
            return int(d.to_integral_value(rounding=ROUND_HALF_UP))
        return int((d * Decimal(1000)).to_integral_value(rounding=ROUND_HALF_UP))
    except Exception:
        pass
    # ISO path
    try:
        s2 = s.replace("Z", "+00:00")
        dt = datetime.fromisoformat(s2)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return int(dt.timestamp() * 1000)
    except Exception:
        return None


def to_iso_utc(ms: Optional[int]) -> str:
    if ms is None:
        return ""
    try:
        return datetime.fromtimestamp(ms / 1000.0, tz=timezone.utc).isoformat().replace("+00:00", "Z")
    except Exception:
        return ""


def pick_col(fieldnames: List[str], candidates: List[str]) -> Optional[str]:
    if not fieldnames:
        return None
    lowered = {name.lower(): name for name in fieldnames}
    for cand in candidates:
        if cand.lower() in lowered:
            return lowered[cand.lower()]
    # relaxed contains match
    for name in fieldnames:
        nl = name.lower()
        for cand in candidates:
            if cand.lower() in nl:
                return name
    return None

class Fill:
    __slots__ = ("symbol", "side", "volume", "price", "epoch_ms", "iso")

    def __init__(self, symbol: str, side: str, volume: Decimal, price: Decimal, epoch_ms: int, iso: str):
        self.symbol = symbol
        self.side = side  # BUY or SELL
        self.volume = Decimal(volume)
        self.price = Decimal(price)
        self.epoch_ms = int(epoch_ms)
        self.iso = iso


def read_fills(orders_path: str, fill_phase: str) -> List[Fill]:
    fills: List[Fill] = []
    try:
        with open(orders_path, newline="", encoding="utf-8-sig") as fh:
            reader = csv.DictReader(fh)
            cols = reader.fieldnames or []
            phase_col = pick_col(cols, ["phase", "event", "status", "type"])  # filter FILLs
            symbol_col = pick_col(cols, ["symbol", "instrument", "ticker"]) or "symbol"
            side_col = pick_col(cols, ["side", "direction"]) or "side"
            price_col = pick_col(cols, [
                "execPrice",
                "price_filled",
                "fill_price",
                "executionPrice",
                "price",
                "avg_price",
                "fillPrice",
                "execprice",
                "execution_price",
                "intendedPrice",
            ])
            size_col = pick_col(cols, [
                "filledSize",
                "size_filled",
                "size",
                "volume",
                "requestedVolume",
                "theoretical_units",
                "theoretical_lots",
            ])
            # Prefer explicit ms epoch columns; avoid generic names that may refer to entry/exit rather than FILL time
            epoch_col = pick_col(cols, [
                "timestamp_ms",
                "epoch_ms",
                "event_time_ms",
                "event_epoch_ms",
                "time_ms",
                "t_ms",
                "epoch",
            ])
            # ISO-like fallbacks strictly for event/fill time (exclude entry_time/exit_time)
            ts_col = pick_col(cols, [
                "timestamp_iso",
                "fill_time",
                "fill_timestamp",
                "event_time",
                "time",
                "timestamp",
            ])

            for row in reader:
                # Filter by FILL phase if column provided
                if phase_col:
                    if str(row.get(phase_col, "")).strip().upper() != str(fill_phase).strip().upper():
                        continue

                symbol = str(row.get(symbol_col, "")).strip() if symbol_col else ""
                if not symbol:
                    symbol = "?"

                side_raw = str(row.get(side_col, "")).strip().upper() if side_col else ""
                if side_raw not in ("BUY", "SELL"):
                    if side_raw in ("LONG", "OPEN_LONG"):
                        side_raw = "BUY"
                    elif side_raw in ("SHORT", "OPEN_SHORT"):
                        side_raw = "SELL"
                    else:
                        continue

                # parse volume (Decimal)
                vol_str = row.get(size_col) if size_col else None
                try:
                    volume = Decimal(str(vol_str)).copy_abs() if vol_str not in (None, "") else None
                except (InvalidOperation, Exception):
                    volume = None
                if not volume or volume <= 0:
                    continue

                # parse price (Decimal), fallback to intendedPrice if needed
                price_val: Optional[Decimal] = None
                cand_vals: List[Optional[str]] = []
                if price_col:
                    cand_vals.append(row.get(price_col))
                for alt in ("execPrice", "price", "fill_price", "intendedPrice"):
                    if alt != price_col and alt in row:
                        cand_vals.append(row.get(alt))
                for v in cand_vals:
                    if v not in (None, ""):
                        try:
                            price_val = Decimal(str(v))
                            break
                        except (InvalidOperation, Exception):
                            continue
                if price_val is None:
                    continue

                # parse time
                epoch_ms = None
                if epoch_col and row.get(epoch_col) not in (None, ""):
                    try:
                        epoch_ms = to_epoch_ms(row.get(epoch_col))
                    except Exception:
                        epoch_ms = None
                if epoch_ms is None and ts_col and row.get(ts_col):
                    epoch_ms = to_epoch_ms(row.get(ts_col))
                if epoch_ms is None:
                    for alt in ("timestamp_iso", "fill_time", "fill_timestamp", "event_time", "timestamp"):
                        if alt in row and row.get(alt):
                            epoch_ms = to_epoch_ms(row.get(alt))
                            if epoch_ms is not None:
                                break
                if epoch_ms is None:
                    continue

                fills.append(Fill(symbol, side_raw, volume, price_val, epoch_ms, to_iso_utc(epoch_ms)))
    except FileNotFoundError:
        raise
    except Exception as e:
        raise RuntimeError(f\"Failed reading orders CSV: {e}\")

    # sort by time to ensure FIFO is chronological
    fills.sort(key=lambda f: f.epoch_ms)
    return fills


def fifo_reconstruct(fills: List[Fill]) -> List[Tuple[str, str, str, str, Decimal, Decimal, Decimal, Decimal, str]]:
    """
    Returns list of rows matching header:
    trade_id,open_time,close_time,symbol,side,volume,open_price,close_price,pnl,status
    """
    # Per-symbol long/short queues
    longs: Dict[str, Deque[Fill]] = defaultdict(deque)
    shorts: Dict[str, Deque[Fill]] = defaultdict(deque)

    closed: List[Tuple[str, str, str, str, Decimal, Decimal, Decimal, Decimal, str]] = []
    seq = 1

    for f in fills:
        sym = f.symbol
        if f.side == "BUY":
            # Close existing shorts first
            vol_left: Decimal = Decimal(f.volume)
            while vol_left > Decimal("0") and shorts[sym]:
                s = shorts[sym][0]
                take = s.volume if s.volume <= vol_left else vol_left
                # Enforce chronological close >= open
                open_ms = s.epoch_ms
                close_ms = f.epoch_ms if f.epoch_ms >= open_ms else open_ms
                pnl = (s.price - f.price) * take  # short pnl = open - close
                trade_id = f"fifo_{seq}"
                seq += 1
                closed.append((
                    trade_id,
                    to_iso_utc(open_ms),
                    to_iso_utc(close_ms),
                    sym,
                    "SHORT",
                    take,
                    s.price,
                    f.price,
                    pnl,
                    "closed",
                ))
                s.volume -= take
                vol_left -= take
                if s.volume <= Decimal("1e-12"):
                    shorts[sym].popleft()
            # Remainder opens long
            if vol_left > Decimal("1e-12"):
                longs[sym].append(Fill(sym, "BUY", vol_left, f.price, f.epoch_ms, f.iso))

        elif f.side == "SELL":
            # Close existing longs first
            while vol_left > Decimal("0") and longs[sym]:
                l = longs[sym][0]
                take = l.volume if l.volume <= vol_left else vol_left
                # Enforce chronological close >= open
                open_ms = l.epoch_ms
                close_ms = f.epoch_ms if f.epoch_ms >= open_ms else open_ms
                pnl = (f.price - l.price) * take  # long pnl = close - open
                trade_id = f"fifo_{seq}"
                seq += 1
                closed.append((
                    trade_id,
                    to_iso_utc(open_ms),
                    to_iso_utc(close_ms),
                    sym,
                    "LONG",
                    take,
                    l.price,
                    f.price,
                    pnl,
                    "closed",
                ))
                l.volume -= take
                vol_left -= take
                if l.volume <= Decimal("1e-12"):
                    longs[sym].popleft()
            # Remainder opens short
            if vol_left > Decimal("1e-12"):
                shorts[sym].append(Fill(sym, "SELL", vol_left, f.price, f.epoch_ms, f.iso))
        else:
            # ignore unknown side
            continue

    return closed


def _q(num: Decimal, places: int = 8) -> str:
    """Quantize Decimal to fixed places with HALF_UP, return string."""
    if not isinstance(num, Decimal):
        try:
            num = Decimal(str(num))
        except Exception:
            num = Decimal(0)
    q = Decimal("1." + ("0" * places))
    return str(num.quantize(q, rounding=ROUND_HALF_UP))


def write_output(out_path: str, rows: List[Tuple[str, str, str, str, Decimal, Decimal, Decimal, Decimal, str]]):
    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    header = [
        "trade_id",
        "open_time",
        "close_time",
        "symbol",
        "side",
        "volume",
        "open_price",
        "close_price",
        "pnl",
        "status",
    ]
    with open(out_path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(header)
        for r in rows:
            # format numerics deterministically using Decimal
            trade_id, open_time, close_time, symbol, side, volume, open_p, close_p, pnl, status = r
            w.writerow([
                trade_id,
                open_time,
                close_time,
                symbol,
                side,
                _q(volume, 8),
                _q(open_p, 8),
                _q(close_p, 8),
                _q(pnl, 8),
                status,
            ])


def main():
    args = parse_args()
    orders_path = args.orders
    out_path = args.out
    fill_phase = args.fill_phase

    if not os.path.exists(orders_path):
        raise FileNotFoundError(f"orders not found: {orders_path}")

    # If original closed_trades_fifo.csv missing next to orders.csv, log NOTE
    artifact_dir = os.path.dirname(os.path.abspath(orders_path))
    orig_closed = os.path.join(artifact_dir, "closed_trades_fifo.csv")
    if not os.path.exists(orig_closed):
        print("NOTE: File closed_trades_fifo.csv was missing, reconstructed from orders.csv (best-effort).")

    fills = read_fills(orders_path, fill_phase)
    rows = fifo_reconstruct(fills)
    write_output(out_path, rows)
    print(f"Reconstruction complete. Output: {out_path}")


if __name__ == "__main__":
    main()
