import importlib.util
import tempfile
import unittest
from pathlib import Path


class NativeAccount:
    m_strAccountID = "account-1"
    m_dAvailable = 12345.67
    m_dBalance = 23456.78
    m_strStatus = "connected"


class DifferentNativeAccount(NativeAccount):
    m_strAccountID = "different-account"


class TradeDetailQuery:
    def __init__(self, result):
        self.result = result
        self.calls = []

    def __call__(self, *arguments):
        self.calls.append(arguments)
        return self.result


class QmtGatewayAccountTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary_directory = tempfile.TemporaryDirectory()
        gateway_path = (
            Path(__file__).resolve().parents[1]
            / "qmt_python"
            / "lean_qmt_gateway.py"
        )
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_account_under_test",
            gateway_path,
        )
        cls.gateway_module = importlib.util.module_from_spec(
            module_specification
        )
        cls.gateway_module.module_directory = cls.temporary_directory.name
        module_specification.loader.exec_module(cls.gateway_module)

    @classmethod
    def tearDownClass(cls):
        cls.temporary_directory.cleanup()

    def test_query_account_returns_normalized_cash(self):
        trade_detail_query = TradeDetailQuery([NativeAccount()])
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
            get_trade_detail_data_function=trade_detail_query,
        )

        result = gateway._execute_operation("query_account", {})

        self.assertEqual(
            [("account-1", "STOCK", "ACCOUNT", "")],
            trade_detail_query.calls,
        )
        self.assertEqual(
            {
                "account_id": "account-1",
                "accounts": [{"available_cash": 12345.67}],
            },
            result,
        )

    def test_query_account_fails_closed_when_cash_cannot_be_determined(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
            get_trade_detail_data_function=TradeDetailQuery([]),
        )

        with self.assertRaises(self.gateway_module._RequestError) as context:
            gateway._execute_operation("query_account", {})

        self.assertEqual(
            "QMT_ACCOUNT_DATA_UNAVAILABLE",
            context.exception.error_code,
        )

    def test_query_account_rejects_returned_account_mismatch(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
            get_trade_detail_data_function=TradeDetailQuery(
                [DifferentNativeAccount()]
            ),
        )

        with self.assertRaises(self.gateway_module._RequestError) as context:
            gateway._execute_operation("query_account", {})

        self.assertEqual("ACCOUNT_MISMATCH", context.exception.error_code)


if __name__ == "__main__":
    unittest.main()
