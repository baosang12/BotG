import sys, json, csv
from typing import Any, Set

# Usage: python reconcile.py --closed <closed_trades_fifo.csv> --closes <trade_closes.log> --risk <risk_snapshots.csv>
args = sys.argv
closed_path = None
closes_path = None
risk_path = None
for i, a in enumerate(args):
    if a == '--closed' and i + 1 < len(args):
        closed_path = args[i + 1]
    if a == '--closes' and i + 1 < len(args):
        closes_path = args[i + 1]
    if a == '--risk' and i + 1 < len(args):
        risk_path = args[i + 1]

def to_float(s):
    try:
        return float(s)
    except Exception:
        return None

def first_not_none(*vals):
    for v in vals:
        if v is not None:
            return v
    return None

def walk_numbers_with_keys(obj: Any, keys: Set[str]) -> float:
    total = 0.0
    def _walk(o):
        nonlocal total
        if isinstance(o, dict):
            for k, v in o.items():
                kl = str(k).lower()
                if kl in keys:
                    fv = to_float(v)
                    if fv is not None:
                        total += fv
                _walk(v)
        elif isinstance(o, list):
            for it in o:
                _walk(it)
    _walk(obj)
    return total

def sum_closed_csv(path: str) -> float:
    # Try multiple common delimiters using DictReader
    priority = ['net_realized_usd', 'realized_usd', 'pnl']
    delimiters = [',', ';', '\t', '|']
    try:
        for delim in delimiters:
            try:
                total = 0.0
                with open(path, 'r', encoding='utf-8-sig', newline='') as f:
                    reader = csv.DictReader(f, delimiter=delim)
                    if not reader.fieldnames:
                        continue
                    # Build case-insensitive map
                    name_map = { (h or '').strip().lower(): h for h in reader.fieldnames }
                    target = None
                    for nm in priority:
                        if nm in name_map:
                            target = name_map[nm]
                            break
                    if target is None:
                        # relaxed search
                        for k in list(name_map.keys()):
                            if 'realized' in k or k == 'pnl':
                                target = name_map[k]
                                break
                    if target is None:
                        continue
                    for row in reader:
                        v = to_float(row.get(target))
                        if v is not None:
                            total += v
                    return total
            except Exception:
                continue
    except Exception:
        pass
    return 0.0

closed_sum = None
closes_sum = None
balance_diff = None

if closed_path:
    try:
        closed_sum = sum_closed_csv(closed_path)
    except Exception:
        closed_sum = None

if closes_path:
    try:
        total = 0.0
        # Peek to decide JSONL vs CSV
        first_line = None
        with open(closes_path, 'r', encoding='utf-8') as fh:
            for ln in fh:
                s = (ln or '').strip()
                if s:
                    first_line = s
                    break
        if first_line and (first_line.startswith('{') or first_line.startswith('[')):
            # JSONL path
            keys = { 'realized_pnl_usd', 'net_realized_usd', 'realized_usd', 'pnl' }
            with open(closes_path, 'r', encoding='utf-8') as fh:
                for line in fh:
                    s = line.strip()
                    if not s:
                        continue
                    try:
                        obj = json.loads(s)
                    except Exception:
                        continue
                    total += walk_numbers_with_keys(obj, keys)
            closes_sum = total
        else:
            # Assume CSV with a column like 'pnl' or 'net_realized_usd'
            closes_sum = sum_closed_csv(closes_path)
    except Exception:
        closes_sum = None

if risk_path:
    try:
        with open(risk_path, 'r', encoding='utf-8', newline='') as f:
            reader = csv.DictReader(f)
            balances = []
            for r in reader:
                # Try common fields for account balance/equity
                b = first_not_none(
                    to_float(r.get('balance')),
                    to_float(r.get('balance_usd')),
                    to_float(r.get('equity')),
                    to_float(r.get('equity_usd')),
                )
                if b is not None:
                    balances.append(b)
            if len(balances) >= 2:
                balance_diff = balances[-1] - balances[0]
    except Exception:
        balance_diff = None

print(json.dumps({
    'closed_sum': closed_sum,
    'closes_sum': closes_sum,
    'balance_diff': balance_diff
}, indent=2))
