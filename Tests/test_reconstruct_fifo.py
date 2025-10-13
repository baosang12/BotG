#!/usr/bin/env python3
"""Unit tests for FIFO reconstruction and schema validation."""

import csv
import json
import os
import tempfile
import unittest
from pathlib import Path

# Import the modules we're testing
import sys
sys.path.append(str(Path(__file__).parent.parent / "path_issues"))

try:
    from reconstruct_fifo import (
        read_fills, reconstruct, build_rows, load_metadata, 
        RunMetadata, Fill
    )
    from validate_artifacts import validate_artifacts, REQUIRED_FILES
except ImportError as e:
    print(f"Warning: Could not import modules for testing: {e}")
    # Create stub classes for basic testing
    class Fill:
        def __init__(self, symbol, side, volume, price, epoch_ms, iso, order_id):
            self.symbol = symbol
            self.side = side
            self.volume = volume
            self.price = price
            self.epoch_ms = epoch_ms
            self.iso = iso
            self.order_id = order_id


class TestFIFOReconstruction(unittest.TestCase):
    """Test FIFO reconstruction logic."""

    def setUp(self):
        self.temp_dir = Path(tempfile.mkdtemp())

    def tearDown(self):
        import shutil
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_simple_fifo_matching(self):
        """Test basic FIFO matching - single buy followed by single sell."""
        if 'reconstruct' not in globals():
            self.skipTest("reconstruct_fifo module not available")

        fills = [
            Fill("EURUSD", "BUY", 1.0, 1.0500, 1000, "2025-01-01T10:00:00Z", "ORD-1"),
            Fill("EURUSD", "SELL", 1.0, 1.0600, 2000, "2025-01-01T10:01:00Z", "ORD-2")
        ]

        matches = reconstruct(fills)
        
        self.assertEqual(len(matches), 1)
        open_fill, close_fill, pnl = matches[0]
        
        self.assertEqual(open_fill.side, "BUY")
        self.assertEqual(close_fill.side, "SELL")
        self.assertEqual(open_fill.order_id, "ORD-1")
        self.assertEqual(close_fill.order_id, "ORD-2")
        self.assertEqual(pnl, 0.01)  # 1.0600 - 1.0500

    def test_partial_fills(self):
        """Test partial fills with multiple open and close orders."""
        if 'reconstruct' not in globals():
            self.skipTest("reconstruct_fifo module not available")

        fills = [
            Fill("EURUSD", "BUY", 0.5, 1.0500, 1000, "2025-01-01T10:00:00Z", "ORD-1"),
            Fill("EURUSD", "BUY", 0.5, 1.0505, 2000, "2025-01-01T10:01:00Z", "ORD-2"),
            Fill("EURUSD", "SELL", 0.3, 1.0510, 3000, "2025-01-01T10:02:00Z", "ORD-3"),
            Fill("EURUSD", "SELL", 0.7, 1.0515, 4000, "2025-01-01T10:03:00Z", "ORD-4")
        ]

        matches = reconstruct(fills)
        
        # Should generate 2 trades
        self.assertEqual(len(matches), 2)
        
        # First trade: 0.3 from first buy
        trade1 = matches[0]
        self.assertEqual(trade1[0].volume, 0.3)
        self.assertEqual(trade1[0].price, 1.0500)
        self.assertEqual(trade1[1].volume, 0.3)
        self.assertEqual(trade1[1].price, 1.0510)
        
        # Second trade: remaining 0.2 from first buy + 0.5 from second buy
        trade2 = matches[1]
        self.assertEqual(trade2[1].volume, 0.7)  # Close volume
        self.assertEqual(trade2[1].price, 1.0515)

    def test_hedging_scenario(self):
        """Test hedging - long position followed by short position on same symbol."""
        if 'reconstruct' not in globals():
            self.skipTest("reconstruct_fifo module not available")

        fills = [
            Fill("EURUSD", "BUY", 1.0, 1.0500, 1000, "2025-01-01T10:00:00Z", "ORD-1"),
            Fill("EURUSD", "SELL", 2.0, 1.0510, 2000, "2025-01-01T10:01:00Z", "ORD-2"),
            Fill("EURUSD", "BUY", 1.0, 1.0520, 3000, "2025-01-01T10:02:00Z", "ORD-3")
        ]

        matches = reconstruct(fills)
        
        # Should generate 2 trades
        self.assertEqual(len(matches), 2)
        
        # First trade: close long position
        trade1 = matches[0]
        self.assertEqual(trade1[0].side, "BUY")    # Open
        self.assertEqual(trade1[1].side, "SELL")   # Close
        self.assertEqual(trade1[0].volume, 1.0)
        
        # Second trade: close short position (opened by excess sell volume)
        trade2 = matches[1]
        self.assertEqual(trade2[0].side, "SELL")   # Open (short)
        self.assertEqual(trade2[1].side, "BUY")    # Close
        self.assertEqual(trade2[0].volume, 1.0)

    def test_symbol_isolation(self):
        """Test that different symbols don't cross-match."""
        if 'reconstruct' not in globals():
            self.skipTest("reconstruct_fifo module not available")

        fills = [
            Fill("EURUSD", "BUY", 1.0, 1.0500, 1000, "2025-01-01T10:00:00Z", "ORD-1"),
            Fill("GBPUSD", "BUY", 1.0, 1.2500, 2000, "2025-01-01T10:01:00Z", "ORD-2"),
            Fill("EURUSD", "SELL", 1.0, 1.0600, 3000, "2025-01-01T10:02:00Z", "ORD-3"),
            Fill("GBPUSD", "SELL", 1.0, 1.2600, 4000, "2025-01-01T10:03:00Z", "ORD-4")
        ]

        matches = reconstruct(fills)
        
        # Should generate 2 trades, one for each symbol
        self.assertEqual(len(matches), 2)
        
        symbols = {match[0].symbol for match in matches}
        self.assertEqual(symbols, {"EURUSD", "GBPUSD"})


