#!/usr/bin/env python3
"""Gate2 artifact validator with schema alias support and KPI calculations."""

from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Dict, Iterable, Iterator, List, Mapping, MutableMapping, Optional, Sequence, Tuple

REQUIRED_FILES: Sequence[str] = (
    "orders.csv",
    "telemetry.csv",
    "risk_snapshots.csv",
    "trade_closes.log",
    "run_metadata.json",
    "closed_trades_fifo_reconstructed.csv",
)

ORDERS_ALIAS_MAP: Mapping[str, Tuple[str, ...]] = {
    "request_id": ("request_id", "order_id", "client_order_id"),
    "ts_request": ("ts_request", "timestamp_request"),
    "ts_ack": ("ts_ack", "timestamp_ack"),
    "ts_fill": ("ts_fill", "timestamp_fill"),
    "status": ("status",),
    "reason": ("reason",),
    "latency_ms": ("latency_ms",),
    "price_requested": ("price_requested",),
    "price_filled": ("price_filled",),
}

RISK_ALIAS_MAP: Mapping[str, Tuple[str, ...]] = {
    "timestamp_utc": ("timestamp_utc", "ts", "timestamp"),
    "equity": ("equity",),
    "R_used": ("R_used", "risk_used"),
    "exposure": ("exposure",),
    "drawdown": ("drawdown",),
}

MIN_TELEMETRY_SPAN_HOURS = 23.75


@dataclass
class ColumnResolution:
    """Resolved column mapping result."""

    mapping: Dict[str, str]
    missing: List[str]

    @property
    def ok(self) -> bool:
        return not self.missing


def _normalize_header(header: Iterable[str]) -> Dict[str, str]:
    """Build a lowercase -> original header lookup."""
    lookup: Dict[str, str] = {}
    for col in header:
        if col is None:
            continue
        lowered = col.strip().lower()
        if lowered and lowered not in lookup:
            lookup[lowered] = col.strip()
    return lookup


def resolve_columns(header: Iterable[str], alias_map: Mapping[str, Sequence[str]]) -> ColumnResolution:
    """Resolve canonical column names using alias definitions."""
    lookup = _normalize_header(header)
    mapping: Dict[str, str] = {}
    missing: List[str] = []

    for canonical, aliases in alias_map.items():
        resolved: Optional[str] = None
        for alias in aliases:
            key = alias.strip().lower()
            if key in lookup:
                resolved = lookup[key]
                break
        if resolved is None:
            missing.append(canonical)
        else:
            mapping[canonical] = resolved

    return ColumnResolution(mapping=mapping, missing=missing)


def _try_float(value: Optional[str]) -> Optional[float]:
    if value is None:
        return None
    text = value.strip()
    if not text:
        return None
    try:
        return float(text)
    except ValueError:
        return None


def _is_number(value: Optional[str]) -> bool:
    return _try_float(value) is not None


def parse_timestamp(value: Optional[str]) -> Optional[datetime]:
    """Parse an ISO-8601 timestamp that may end with Z."""
    if value is None:
        return None
    text = value.strip()
    if not text:
        return None
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    try:
        return datetime.fromisoformat(text)
    except ValueError:
        return None


def compute_span_hours(timestamp_iter: Iterable[Optional[str]]) -> float:
    """Compute span in hours from an iterable of ISO timestamps."""
    first: Optional[datetime] = None
    last: Optional[datetime] = None
    for raw_value in timestamp_iter:
        parsed = parse_timestamp(raw_value)
        if parsed is None:
            continue
        if first is None:
            first = parsed
        last = parsed
    if first is None or last is None:
        return 0.0
    span_seconds = (last - first).total_seconds()
    if span_seconds < 0:
        return 0.0
    return round(span_seconds / 3600.0, 3)


def percentile(values: Sequence[float], pct: float) -> Optional[float]:
    """Compute percentile with linear interpolation."""
    if not values:
        return None
    if pct <= 0:
        return float(min(values))
    if pct >= 1:
        return float(max(values))
    ordered = sorted(values)
    factor = (len(ordered) - 1) * pct
    lower_index = math.floor(factor)
    upper_index = math.ceil(factor)
    if lower_index == upper_index:
        return float(ordered[int(factor)])
    lower_value = ordered[lower_index]
    upper_value = ordered[upper_index]
    weight = factor - lower_index
    return float(lower_value + (upper_value - lower_value) * weight)


