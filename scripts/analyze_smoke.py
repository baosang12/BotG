#!/usr/bin/env python3
import os, sys, json, csv, math
from datetime import datetime

def find_latest_run(log_path: str) -> str | None:
    art = os.path.join(log_path, 'artifacts')
    if not os.path.isdir(art):
        return None
    runs = [os.path.join(art, d) for d in os.listdir(art) if d.startswith('telemetry_run_')]
    runs = [d for d in runs if os.path.isdir(d)]
    if not runs:
        return None
    runs.sort(key=lambda d: os.path.getmtime(d), reverse=True)
    return runs[0]

def try_float(s):
    try:
        return float(s)
    except Exception:
        return None

def parse_csv(path):
    with open(path, 'r', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        rows = list(reader)
    return rows, reader.fieldnames

def write_csv(path, rows, fieldnames):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=fieldnames)
        w.writeheader()
        for r in rows:
            w.writerow(r)

def percentile(values, q):
    if not values:
        return None
    xs = sorted(values)
    idx = int(round((len(xs)-1) * q))
    return xs[idx]

def main():
    botg_root = os.environ.get('BOTG_ROOT') or os.getcwd()
    log_path = os.environ.get('BOTG_LOG_PATH') or os.path.join(os.path.expanduser('~'), 'AppData', 'Local', 'BotG', 'logs')
    out_dir = os.path.join(botg_root, 'path_issues')
    os.makedirs(out_dir, exist_ok=True)
    steps_log = os.path.join(out_dir, f'agent_steps_{datetime.utcnow().strftime("%Y%m%d_%H%M%S")}.log')
    def log(msg):
        print(msg)
        try:
            with open(steps_log, 'a', encoding='utf-8') as f:
                f.write(msg + '\n')
        except Exception:
            pass

    run_dir = find_latest_run(log_path)
    if not run_dir:
        log('[ERR] No telemetry_run_* found under ' + log_path)
        return 2
    log('[INFO] Using run_dir: ' + run_dir)

    # Load orders
    orders_csv = os.path.join(run_dir, 'orders.csv')
    if not os.path.isfile(orders_csv):
        log('[ERR] orders.csv missing in run_dir')
        return 3
    orders, order_fields = parse_csv(orders_csv)

    # Investigate DRAIN-SELL missing latency
    drain_rows = [r for r in orders if 'DRAIN-SELL' in (r.get('orderId','') or r.get('order_id','')) and (r.get('status')=='FILL' or r.get('phase')=='FILL')]
    missing_latency = [r for r in drain_rows if not (r.get('latency_ms') or '').strip()]
    inv = {
        'drain_sell_total_fills': len(drain_rows),
        'drain_sell_missing_latency': len(missing_latency)
    }
    with open(os.path.join(out_dir, 'investigate_drain.json'),'w',encoding='utf-8') as f:
        json.dump(inv, f, indent=2)
    log(f"[INFO] DRAIN-SELL fills={inv['drain_sell_total_fills']} missing_latency={inv['drain_sell_missing_latency']}")

    # Reconcile: base on closed_trades if present, else create minimal by pairing fills (fallback: copy file)
    closed_csv = os.path.join(run_dir, 'closed_trades_fifo.csv')
    recon_out = os.path.join(out_dir, 'closed_trades_fifo_reconstructed.csv')
    report = {'source': 'closed_trades_fifo.csv' if os.path.isfile(closed_csv) else 'orders.csv', 'unmatched_orders_count': 0, 'unmatched_trade_closes_count': 0}
    if os.path.isfile(closed_csv):
        # augment with latency/slippage joined from orders by entry/exit ids when possible
        closed, closed_fields = parse_csv(closed_csv)
        # Build latency/slip maps by orderId for FILL rows
        fill_rows = [r for r in orders if (r.get('status')=='FILL' or r.get('phase')=='FILL')]
        fill_by_id = {}
        for r in fill_rows:
            oid = r.get('orderId') or r.get('order_id')
            if not oid: 
                continue
            slip = r.get('slippage')
            if slip is None or str(slip)=='' or str(slip).lower()=='nan':
                pr = try_float(r.get('price_filled') or r.get('execPrice'))
                rq = try_float(r.get('price_requested') or r.get('intendedPrice'))
                slip = None if pr is None or rq is None else (pr - rq)
            lat = r.get('latency_ms')
            fill_by_id[oid] = {
                'latency_ms': int(try_float(lat)) if lat not in (None, '') and try_float(lat) is not None else '' ,
                'slippage': try_float(slip) if slip not in (None, '') else '' ,
                'timestamp_fill': r.get('timestamp_iso') or ''
            }
        # augment
        out_fields = list(closed_fields) + [fn for fn in ['entry_latency_ms','exit_latency_ms','entry_slippage','exit_slippage'] if fn not in closed_fields]
        aug = []
        for tr in closed:
            e = tr.get('entry_order_id'); x = tr.get('exit_order_id')
            enr = fill_by_id.get(e, {})
            exr = fill_by_id.get(x, {})
            tr = dict(tr)
            tr['entry_latency_ms'] = enr.get('latency_ms','')
            tr['exit_latency_ms'] = exr.get('latency_ms','')
            tr['entry_slippage'] = enr.get('slippage','')
            tr['exit_slippage'] = exr.get('slippage','')
            aug.append(tr)
        write_csv(recon_out, aug, out_fields)
        # Simple consistency vs trade_closes.log
        tclose = os.path.join(run_dir, 'trade_closes.log')
        seen_ids = set()
        if os.path.isfile(tclose):
            with open(tclose,'r',encoding='utf-8') as f:
                for line in f:
                    parts = line.strip().split()
                    if len(parts) >= 3 and parts[1]=='CLOSED':
                        seen_ids.add(parts[2])
        reported = set([r.get('trade_id') for r in aug if r.get('trade_id')])
        report['unmatched_trade_closes_count'] = max(0, len(seen_ids - reported))
    else:
        # minimal fallback: write fills only with computed slippage; not ideal but ensures downstream files exist
        fills = [r for r in orders if (r.get('status')=='FILL' or r.get('phase')=='FILL')]
        out_fields = ['orderId','side','price_requested','price_filled','size_filled','timestamp_request','timestamp_fill','latency_ms','slippage']
        out_rows = []
        for r in fills:
            oid = r.get('orderId') or r.get('order_id')
            pr = r.get('price_requested') or r.get('intendedPrice')
            pf = r.get('price_filled') or r.get('execPrice')
            sz = r.get('size_filled') or r.get('filledSize')
            lat = r.get('latency_ms')
            slip = r.get('slippage')
            if not slip:
                pfn = try_float(pf); prn = try_float(pr)
                slip = '' if (pfn is None or prn is None) else (pfn - prn)
            out_rows.append({
                'orderId': oid,
                'side': r.get('side') or r.get('action'),
                'price_requested': pr,
                'price_filled': pf,
                'size_filled': sz,
                'timestamp_request': r.get('timestamp_request') or '',
                'timestamp_fill': r.get('timestamp_iso') or '',
                'latency_ms': lat or '',
                'slippage': slip
            })
        write_csv(recon_out, out_rows, out_fields)

    # Percentiles and per-hour summary
    # Extract slippage and latency from orders fills
    fills = [r for r in orders if (r.get('status')=='FILL' or r.get('phase')=='FILL')]
    slip_vals = []
    lat_vals = []
    by_hour = {}
    for r in fills:
        slip = r.get('slippage')
        if not slip:
            pr = try_float(r.get('price_requested') or r.get('intendedPrice'))
            pf = try_float(r.get('price_filled') or r.get('execPrice'))
            if pr is not None and pf is not None:
                slip = pf - pr
        s = try_float(slip)
        if s is not None:
            slip_vals.append(abs(s))
        lt = try_float(r.get('latency_ms'))
        if lt is not None:
            lat_vals.append(lt)
        ts_iso = r.get('timestamp_iso') or ''
        hr = ts_iso[:13] if len(ts_iso)>=13 else ''
        if hr:
            d = by_hour.setdefault(hr, {'requests':0,'fills':0,'lat_samples':[],'slip_samples':[]})
            d['fills'] += 1

    # count requests per hour
    reqs = [r for r in orders if (r.get('status')=='REQUEST' or r.get('phase')=='REQUEST')]
    for r in reqs:
        ts_iso = r.get('timestamp_iso') or ''
        hr = ts_iso[:13] if len(ts_iso)>=13 else ''
        if hr:
            d = by_hour.setdefault(hr, {'requests':0,'fills':0,'lat_samples':[],'slip_samples':[]})
            d['requests'] += 1

    # recompute per-hour medians
    for r in fills:
        ts_iso = r.get('timestamp_iso') or ''
        hr = ts_iso[:13] if len(ts_iso)>=13 else ''
        if hr and hr in by_hour:
            lt = try_float(r.get('latency_ms'))
            if lt is not None:
                by_hour[hr]['lat_samples'].append(lt)
            slip = r.get('slippage')
            if not slip:
                pr = try_float(r.get('price_requested') or r.get('intendedPrice'))
                pf = try_float(r.get('price_filled') or r.get('execPrice'))
                if pr is not None and pf is not None:
                    slip = pf - pr
            s = try_float(slip)
            if s is not None:
                by_hour[hr]['slip_samples'].append(s)

    def median(a):
        if not a: return None
        b = sorted(a)
        n = len(b)
        m = n//2
        if n%2==1:
            return b[m]
        return 0.5*(b[m-1]+b[m])

    # write percentiles JSON
    pct = {
        'slip_abs': { 'p50': percentile(slip_vals,0.5), 'p75': percentile(slip_vals,0.75), 'p90': percentile(slip_vals,0.9), 'p95': percentile(slip_vals,0.95), 'p99': percentile(slip_vals,0.99) },
        'latency_ms': { 'p50': percentile(lat_vals,0.5), 'p75': percentile(lat_vals,0.75), 'p90': percentile(lat_vals,0.9), 'p95': percentile(lat_vals,0.95), 'p99': percentile(lat_vals,0.99) }
    }
    with open(os.path.join(out_dir, 'slip_latency_percentiles.json'),'w',encoding='utf-8') as f:
        json.dump(pct, f, indent=2)

    # per-hour CSV
    hourly_path = os.path.join(out_dir, 'fillrate_by_hour.csv')
    with open(hourly_path,'w',newline='',encoding='utf-8') as f:
        w = csv.writer(f)
        w.writerow(['hour','requests','fills','fill_rate','median_latency_ms','median_slippage'])
        for hr in sorted(by_hour.keys()):
            d = by_hour[hr]
            req = d['requests']; fl = d['fills']
            fr = (float(fl)/req) if req>0 else 0.0
            w.writerow([hr, req, fl, f"{fr:.4f}", median(d['lat_samples']) if d['lat_samples'] else '', median(d['slip_samples']) if d['slip_samples'] else ''])

    # Top outliers (abs slippage)
    out_rows = sorted(fills, key=lambda r: abs(try_float(r.get('slippage') or 0) or 0), reverse=True)[:20]
    out_path = os.path.join(out_dir, 'top20_slippage.csv')
    if out_rows:
        fields = list(out_rows[0].keys())
        write_csv(out_path, out_rows, fields)

    # Save reconstruct report
    with open(os.path.join(out_dir,'reconstruct_report.json'),'w',encoding='utf-8') as f:
        json.dump(report, f, indent=2)

    # Try plots if matplotlib available
    try:
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        # Slippage histogram
        if slip_vals:
            plt.figure(figsize=(6,4))
            plt.hist(slip_vals, bins=100)
            plt.title('Slippage distribution (abs)')
            plt.xlabel('abs(slippage)')
            plt.ylabel('count')
            plt.tight_layout()
            plt.savefig(os.path.join(out_dir,'slippage_hist.png'))
            plt.close()
        # Latency percentiles plot (CDF-ish)
        if lat_vals:
            xs = sorted(lat_vals)
            ys = [i/(len(xs)-1) if len(xs)>1 else 1.0 for i,_ in enumerate(xs)]
            plt.figure(figsize=(6,4))
            plt.plot(xs, ys)
            plt.title('Latency empirical CDF')
            plt.xlabel('latency_ms')
            plt.ylabel('cdf')
            plt.tight_layout()
            plt.savefig(os.path.join(out_dir,'latency_percentiles.png'))
            plt.close()
    except Exception as e:
        log('[WARN] Plotting skipped: ' + str(e))

    log('[OK] Analysis complete.')
    return 0

