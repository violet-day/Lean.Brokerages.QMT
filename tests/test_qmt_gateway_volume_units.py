import importlib.util
import os
import tempfile
import unittest
from pathlib import Path


class QmtGatewayVolumeUnitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"
        cls.temporary_directory = tempfile.TemporaryDirectory()
        previous_runtime_log_path = os.environ.get("QMT_GATEWAY_RUNTIME_LOG_PATH")
        os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = str(
            Path(cls.temporary_directory.name) / "gateway.log"
        )
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_volume_unit_test",
            module_path,
        )
        cls.gateway_module = importlib.util.module_from_spec(module_specification)
        try:
            module_specification.loader.exec_module(cls.gateway_module)
        finally:
            if previous_runtime_log_path is None:
                del os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"]
            else:
                os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = (
                    previous_runtime_log_path
                )

    @classmethod
    def tearDownClass(cls):
        cls.temporary_directory.cleanup()

    def test_quote_prefers_original_volume_in_shares(self):
        quote = self.gateway_module._normalize_quote(
            "600000.SH",
            {
                "lastPrice": 10,
                "volume": 1_234,
                "pvolume": 123_400,
            },
        )

        self.assertEqual(123_400, quote["volume"])
        self.assertEqual("shares", quote["volume_unit"])

    def test_quote_converts_lot_volume_when_original_volume_is_missing(self):
        quote = self.gateway_module._normalize_quote(
            "600000.SH",
            {"lastPrice": 10, "volume": 1_234},
        )

        self.assertEqual(123_400, quote["volume"])
        self.assertEqual("shares", quote["volume_unit"])

    def test_history_keeps_share_volume_when_turnover_matches(self):
        history_bar, source_volume_unit = (
            self.gateway_module._normalize_history_bar(
                {
                    "stime": "20260924000000",
                    "open": 10,
                    "high": 10,
                    "low": 10,
                    "close": 10,
                    "volume": 123_400,
                    "amount": 1_234_000,
                }
            )
        )

        self.assertEqual(123_400, history_bar["volume"])
        self.assertEqual("shares", source_volume_unit)

    def test_history_converts_lot_volume_when_turnover_matches(self):
        history_bar, source_volume_unit = (
            self.gateway_module._normalize_history_bar(
                {
                    "stime": "20260924000000",
                    "open": 10,
                    "high": 10,
                    "low": 10,
                    "close": 10,
                    "volume": 1_234,
                    "amount": 1_234_000,
                }
            )
        )

        self.assertEqual(123_400, history_bar["volume"])
        self.assertEqual("lots", source_volume_unit)

    def test_history_uses_declared_fallback_unit_without_turnover(self):
        history_bar, source_volume_unit = (
            self.gateway_module._normalize_history_bar(
                {
                    "stime": "20260924000000",
                    "open": 10,
                    "high": 10,
                    "low": 10,
                    "close": 10,
                    "volume": 1_234,
                },
                volume_unit_without_amount="lots",
            )
        )

        self.assertEqual(123_400, history_bar["volume"])
        self.assertEqual("lots_unverified", source_volume_unit)

    def test_tick_history_downloads_and_returns_time_ordered_ticks(self):
        download_calls = []
        market_data_calls = []

        def download_history(*arguments):
            download_calls.append(arguments)

        def get_market_data(**arguments):
            market_data_calls.append(arguments)
            return {
                "600000.SH": [
                    {
                        "time": "20260930093003",
                        "lastPrice": 10.01,
                        "volume": 124,
                        "pvolume": 12_400,
                        "amount": 124_124,
                    },
                    {
                        "time": "20260930093000",
                        "lastPrice": 10,
                        "volume": 123,
                        "pvolume": 12_300,
                        "amount": 123_000,
                    },
                ]
            }

        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="history-test",
            down_history_data_function=download_history,
            get_market_data_function=get_market_data,
        )

        response = gateway._query_history(
            {
                "stock_code": "600000.SH",
                "period": "tick",
                "start_time": "20260930",
                "end_time": "20260930",
            }
        )

        self.assertEqual(
            [("600000.SH", "tick", "20260930", "20260930")],
            download_calls,
        )
        self.assertFalse(market_data_calls[0]["fill_data"])
        self.assertEqual("shares", response["volume_unit"])
        self.assertEqual(
            ["20260930093000", "20260930093003"],
            [history_tick["time"] for history_tick in response["ticks"]],
        )
        self.assertEqual(12_300, response["ticks"][0]["volume"])


if __name__ == "__main__":
    unittest.main()