def load_csv(path: Path) -> Iterable[MutableMapping[str, str]]:
    """Yield rows from a CSV using UTF-8-sig encoding."""
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            yield row


def iter_csv_column(path: Path, column: str) -> Iterator[Optional[str]]:
    """Iterate over a single column in a CSV file."""
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            yield row.get(column)


def analyze_orders(path: Path, column_map: Mapping[str, str]) -> Dict[str, object]:
    """Compute KPI statistics from orders.csv."""
    requests = 0
    fills = 0
    latencies: List[float] = []

    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            status_raw = row.get(column_map["status"], "")
            status = status_raw.strip().upper()
            if status == "REQUEST":
                requests += 1
            elif status == "FILL":
                fills += 1
                latency_raw = row.get(column_map["latency_ms"], "")
                if latency_raw:
                    try:
                        latencies.append(float(latency_raw))
                    except ValueError:
                        continue

    fill_rate_percent = 0.0
    if requests > 0:
        fill_rate_percent = round((fills / requests) * 100.0, 2)

    median_latency = round(statistics.median(latencies), 2) if latencies else None
    p95_latency = round(percentile(latencies, 0.95), 2) if latencies else None

    return {
        "request_count": requests,
        "fill_count": fills,
        "fill_rate_percent": fill_rate_percent,
        "latency_ms_p50": median_latency,
        "latency_ms_p95": p95_latency,
        "latency_samples": latencies,
    }


def evaluate_order_enrichment(path: Path, column_map: Mapping[str, str]) -> Dict[str, object]:
    fills = 0
    missing_symbol_or_bidask = 0
    slippage_pips: List[float] = []
    latency_ms: List[float] = []

    status_col = column_map.get("status", "status")
    side_col = column_map.get("side", "side")
    price_req_col = column_map.get("price_requested")
    price_fill_col = column_map.get("price_filled")
    latency_col = column_map.get("latency_ms")

    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            status = (row.get(status_col, "") or "").strip().upper()
            if status != "FILL":
                continue
            fills += 1

            symbol = (row.get("symbol") or "").strip()
            bid_req_raw = row.get("bid_at_request")
            ask_req_raw = row.get("ask_at_request")
            bid_fill_raw = row.get("bid_at_fill")
            ask_fill_raw = row.get("ask_at_fill")

            has_request_quote = _is_number(bid_req_raw) and _is_number(ask_req_raw)
            has_fill_quote = _is_number(bid_fill_raw) and _is_number(ask_fill_raw)

            if not symbol or not has_request_quote or not has_fill_quote:
                missing_symbol_or_bidask += 1

            if latency_col:
                lat_val = _try_float(row.get(latency_col))
                if lat_val is not None:
                    latency_ms.append(lat_val)

            pip_size = None
            if has_request_quote:
                spread_price = float(ask_req_raw) - float(bid_req_raw)
                spread_pips_req = _try_float(row.get("spread_pips_at_request"))
                if spread_pips_req and spread_pips_req > 0 and spread_price > 0:
                    pip_size = spread_price / spread_pips_req

            if pip_size is None and has_fill_quote:
                spread_price_fill = float(ask_fill_raw) - float(bid_fill_raw)
                spread_pips_fill = _try_float(row.get("spread_pips_at_fill"))
                if spread_pips_fill and spread_pips_fill > 0 and spread_price_fill > 0:
                    pip_size = spread_price_fill / spread_pips_fill

            if pip_size and pip_size > 0:
                price_requested = _try_float(row.get(price_req_col)) if price_req_col else _try_float(row.get("price_requested"))
                price_filled = _try_float(row.get(price_fill_col)) if price_fill_col else _try_float(row.get("price_filled"))
                side = (row.get(side_col, "") or "").strip().upper()
                ask_req_val = _try_float(ask_req_raw)
                bid_req_val = _try_float(bid_req_raw)

                slip_pips: Optional[float] = None
                if side == "BUY" and price_filled is not None and ask_req_val is not None:
                    slip_pips = (price_filled - ask_req_val) / pip_size
                elif side == "SELL" and price_filled is not None and bid_req_val is not None:
                    slip_pips = (bid_req_val - price_filled) / pip_size

                if slip_pips is not None:
                    slippage_pips.append(abs(slip_pips))

    return {
        "fills": fills,
        "missing_symbol_or_bidask": missing_symbol_or_bidask,
        "slippage_pips": slippage_pips,
        "latency_samples": latency_ms,
    }


