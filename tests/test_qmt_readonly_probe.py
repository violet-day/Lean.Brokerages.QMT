import importlib
import io
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


if __name__ == "__main__":
    unittest.main()
