import json
import tempfile
import unittest
from pathlib import Path

import pandas as pd

from scripts.analyzers.join_l1_fills import main as join_main


class JoinL1FillsTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory()
        self.tmp_path = Path(self._tmpdir.name)

    def tearDown(self) -> None:
        self._tmpdir.cleanup()

    def test_side_ref_buy_sell(self) -> None:
        orders_path = self.tmp_path / "orders.csv"
        l1_path = self.tmp_path / "l1.csv"
        fees_path = self.tmp_path / "fees.csv"
        kpi_path = self.tmp_path / "kpi.json"

        pd.DataFrame(
            [
                {
                    "order_id": "1",
                    "symbol": "EURUSD",
                    "side": "BUY",
                    "lots": 1,
                    "timestamp_submit": "2025-01-01T00:00:00Z",
                    "timestamp_fill": "2025-01-01T00:00:00Z",
                    "price_filled": 1.10020,
                },
                {
                    "order_id": "2",
                    "symbol": "EURUSD",
                    "side": "SELL",
                    "lots": 1,
                    "timestamp_submit": "2025-01-01T00:00:01Z",
                    "timestamp_fill": "2025-01-01T00:00:01Z",
                    "price_filled": 1.10000,
                },
            ]
        ).to_csv(orders_path, index=False)

        pd.DataFrame(
            [
                {"timestamp": "2025-01-01T00:00:00Z", "bid": 1.10000, "ask": 1.10010},
                {"timestamp": "2025-01-01T00:00:01Z", "bid": 1.09990, "ask": 1.10000},
            ]
        ).to_csv(l1_path, index=False)

        join_main(str(orders_path), str(l1_path), str(fees_path), str(kpi_path))

        self.assertTrue(fees_path.exists())
        fees_df = pd.read_csv(fees_path)
        self.assertEqual(list(fees_df["px_ref"]), [1.10010, 1.09990])
        self.assertAlmostEqual(fees_df.loc[0, "slip_pts"], 1.0, places=6)
        self.assertAlmostEqual(fees_df.loc[1, "slip_pts"], -1.0, places=6)

        self.assertTrue(kpi_path.exists())
        with open(kpi_path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
        self.assertAlmostEqual(data["BUY_median_slip_pts"], 1.0, places=6)
        self.assertAlmostEqual(data["SELL_median_slip_pts"], -1.0, places=6)


if __name__ == "__main__":
    unittest.main()
