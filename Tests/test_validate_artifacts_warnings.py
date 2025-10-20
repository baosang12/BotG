#!/usr/bin/env python3
"""Tests covering warning scenarios in the artifact validator."""

from __future__ import annotations

import csv
import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

import scripts.validate_artifacts as validator


ORDERS_FIELDNAMES = [
    "phase",
    "timestamp_iso",
    "epoch_ms",
    "order_id",
    "intendedPrice",
    "stopLoss",
    "execPrice",
    "theoretical_lots",
    "theoretical_units",
    "requestedVolume",
    "filledSize",
    "slippage",
    "brokerMsg",
    "client_order_id",
    "side",
    "action",
    "type",
    "status",
    "reason",
    "latency_ms",
    "price_requested",
    "price_filled",
    "size_requested",
    "size_filled",
    "session",
    "host",
    "timestamp_request",
    "timestamp_ack",
    "timestamp_fill",
    "symbol",
    "bid_at_request",
    "ask_at_request",
    "spread_pips_at_request",
    "bid_at_fill",
    "ask_at_fill",
    "spread_pips_at_fill",
    "request_server_time",
    "fill_server_time",
]


class TestValidateArtifactsWarnings(unittest.TestCase):
    """Validator should surface soft failures as warnings only."""

    def _prepare_artifacts(
        self,
        *,
        missing_quotes: bool = False,
        constant_tps: bool = False,
        perfect_execution: bool = False,
    ) -> Path:
        temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(temp_dir.cleanup)
        root = Path(temp_dir.name)

        self._write_run_metadata(root)
        self._write_closed_trades(root)
        self._write_risk_snapshots(root)
        self._write_telemetry(root, constant_tps=constant_tps)
        self._write_orders(
            root,
            missing_quotes=missing_quotes,
            perfect_execution=perfect_execution,
        )
        return root

    def _write_run_metadata(self, root: Path) -> None:
        data = {"run_id": "unit-test", "session": "gate3"}
        (root / "run_metadata.json").write_text(json.dumps(data), encoding="utf-8")

    def _write_closed_trades(self, root: Path) -> None:
        (root / "trade_closes.log").write_text("", encoding="utf-8")
        (root / "closed_trades_fifo_reconstructed.csv").write_text(
            "trade_id\nT1\n",
            encoding="utf-8",
        )

    def _write_risk_snapshots(self, root: Path) -> None:
        with (root / "risk_snapshots.csv").open("w", encoding="utf-8", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["timestamp_utc", "equity", "R_used", "exposure", "drawdown"])
            writer.writerow(["2025-01-01T00:00:00Z", "10000", "0.5", "250", "12"])

    def _write_telemetry(self, root: Path, *, constant_tps: bool) -> None:
        start = datetime(2025, 1, 1, tzinfo=timezone.utc)
        with (root / "telemetry.csv").open("w", encoding="utf-8", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["timestamp_iso", "ticksPerSec"])
            for hour in range(25):
                value = 2.0 if constant_tps else 2.0 + (hour % 5) * 0.05
                moment = start + timedelta(hours=hour)
                writer.writerow([moment.isoformat().replace("+00:00", "Z"), f"{value:.2f}"])

    def _write_orders(
        self,
        root: Path,
        *,
        missing_quotes: bool,
        perfect_execution: bool,
    ) -> None:
        orders_path = root / "orders.csv"
        with orders_path.open("w", encoding="utf-8", newline="") as handle:
            writer = csv.DictWriter(handle, fieldnames=ORDERS_FIELDNAMES)
            writer.writeheader()

            rows = self._build_order_rows(
                missing_quotes=missing_quotes,
                perfect_execution=perfect_execution,
            )
            for row in rows:
                writer.writerow(row)

    def _build_order_rows(
        self,
        *,
        missing_quotes: bool,
        perfect_execution: bool,
    ) -> list[dict[str, str]]:
        base_request = {
            "phase": "REQUEST",
            "intendedPrice": "1.2345",
            "theoretical_lots": "0.10",
            "theoretical_units": "10000",
            "requestedVolume": "10000",
            "brokerMsg": "OK",
            "action": "BUY",
            "type": "Market",
            "status": "REQUEST",
            "latency_ms": "5",
            "price_requested": "1.2345",
            "size_requested": "10000",
            "session": "EURUSD",
            "host": "unit-test",
        }

        base_fill = {
            "phase": "FILL",
            "execPrice": "1.2346",
            "filledSize": "10000",
            "slippage": "0",
            "brokerMsg": "OK",
            "action": "BUY",
            "type": "Market",
            "status": "FILL",
            "latency_ms": "5",
            "price_requested": "1.2345",
            "price_filled": "1.2346",
            "size_requested": "10000",
            "size_filled": "10000",
            "session": "EURUSD",
            "host": "unit-test",
            "symbol": "EURUSD",
            "bid_at_request": "1.2345",
            "ask_at_request": "1.2346",
            "spread_pips_at_request": "1",
            "bid_at_fill": "1.2345",
            "ask_at_fill": "1.2346",
            "spread_pips_at_fill": "1",
        }

        if perfect_execution:
            base_fill["price_filled"] = "1.2346"
            base_fill["latency_ms"] = "5"

        rows: list[dict[str, str]] = []

        for idx in range(2):
            order_id = f"O{idx + 1}"
            ts_request = f"2025-01-01T{idx:02d}:00:00Z"
            ts_fill = f"2025-01-01T{idx:02d}:00:01Z"

            request_row = {
                **base_request,
                "timestamp_iso": ts_request,
                "epoch_ms": str(idx * 1000),
                "order_id": order_id,
                "client_order_id": order_id,
                "side": "BUY" if idx % 2 == 0 else "SELL",
                "reason": "",
                "timestamp_request": ts_request,
                "timestamp_ack": ts_request,
                "timestamp_fill": "",
                "symbol": "EURUSD",
                "bid_at_request": "1.2345",
                "ask_at_request": "1.2346",
                "spread_pips_at_request": "1",
                "bid_at_fill": "",
                "ask_at_fill": "",
                "spread_pips_at_fill": "",
                "request_server_time": ts_request,
                "fill_server_time": "",
            }

            fill_row = {
                **base_fill,
                "timestamp_iso": ts_fill,
                "epoch_ms": str(idx * 1000 + 500),
                "order_id": order_id,
                "client_order_id": order_id,
                "side": "BUY" if idx % 2 == 0 else "SELL",
                "reason": "",
                "timestamp_request": ts_request,
                "timestamp_ack": ts_request,
                "timestamp_fill": ts_fill,
                "request_server_time": ts_request,
                "fill_server_time": ts_fill,
            }

            if idx == 1 and missing_quotes:
                for key in [
                    "symbol",
                    "bid_at_request",
                    "ask_at_request",
                    "spread_pips_at_request",
                    "bid_at_fill",
                    "ask_at_fill",
                    "spread_pips_at_fill",
                ]:
                    request_row[key] = ""
                    fill_row[key] = ""

            if perfect_execution and fill_row["side"] == "SELL":
                fill_row["price_filled"] = request_row["bid_at_request"]

            rows.append(request_row)
            rows.append(fill_row)

        return rows

    def test_missing_quotes_warning_when_coverage_low(self) -> None:
        root = self._prepare_artifacts(missing_quotes=True)
        result = validator.validate_artifacts(root)

        self.assertIn("missing-symbol-or-bidask", result["warnings"])

    def test_constant_tps_triggers_warning(self) -> None:
        root = self._prepare_artifacts(constant_tps=True)
        result = validator.validate_artifacts(root)

        self.assertIn("constant-tps", result["warnings"])

    def test_suspiciously_perfect_execution_warning(self) -> None:
        root = self._prepare_artifacts(perfect_execution=True)
        result = validator.validate_artifacts(root)

        self.assertIn("suspiciously-perfect-execution", result["warnings"])


if __name__ == "__main__":
    unittest.main()