class TestSchemaValidation(unittest.TestCase):
    """Test artifact schema validation."""

    def setUp(self):
        self.temp_dir = Path(tempfile.mkdtemp())

    def tearDown(self):
        import shutil
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    def create_valid_artifacts(self):
        """Create a complete set of valid artifacts for testing."""
        
        # orders.csv
        orders_csv = """phase,timestamp_iso,orderId,side,execPrice,filledSize,symbol
FILL,2025-01-01T10:00:00Z,ORD-1,BUY,1.0500,1.0,EURUSD
FILL,2025-01-01T10:01:00Z,ORD-2,SELL,1.0600,1.0,EURUSD"""
        (self.temp_dir / "orders.csv").write_text(orders_csv)

        # telemetry.csv
        telemetry_csv = """timestamp,event_type,order_id,symbol,side
2025-01-01T10:00:00Z,FILL,ORD-1,EURUSD,BUY
2025-01-01T10:01:00Z,FILL,ORD-2,EURUSD,SELL"""
        (self.temp_dir / "telemetry.csv").write_text(telemetry_csv)

        # risk_snapshots.csv with enough rows
        risk_headers = "timestamp,symbol,position_size,unrealized_pnl,margin_used"
        risk_rows = []
        for i in range(1500):  # Exceed minimum 1300 rows
            row = f"2025-01-01T10:{i:02d}:00Z,EURUSD,0.0,0.0,0.0"
            risk_rows.append(row)
        risk_csv = risk_headers + "\n" + "\n".join(risk_rows)
        (self.temp_dir / "risk_snapshots.csv").write_text(risk_csv)

        # trade_closes.log
        log_content = """2025-01-01T10:01:00Z CLOSED ORD-2 symbol=EURUSD size=1.0 pnl=100.0
2025-01-01T10:02:00Z SYSTEM heartbeat"""
        (self.temp_dir / "trade_closes.log").write_text(log_content)

        # run_metadata.json
        metadata = {
            "mode": "paper",
            "simulation": {"enabled": False},
            "point_value_per_lot": {"EURUSD": "100000"},
            "default_point_value": "1.0"
        }
        (self.temp_dir / "run_metadata.json").write_text(json.dumps(metadata, indent=2))

        # closed_trades_fifo_reconstructed.csv
        trades_csv = """timestamp,order_id,symbol,position_side,qty,open_time,close_time,pnl_currency,mae_pips,mfe_pips
2025-01-01T10:01:00Z,ORD-2,EURUSD,LONG,1.0,2025-01-01T10:00:00Z,2025-01-01T10:01:00Z,100.0,0.0,0.0"""
        (self.temp_dir / "closed_trades_fifo_reconstructed.csv").write_text(trades_csv)

    def test_valid_artifacts_pass(self):
        """Test that valid artifacts pass validation."""
        if 'validate_artifacts' not in globals():
            self.skipTest("validate_artifacts module not available")

        self.create_valid_artifacts()
        
        results = validate_artifacts(self.temp_dir)
        
        self.assertTrue(results["overall_valid"])
        self.assertEqual(results["summary"]["files_present"], 6)
        self.assertEqual(results["summary"]["files_valid"], 6)

    def test_missing_file_fails(self):
        """Test that missing required file fails validation."""
        if 'validate_artifacts' not in globals():
            self.skipTest("validate_artifacts module not available")

        self.create_valid_artifacts()
        
        # Remove a required file
        (self.temp_dir / "orders.csv").unlink()
        
        results = validate_artifacts(self.temp_dir)
        
        self.assertFalse(results["overall_valid"])
        self.assertEqual(results["summary"]["files_present"], 5)

    def test_missing_columns_fail(self):
        """Test that missing required columns fail validation."""
        if 'validate_artifacts' not in globals():
            self.skipTest("validate_artifacts module not available")

        self.create_valid_artifacts()
        
        # Create orders.csv with missing required columns
        invalid_orders = """timestamp,order_id
2025-01-01T10:00:00Z,ORD-1"""
        (self.temp_dir / "orders.csv").write_text(invalid_orders)
        
        results = validate_artifacts(self.temp_dir)
        
        self.assertFalse(results["overall_valid"])
        orders_result = results["files"]["orders.csv"]
        self.assertFalse(orders_result["columns_valid"])
        self.assertGreater(len(orders_result["missing_columns"]), 0)

    def test_insufficient_rows_fail(self):
        """Test that insufficient row count fails validation."""
        if 'validate_artifacts' not in globals():
            self.skipTest("validate_artifacts module not available")

        self.create_valid_artifacts()
        
        # Create risk_snapshots.csv with too few rows
        insufficient_risk = """timestamp,symbol,position_size,unrealized_pnl,margin_used
2025-01-01T10:00:00Z,EURUSD,0.0,0.0,0.0"""
        (self.temp_dir / "risk_snapshots.csv").write_text(insufficient_risk)
        
        results = validate_artifacts(self.temp_dir)
        
        self.assertFalse(results["overall_valid"])
        risk_result = results["files"]["risk_snapshots.csv"]
        self.assertGreater(len(risk_result["errors"]), 0)

    def test_invalid_mode_fails(self):
        """Test that non-paper mode fails validation."""
        if 'validate_artifacts' not in globals():
            self.skipTest("validate_artifacts module not available")

        self.create_valid_artifacts()
        
        # Create metadata with invalid mode
        invalid_metadata = {
            "mode": "live",
            "simulation": {"enabled": False}
        }
        (self.temp_dir / "run_metadata.json").write_text(json.dumps(invalid_metadata))
        
        results = validate_artifacts(self.temp_dir)
        
        self.assertFalse(results["overall_valid"])
        meta_result = results["files"]["run_metadata.json"]
        self.assertFalse(meta_result["mode_valid"])

    def test_simulation_enabled_fails(self):
        """Test that enabled simulation fails validation."""
        if 'validate_artifacts' not in globals():
            self.skipTest("validate_artifacts module not available")

        self.create_valid_artifacts()
        
        # Create metadata with simulation enabled
        invalid_metadata = {
            "mode": "paper",
            "simulation": {"enabled": True}
        }
        (self.temp_dir / "run_metadata.json").write_text(json.dumps(invalid_metadata))
        
        results = validate_artifacts(self.temp_dir)
        
        self.assertFalse(results["overall_valid"])
        meta_result = results["files"]["run_metadata.json"]
        self.assertFalse(meta_result["simulation_disabled"])


if __name__ == "__main__":
    unittest.main()