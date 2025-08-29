
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

                rows.append({
                    'size': size,
                    'open_price': open_price,
                    'close_price': close_price,

                    'side': side,
                    'open_time_iso': open_time,
                    'close_time_iso': close_time,
                })
            except Exception:
                pass
    return rows


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

        'equity_series': series[:200],
        'max_drawdown': mdd,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)

    with open(args.out, 'w', encoding='utf-8') as f:
        json.dump(out, f)

if __name__ == '__main__':
    main()