if __name__ == '__main__':
    sys.exit(main())
#!/usr/bin/env python3
"""
Analyze a smoke artifact folder:
 - Reads closed_trades_fifo.csv and orders.csv
 - Produces: equity series CSV, per-hour summary CSV, summary_stats.json
 - Optional: equity_curve.png if matplotlib is installed (not required)

Usage:
  python scripts/analyze_smoke.py --base <path to smoke_*>  # explicit
  python scripts/analyze_smoke.py                           # auto-detect latest under ./artifacts

This script uses only Python stdlib by default; if matplotlib is installed, it will render an equity curve PNG.
"""

import argparse
import csv
import json
import os
import sys
from datetime import datetime, timezone
from typing import Optional, List, Dict, Tuple


def iso_parse(dt: str) -> Optional[datetime]:
    if not dt:
        return None
    try:
        # fromisoformat supports "+HH:MM" offsets
        return datetime.fromisoformat(dt)
    except Exception:
        # Try stripping Z
        if dt.endswith("Z"):
            try:
                return datetime.fromisoformat(dt[:-1]).replace(tzinfo=timezone.utc)
            except Exception:
                return None
        return None


def floor_to_hour(ts: datetime) -> datetime:
    return ts.replace(minute=0, second=0, microsecond=0)


def autodetect_latest_smoke(base_ws: str) -> Optional[str]:
    arts = os.path.join(base_ws, "artifacts")
    if not os.path.isdir(arts):
        return None
    # Only consider directories for telemetry_run_*
    trun_dirs = [os.path.join(arts, d) for d in os.listdir(arts)
                 if d.startswith("telemetry_run_") and os.path.isdir(os.path.join(arts, d))]
    # Fallback: directly look for smoke_* dirs under artifacts
    smoke_dirs: List[str] = []
    for tr in sorted(trun_dirs):
        try:
            subs = [os.path.join(tr, d) for d in os.listdir(tr)
                    if d.startswith("smoke_") and os.path.isdir(os.path.join(tr, d))]
            smoke_dirs.extend(subs)
        except Exception:
            continue
    if not smoke_dirs:
        # As a final fallback, search one level under artifacts for smoke_* dirs (if any)
        smoke_dirs = [os.path.join(arts, d) for d in os.listdir(arts)
                      if d.startswith("smoke_") and os.path.isdir(os.path.join(arts, d))]
    if not smoke_dirs:
        return None
    smoke_dirs.sort()
    return smoke_dirs[-1]


