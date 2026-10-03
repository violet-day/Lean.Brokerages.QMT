import sys
import unittest
from datetime import date
from pathlib import Path


SCRIPTS_DIRECTORY = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS_DIRECTORY))

from download_qmt_history import (
    history_bars_with_share_volume,
    quantconnect_minute_rows,
)


class DownloadQmtHistoryTests(unittest.TestCase):
    def test_missing_volume_unit_treats_qmt_volume_as_lots(self):
        history_bars = history_bars_with_share_volume(
            {
                "bars": [{"time": "20260930093000", "volume": 156}],
            }
        )

        self.assertEqual(15_600, history_bars[0]["volume"])

    def test_explicit_lot_volume_is_converted_to_shares(self):
        history_bars = history_bars_with_share_volume(
            {
                "bars": [{"time": "20260930093000", "volume": 156}],
                "volume_unit": "lots",
            }
        )

        self.assertEqual(15_600, history_bars[0]["volume"])

    def test_share_volume_is_not_converted_twice(self):
        history_bars = history_bars_with_share_volume(
            {
                "bars": [{"time": "20260930093000", "volume": 15_600}],
                "volume_unit": "shares",
            }
        )

        self.assertEqual(15_600, history_bars[0]["volume"])

    def test_qmt_end_times_are_converted_to_lean_bar_start_times(self):
        history_bars = [
            {
                "time": "20260804093000",
                "open": 10,
                "high": 10,
                "low": 10,
                "close": 10,
                "volume": 100,
            },
            {
                "time": "20260804093100",
                "open": 10.1,
                "high": 10.3,
                "low": 9.9,
                "close": 10.2,
                "volume": 200,
            },
            {
                "time": "20260804113000",
                "open": 10.2,
                "high": 10.4,
                "low": 10.1,
                "close": 10.3,
                "volume": 300,
            },
            {
                "time": "20260804130100",
                "open": 10.3,
                "high": 10.5,
                "low": 10.2,
                "close": 10.4,
                "volume": 400,
            },
            {
                "time": "20260804150000",
                "open": 10.5,
                "high": 10.5,
                "low": 10.5,
                "close": 10.5,
                "volume": 500,
            },
        ]

        rows = quantconnect_minute_rows(
            history_bars,
            date(2026, 8, 4),
        )

        self.assertEqual(
            [
                "34200000,100000,103000,99000,102000,300",
                "41340000,102000,104000,101000,103000,300",
                "46800000,103000,105000,102000,104000,400",
                "53940000,105000,105000,105000,105000,500",
            ],
            rows,
        )


if __name__ == "__main__":
    unittest.main()
