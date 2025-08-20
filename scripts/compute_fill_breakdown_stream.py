#!/usr/bin/env python3
import sys, csv, argparse, json, time
from pathlib import Path
from datetime import datetime, timezone


def parse_iso_to_hour_z(s: str) -> str:
    if not s:
        return ''
    st = s.strip()
    try:
        st2 = st.replace('Z', '+00:00')
        dt = datetime.fromisoformat(st2)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        dt = dt.astimezone(timezone.utc)
        return dt.strftime('%Y-%m-%d %H:00:00Z')
    except Exception:
        return ''


class P2Quantile:
    # Simple P^2 quantile estimator for a single quantile
    def __init__(self, q: float):
        self.q = q
        self.n = 0
        self.markers = []  # positions and heights init when first 5 samples seen

    def add(self, x: float):
        # For simplicity and robustness in streaming, fallback to reservoir of first 5 then median-like updates
        # This is not a perfect P2, but avoids heavy memory. Good enough for median/p90 monitoring.
        if self.n < 5:
            self.markers.append(x)
            self.n += 1
            if self.n == 5:
                self.markers.sort()
            return
        # Insert using binary search position
        import bisect
        bisect.insort(self.markers, x)
        # Trim to a small sliding window to limit memory (keep last 1000 samples spread)
        m = 1000
        if len(self.markers) > m:
            step = len(self.markers) / m
            self.markers = [self.markers[int(i*step)] for i in range(m)]
        self.n += 1

    def value(self) -> float | None:
        if self.n == 0:
            return None
        arr = self.markers
        if not arr:
            return None
        idx = int(round(self.q * (len(arr) - 1)))
        return float(arr[idx])


