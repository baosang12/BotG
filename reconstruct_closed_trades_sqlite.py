#!/usr/bin/env python3
"""
Reconstruct closed trades (FIFO) from orders.csv fills.

Usage:

"""

import argparse
import csv
import os
from datetime import datetime, timezone
from collections import defaultdict, deque
from typing import Dict, Deque, List, Tuple, Optional



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

                    symbol = "?"

                side_raw = str(row.get(side_col, "")).strip().upper() if side_col else ""
                if side_raw not in ("BUY", "SELL"):

                    volume = None
                if not volume or volume <= 0:
                    continue


                for alt in ("execPrice", "price", "fill_price", "intendedPrice"):
                    if alt != price_col and alt in row:
                        cand_vals.append(row.get(alt))
                for v in cand_vals:

                if price_val is None:
                    continue

                # parse time
                epoch_ms = None
                if epoch_col and row.get(epoch_col) not in (None, ""):
                    try:

                    except Exception:
                        epoch_ms = None
                if epoch_ms is None and ts_col and row.get(ts_col):
                    epoch_ms = to_epoch_ms(row.get(ts_col))
                if epoch_ms is None:

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

        raise RuntimeError(f"Failed reading orders CSV: {e}")

    # sort by time to ensure FIFO is chronological
    fills.sort(key=lambda f: f.epoch_ms)
    return fills



    """
    Returns list of rows matching header:
    trade_id,open_time,close_time,symbol,side,volume,open_price,close_price,pnl,status
    """
    # Per-symbol long/short queues
    longs: Dict[str, Deque[Fill]] = defaultdict(deque)
    shorts: Dict[str, Deque[Fill]] = defaultdict(deque)


    seq = 1

    for f in fills:
        sym = f.symbol
        if f.side == "BUY":
            # Close existing shorts first

                pnl = (s.price - f.price) * take  # short pnl = open - close
                trade_id = f"fifo_{seq}"
                seq += 1
                closed.append((
                    trade_id,

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

                longs[sym].append(Fill(sym, "BUY", vol_left, f.price, f.epoch_ms, f.iso))

        elif f.side == "SELL":
            # Close existing longs first

                pnl = (f.price - l.price) * take  # long pnl = close - open
                trade_id = f"fifo_{seq}"
                seq += 1
                closed.append((
                    trade_id,

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

                shorts[sym].append(Fill(sym, "SELL", vol_left, f.price, f.epoch_ms, f.iso))
        else:
            # ignore unknown side
            continue

    return closed



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

            trade_id, open_time, close_time, symbol, side, volume, open_p, close_p, pnl, status = r
            w.writerow([
                trade_id,
                open_time,
                close_time,
                symbol,
                side,

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
