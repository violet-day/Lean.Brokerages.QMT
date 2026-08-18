import importlib.util
import os
import tempfile
import unittest
from pathlib import Path


class NativeOrder:
    m_strInstrumentID = "600000.SH"
    m_strOrderSysID = "native-order-1"
    m_strRemark = "42"
    m_nDirection = 48
    m_nOrderPriceType = 50
    m_nOrderStatus = 57
    m_nVolumeTotalOriginal = 100
    m_nVolumeTraded = 0
    m_dLimitPrice = 10.5
    m_dTradedPrice = 0
    m_nOrderSubmitStatus = 52
    m_nErrorID = 1001
    m_strErrorMsg = "price outside limit"
    m_strCancelInfo = "counter rejected order"
    m_strInsertDate = "20260817"
    m_strInsertTime = "093001"


class QmtGatewayOrderStatusTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"
        cls.temporary_directory = tempfile.TemporaryDirectory()
        runtime_log_path = str(
            Path(cls.temporary_directory.name) / "qmt-gateway-order-status.log"
        )
        previous_runtime_log_path = os.environ.get(
            "QMT_GATEWAY_RUNTIME_LOG_PATH"
        )
        os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = runtime_log_path
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_order_status_test",
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

    def test_normalizes_order_status_and_rejection_fields(self):
        normalized_order = self.gateway_module._normalize_order(NativeOrder())

        self.assertEqual(57, normalized_order["status"])
        self.assertEqual(52, normalized_order["submit_status"])
        self.assertEqual(1001, normalized_order["error_id"])
        self.assertEqual("price outside limit", normalized_order["error_message"])
        self.assertEqual(
            "counter rejected order",
            normalized_order["cancel_information"],
        )

    def test_uses_client_order_id_as_passorder_user_order_id(self):
        passorder_arguments = []

        def record_passorder_arguments(*arguments):
            passorder_arguments.append(arguments)

        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="order-status-test",
            passorder_function=record_passorder_arguments,
        )

        response = gateway._place_order(
            {
                "client_order_id": "42",
                "stock_code": "600000.SH",
                "order_type": "limit",
                "direction": "buy",
                "quantity": 100,
                "limit_price": 10.5,
            }
        )

        self.assertTrue(response["accepted"])
        self.assertEqual("42", response["client_order_id"])
        self.assertEqual(1, len(passorder_arguments))
        self.assertEqual("42", passorder_arguments[0][9])

    def test_maps_market_order_styles_to_qmt_price_types(self):
        test_cases = (
            ("600000.SH", "latest-price", 5, -1.0),
            ("000001.SZ", "latest-price", 5, -1.0),
            ("830799.BJ", "latest-price", 5, -1.0),
            ("600000.SH", "five-level-immediate-or-cancel", 42, 0.0),
            ("000001.SZ", "five-level-immediate-or-cancel", 47, 0.0),
            ("830799.BJ", "five-level-immediate-or-cancel", 42, 0.0),
            ("600000.SH", "five-level-immediate-to-limit", 43, 0.0),
            ("830799.BJ", "five-level-immediate-to-limit", 43, 0.0),
            ("600000.SH", "counterparty-best", 44, 0.0),
            ("000001.SZ", "counterparty-best", 44, 0.0),
            ("830799.BJ", "counterparty-best", 44, 0.0),
            ("600000.SH", "own-best", 45, 0.0),
            ("000001.SZ", "own-best", 45, 0.0),
            ("830799.BJ", "own-best", 45, 0.0),
            ("000001.SZ", "immediate-or-cancel", 46, 0.0),
            ("000001.SZ", "fill-or-kill", 48, 0.0),
        )

        for stock_code, market_order_style, price_type, price in test_cases:
            with self.subTest(
                stock_code=stock_code,
                market_order_style=market_order_style,
            ):
                passorder_arguments = []

                def record_passorder_arguments(*arguments):
                    passorder_arguments.append(arguments)

                gateway = self.gateway_module.LeanQmtGateway(
                    context_info=object(),
                    account_id="market-order-test",
                    passorder_function=record_passorder_arguments,
                )
                response = gateway._place_order(
                    {
                        "client_order_id": "43",
                        "stock_code": stock_code,
                        "order_type": "market",
                        "direction": "buy",
                        "quantity": 100,
                        "market_order_style": market_order_style,
                        "qmt_price_type": price_type,
                        "qmt_price": price,
                    }
                )

                self.assertTrue(response["accepted"])
                self.assertEqual(1, len(passorder_arguments))
                self.assertEqual(price_type, passorder_arguments[0][4])
                self.assertEqual(price, passorder_arguments[0][5])

    def test_rejects_market_order_style_unsupported_by_exchange(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="market-order-test",
            passorder_function=lambda *arguments: None,
        )

        with self.assertRaises(self.gateway_module._RequestError) as error:
            gateway._place_order(
                {
                    "client_order_id": "44",
                    "stock_code": "600000.SH",
                    "order_type": "market",
                    "direction": "buy",
                    "quantity": 100,
                    "market_order_style": "fill-or-kill",
                    "qmt_price_type": 48,
                    "qmt_price": 0,
                }
            )

        self.assertEqual(
            "UNSUPPORTED_MARKET_ORDER_STYLE",
            error.exception.error_code,
        )

    def test_rejects_market_order_values_that_do_not_match_style(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="market-order-test",
            passorder_function=lambda *arguments: None,
        )

        with self.assertRaises(self.gateway_module._RequestError) as error:
            gateway._place_order(
                {
                    "client_order_id": "45",
                    "stock_code": "600000.SH",
                    "order_type": "market",
                    "direction": "buy",
                    "quantity": 100,
                    "market_order_style": "five-level-immediate-or-cancel",
                    "qmt_price_type": 5,
                    "qmt_price": -1,
                }
            )

        self.assertEqual("INVALID_REQUEST", error.exception.error_code)

    def test_order_error_callback_preserves_structured_rejection(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=None,
            account_id="order-status-test",
            bind_port=0,
        )

        gateway.order_error_callback(NativeOrder(), "callback rejection")

        _, event_message = gateway.get_queued_outgoing_message()
        self.assertEqual("order", event_message["operation"])
        self.assertEqual(57, event_message["payload"]["status"])
        self.assertEqual(52, event_message["payload"]["submit_status"])
        self.assertEqual(1001, event_message["payload"]["error_id"])
        self.assertEqual(
            "callback rejection",
            event_message["payload"]["error_message"],
        )


if __name__ == "__main__":
    unittest.main()