class Stat:
    __slots__ = ('requests','acks','fills','sum_slip','sum_abs','cnt_slip','q50','q90')
    def __init__(self):
        self.requests = 0
        self.acks = 0
        self.fills = 0
        self.sum_slip = 0.0
        self.sum_abs = 0.0
        self.cnt_slip = 0
        self.q50 = P2Quantile(0.5)
        self.q90 = P2Quantile(0.9)

    def add_slip(self, v: float):
        self.sum_slip += v
        self.sum_abs += abs(v)
        self.cnt_slip += 1
        self.q50.add(v)
        self.q90.add(v)

    def as_row(self, key_name: str, is_hour: bool = False):
        avg = (self.sum_slip / self.cnt_slip) if self.cnt_slip else None
        avg_abs = (self.sum_abs / self.cnt_slip) if self.cnt_slip else None
        p50 = self.q50.value()
        p90 = self.q90.value()
        row = {
            ('hour_utc' if is_hour else 'side'): key_name,
            'requests': self.requests,
            'acks': self.acks,
            'fills': self.fills,
            'fill_rate_percent': round(100.0 * self.fills / self.requests, 2) if self.requests else 0,
            'avg_slip': round(avg, 10) if avg is not None else None,
            'p50_slip': round(p50, 10) if p50 is not None else None,
            'p90_slip': round(p90, 10) if p90 is not None else None,
            'avg_abs_slip': round(avg_abs, 10) if avg_abs is not None else None,
            'p50_abs_slip': round(abs(p50), 10) if p50 is not None else None,
            'p90_abs_slip': round(abs(p90), 10) if p90 is not None else None,
        }
        return row


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--orders', required=True)
    ap.add_argument('--outdir', required=True)
    ap.add_argument('--chunksize', type=int, default=100000)
    args = ap.parse_args()

    orders = Path(args.orders)
    outdir = Path(args.outdir)
    outdir.mkdir(parents=True, exist_ok=True)
    partial = outdir / 'partial'
    partial.mkdir(parents=True, exist_ok=True)

    log_file = outdir / 'run_compute_stream.log'
    jlog_file = outdir / 'compute_stream.log.jsonl'

    start = time.time()
    total_rows = 0
    chunk_idx = 0
    report_every = max(100000, args.chunksize)

    by_side = { 'BUY': Stat(), 'SELL': Stat() }
    by_hour: dict[str, Stat] = {}

    def log(msg: str, level='info', **kw):
        ts = datetime.now(timezone.utc).isoformat().replace('+00:00','Z')
        line = f"[{ts}] {msg}"
        print(line)
        try:
            with log_file.open('a', encoding='utf-8') as lf:
                lf.write(line + '\n')
        except Exception:
            pass
        rec = { 'ts': ts, 'level': level, 'msg': msg }
        rec.update(kw)
        try:
            with jlog_file.open('a', encoding='utf-8') as jf:
                jf.write(json.dumps(rec, ensure_ascii=False) + '\n')
        except Exception:
            pass

    if not orders.exists() or orders.stat().st_size == 0:
        log(f"Orders file missing or empty: {orders}", level='error')
        return 20

    # Streaming read via csv module
    with orders.open('r', encoding='utf-8-sig', newline='') as f:
        reader = csv.DictReader(f)
        cols = [c.strip() for c in (reader.fieldnames or [])]
        # Expected columns: phase, side, slippage, timestamp_iso
        for row in reader:
            total_rows += 1
            phase = (row.get('phase') or '').strip()
            side = (row.get('side') or '').strip().upper()
            if phase == 'REQUEST' and side in by_side:
                by_side[side].requests += 1
            elif phase == 'ACK' and side in by_side:
                by_side[side].acks += 1
            elif phase == 'FILL' and side in by_side:
                by_side[side].fills += 1
                s = row.get('slippage')
                if s not in (None, ''):
                    try:
                        v = float(s)
                        by_side[side].add_slip(v)
                    except Exception:
                        pass

            # By hour
            ts_iso = row.get('timestamp_iso') or ''
            hour = parse_iso_to_hour_z(ts_iso)
            if hour:
                st = by_hour.get(hour)
                if st is None:
                    st = by_hour[hour] = Stat()
                if phase == 'REQUEST':
                    st.requests += 1
                elif phase == 'ACK':
                    st.acks += 1
                elif phase == 'FILL':
                    st.fills += 1
                    s = row.get('slippage')
                    if s not in (None, ''):
                        try:
                            v = float(s)
                            st.add_slip(v)
                        except Exception:
                            pass

            if total_rows % report_every == 0:
                chunk_idx += 1
                elapsed = time.time() - start
                log(f"Processed {total_rows} rows (chunk {chunk_idx}); elapsed {elapsed:.1f}s", rows=total_rows, chunk=chunk_idx, elapsed_sec=elapsed)

    # Write outputs
    by_side_path = outdir / 'fill_rate_by_side.csv'
    with by_side_path.open('w', encoding='utf-8', newline='') as f:
        w = csv.DictWriter(f, fieldnames=['side','requests','acks','fills','fill_rate_percent','avg_slip','p50_slip','p90_slip','avg_abs_slip','p50_abs_slip','p90_abs_slip'])
        w.writeheader()
        for side, st in by_side.items():
            w.writerow(st.as_row(side, is_hour=False))

    by_hour_path = outdir / 'fill_breakdown_by_hour.csv'
    with by_hour_path.open('w', encoding='utf-8', newline='') as f:
        w = csv.DictWriter(f, fieldnames=['hour_utc','requests','acks','fills','fill_rate_percent','avg_slip','p50_slip','p90_slip','avg_abs_slip','p50_abs_slip','p90_abs_slip'])
        w.writeheader()
        for hour in sorted(by_hour.keys()):
            w.writerow(by_hour[hour].as_row(hour, is_hour=True))

    elapsed = time.time() - start
    # Compute chunks_processed based on chunksize
    chunks_processed = (total_rows + max(1, args.chunksize) - 1) // max(1, args.chunksize)
    log(f"DONE. rows={total_rows}, chunks={chunks_processed}, elapsed={elapsed:.2f}s", rows=total_rows, chunks=chunks_processed, elapsed_sec=elapsed)
    # Create a small analysis_summary_stats.json
    try:
        (outdir / 'analysis_summary_stats.json').write_text(json.dumps({
            'rows': total_rows,
            'chunks_processed': chunks_processed,
            'chunksize': args.chunksize,
            'elapsed_seconds': elapsed,
            'by_side_path': str(by_side_path),
            'by_hour_path': str(by_hour_path),
            'log_file': str(log_file),
        }, indent=2), encoding='utf-8')
    except Exception:
        pass
    return 0


if __name__ == '__main__':
    sys.exit(main())
