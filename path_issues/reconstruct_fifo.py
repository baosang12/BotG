#!/usr/bin/env python3
"""Reconstruct FIFO closed trades from orders.csv fills with comprehensive P&L calculation.

Outputs a complete CSV with symbol-aware FIFO matching, commission/spread/slippage costs,
and optional MAE/MFE calculation from OHLC bars.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import re
from collections import defaultdict, deque
from dataclasses import dataclass
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP
from pathlib import Path
from typing import Any, Deque, Dict, Iterable, List, Optional, Tuple

EPSILON = Decimal("1e-12")


@dataclass
class Fill:
    symbol: str
    side: str
    volume: Decimal
    price: Decimal
    epoch_ms: int
    iso: str
    order_id: str
    commission: Decimal = Decimal("0")
    spread_cost: Decimal = Decimal("0")
    slippage_pips: Decimal = Decimal("0")

    def clone_with_volume(self, volume: Decimal) -> "Fill":
        return Fill(
            self.symbol, self.side, Decimal(volume), self.price, self.epoch_ms, self.iso, self.order_id,
            self.commission, self.spread_cost, self.slippage_pips
        )


@dataclass
class CloseLogEntry:
    timestamp_iso: str
    pnl: Decimal


@dataclass
class RunMetadata:
    mode: str = "paper"
    simulation_enabled: bool = False
    point_value_per_lot: Dict[str, Decimal] = None
    default_point_value: Decimal = Decimal("1.0")

    def __post_init__(self):
        if self.point_value_per_lot is None:
            self.point_value_per_lot = {}


@dataclass
class BarData:
    symbol: str
    timestamp: int
    open_price: Decimal
    high: Decimal
    low: Decimal
    close_price: Decimal


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Reconstruct FIFO trades with enhanced P&L calculation")
    parser.add_argument("--orders", required=True, help="Path to orders.csv")
    parser.add_argument("--closes", required=False, help="Path to trade_closes.log")
    parser.add_argument("--meta", required=False, help="Path to run_metadata.json")
    parser.add_argument("--out", required=True, help="Output CSV path")
    parser.add_argument("--bars-dir", required=False, help="Directory containing OHLC bars for MAE/MFE")
    parser.add_argument("--fill-phase", default="FILL", help="Phase value marking fill rows")
    return parser.parse_args()


def load_metadata(path: Optional[str]) -> RunMetadata:
    if not path or not Path(path).exists():
        return RunMetadata()
    
    try:
        with open(path, 'r', encoding='utf-8') as f:
            data = json.load(f)
        
        return RunMetadata(
            mode=data.get("mode", "paper"),
            simulation_enabled=data.get("simulation", {}).get("enabled", False),
            point_value_per_lot={k: Decimal(str(v)) for k, v in data.get("point_value_per_lot", {}).items()},
            default_point_value=Decimal(str(data.get("default_point_value", "1.0")))
        )
    except (json.JSONDecodeError, KeyError, ValueError) as e:
        print(f"Warning: Failed to parse metadata from {path}: {e}")
        return RunMetadata()


def load_bars(bars_dir: Optional[str], symbol: str, start_ms: int, end_ms: int) -> List[BarData]:
    if not bars_dir:
        return []
    
    bars_path = Path(bars_dir) / f"{symbol.lower()}_bars.csv"
    if not bars_path.exists():
        return []
    
    bars = []
    try:
        with open(bars_path, 'r', encoding='utf-8') as f:
            reader = csv.DictReader(f)
            for row in reader:
                timestamp = int(row.get('timestamp_ms', 0))
                if start_ms <= timestamp <= end_ms:
                    bars.append(BarData(
                        symbol=symbol,
                        timestamp=timestamp,
                        open_price=Decimal(str(row.get('open', '0'))),
                        high=Decimal(str(row.get('high', '0'))),
                        low=Decimal(str(row.get('low', '0'))),
                        close_price=Decimal(str(row.get('close', '0')))
                    ))
    except (FileNotFoundError, ValueError, KeyError):
        pass
    
    return bars


def calculate_mae_mfe(bars: List[BarData], open_price: Decimal, side: str) -> Tuple[Decimal, Decimal]:
    """Calculate Maximum Adverse/Favorable Excursion in pips"""
    if not bars:
        return Decimal('NaN'), Decimal('NaN')
    
    is_long = side.upper() in ('BUY', 'LONG')
    mae = Decimal('0')  # Most adverse (negative) excursion
    mfe = Decimal('0')  # Most favorable (positive) excursion
    
    for bar in bars:
        if is_long:
            # For long: adverse is when price goes below open, favorable when above
            adverse_excursion = min(Decimal('0'), bar.low - open_price)
            favorable_excursion = max(Decimal('0'), bar.high - open_price)
        else:
            # For short: adverse is when price goes above open, favorable when below
            adverse_excursion = min(Decimal('0'), open_price - bar.high)
            favorable_excursion = max(Decimal('0'), open_price - bar.low)
        
        mae = min(mae, adverse_excursion)
        mfe = max(mfe, favorable_excursion)
    
    # Convert to pips (assuming 1 pip = 0.0001 for most pairs)
    pip_size = Decimal('0.0001')
    mae_pips = mae / pip_size
    mfe_pips = mfe / pip_size
    
    return mae_pips, mfe_pips


def read_closes_log(path: Optional[str]) -> Dict[str, CloseLogEntry]:
    if not path:
        return {}
    log_path = Path(path)
    if not log_path.exists():
        return {}

    pattern = re.compile(
        r"^(?P<ts>[^\s]+)\s+CLOSED\s+(?P<order>[A-Z0-9\-]+)\s+[^=]+size=(?P<size>[-0-9.]+)\s+pnl=(?P<pnl>[-0-9.eE]+)"
    )
    results: Dict[str, CloseLogEntry] = {}
    with log_path.open("r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = pattern.search(line)
            if not match:
                continue
            ts_iso = match.group("ts")
            order_raw = match.group("order")
            pnl_raw = match.group("pnl")
            try:
                pnl_val = Decimal(pnl_raw)
            except (InvalidOperation, ValueError):
                continue
            normalized = normalize_order_id(order_raw)
            results[normalized] = CloseLogEntry(ts_iso, pnl_val)
    return results


def normalize_order_id(order_id: Optional[str]) -> str:
    if not order_id:
        return ""
    token = str(order_id).strip()
    token = token.lstrip("T-")  # trade_closes.log prefixes orders with T-
    return token.lower()


def pick_column(fieldnames: Iterable[str], candidates: Iterable[str]) -> Optional[str]:
    lowered = {name.lower(): name for name in fieldnames}
    for cand in candidates:
        if cand.lower() in lowered:
            return lowered[cand.lower()]
    for name in fieldnames:
        nl = name.lower()
        for cand in candidates:
            if cand.lower() in nl:
                return name
    return None


def parse_epoch_ms(value: Optional[str]) -> Optional[int]:
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    # Numeric fast-path
    try:
        if text.isdigit():
            num = int(text)
            if num > 10_000_000_000:  # already ms
                return num
            return num * 1000
        num_dec = Decimal(text)
        if num_dec > Decimal(10_000_000_000):
            return int(num_dec.to_integral_value(rounding=ROUND_HALF_UP))
        return int((num_dec * Decimal(1000)).to_integral_value(rounding=ROUND_HALF_UP))
    except (InvalidOperation, ValueError):
        pass
    # ISO fallback
    try:
        iso_norm = text.replace("Z", "+00:00")
        dt = datetime.fromisoformat(iso_norm)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return int(dt.timestamp() * 1000)
    except ValueError:
        return None


def epoch_to_iso(epoch_ms: int) -> str:
    dt = datetime.fromtimestamp(epoch_ms / 1000.0, tz=timezone.utc)
    return dt.isoformat().replace("+00:00", "Z")


def safe_decimal(value: str, default: Decimal = Decimal("0")) -> Decimal:
    try:
        return Decimal(str(value).strip()) if value else default
    except (InvalidOperation, ValueError):
        return default


def read_fills(orders_path: Path, fill_phase: str) -> List[Fill]:
    if not orders_path.exists():
        raise FileNotFoundError(f"orders.csv not found: {orders_path}")

    fills: List[Fill] = []
    with orders_path.open("r", newline="", encoding="utf-8-sig") as fh:
        reader = csv.DictReader(fh)
        fieldnames = reader.fieldnames or []

        phase_col = pick_column(fieldnames, ["phase", "event", "status", "type"])
        symbol_col = pick_column(fieldnames, ["symbol", "instrument", "ticker"])
        side_col = pick_column(fieldnames, ["side", "direction"])
        order_col = pick_column(fieldnames, ["orderId", "order_id", "client_order_id", "broker_order_id"])
        price_col = pick_column(
            fieldnames,
            [
                "execPrice", "price_filled", "price", "fill_price", "executionPrice", 
                "fillPrice", "executed_price"
            ],
        )
        volume_col = pick_column(
            fieldnames,
            [
                "filledSize", "size_filled", "size", "volume", "requestedVolume", 
                "quantity", "theoretical_units", "theoretical_lots", "requested_lots"
            ],
        )
        epoch_col = pick_column(
            fieldnames,
            [
                "epoch_ms", "timestamp_ms", "fill_epoch_ms", "event_epoch_ms", "time_ms", "epoch"
            ],
        )
        iso_col = pick_column(
            fieldnames,
            [
                "timestamp_iso", "fill_time", "fill_timestamp", "event_time", "timestamp"
            ],
        )
        
        # Cost columns
        commission_col = pick_column(fieldnames, ["commission", "fee", "brokerage_fee"])
        spread_col = pick_column(fieldnames, ["spread_cost", "spread", "bid_ask_spread_cost"])
        slippage_col = pick_column(fieldnames, ["slippage_pips", "slippage", "price_slippage_pips"])

        for row in reader:
            if phase_col:
                phase = str(row.get(phase_col, "")).strip().upper()
                if phase != str(fill_phase).strip().upper():
                    continue

            symbol = str(row.get(symbol_col, "")).strip() if symbol_col else "UNKNOWN"
            if not symbol or symbol == "":
                symbol = "UNKNOWN"

            side_raw = str(row.get(side_col, "")).strip().upper() if side_col else ""
            if side_raw in ("LONG", "OPEN_LONG"):
                side = "BUY"
            elif side_raw in ("SHORT", "OPEN_SHORT"):
                side = "SELL"
            elif side_raw in ("BUY", "SELL"):
                side = side_raw
            else:
                continue

            order_id_raw = str(row.get(order_col, "")).strip() if order_col else ""
            order_id = order_id_raw or f"fill_{len(fills)+1}"

            try:
                volume = Decimal(str(row.get(volume_col, "")).strip()) if volume_col else None
            except (InvalidOperation, TypeError):
                volume = None
            if not volume or volume <= 0:
                continue

            price = None
            for candidate in (
                price_col, "execPrice", "price_filled", "fill_price", "price", "intendedPrice"
            ):
                if candidate and candidate in row and row[candidate] not in (None, ""):
                    try:
                        price = Decimal(str(row[candidate]).strip())
                        break
                    except (InvalidOperation, TypeError):
                        continue
            if price is None:
                continue

            epoch_ms = None
            if epoch_col and row.get(epoch_col):
                epoch_ms = parse_epoch_ms(row.get(epoch_col))
            if epoch_ms is None and iso_col and row.get(iso_col):
                epoch_ms = parse_epoch_ms(row.get(iso_col))
            if epoch_ms is None:
                # search fallback iso-friendly columns
                for candidate in ("timestamp_iso", "fill_time", "timestamp"):
                    if candidate in row and row[candidate]:
                        epoch_ms = parse_epoch_ms(row[candidate])
                        if epoch_ms is not None:
                            break
            if epoch_ms is None:
                continue

            iso = epoch_to_iso(epoch_ms)
            
            # Extract cost components
            commission = safe_decimal(row.get(commission_col, ""))
            spread_cost = safe_decimal(row.get(spread_col, ""))
            slippage_pips = safe_decimal(row.get(slippage_col, ""))
            
            fills.append(Fill(symbol, side, volume, price, epoch_ms, iso, order_id, commission, spread_cost, slippage_pips))

    fills.sort(key=lambda f: f.epoch_ms)
    return fills


def quantize(value: Decimal, places: int = 8) -> str:
    if math.isnan(float(value)):
        return "NaN"
    q = Decimal("1." + ("0" * places))
    return str(value.quantize(q, rounding=ROUND_HALF_UP))


def quantize_float(value: float, places: int = 6) -> str:
    if math.isnan(value):
        return "NaN"
    return f"{value:.{places}f}"


def reconstruct(fills: List[Fill]) -> List[Tuple[Fill, Fill, Decimal]]:
    # Symbol-aware FIFO queues
    longs: Dict[str, Deque[Fill]] = defaultdict(deque)
    shorts: Dict[str, Deque[Fill]] = defaultdict(deque)
    results: List[Tuple[Fill, Fill, Decimal]] = []

    for fill in fills:
        symbol = fill.symbol
        if fill.side == "BUY":
            volume_left = Decimal(fill.volume)
            while volume_left > EPSILON and shorts[symbol]:
                open_fill = shorts[symbol][0]
                take = open_fill.volume if open_fill.volume <= volume_left else volume_left
                pnl = (open_fill.price - fill.price) * take  # short pnl = open - close
                results.append((open_fill.clone_with_volume(take), fill.clone_with_volume(take), pnl))
                open_fill.volume -= take
                volume_left -= take
                if open_fill.volume <= EPSILON:
                    shorts[symbol].popleft()
            if volume_left > EPSILON:
                longs[symbol].append(fill.clone_with_volume(volume_left))
        elif fill.side == "SELL":
            volume_left = Decimal(fill.volume)
            while volume_left > EPSILON and longs[symbol]:
                open_fill = longs[symbol][0]
                take = open_fill.volume if open_fill.volume <= volume_left else volume_left
                pnl = (fill.price - open_fill.price) * take  # long pnl = close - open
                results.append((open_fill.clone_with_volume(take), fill.clone_with_volume(take), pnl))
                open_fill.volume -= take
                volume_left -= take
                if open_fill.volume <= EPSILON:
                    longs[symbol].popleft()
            if volume_left > EPSILON:
                shorts[symbol].append(fill.clone_with_volume(volume_left))

    return results


def build_rows(matches: List[Tuple[Fill, Fill, Decimal]], closes_lookup: Dict[str, CloseLogEntry], 
               metadata: RunMetadata, bars_dir: Optional[str]) -> List[Dict[str, str]]:
    rows: List[Dict[str, str]] = []
    seq = 1
    
    for open_fill, close_fill, base_pnl in matches:
        close_epoch = close_fill.epoch_ms
        open_epoch = open_fill.epoch_ms
        if close_epoch < open_epoch:
            close_epoch = open_epoch
        holding_minutes = max(0.0, (close_epoch - open_epoch) / 60000.0)

        close_id_norm = normalize_order_id(close_fill.order_id)
        log_entry = closes_lookup.get(close_id_norm)
        if log_entry:
            timestamp_iso = log_entry.timestamp_iso
        else:
            timestamp_iso = close_fill.iso

        # Enhanced P&L calculation
        symbol = close_fill.symbol
        point_value = metadata.point_value_per_lot.get(symbol, metadata.default_point_value)
        
        # P&L = (price_diff * point_value * qty * direction) - costs
        is_long = open_fill.side == "BUY"
        price_diff = close_fill.price - open_fill.price
        if not is_long:  # Short position
            price_diff = -price_diff
        
        gross_pnl = price_diff * point_value * open_fill.volume
        total_commission = open_fill.commission + close_fill.commission
        total_spread = open_fill.spread_cost + close_fill.spread_cost
        
        # Convert slippage pips to currency (assuming 1 pip = point_value * 0.0001)
        pip_value = point_value * Decimal("0.0001")
        slippage_cost = (open_fill.slippage_pips + close_fill.slippage_pips) * pip_value * open_fill.volume
        
        net_pnl = gross_pnl - total_commission - total_spread - slippage_cost
        
        # MAE/MFE calculation
        bars = load_bars(bars_dir, symbol, open_epoch, close_epoch)
        mae_pips, mfe_pips = calculate_mae_mfe(bars, open_fill.price, open_fill.side)

        row = {
            "timestamp": timestamp_iso,
            "order_id": close_fill.order_id or f"fifo_{seq}",
            "symbol": symbol,
            "position_side": "SHORT" if open_fill.side == "SELL" else "LONG",
            "qty": quantize(open_fill.volume, 8),
            "open_time": open_fill.iso,
            "close_time": close_fill.iso,
            "open_order_id": open_fill.order_id,
            "close_order_id": close_fill.order_id,
            "open_price": quantize(open_fill.price, 8),
            "close_price": quantize(close_fill.price, 8),
            "pnl_currency": quantize(net_pnl, 2),
            "gross_pnl": quantize(gross_pnl, 2),
            "commission": quantize(total_commission, 2),
            "spread_cost": quantize(total_spread, 2),
            "slippage_cost": quantize(slippage_cost, 2),
            "holding_minutes": quantize_float(holding_minutes, 6),
            "mae_pips": quantize(mae_pips, 4),
            "mfe_pips": quantize(mfe_pips, 4),
        }
        rows.append(row)
        seq += 1
    return rows


def write_output(path: Path, rows: List[Dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    header = [
        "timestamp", "order_id", "symbol", "position_side", "qty",
        "open_time", "close_time", "open_order_id", "close_order_id",
        "open_price", "close_price", "pnl_currency", "gross_pnl",
        "commission", "spread_cost", "slippage_cost", "holding_minutes",
        "mae_pips", "mfe_pips"
    ]
    with path.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=header)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)


def main() -> int:
    args = parse_args()
    orders_path = Path(args.orders)
    output_path = Path(args.out)

    metadata = load_metadata(args.meta)
    fills = read_fills(orders_path, args.fill_phase)
    
    if not fills:
        write_output(output_path, [])
        print("⚠ No fills found in orders.csv; wrote empty reconstruction file")
        return 0

    matches = reconstruct(fills)
    closes_lookup = read_closes_log(args.closes)
    rows = build_rows(matches, closes_lookup, metadata, args.bars_dir)
    write_output(output_path, rows)

    # Summary statistics
    total_pnl = sum(Decimal(row["pnl_currency"]) for row in rows)
    print(f"✓ Reconstructed {len(rows)} closed trades -> {output_path}")
    print(f"✓ Total P&L: {quantize(total_pnl, 2)} currency units")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())