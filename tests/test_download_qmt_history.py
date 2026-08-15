import tempfile
import unittest
import zipfile
from datetime import date
from pathlib import Path

from scripts.download_qmt_history import (
    normalize_ticker,
    parse_trading_date,
    quantconnect_minute_rows,
    write_quantconnect_minute_zip,
)


class DownloadQmtHistoryTests(unittest.TestCase):
    def test_writes_quantconnect_minute_zip(self):
        trading_date = date(2026, 8, 14)
        history_bars = [
            {
                "time": "20260814093000",
                "open": 10.01,
                "high": 10.125,
                "low": 9.9,
                "close": 10.005,
                "volume": 1000.0,
            },
            {
                "time": "2026-08-14 09:31:00",
                "open": 10.005,
                "high": 10.02,
                "low": 10.0,
                "close": 10.01,
                "volume": 250,
            },
            {
                "time": "20260815093000",
                "open": 11,
                "high": 11,
                "low": 11,
                "close": 11,
                "volume": 1,
            },
        ]
        quantconnect_rows = quantconnect_minute_rows(history_bars, trading_date)

        with tempfile.TemporaryDirectory() as temporary_directory:
            target_zip_path = write_quantconnect_minute_zip(
                quantconnect_rows,
                "600000.SH",
                trading_date,
                Path(temporary_directory),
            )

            expected_zip_path = (
                Path(temporary_directory)
                / "equity"
                / "china"
                / "minute"
                / "600000"
                / "20260814_trade.zip"
            )
            self.assertEqual(target_zip_path, expected_zip_path)
            with zipfile.ZipFile(target_zip_path) as history_zip_file:
                self.assertEqual(
                    history_zip_file.namelist(),
                    ["20260814_600000_trade.csv"],
                )
                csv_lines = history_zip_file.read(
                    "20260814_600000_trade.csv"
                ).decode("utf-8").splitlines()
            self.assertEqual(
                csv_lines,
                [
                    "34200000,100100,101250,99000,100050,1000",
                    "34260000,100050,100200,100000,100100,250",
                ],
            )

    def test_normalizes_inputs(self):
        self.assertEqual(normalize_ticker(" 600000.sh "), "600000.SH")
        self.assertEqual(parse_trading_date("2026-08-14"), date(2026, 8, 14))
        with self.assertRaises(ValueError):
            normalize_ticker("600000")


if __name__ == "__main__":
    unittest.main()
