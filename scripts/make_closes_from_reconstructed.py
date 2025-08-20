#!/usr/bin/env python3
# scripts/make_closes_from_reconstructed.py (no pandas)
import sys, csv, json
from pathlib import Path
from datetime import datetime, timezone

ART = Path(r'.\artifacts\telemetry_run_20250819_154459')
IN = ART / 'closed_trades_fifo_reconstructed.csv'
CLEAN = ART / 'closed_trades_fifo_reconstructed_cleaned.csv'
DUPS = ART / 'duplicate_groups_from_reconstructed.csv'
CLOSES_CSV = ART / 'trade_closes_like_from_reconstructed.csv'
CLOSES_JSONL = ART / 'trade_closes_like_from_reconstructed.jsonl'


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
    if not IN.exists():
        print('ERROR: input not found:', IN)
        sys.exit(2)
    print('Loading', IN)

    rows_in = 0
    seen_keys = set()
    dup_counts = {}
    key_cols = ['open_time','close_time','open_price','close_price','pnl','side','volume','symbol']

    with IN.open('r', encoding='utf-8-sig', newline='') as fin, \
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
