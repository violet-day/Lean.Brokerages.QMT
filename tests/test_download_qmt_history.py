import sys
import unittest
from pathlib import Path


SCRIPTS_DIRECTORY = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS_DIRECTORY))

from download_qmt_history import history_bars_with_share_volume


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


if __name__ == "__main__":
    unittest.main()
