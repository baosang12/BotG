#!/usr/bin/env python3
# scripts/make_closes_from_reconstructed.py (no pandas)
import sys, csv, json, argparse
from pathlib import Path
from datetime import datetime, timezone

# Accept --artifact and optional --input; default to using reconstructed file if present,
# otherwise fall back to closed_trades_fifo.csv inside the artifact directory.
def parse_args():
    p = argparse.ArgumentParser(description='Prepare cleaned closes and trade_closes CSV/JSONL for reconcile.')
    p.add_argument('--artifact', default='.', help='Artifact directory containing CSVs')
    p.add_argument('--input', default=None, help='Explicit input CSV (overrides autodetect)')
    return p.parse_args()


def to_iso_utc(val: str) -> str:
    if not val:
        return ''
    s = str(val).strip()
    # Try parse as iso
    try:
        s2 = s.replace('Z', '+00:00')
        dt = datetime.fromisoformat(s2)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc).isoformat().replace('+00:00', 'Z')
    except Exception:
        return s


def main():
    args = parse_args()
    art = Path(args.artifact)
    art.mkdir(parents=True, exist_ok=True)

    # Determine input file
    if args.input:
        inp = Path(args.input)
    else:
        recon = art / 'closed_trades_fifo_reconstructed.csv'
        if recon.exists():
            inp = recon
        else:
            fallback = art / 'closed_trades_fifo.csv'
            inp = fallback

    if not inp.exists():
        print('ERROR: input not found:', inp)
        sys.exit(2)
    print('Loading', inp)

    rows_in = 0
    seen_keys = set()
    dup_counts = {}
    key_cols = ['open_time','close_time','open_price','close_price','pnl','side','volume','symbol']

    CLEAN = art / 'closed_trades_fifo_reconstructed_cleaned.csv'
    DUPS = art / 'duplicate_groups_from_reconstructed.csv'
    CLOSES_CSV = art / 'trade_closes_like_from_reconstructed.csv'
    CLOSES_JSONL = art / 'trade_closes_like_from_reconstructed.jsonl'

    with inp.open('r', encoding='utf-8-sig', newline='') as fin, \
         CLEAN.open('w', encoding='utf-8', newline='') as fclean, \
         CLOSES_CSV.open('w', encoding='utf-8', newline='') as fcsv, \
         CLOSES_JSONL.open('w', encoding='utf-8') as fjsonl:
        reader = csv.DictReader(fin)
        cols = reader.fieldnames or []
        wclean = csv.DictWriter(fclean, fieldnames=cols)
        wclean.writeheader()

        wclose = csv.writer(fcsv)
        wclose.writerow(['trade_id','close_time','pnl'])

        trade_id_col = 'trade_id' if 'trade_id' in cols else None
        close_time_col = 'close_time' if 'close_time' in cols else None
        pnl_col = 'pnl' if 'pnl' in cols else ('net_realized_usd' if 'net_realized_usd' in cols else None)

        pnl_sum = 0.0
        for r in reader:
            rows_in += 1
            key = tuple((c, (r.get(c) or '').strip()) for c in key_cols)
            if key in seen_keys:
                dup_counts[key] = dup_counts.get(key, 1) + 1
                continue
            seen_keys.add(key)
            wclean.writerow(r)

            tid = str(r.get(trade_id_col) or '') if trade_id_col else ''
            ct = to_iso_utc(r.get(close_time_col) or '') if close_time_col else ''
            try:
                pnl_v = float(r.get(pnl_col)) if pnl_col and r.get(pnl_col) not in (None, '') else 0.0
            except Exception:
                pnl_v = 0.0
            pnl_sum += pnl_v
            wclose.writerow([tid, ct, f"{pnl_v:.10f}"])
            fjsonl.write(json.dumps({"trade_id": tid, "close_time": ct, "pnl": pnl_v}, ensure_ascii=False) + '\n')

    print('Wrote cleaned closed trades:', CLEAN)
    print('Wrote closes CSV:', CLOSES_CSV)
    print('Wrote closes JSONL:', CLOSES_JSONL)

    if dup_counts:
        with DUPS.open('w', encoding='utf-8', newline='') as f:
            out_cols = key_cols + ['count']
            w = csv.writer(f)
            w.writerow(out_cols)
            for key, cnt in sorted(dup_counts.items(), key=lambda kv: kv[1], reverse=True):
                vals = [v for (_, v) in key] + [cnt]
                w.writerow(vals)
        print('Wrote duplicate groups:', DUPS, 'count:', len(dup_counts))
    else:
        print('No exact duplicates found on key columns')

    summary = {
        'rows_in': rows_in,
        'rows_cleaned': len(seen_keys),
        'pnl_sum_cleaned': pnl_sum,
        'duplicate_groups': len(dup_counts),
    }
    print('SUMMARY:', summary)


if __name__ == '__main__':
    main()
