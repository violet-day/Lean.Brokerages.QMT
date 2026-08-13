import importlib
import io
import os
import tempfile
import unittest
from contextlib import redirect_stdout

from qmt_python import lean_qmt_readonly_probe


class FakeContextInfo:
    def __init__(self):
        self.account_id = None
        self.subscription = None

    def set_account(self, account_id):
        self.account_id = account_id

    def get_instrumentdetail(self, stock_code):
        return {"InstrumentName": "TEST"}

    def get_market_data_ex(self, **kwargs):
        return {kwargs["stock_code"][0]: [{"close": 10.0}]}

    def subscribe_quote(self, stock_code, period, dividend_type, callback):
        self.subscription = (stock_code, period, dividend_type, callback)
        return 7


class ReadonlyProbeTests(unittest.TestCase):
    def setUp(self):
        self.probe = importlib.reload(lean_qmt_readonly_probe)

    def test_init_runs_read_only_queries_and_subscription(self):
        context_info = FakeContextInfo()
        query_types = []

        def query_trade_detail(account_id, account_type, detail_type, strategy=""):
            query_types.append(detail_type)
            return []

        output = io.StringIO()
        with redirect_stdout(output):
            self.probe.init(
                context_info,
                get_trade_detail_data_function=query_trade_detail,
                injected_account_id="test-account",
            )

        self.assertEqual(context_info.account_id, "test-account")
        self.assertEqual(query_types, ["ACCOUNT", "POSITION", "ORDER", "DEAL"])
        self.assertEqual(context_info.subscription[:3], ("000001.SZ", "tick", "none"))
        self.assertIn("query_ok", output.getvalue())
        self.assertIn("init_complete", output.getvalue())

    def test_missing_account_stops_before_queries(self):
        context_info = FakeContextInfo()
        query_count = [0]

        def query_trade_detail(*args):
            query_count[0] += 1
            return []

        output = io.StringIO()
        with redirect_stdout(output):
            self.probe.init(
                context_info,
                get_trade_detail_data_function=query_trade_detail,
                injected_account_id="",
            )

        self.assertIsNone(context_info.account_id)
        self.assertEqual(query_count[0], 0)
        self.assertIn("account_missing", output.getvalue())

    def test_load_config_reads_latest_file_without_importlib(self):
        original_module_file = self.probe.__file__

        with tempfile.TemporaryDirectory() as temporary_directory:
            self.probe.__file__ = os.path.join(
                temporary_directory,
                "lean_qmt_readonly_probe.py",
            )
            config_path = os.path.join(
                temporary_directory,
                "qmt_local_config.py",
            )

            with open(config_path, "w") as config_file:
                config_file.write(
                    'ACCOUNT_ID = "first"\n'
                    'PROBE_STOCK_CODE = "600000.SH"\n'
                    "SUBSCRIBE_TICKS = False\n"
                )
            self.assertEqual(
                self.probe._load_config(),
                ("first", "600000.SH", False),
            )

            with open(config_path, "w") as config_file:
                config_file.write(
                    'ACCOUNT_ID = "second-account"\n'
                    'PROBE_STOCK_CODE = "000001.SZ"\n'
                    "SUBSCRIBE_TICKS = True\n"
                )
            self.assertEqual(
                self.probe._load_config(),
                ("second-account", "000001.SZ", True),
            )

        self.probe.__file__ = original_module_file


if __name__ == "__main__":
    unittest.main()
