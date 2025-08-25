#!/usr/bin/env python3
import argparse
import csv
import json
import re
from collections import deque, defaultdict
from datetime import datetime
from pathlib import Path

# Helper: parse ISO timestamp safely
def parse_dt(s):
    try:
        return datetime.fromisoformat(s.replace('Z', '+00:00'))
    except Exception:
        return None

def to_float(x):
    try:
        if x is None or x == "":
            return None
        return float(x)
    except Exception:
        return None

def infer_side_from_msg(msg: str):
    if not msg:
        return None
    m = re.search(r"Action\s*=\s*(Buy|Sell)", msg, re.IGNORECASE)
    if m:
        v = m.group(1).upper()
        return 'BUY' if v.startswith('B') else 'SELL'
    m2 = re.search(r"\b(BUY|SELL)\b", msg, re.IGNORECASE)
    if m2:
        v = m2.group(1).upper()
        return v
    return None

def read_orders(path: Path):
    with path.open('r', newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        rows = list(reader)
    return rows

def build_order_side_map(rows):
    # Prefer explicit side column, fall back to REQUEST brokerMsg, then any brokerMsg
    side_map = {}
    for r in rows:
        oid = r.get('orderId')
        if not oid:
            continue
        side_col = (r.get('side') or '').upper().strip()
        if side_col in ('BUY','SELL'):
            side_map[oid] = side_col
    if side_map:
        return side_map
    for r in rows:
        if (r.get('phase') or '').upper() == 'REQUEST':
            side = infer_side_from_msg(r.get('brokerMsg', ''))
            if side:
                side_map[r.get('orderId')] = side
    if not side_map:
        for r in rows:
            side = infer_side_from_msg(r.get('brokerMsg', ''))
            if side:
                side_map[r.get('orderId')] = side
    return side_map

def fifo_pnl(rows, point_value_per_unit: float, default_commission: float = 0.0):
    # Extract fills
    fills = []
    for r in rows:
        if (r.get('phase') or '').upper() != 'FILL':
            continue
        filled = to_float(r.get('filledSize'))
        price = to_float(r.get('execPrice'))
        ts = parse_dt(r.get('timestamp_iso'))
        if not ts or price is None or not filled or filled <= 0:
            continue
        fills.append({
            'orderId': r.get('orderId'),
            'timestamp': ts,
            'price': price,
            'size': float(filled),
            'brokerMsg': r.get('brokerMsg',''),
            'side_col': (r.get('side') or '').upper().strip(),
        })
    fills.sort(key=lambda x: x['timestamp'])

    # Map orderId -> side
    side_map = build_order_side_map(rows)

    longs = deque()   # BUY entries
    shorts = deque()  # SELL entries
    closed = []
    unmatched = 0

    for f in fills:
        side = f.get('side_col') if f.get('side_col') in ('BUY','SELL') else side_map.get(f['orderId'])
        if side not in ('BUY','SELL'):
            # skip if unknown side
            unmatched += 1
            continue
        price = f['price']
        qty = f['size']
        ts = f['timestamp']
        oid = f['orderId']
        commission = default_commission

        if side == 'BUY':
            # BUY closes shorts first; remaining opens long
            remaining = qty
            while remaining > 0 and shorts:
                e = shorts[0]
                take = min(e['size'], remaining)
                pnl_price = (e['price'] - price) * take  # short entry - buy to cover
                realized = pnl_price * point_value_per_unit
                closed.append({
                    'side_closed': 'SHORT',
                    'entry_orderid': e['orderId'],
                    'entry_time': e['timestamp'].isoformat(),
                    'entry_price': e['price'],
                    'exit_orderid': oid,
                    'exit_time': ts.isoformat(),
                    'exit_price': price,
                    'size': take,
                    'pnl_price_units': pnl_price,
                    'realized_usd': realized,
                    'commission': commission,
                    'net_realized_usd': realized - commission,
                })
                e['size'] -= take
                remaining -= take
                if e['size'] <= 0:
                    shorts.popleft()
            if remaining > 0:
                longs.append({'orderId': oid, 'timestamp': ts, 'price': price, 'size': remaining})
        else:  # SELL
            remaining = qty
            while remaining > 0 and longs:
                e = longs[0]
                take = min(e['size'], remaining)
                pnl_price = (price - e['price']) * take  # sell - buy
                realized = pnl_price * point_value_per_unit
                closed.append({
                    'side_closed': 'LONG',
                    'entry_orderid': e['orderId'],
                    'entry_time': e['timestamp'].isoformat(),
                    'entry_price': e['price'],
                    'exit_orderid': oid,
                    'exit_time': ts.isoformat(),
                    'exit_price': price,
                    'size': take,
                    'pnl_price_units': pnl_price,
                    'realized_usd': realized,
                    'commission': commission,
                    'net_realized_usd': realized - commission,
                })
                e['size'] -= take
                remaining -= take
                if e['size'] <= 0:
                    longs.popleft()
            if remaining > 0:
                shorts.append({'orderId': oid, 'timestamp': ts, 'price': price, 'size': remaining})

    return closed, unmatched

def main():
    ap = argparse.ArgumentParser(description='Compute FIFO P&L from orders.csv')
    ap.add_argument('--orders', type=str, default=None, help='Path to orders.csv (default: BOTG_LOG_PATH/orders.csv)')
    ap.add_argument('--out', type=str, default=None, help='Output CSV path (default: orders dir/closed_trades_fifo.csv)')
    ap.add_argument('--pvu', type=float, default=1.0, help='Point value per unit in account currency (USD)')
    ap.add_argument('--commission', type=float, default=0.0, help='Commission per fill (USD), applied at exit side')
    ap.add_argument('--closes', type=str, default=None, help='Optional path to trade_closes.log (jsonl) for reconciliation')
    ap.add_argument('--meta', type=str, default=None, help='Optional path to run_metadata.json for metadata and PVU')
    args = ap.parse_args()

    log_dir = Path((Path.cwd() / 'logs'))
    env_log = Path(Path.home() / 'botg' / 'logs')
    orders_path = None
    if args.orders:
        orders_path = Path(args.orders)
    else:
        # Try BOTG_LOG_PATH env
        import os
        env = os.environ.get('BOTG_LOG_PATH')
        if env:
            orders_path = Path(env) / 'orders.csv'
        else:
            # fallback recent local logs
            orders_path = Path('D:/botg/logs/orders.csv') if Path('D:/botg/logs/orders.csv').exists() else (log_dir / 'orders.csv')

    if not orders_path.exists():
        raise SystemExit(f"orders.csv not found at {orders_path}")

    # If meta provided, try to use its PVU
    if args.meta:
        try:
            meta = json.loads(Path(args.meta).read_text(encoding='utf-8'))
            pvu_meta = None
            if isinstance(meta, dict):
                pvu_meta = meta.get('point_value_per_unit')
            if isinstance(pvu_meta, (int, float)) and pvu_meta > 0:
                args.pvu = float(pvu_meta)
        except Exception:
            pass

    rows = read_orders(orders_path)
    closed, unmatched = fifo_pnl(rows, point_value_per_unit=args.pvu, default_commission=args.commission)

    out_path = Path(args.out) if args.out else (orders_path.parent / 'closed_trades_fifo.csv')
    # Write CSV
    if closed:
        cols = list(closed[0].keys())
        with out_path.open('w', newline='', encoding='utf-8') as f:
            w = csv.DictWriter(f, fieldnames=cols)
            w.writeheader()
            for r in closed:
                w.writerow(r)
    else:
        with out_path.open('w', newline='', encoding='utf-8') as f:
            w = csv.writer(f)
            w.writerow(['side_closed','entry_orderid','entry_time','entry_price','exit_orderid','exit_time','exit_price','size','pnl_price_units','realized_usd','commission','net_realized_usd'])

    # Summary JSON
    total = sum(r['net_realized_usd'] for r in closed) if closed else 0.0
    summary = {
        'orders': str(orders_path),
        'closed_trades_csv': str(out_path),
        'closed_trades_count': len(closed),
        'total_net_realized_usd': total,
        'pvu_used': args.pvu,
        'commission_per_exit': args.commission,
        'unmatched_fills_count': unmatched,
    }
    if args.closes and Path(args.closes).exists():
        try:
            rc_lines = Path(args.closes).read_text(encoding='utf-8').splitlines()
            rc = [json.loads(l) for l in rc_lines if l.strip()]
            # New TradeCloseLogger writes { timestamp_iso, payload: {...} }
            def extract_realized(obj):
                if isinstance(obj, dict):
                    if 'payload' in obj and isinstance(obj['payload'], dict):
                        return float(obj['payload'].get('realized_pnl_usd', 0.0) or 0.0)
                    return float(obj.get('realized_pnl_usd', 0.0) or 0.0)
                return 0.0
            summary['trade_closes_count'] = len(rc)
            summary['trade_closes_sum_usd'] = sum(extract_realized(x) for x in rc)
        except Exception:
            pass
    if args.meta and Path(args.meta).exists():
        summary['run_metadata'] = str(Path(args.meta))
    with (out_path.parent / 'pnl_summary.json').open('w', encoding='utf-8') as jf:
        json.dump(summary, jf, indent=2)

    print(json.dumps(summary, indent=2))

if __name__ == '__main__':
    main()