def detect_constant_tps(path: Path) -> bool:
    values: List[float] = []
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            val = _try_float(row.get("ticksPerSec"))
            if val is not None:
                values.append(val)

    if len(values) < 10:
        return False

    return max(values) - min(values) <= 0.01


def load_headers(path: Path) -> List[str]:
    """Load CSV header only."""
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.reader(handle)
        header = next(reader, [])
    return [h.strip() for h in header if h is not None]


def validate_artifacts(artifacts_dir: Path, *, strict: bool = False) -> Dict[str, object]:
    """Validate Gate2 artifacts and produce a result dictionary."""
    base_path = Path(artifacts_dir)
    result: Dict[str, object] = {
        "pass": False,
        "reasons": [],
        "telemetry_span_hours": 0.0,
        "files_present": [],
        "schema_ok": True,
        "risk_violations": {"weekly": False, "daily": False},
        "kpi": {},
        "files": {},
        "schema": {
            "orders": {"used_aliases": {}, "missing": []},
            "risk": {"used_aliases": {}, "missing": []},
        },
        "warnings": [],
    }

    missing_files: List[str] = []
    for filename in REQUIRED_FILES:
        file_path = base_path / filename
        if file_path.exists():
            result["files_present"].append(filename)
            result["files"][filename] = "ok"
        else:
            missing_files.append(filename)
            result["reasons"].append(f"missing:{filename}")
            result["files"][filename] = "missing"

    if missing_files:
        result["schema_ok"] = False
        return result

    # Validate orders schema via alias mapping
    orders_header = load_headers(base_path / "orders.csv")
    orders_resolution = resolve_columns(orders_header, ORDERS_ALIAS_MAP)
    if not orders_resolution.ok:
        result["schema_ok"] = False
        for column in orders_resolution.missing:
            result["reasons"].append(f"orders.missing:{column}")
        result["schema"]["orders"]["missing"] = orders_resolution.missing
    result["schema"]["orders"]["used_aliases"] = {
        canonical: orders_resolution.mapping.get(canonical)
        for canonical in ORDERS_ALIAS_MAP
        if canonical in orders_resolution.mapping
    }

    # Validate risk schema via alias mapping
    risk_header = load_headers(base_path / "risk_snapshots.csv")
    risk_resolution = resolve_columns(risk_header, RISK_ALIAS_MAP)
    if not risk_resolution.ok:
        result["schema_ok"] = False
        for column in risk_resolution.missing:
            result["reasons"].append(f"risk.missing:{column}")
        result["schema"]["risk"]["missing"] = risk_resolution.missing
    result["schema"]["risk"]["used_aliases"] = {
        canonical: risk_resolution.mapping.get(canonical)
        for canonical in RISK_ALIAS_MAP
        if canonical in risk_resolution.mapping
    }

    # Telemetry span hours
    telemetry_path = base_path / "telemetry.csv"
    telemetry_span = compute_span_hours(
        iter_csv_column(telemetry_path, "timestamp_iso")
    )
    result["telemetry_span_hours"] = telemetry_span
    if telemetry_span < MIN_TELEMETRY_SPAN_HOURS:
        result["reasons"].append(f"telemetry_span_hours={telemetry_span} (<{MIN_TELEMETRY_SPAN_HOURS})")

    if detect_constant_tps(telemetry_path):
        result["warnings"].append("constant-tps")

    # KPI calculations when schema ok
    if orders_resolution.ok:
        orders_stats = analyze_orders(base_path / "orders.csv", orders_resolution.mapping)
        result["kpi"]["orders"] = orders_stats
        result["kpi"]["requests"] = orders_stats["request_count"]
        result["kpi"]["fills"] = orders_stats["fill_count"]
        result["kpi"]["fill_rate_percent"] = orders_stats["fill_rate_percent"]
        result["kpi"]["fill_rate"] = orders_stats["fill_rate_percent"]
        result["kpi"]["latency_ms_p50"] = orders_stats["latency_ms_p50"]
        result["kpi"]["latency_ms_p95"] = orders_stats["latency_ms_p95"]

        if orders_stats["request_count"] == 0:
            result["reasons"].append("orders.no_requests")
        elif orders_stats["fill_rate_percent"] < 99.5:
            result["reasons"].append(
                f"orders.fill_rate_percent={orders_stats['fill_rate_percent']} (<99.5)"
            )

        enrichment = evaluate_order_enrichment(base_path / "orders.csv", orders_resolution.mapping)
        fills = enrichment["fills"] or 0
        if fills:
            missing_ratio = enrichment["missing_symbol_or_bidask"] / fills  # type: ignore[arg-type]
            if missing_ratio > 0.05:
                result["warnings"].append("missing-symbol-or-bidask")

        slippage_samples: List[float] = enrichment["slippage_pips"]  # type: ignore[assignment]
        latency_samples: List[float] = orders_stats.get("latency_samples", [])  # type: ignore[assignment]
        if not latency_samples:
            latency_samples = enrichment["latency_samples"]  # type: ignore[assignment]

        slippage_p95 = percentile(slippage_samples, 0.95) if slippage_samples else None
        latency_p95 = percentile(latency_samples, 0.95) if latency_samples else None
        if (
            orders_stats["fill_rate_percent"] >= 99.9
            and slippage_p95 is not None
            and latency_p95 is not None
            and slippage_p95 < 0.1
            and latency_p95 < 10.0
        ):
            result["warnings"].append("suspiciously-perfect-execution")
    else:
        result["schema_ok"] = False

    risk_nonzero = {"R_used": False, "drawdown": False}
    if risk_resolution.ok:
        risk_path = base_path / "risk_snapshots.csv"
        with risk_path.open("r", encoding="utf-8-sig", newline="") as handle:
            reader = csv.DictReader(handle)
            for row in reader:
                for key in risk_nonzero:
                    resolved = risk_resolution.mapping.get(key)
                    if not resolved:
                        continue
                    value = row.get(resolved)
                    if value:
                        try:
                            if float(value) != 0.0:
                                risk_nonzero[key] = True
                        except ValueError:
                            risk_nonzero[key] = True
        zero_columns = [col for col, has_nonzero in risk_nonzero.items() if not has_nonzero]
        if zero_columns:
            joined = ", ".join(sorted(zero_columns))
            result["warnings"].append(
                f"risk_snapshots: {joined} constant == 0; defer fix to next session"
            )
    else:
        result["schema_ok"] = False

    result["kpi"].setdefault("requests", 0)
    result["kpi"].setdefault("fills", 0)
    result["kpi"].setdefault("fill_rate", 0.0)
    result["kpi"].setdefault("fill_rate_percent", 0.0)
    result["kpi"]["telemetry_span_hours"] = telemetry_span

    if strict and result["warnings"]:
        result["reasons"].extend([f"warning:{msg}" for msg in result["warnings"]])

    result["pass"] = not result["reasons"] and result["schema_ok"]
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Gate2 artifact validator")
    parser.add_argument("--artifacts", required=True, help="Path to artifacts directory")
    parser.add_argument("--out", help="Optional output file for the validation report")
    parser.add_argument("--strict", action="store_true", help="Treat warnings as failures")
    args = parser.parse_args()

    results = validate_artifacts(Path(args.artifacts), strict=args.strict)
    output = json.dumps(results, indent=2, ensure_ascii=False)
    print(output)

    if args.out:
        out_path = Path(args.out)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(output, encoding="utf-8")

    return 0 if results.get("pass") else 1


if __name__ == "__main__":
    raise SystemExit(main())
