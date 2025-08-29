import argparse, csv, json, os, glob
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
                # Always compute instrument-units PnL = (close-open)*size; treat provided net as optional
                pnl_units = (close_price - open_price) * size
                # Optional currency PnL if provided by upstream
                pnl_ccy = None
                try:
                    net = row.get('pnl_in_account_currency')
                    if net not in (None, ''):
                        pnl_ccy = float(net)
                    else:
                        gross = row.get('gross_pnl')
                        fee = row.get('fee')
                        if gross not in (None, ''):
                            g = float(gross)
                            f = float(fee or '0')
                            pnl_ccy = g - f
                except Exception:
                    pnl_ccy = None
                side = (row.get('side') or '').lower()
                open_time = row.get('open_time_iso') or row.get('open_time') or ''
                close_time = row.get('close_time_iso') or row.get('close_time') or ''
                rows.append({
                    'size': size,
                    'open_price': open_price,
                    'close_price': close_price,
                    'pnl_units': pnl_units,
                    'pnl_ccy': pnl_ccy,
                    'side': side,
                    'open_time_iso': open_time,
                    'close_time_iso': close_time,
                })
            except Exception:
                pass
    return rows

def compute_equity(rows, start_equity=0.0, use_currency=False, pvu=None):
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
        pnl = r.get('pnl_ccy')
        if use_currency:
            if pnl is None:
                # derive currency pnl from units via PVU when available
                if pvu is not None:
                    pnl = r.get('pnl_units', 0.0) * pvu
                else:
                    pnl = 0.0
        else:
            pnl = r.get('pnl_units', 0.0)
        equity += (pnl or 0.0)
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

def try_read_json(path):
    try:
        with open(path, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return None

def discover_pvu(run_dir):
    """Attempt to read point value per unit/lot from run metadata or side files."""
    # 1) run_metadata.json common locations/keys
    md = try_read_json(os.path.join(run_dir, 'run_metadata.json'))
    pvl = None
    pvu = None
    if isinstance(md, dict):
        try:
            if 'pointValuePerLot' in md and md['pointValuePerLot']:
                pvl = float(md['pointValuePerLot'])
        except Exception:
            pass
        try:
            extra = md.get('extra') or {}
            if isinstance(extra, dict) and extra.get('pointValuePerLot'):
                pvl = float(extra['pointValuePerLot'])
        except Exception:
            pass
        try:
            cfg = ((md.get('config_snapshot') or {}).get('execution') or {})
            if cfg.get('pointValuePerLot'):
                pvl = float(cfg['pointValuePerLot'])
            if cfg.get('pointValuePerUnit'):
                pvu = float(cfg['pointValuePerUnit'])
        except Exception:
            pass
        try:
            if md.get('pvu_used'):
                pvu = float(md['pvu_used'])
        except Exception:
            pass
    # 2) risk snapshot files (scan shallow)
    if pvu is None and pvl is None:
        try:
            for p in glob.glob(os.path.join(run_dir, '*risk*.*')):
                x = try_read_json(p)
                if not isinstance(x, dict):
                    continue
                for k, v in x.items():
                    try:
                        if 'point' in k.lower() and 'lot' in k.lower() and pvl is None:
                            pvl = float(v)
                        if 'pvu' in k.lower() and pvu is None:
                            pvu = float(v)
                    except Exception:
                        continue
        except Exception:
            pass
    # 3) Convert per-lot to per-unit if lot size available in metadata
    if pvu is None and pvl is not None:
        # Try to read lot size default from metadata snapshot
        lot_size = None
        try:
            rs = (((md or {}).get('config_snapshot') or {}).get('risk') or {})
            if isinstance(rs, dict):
                ls = rs.get('LotSizeDefault') or rs.get('lot_size_default')
                if ls:
                    lot_size = float(ls)
        except Exception:
            pass
        if lot_size and lot_size > 0:
            pvu = pvl / lot_size
    return pvu, pvl

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--closed-trades', required=True)
    ap.add_argument('--out', required=True)
    args = ap.parse_args()
    rows = read_closed(args.closed_trades)
    run_dir = os.path.dirname(args.closed_trades)
    pvu, pvl = discover_pvu(run_dir)
    # Prefer currency if PVU known or currency PnL exists across rows
    any_ccy = any(r.get('pnl_ccy') is not None for r in rows)
    use_currency = any_ccy or (pvu is not None)
    series, total = compute_equity(rows, start_equity=0.0, use_currency=use_currency, pvu=pvu)
    # Totals in both spaces
    total_units = sum(r.get('pnl_units', 0.0) for r in rows)
    total_ccy = None
    if use_currency:
        if any_ccy:
            total_ccy = sum((r.get('pnl_ccy') or 0.0) for r in rows)
        elif pvu is not None:
            total_ccy = total_units * pvu
    mdd = max_drawdown(series)
    out = {
        'trades': len(rows),
        'total_pnl': total_ccy if (total_ccy is not None) else total_units,  # backward-compatible
        'total_pnl_instrument_units': total_units,
        'total_pnl_account_currency': total_ccy,
        'pvu_used': pvu,
        'pvl_observed': pvl,
        'space': 'currency' if (total_ccy is not None) else 'instrument_units',
        'equity_series': series[:200],
        'max_drawdown': mdd,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    # Write diagnostics preview (first 100 rows)
    try:
        diag_path = os.path.join(run_dir, 'diagnostic_pnl_preview.csv')
        with open(diag_path, 'w', newline='', encoding='utf-8') as df:
            cols = ['close_time_iso','open_time_iso','side','size','open_price','close_price','pnl_units','pnl_currency']
            w = csv.DictWriter(df, fieldnames=cols)
            w.writeheader()
            for r in rows[:100]:
                pnl_cur = r.get('pnl_ccy')
                if pnl_cur is None and pvu is not None:
                    pnl_cur = r.get('pnl_units', 0.0) * pvu
                w.writerow({
                    'close_time_iso': r.get('close_time_iso',''),
                    'open_time_iso': r.get('open_time_iso',''),
                    'side': r.get('side',''),
                    'size': f"{r.get('size',0.0):.10f}",
                    'open_price': f"{r.get('open_price',0.0):.10f}",
                    'close_price': f"{r.get('close_price',0.0):.10f}",
                    'pnl_units': f"{r.get('pnl_units',0.0):.10f}",
                    'pnl_currency': (f"{pnl_cur:.10f}" if pnl_cur is not None else ''),
                })
    except Exception:
        pass
    with open(args.out, 'w', encoding='utf-8') as f:
        json.dump(out, f)

if __name__ == '__main__':
    main()
