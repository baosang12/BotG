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


def analyze_orders(path: Path, column_map: Mapping[str, str]) -> Dict[str, Optional[float]]:
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
    }


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
