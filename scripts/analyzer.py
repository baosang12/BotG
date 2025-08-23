import argparse, csv, json, os
from datetime import datetime

def read_closed(path):
    rows = []
    with open(path, newline='', encoding='utf-8') as f:
        r = csv.DictReader(f)
        for row in r:
            try:
                size = float(row.get('size', '0') or '0')
                open_price = float(row.get('open_price', '0') or '0')
                close_price = float(row.get('close_price', '0') or '0')
                # Prefer net pnl; if gross available, subtract fee to compute net
                net = row.get('pnl_in_account_currency')
                fee = row.get('fee')
                gross = row.get('gross_pnl')
                pnl = 0.0
                try:
                    if net not in (None, ''):
                        pnl = float(net)
                    elif gross not in (None, ''):
                        g = float(gross)
                        f = float(fee or '0')
                        pnl = g - f
                    else:
                        pnl = 0.0
                except Exception:
                    pnl = 0.0
                side = (row.get('side') or '').lower()
                open_time = row.get('open_time_iso') or ''
                close_time = row.get('close_time_iso') or ''
                rows.append({
                    'size': size,
                    'open_price': open_price,
                    'close_price': close_price,
                    'pnl': pnl,
                    'side': side,
                    'open_time_iso': open_time,
                    'close_time_iso': close_time,
                })
            except Exception:
                pass
    return rows

def compute_equity(rows, start_equity=10000.0):
    # Sort by close time
    def parse_iso(s):
        try:
            return datetime.fromisoformat(s.replace('Z','+00:00'))
        except Exception:
            return None
    rows = [r for r in rows if parse_iso(r['close_time_iso'])]
    rows.sort(key=lambda r: parse_iso(r['close_time_iso']))
    equity = start_equity
    series = []
    for r in rows:
        equity += r['pnl']
        series.append({'t': r['close_time_iso'], 'equity': equity})
    return series, equity - start_equity

def max_drawdown(series):
    peak = None
    mdd = 0.0
    for p in series:
        v = p['equity']
        if peak is None or v > peak:
            peak = v
        dd = 0.0 if peak is None else (peak - v)
        if dd > mdd:
            mdd = dd
    return mdd

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--closed-trades', required=True)
    ap.add_argument('--out', required=True)
    args = ap.parse_args()
    rows = read_closed(args.closed_trades)
    series, total_pnl = compute_equity(rows)
    mdd = max_drawdown(series)
    out = {
        'trades': len(rows),
        'total_pnl': total_pnl,
        'equity_series': series[:200],
        'max_drawdown': mdd,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, 'w', encoding='utf-8') as f:
        json.dump(out, f)

if __name__ == '__main__':
    main()
