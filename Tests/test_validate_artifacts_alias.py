#!/usr/bin/env python3
"""Tests for Gate2 artifact validator alias mapping and KPIs."""

import json
import subprocess
import sys
import tempfile
from pathlib import Path
import unittest

import scripts.validate_artifacts as validator

FIXTURE_ROOT = Path(__file__).parent / "fixtures" / "gate2_alias_sample"


class TestValidateArtifactsAlias(unittest.TestCase):
    """Validate alias mapping behaviour and KPI calculations."""

    def test_minimal_fixture_passes_with_alias_mapping(self):
        """Validator should pass when only alias columns are present."""
        artifacts_dir = FIXTURE_ROOT / "minimal"
        result = validator.validate_artifacts(artifacts_dir)

        self.assertTrue(result["pass"])
        self.assertTrue(result["schema_ok"])
        self.assertGreaterEqual(
            result["telemetry_span_hours"], validator.MIN_TELEMETRY_SPAN_HOURS
        )
        self.assertAlmostEqual(result["kpi"]["fill_rate"], 100.0, places=3)
        self.assertEqual(result["kpi"]["requests"], 2)
        self.assertEqual(result["kpi"]["fills"], 2)
        self.assertAlmostEqual(result["kpi"]["latency_ms_p50"], 102.5, places=3)

        used_aliases = result["schema"]["orders"]["used_aliases"]
        self.assertEqual(used_aliases["request_id"], "order_id")
        self.assertEqual(used_aliases["ts_request"], "timestamp_request")

        self.assertTrue(result["warnings"])
        self.assertIn("risk_snapshots", result["warnings"][0])

    def test_validator_would_fail_without_alias_support(self):
        """Removing the alias should revert to the legacy failure behaviour."""
        artifacts_dir = FIXTURE_ROOT / "minimal"

        ok_result = validator.validate_artifacts(artifacts_dir)
        self.assertTrue(ok_result["pass"])

        original_alias = validator.ORDERS_ALIAS_MAP["request_id"]
        try:
            validator.ORDERS_ALIAS_MAP["request_id"] = ("request_id",)
            fail_result = validator.validate_artifacts(artifacts_dir)
        finally:
            validator.ORDERS_ALIAS_MAP["request_id"] = original_alias

        self.assertFalse(fail_result["pass"])
        self.assertIn("orders.missing:request_id", fail_result["reasons"])

    def test_partial_fixture_kpis_include_latency_quantiles(self):
        """KPI output should reflect fill/request ratio and latency quantiles."""
        artifacts_dir = FIXTURE_ROOT / "partial"
        result = validator.validate_artifacts(artifacts_dir)

        self.assertTrue(result["pass"])
        self.assertAlmostEqual(result["kpi"]["fill_rate"], 100.0, places=3)
        self.assertEqual(result["kpi"]["requests"], 4)
        self.assertEqual(result["kpi"]["fills"], 4)
        self.assertAlmostEqual(result["kpi"]["latency_ms_p50"], 130.0, places=3)
        self.assertAlmostEqual(result["kpi"]["latency_ms_p95"], 201.0, places=3)
        self.assertGreaterEqual(
            result["telemetry_span_hours"], validator.MIN_TELEMETRY_SPAN_HOURS
        )
        self.assertFalse(result["warnings"])

    def test_cli_writes_utf8_without_bom_and_exit_code(self):
        """CLI should exit 0 and emit UTF-8 JSON without BOM for valid artifacts."""
        artifacts_dir = FIXTURE_ROOT / "minimal"
        with tempfile.TemporaryDirectory() as tmpdir:
            out_path = Path(tmpdir) / "report.json"
            completed = subprocess.run(
                [
                    sys.executable,
                    str(Path("scripts") / "validate_artifacts.py"),
                    "--artifacts",
                    str(artifacts_dir),
                    "--out",
                    str(out_path),
                ],
                capture_output=True,
                text=True,
            )

            self.assertEqual(completed.returncode, 0)
            self.assertTrue(out_path.exists())

            raw = out_path.read_bytes()
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))

            data = json.loads(raw.decode("utf-8"))
            self.assertTrue(data["pass"])
            self.assertIn("orders", data["schema"])


if __name__ == "__main__":
    unittest.main()
