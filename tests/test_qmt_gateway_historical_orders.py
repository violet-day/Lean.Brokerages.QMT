import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path


class NativeHistoricalOrder:
    m_strExchangeID = "SZ"
    m_strInstrumentID = "000001"
    m_strOrderSysID = "history-order-1"
    m_strRemark = "17"
    m_strStrategyName = "LeanQmtGateway"
    m_nDirection = 48
    m_nOrderPriceType = 49
    m_nOrderStatus = 56
    m_nVolumeTotalOriginal = 100
    m_nVolumeTraded = 100
    m_dLimitPrice = 0
    m_dTradedPrice = 11.23
    m_strInsertDate = "20260821"
    m_strInsertTime = "100102"


class HistoricalOrderContext:
    def __init__(self):
        self.calls = []

    def get_tradedatafromerds(
        self,
        account_type,
        account_id,
        start_date,
        end_date,
    ):
        self.calls.append((account_type, account_id, start_date, end_date))
        return [NativeHistoricalOrder()]


class IncompatibleHistoricalOrderContext:
    context = object()

    def get_tradedatafromerds(self, *arguments):
        raise AttributeError("underlying ContextInfo has no historical method")


class QmtGatewayHistoricalOrdersTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"
        cls.temporary_directory = tempfile.TemporaryDirectory()
        previous_runtime_log_path = os.environ.get("QMT_GATEWAY_RUNTIME_LOG_PATH")
        os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = str(
            Path(cls.temporary_directory.name) / "qmt-history.log"
        )
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_historical_orders_test",
            module_path,
        )
        cls.gateway_module = importlib.util.module_from_spec(module_specification)
        try:
            module_specification.loader.exec_module(cls.gateway_module)
        finally:
            if previous_runtime_log_path is None:
                del os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"]
            else:
                os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = previous_runtime_log_path

    @classmethod
    def tearDownClass(cls):
        cls.temporary_directory.cleanup()

    def test_queries_inclusive_date_range_and_normalizes_orders(self):
        context = HistoricalOrderContext()
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=context,
            account_id="account-1",
        )

        result = gateway._execute_operation(
            "query_historical_orders",
            {"start_date": "20260821", "end_date": "20260826"},
        )

        self.assertEqual(
            [("STOCK", "account-1", "20260821", "20260826")],
            context.calls,
        )
        self.assertEqual(1, len(result["orders"]))
        order = result["orders"][0]
        self.assertEqual("000001.SZ", order["stock_code"])
        self.assertEqual("buy", order["direction"])
        self.assertEqual(100, order["traded_volume"])
        self.assertEqual("LeanQmtGateway", order["strategy_name"])
        self.assertEqual("20260821 100102", order["time"])

    def test_rejects_invalid_or_reversed_dates(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=HistoricalOrderContext(),
            account_id="account-1",
        )

        for payload in (
            {"start_date": "2026-08-21", "end_date": "20260826"},
            {"start_date": "20260827", "end_date": "20260826"},
        ):
            with self.subTest(payload=payload):
                with self.assertRaises(self.gateway_module._RequestError) as context:
                    gateway._execute_operation("query_historical_orders", payload)
                self.assertEqual("INVALID_REQUEST", context.exception.error_code)

    def test_reports_incomplete_archive_when_qmt_history_api_is_unavailable(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
        )

        with self.assertRaises(self.gateway_module._RequestError) as context:
            gateway._execute_operation(
                "query_historical_orders",
                {"start_date": "20260821", "end_date": "20260826"},
            )

        self.assertEqual(
            "HISTORICAL_ARCHIVE_INCOMPLETE",
            context.exception.error_code,
        )

    def test_reads_complete_daily_archive_when_native_api_is_unavailable(self):
        archive_directory = Path(
            self.gateway_module.HISTORICAL_ORDER_ARCHIVE_DIRECTORY
        )
        archive_directory.mkdir(parents=True, exist_ok=True)
        for archive_date, orders in (
            ("20260824", []),
            (
                "20260825",
                [
                    {
                        "stock_code": "000001.SZ",
                        "order_id": "archived-1",
                        "client_order_id": "17",
                        "direction": "buy",
                        "order_type": "market",
                        "status": 56,
                        "original_volume": 100,
                        "traded_volume": 100,
                        "limit_price": 0,
                        "traded_price": 11.23,
                        "remark": "17",
                        "strategy_name": "LeanQmtGateway",
                        "time": "20260825 100102",
                    }
                ],
            ),
        ):
            archive_path = archive_directory / f"qmt-orders-{archive_date}.json"
            archive_path.write_text(
                json.dumps(
                    {
                        "account_id": "account-1",
                        "archive_date": archive_date,
                        "orders": orders,
                    }
                ),
                encoding="utf-8",
            )
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
        )

        result = gateway._execute_operation(
            "query_historical_orders",
            {"start_date": "20260824", "end_date": "20260825"},
        )

        self.assertEqual(["archived-1"], [order["order_id"] for order in result["orders"]])

    def test_rejects_account_mismatch(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=HistoricalOrderContext(),
            account_id="account-1",
        )

        with self.assertRaises(self.gateway_module._RequestError) as context:
            gateway._execute_operation(
                "query_historical_orders",
                {
                    "account_id": "different-account",
                    "start_date": "20260825",
                    "end_date": "20260825",
                },
            )

        self.assertEqual("ACCOUNT_MISMATCH", context.exception.error_code)

    def test_incompatible_native_api_uses_archive_error_instead_of_name_error(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=IncompatibleHistoricalOrderContext(),
            account_id="account-1",
        )

        with self.assertRaises(self.gateway_module._RequestError) as context:
            gateway._execute_operation(
                "query_historical_orders",
                {"start_date": "20260820", "end_date": "20260820"},
            )

        self.assertEqual(
            "HISTORICAL_ARCHIVE_INCOMPLETE",
            context.exception.error_code,
        )


if __name__ == "__main__":
    unittest.main()