def read_closed_trades(path: str) -> List[Dict[str, str]]:
    rows: List[Dict[str, str]] = []
    with open(path, newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for r in reader:
            rows.append(r)
    return rows


def read_orders(path: str) -> List[Dict[str, str]]:
    try:
        with open(path, newline='', encoding='utf-8') as f:
            return list(csv.DictReader(f))
    except FileNotFoundError:
        return []


def compute_equity(trades: List[Dict[str, str]]) -> Tuple[List[Dict[str, str]], Dict[str, float]]:
    # Expect 'net_realized_usd' and a time column (prefer exit_time)
    series: List[Dict[str, str]] = []
    # Prepare tuples (time, net)
    recs: List[Tuple[datetime, float, Dict[str, str]]] = []
    for r in trades:
        net = 0.0
        try:
            net = float(r.get('net_realized_usd') or r.get('realized_usd') or 0.0)
        except Exception:
            net = 0.0
        t = iso_parse(r.get('exit_time') or r.get('entry_time') or '')
        if t is None:
            # Put unknown time at the end in stable order
            t = datetime.max.replace(tzinfo=None)
        recs.append((t, net, r))
    recs.sort(key=lambda x: x[0])

    equity = 0.0
    eq_max = 0.0
    max_dd = 0.0
    max_dd_time: Optional[datetime] = None

    for t, net, r in recs:
        equity += net
        eq_max = max(eq_max, equity)
        dd = equity - eq_max
        if dd < max_dd:
            max_dd = dd
            max_dd_time = t
        series.append({
            'time': (t.isoformat() if t != datetime.max.replace(tzinfo=None) else ''),
            'net_realized_usd': f"{net:.10f}",
            'equity': f"{equity:.10f}",
        })

    stats = {
        'total_trades': len(trades),
        'net_realized_usd': float(f"{equity:.10f}"),
        'max_drawdown_usd': float(f"{max_dd:.10f}"),
        'max_drawdown_time': (max_dd_time.isoformat() if max_dd_time else None),
    }
    return series, stats


def per_hour_summary(trades: List[Dict[str, str]]) -> List[Dict[str, str]]:
    buckets: Dict[str, Dict[str, float]] = {}
    for r in trades:
        t = iso_parse(r.get('exit_time') or r.get('entry_time') or '')
        if not t:
            continue
        h = floor_to_hour(t)
        key = h.isoformat()
        try:
            net = float(r.get('net_realized_usd') or r.get('realized_usd') or 0.0)
        except Exception:
            net = 0.0
        b = buckets.setdefault(key, {'trades': 0.0, 'net_usd': 0.0})
        b['trades'] += 1
        b['net_usd'] += net
    out: List[Dict[str, str]] = []
    for k in sorted(buckets.keys()):
        v = buckets[k]
        avg = (v['net_usd'] / v['trades']) if v['trades'] else 0.0
        out.append({
            'hour': k,
            'trades': str(int(v['trades'])),
            'net_usd': f"{v['net_usd']:.10f}",
            'mean_per_trade': f"{avg:.10f}",
        })
    return out


def fill_rate_from_orders(orders: List[Dict[str, str]]) -> Dict[str, float]:
    if not orders:
        return {}
    # Expect a 'phase' column with REQUEST/ACK/FILL
    counts: Dict[str, int] = {}
    for r in orders:
        phase = (r.get('phase') or '').strip().upper()
        counts[phase] = counts.get(phase, 0) + 1
    req = counts.get('REQUEST', 0)
    fill = counts.get('FILL', 0)
    rate = (fill / req) if req else 0.0
    return {'REQUEST': req, 'FILL': fill, 'fill_rate': rate}


def fill_breakdown(orders: List[Dict[str, str]], group_key: str) -> List[Dict[str, str]]:
    """Compute fill-rate breakdown by a specific orders.csv column (if present)."""
    if not orders:
        return []
    if not any(group_key in r for r in orders):
        return []
    # Bucket by group_key, count REQUEST and FILL
    agg: Dict[str, Dict[str, int]] = {}
    for r in orders:
        key = (r.get(group_key) or '').strip()
        phase = (r.get('phase') or '').strip().upper()
        g = agg.setdefault(key, {'REQUEST': 0, 'FILL': 0})
        if phase in ('REQUEST', 'FILL'):
            g[phase] += 1
    out: List[Dict[str, str]] = []
    for k in sorted(agg.keys()):
        v = agg[k]
        req = v.get('REQUEST', 0)
        fil = v.get('FILL', 0)
        rate = (fil / req) if req else 0.0
        out.append({'group': k, 'REQUEST': str(req), 'FILL': str(fil), 'fill_rate': f"{rate:.6f}"})
    return out


def maybe_plot_equity_png(base: str, series: List[Dict[str, str]]) -> Optional[str]:
    try:
        import matplotlib
        matplotlib.use('Agg')  # no display
        import matplotlib.pyplot as plt
    except Exception:
        return None
    times = []
    values = []
    for r in series:
        t = r.get('time')
        if not t:
            continue
        dt = iso_parse(t)
        if not dt:
            continue
        times.append(dt)
        try:
            values.append(float(r.get('equity', '0')))
        except Exception:
            values.append(0.0)
    if not times:
        return None
    try:
        import matplotlib.pyplot as plt  # type: ignore
        fig = plt.figure(figsize=(10, 4))
        ax = fig.add_subplot(111)
        ax.plot(times, values)
        ax.set_title('Equity curve (net_realized_usd cumulative)')
        ax.set_xlabel('Time')
        ax.set_ylabel('USD')
        ax.grid(True, alpha=0.3)
        fig.tight_layout()
        out_png = os.path.join(base, 'analysis_equity_curve.png')
        fig.savefig(out_png)
        plt.close(fig)
        return out_png
    except Exception:
        return None


def write_csv(path: str, rows: List[Dict[str, str]]):
    if not rows:
        # Create empty file with no rows
        with open(path, 'w', encoding='utf-8') as f:
            f.write('')
        return
    fieldnames = list(rows[0].keys())
    with open(path, 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=fieldnames)
        w.writeheader()
        for r in rows:
            w.writerow(r)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', '-b', default=None, help='Path to smoke_* folder (contains closed_trades_fifo.csv). If omitted, auto-detect latest under ./artifacts')
    args = ap.parse_args()

    if args.base:
        base = args.base
    else:
        base = autodetect_latest_smoke(os.getcwd())
        if not base:
            print('ERROR: Could not auto-detect latest smoke folder under ./artifacts', file=sys.stderr)
            sys.exit(2)

    closed = os.path.join(base, 'closed_trades_fifo.csv')
    orders_path = os.path.join(base, 'orders.csv')
    if not os.path.isfile(closed):
        print('ERROR: closed_trades_fifo.csv not found at', closed, file=sys.stderr)
        sys.exit(1)

    trades = read_closed_trades(closed)
    series, eq_stats = compute_equity(trades)
    per_hour = per_hour_summary(trades)
    orders = read_orders(orders_path)
    fill_stats = fill_rate_from_orders(orders)
    fill_by_type = fill_breakdown(orders, 'order_type')
    fill_by_reason = fill_breakdown(orders, 'reason')
    # per-hour fill breakdown from orders timestamps if present
    fill_by_hour: List[Dict[str, str]] = []
    try:
        # group by hour using 'time' column if available; fallback to request_time/intended_time
        buckets: Dict[str, Dict[str, int]] = {}
        for r in orders:
            ts = r.get('time') or r.get('request_time') or r.get('intended_time') or ''
            t = iso_parse(ts)
            if not t:
                continue
            hkey = floor_to_hour(t).isoformat()
            phase = (r.get('phase') or '').strip().upper()
            b = buckets.setdefault(hkey, {'REQUEST': 0, 'FILL': 0})
            if phase in ('REQUEST', 'FILL'):
                b[phase] += 1
        for k in sorted(buckets.keys()):
            v = buckets[k]
            req = v.get('REQUEST', 0)
            fil = v.get('FILL', 0)
            rate = (fil / req) if req else 0.0
            fill_by_hour.append({'hour': k, 'REQUEST': str(req), 'FILL': str(fil), 'fill_rate': f"{rate:.6f}"})
    except Exception:
        fill_by_hour = []

    # Write outputs
    out_series = os.path.join(base, 'analysis_equity_series.csv')
    out_hour = os.path.join(base, 'analysis_per_hour.csv')
    out_summary = os.path.join(base, 'analysis_summary_stats.json')
    out_fill = os.path.join(base, 'analysis_fill_breakdown.json')
    write_csv(out_series, series)
    write_csv(out_hour, per_hour)
    summary = {
        'base': base,
        'equity': eq_stats,
        'fill_stats': fill_stats,
    }
    with open(out_summary, 'w', encoding='utf-8') as f:
        json.dump(summary, f, indent=2)

    with open(out_fill, 'w', encoding='utf-8') as f:
        json.dump({
            'overall': fill_stats,
            'by_order_type': fill_by_type,
            'by_reason': fill_by_reason,
            'by_hour': fill_by_hour,
        }, f, indent=2)

    png = maybe_plot_equity_png(base, series)
    if png:
        summary['equity_png'] = png
        with open(out_summary, 'w', encoding='utf-8') as f:
            json.dump(summary, f, indent=2)

    # Compact console output
    print(json.dumps({
        'equity_final_usd': summary['equity']['net_realized_usd'],
        'max_drawdown_usd': summary['equity']['max_drawdown_usd'],
        'trades': summary['equity']['total_trades'],
        'fill_rate': (summary.get('fill_stats') or {}).get('fill_rate', None),
        'base': base,
        'equity_png': png or None,
    }))


if __name__ == '__main__':
    main()
#!/usr/bin/env python3
"""
Analyze a smoke run folder:
 - Inputs: closed_trades_fifo.csv, orders.csv in the given base folder
 - Outputs:
    * analysis_smoke_equity_series.csv
    * analysis_smoke_per_hour.csv
    * analysis_smoke_summary_stats.json
    * analysis_smoke_orders_fillrate.json
    * analysis_smoke_orders_fillrate_per_hour.csv
    * Optionally analysis_smoke_equity_curve.png if matplotlib available

Usage:
  python scripts/analyze_smoke.py --base <path-to-smoke-folder>
  # If omitted, defaults to current working directory

This script uses only Python standard library by default. If matplotlib is
installed, it will also save a PNG equity curve chart.
"""
import argparse
import csv
import datetime as dt
import json
import math
import os
import sys
from typing import Dict, List, Optional, Tuple


def parse_iso(ts: str) -> Optional[dt.datetime]:
    if not ts:
        return None
    try:
        ts = ts.strip()
        return dt.datetime.fromisoformat(ts)
    except Exception:
        try:
            if ts.endswith('Z'):
                return dt.datetime.fromisoformat(ts[:-1])
        except Exception:
            pass
    return None


def read_closed_trades(path: str) -> List[Dict[str, str]]:
    rows: List[Dict[str, str]] = []
    with open(path, 'r', newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for r in reader:
            rows.append(r)
    return rows


def read_orders(path: str) -> List[Dict[str, str]]:
    rows: List[Dict[str, str]] = []
    with open(path, 'r', newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for r in reader:
            rows.append(r)
    return rows


def to_float(v: Optional[str]) -> float:
    if v is None:
        return 0.0
    try:
        return float(v)
    except Exception:
        try:
            return float(str(v).replace(',', '.'))
        except Exception:
            return 0.0


def equity_and_stats(closed_rows: List[Dict[str, str]], base: str) -> Tuple[Dict[str, float], List[Dict[str, str]], List[Dict[str, str]]]:
    time_col = 'exit_time' if closed_rows and 'exit_time' in closed_rows[0] else 'entry_time'
    net_col = 'net_realized_usd'
    if closed_rows and net_col not in closed_rows[0]:
        for c in ('net_realized', 'realized_usd', 'realized'):
            if c in closed_rows[0]:
                net_col = c
                break

    def parse_time(r):
        return parse_iso(r.get(time_col, '')) or dt.datetime.min

    closed_rows_sorted = sorted(closed_rows, key=parse_time)

    equity_series: List[Dict[str, str]] = []
    per_hour: Dict[dt.datetime, Dict[str, float]] = {}
    equity = 0.0
    eq_max = -math.inf
    max_dd = 0.0
    max_dd_time: Optional[dt.datetime] = None

    wins = 0
    losses = 0
    for r in closed_rows_sorted:
        t = parse_time(r)
        net = to_float(r.get(net_col))
        equity += net
        if net > 0:
            wins += 1
        else:
            losses += 1
        eq_max = max(eq_max, equity)
        dd = equity - eq_max
        if dd < max_dd:
            max_dd = dd
            max_dd_time = t
        equity_series.append({
            'entry_time': r.get('entry_time', ''),
            'exit_time': r.get('exit_time', ''),
            'net_realized_usd': f"{net:.10f}",
            'equity': f"{equity:.10f}",
        })
        if t:
            hour = t.replace(minute=0, second=0, microsecond=0)
            agg = per_hour.setdefault(hour, {'trades': 0, 'net_usd': 0.0})
            agg['trades'] += 1
            agg['net_usd'] += net

    per_hour_rows = []
    for hour in sorted(per_hour.keys()):
        agg = per_hour[hour]
        mean = (agg['net_usd'] / agg['trades']) if agg['trades'] else 0.0
        per_hour_rows.append({
            'hour': hour.isoformat(),
            'trades': str(agg['trades']),
            'net_usd': f"{agg['net_usd']:.10f}",
            'mean': f"{mean:.10f}",
        })

    total_trades = len(closed_rows_sorted)
    stats: Dict[str, object] = {
        'total_trades': total_trades,
        'net_realized_usd': equity,
        'avg_per_trade': (equity / total_trades) if total_trades else 0.0,
        'wins': wins,
        'losses': losses,
        'win_rate': (wins / total_trades) if total_trades else 0.0,
        'max_drawdown_usd': max_dd,
        'max_drawdown_time': max_dd_time.isoformat() if max_dd_time else None,
    }

    try:
        import matplotlib.pyplot as plt  # type: ignore
        times = [parse_iso(r.get(time_col, '')) or dt.datetime.min for r in closed_rows_sorted]
        eq_vals = [float(e['equity']) for e in equity_series]
        if times and eq_vals:
            plt.figure(figsize=(10, 4))
            plt.plot(times, eq_vals)
            plt.title('Equity curve (cumulative net_realized_usd)')
            plt.xlabel('Time')
            plt.ylabel('Equity (USD)')
            plt.grid(True)
            out_png = os.path.join(base, 'analysis_smoke_equity_curve.png')
            plt.tight_layout()
            plt.savefig(out_png)
            plt.close()
            stats['equity_curve_png'] = out_png
    except Exception:
        stats['equity_curve_png'] = None

    return stats, equity_series, per_hour_rows


def write_csv(path: str, rows: List[Dict[str, str]]) -> None:
    if not rows:
        with open(path, 'w', newline='', encoding='utf-8') as f:
            f.write('# no data\n')
        return
    fieldnames = list(rows[0].keys())
    with open(path, 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=fieldnames)
        w.writeheader()
        for r in rows:
            w.writerow(r)


def orders_fill_rate(orders_rows: List[Dict[str, str]]):
    ts_col = 'timestamp'
    if orders_rows and 'timestamp_iso' in orders_rows[0]:
        ts_col = 'timestamp_iso'

    by_order: Dict[str, Dict[str, object]] = {}
    for r in orders_rows:
        oid = r.get('order_id') or r.get('orderId') or r.get('id')
        if not oid:
            continue
        phase = (r.get('phase') or '').upper()
        t = parse_iso(r.get(ts_col, ''))
        entry = by_order.setdefault(oid, {'request_time': None, 'filled': False, 'phase_seen': set()})
        if phase == 'REQUEST' and not entry['request_time']:
            entry['request_time'] = t
        if phase == 'FILL':
            entry['filled'] = True
        phases = entry['phase_seen']  # type: ignore
        if isinstance(phases, set):
            phases.add(phase)

    req_count = sum(1 for v in by_order.values() if v['request_time'])
    fill_count = sum(1 for v in by_order.values() if v['filled'])
    fill_rate = (fill_count / req_count) if req_count else 0.0

    per_hour: Dict[dt.datetime, Dict[str, int]] = {}
    for v in by_order.values():
        rt = v['request_time']
        if not isinstance(rt, dt.datetime):
            continue
        hour = rt.replace(minute=0, second=0, microsecond=0)
        agg = per_hour.setdefault(hour, {'requests': 0, 'fills': 0})
        agg['requests'] += 1
        if v['filled']:
            agg['fills'] += 1

    per_hour_rows: List[Dict[str, str]] = []
    for hour in sorted(per_hour.keys()):
        agg = per_hour[hour]
        rate = (agg['fills'] / agg['requests']) if agg['requests'] else 0.0
        per_hour_rows.append({
            'hour': hour.isoformat(),
            'requests': str(agg['requests']),
            'fills': str(agg['fills']),
            'fill_rate': f"{rate:.6f}",
        })

    summary = {
        'orders_seen': len(by_order),
        'requests': req_count,
        'fills': fill_count,
        'overall_fill_rate': fill_rate,
    }
    return summary, per_hour_rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', '-b', default='.', help='Path to smoke_* folder containing closed_trades_fifo.csv and orders.csv')
    args = ap.parse_args()
    base = args.base

    closed_csv = os.path.join(base, 'closed_trades_fifo.csv')
    orders_csv = os.path.join(base, 'orders.csv')
    if not os.path.exists(closed_csv):
        print(f"closed_trades_fifo.csv not found at {closed_csv}")
        return 2
    if not os.path.exists(orders_csv):
        print(f"orders.csv not found at {orders_csv}")

    closed_rows = read_closed_trades(closed_csv)
    stats, equity_series, per_hour_rows = equity_and_stats(closed_rows, base)

    out_equity_series = os.path.join(base, 'analysis_smoke_equity_series.csv')
    out_per_hour = os.path.join(base, 'analysis_smoke_per_hour.csv')
    out_summary_json = os.path.join(base, 'analysis_smoke_summary_stats.json')
    write_csv(out_equity_series, equity_series)
    write_csv(out_per_hour, per_hour_rows)
    with open(out_summary_json, 'w', encoding='utf-8') as f:
        json.dump(stats, f, indent=2)

    print('[analyze] Equity/Drawdown summary:')
    print(json.dumps(stats, indent=2))

    if os.path.exists(orders_csv):
        orders_rows = read_orders(orders_csv)
        fill_summary, fill_per_hour_rows = orders_fill_rate(orders_rows)
        out_fill_json = os.path.join(base, 'analysis_smoke_orders_fillrate.json')
        out_fill_per_hour = os.path.join(base, 'analysis_smoke_orders_fillrate_per_hour.csv')
        with open(out_fill_json, 'w', encoding='utf-8') as f:
            json.dump(fill_summary, f, indent=2)
        write_csv(out_fill_per_hour, fill_per_hour_rows)
        print('[analyze] Orders fill-rate summary:')
        print(json.dumps(fill_summary, indent=2))
    else:
        print('[analyze] orders.csv not found; skipping fill-rate breakdown')

    print('\nSaved files:')
    print(' -', out_equity_series)
    print(' -', out_per_hour)
    print(' -', out_summary_json)
    if os.path.exists(orders_csv):
        print(' -', out_fill_json)
        print(' -', out_fill_per_hour)
    return 0


if __name__ == '__main__':
    sys.exit(main())
