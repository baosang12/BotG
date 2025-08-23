import argparse, os, json, csv
import math
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt


def load_analysis(art: Path):
    js = art / 'analysis_summary.json'
    if js.exists():
        try:
            return json.loads(js.read_text(encoding='utf-8'))
        except Exception:
            return None
    return None


def load_closed(art: Path):
    rows = []
    p = art / 'closed_trades_fifo.csv'
    if not p.exists():
        return rows
    with p.open(newline='', encoding='utf-8') as f:
        r = csv.DictReader(f)
        for row in r:
            try:
                close_t = row.get('close_time_iso') or ''
                pnl = float(row.get('pnl_in_account_currency') or '0')
                rows.append({'t': close_t, 'pnl': pnl})
            except Exception:
                pass
    return rows


def plot_equity(series, out_path: Path):
    if not series:
        return
    xs = [i for i,_ in enumerate(series)]
    ys = [p.get('equity', 0.0) for p in series]
    plt.figure(figsize=(10,4))
    plt.plot(xs, ys, lw=1.0)
    plt.title('Equity Curve')
    plt.xlabel('Trade # (ordered by close time)')
    plt.ylabel('Equity')
    plt.grid(True, alpha=0.2)
    plt.tight_layout()
    plt.savefig(out_path, dpi=150)
    plt.close()


def plot_hist(data, title, xlabel, out_path: Path, bins=50):
    if not data:
        return
    plt.figure(figsize=(6,4))
    plt.hist(data, bins=bins, alpha=0.8)
    plt.title(title)
    plt.xlabel(xlabel)
    plt.ylabel('Count')
    plt.grid(True, alpha=0.2)
    plt.tight_layout()
    plt.savefig(out_path, dpi=150)
    plt.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--artifacts', required=True)
    args = ap.parse_args()
    art = Path(args.artifacts)
    art.mkdir(parents=True, exist_ok=True)

    analysis = load_analysis(art) or {}
    series = analysis.get('equity_series') or []
    closed = load_closed(art)

    # Equity curve
    plot_equity(series, art / 'equity_curve.png')

    # PnL histogram
    pnl = [r['pnl'] for r in closed]
    plot_hist(pnl, 'PnL Per Trade', 'PnL', art / 'pnl_histogram.png')

    # PnL by hour (approx: group by first 13 chars yyyy-mm-ddThh)
    by_hour = {}
    for r in closed:
        t = (r['t'] or '')[:13]
        if not t:
            continue
        by_hour.setdefault(t, 0.0)
        by_hour[t] += r['pnl']
    if by_hour:
        items = sorted(by_hour.items())
        xs = [i for i,_ in enumerate(items)]
        ys = [v for _,v in items]
        plt.figure(figsize=(10,4))
        plt.bar(xs, ys, width=0.8)
        plt.title('PnL by Hour')
        plt.xlabel('Hour bucket')
        plt.ylabel('Total PnL')
        plt.grid(True, axis='y', alpha=0.2)
        plt.tight_layout()
        plt.savefig(art / 'pnl_by_hour.png', dpi=150)
        plt.close()

if __name__ == '__main__':
    main()
