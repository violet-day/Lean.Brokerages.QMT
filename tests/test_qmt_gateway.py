import importlib
import json
import socket
import threading
import time
import unittest

from qmt_python import lean_qmt_gateway


class NativeRow:
    def __init__(self, **fields):
        for field_name, field_value in fields.items():
            setattr(self, field_name, field_value)


class FakeContextInfo:
    def __init__(self):
        self.account_id = None
        self.next_subscription_id = 71
        self.scheduled_callbacks = []
        self.subscriptions = {}
        self.unsubscribed_ids = []

    def set_account(self, account_id):
        self.account_id = account_id

    def run_time(self, function_name, period, start_time, market):
        self.scheduled_callbacks.append(
            (function_name, period, start_time, market)
        )

    def subscribe_quote(self, stock_code, period, dividend_type, callback):
        subscription_id = self.next_subscription_id
        self.next_subscription_id += 1
        self.subscriptions[subscription_id] = (
            stock_code,
            period,
            dividend_type,
            callback,
        )
        return subscription_id

    def unsubscribe_quote(self, subscription_id):
        self.unsubscribed_ids.append(subscription_id)
        self.subscriptions.pop(subscription_id, None)
        return True

    def get_market_data_ex(self, **kwargs):
        return {
            kwargs["stock_code"][0]: [
                {
                    "time": "20260813093100",
                    "open": 10.0,
                    "high": 10.2,
                    "low": 9.9,
                    "close": 10.1,
                    "volume": 1200,
                }
            ]
        }


def request_message(request_id, operation, payload=None):
    return {
        "protocol_version": 1,
        "message_type": "request",
        "request_id": request_id,
        "operation": operation,
        "success": None,
        "error_code": "",
        "error_message": "",
        "payload": payload or {},
    }


class QmtGatewayTests(unittest.TestCase):
    def setUp(self):
        self.gateway_module = importlib.reload(lean_qmt_gateway)
        self.context_info = FakeContextInfo()
        self.query_thread_identifiers = []
        self.query_rows = {
            "ACCOUNT": [NativeRow(m_dAvailable=12345.67)],
            "POSITION": [
                NativeRow(
                    m_strInstrumentID="600000.SH",
                    m_nVolume=200,
                    m_dOpenPrice=9.5,
                    m_dLastPrice=10.0,
                    m_dMarketValue=2000.0,
                )
            ],
            "ORDER": [
                NativeRow(
                    m_strInstrumentID="600000.SH",
                    m_strOrderSysID="native-1",
                    m_nOrderStatus=55,
                    m_nDirection=48,
                    m_nOrderPriceType=50,
                    m_nVolumeTotalOriginal=200,
                    m_nVolumeTraded=100,
                    m_dLimitPrice=9.8,
                    m_dTradedPrice=9.75,
                    m_strRemark="client-1",
                    m_strInsertDate="20260813",
                    m_strInsertTime="093100",
                )
            ],
        }

        def query_trade_detail(
            account_id,
            account_type,
            detail_type,
            strategy_name="",
        ):
            self.query_thread_identifiers.append(threading.get_ident())
            self.assertEqual(account_id, "test-account")
            self.assertEqual(account_type, "STOCK")
            return self.query_rows[detail_type]

        self.query_trade_detail = query_trade_detail
        self.history_downloads = []

        def down_history_data(stock_code, period, start_time, end_time):
            self.history_downloads.append(
                (stock_code, period, start_time, end_time)
            )

        self.gateway = self.gateway_module.LeanQmtGateway(
            context_info=self.context_info,
            account_id="test-account",
            get_trade_detail_data_function=self.query_trade_detail,
            down_history_data_function=down_history_data,
            get_market_data_function=self.context_info.get_market_data_ex,
            subscribe_quote_function=self.context_info.subscribe_quote,
            unsubscribe_quote_function=self.context_info.unsubscribe_quote,
            bind_port=0,
        )

    def tearDown(self):
        self.gateway.stop()

    def process_request(self, operation, payload=None, request_id="request-1"):
        response = self.gateway._process_request(
            request_message(request_id, operation, payload)
        )
        self.assertEqual(response["request_id"], request_id)
        self.assertEqual(response["operation"], operation)
        return response

    def test_hello_validates_account_and_reports_safety_state(self):
        response = self.process_request(
            "hello",
            {"account_id": "test-account"},
        )

        self.assertTrue(response["success"])
        self.assertEqual(response["payload"]["account_id"], "test-account")
        self.assertFalse(response["payload"]["trading_enabled"])

        mismatch_response = self.process_request(
            "hello",
            {"account_id": "wrong-account"},
            request_id="request-2",
        )
        self.assertFalse(mismatch_response["success"])
        self.assertEqual(mismatch_response["error_code"], "ACCOUNT_MISMATCH")

    def test_queries_normalize_confirmed_qmt_fields(self):
        account_response = self.process_request("query_account")
        positions_response = self.process_request(
            "query_positions",
            request_id="request-2",
        )
        orders_response = self.process_request(
            "query_orders",
            request_id="request-3",
        )

        self.assertEqual(
            account_response["payload"]["accounts"],
            [{"available_cash": 12345.67}],
        )
        self.assertEqual(
            positions_response["payload"]["positions"][0],
            {
                "stock_code": "600000.SH",
                "volume": 200.0,
                "open_price": 9.5,
                "last_price": 10.0,
                "market_value": 2000.0,
            },
        )
        order = orders_response["payload"]["orders"][0]
        self.assertEqual(order["order_id"], "native-1")
        self.assertEqual(order["client_order_id"], "client-1")
        self.assertEqual(order["direction"], "buy")
        self.assertEqual(order["order_type"], "limit")
        self.assertEqual(order["status"], 55)
        self.assertEqual(order["traded_volume"], 100.0)

    def test_query_history_downloads_and_normalizes_qmt_bars(self):
        response = self.process_request(
            "query_history",
            {
                "stock_code": "600000.SH",
                "period": "1m",
                "start_time": "20260813093000",
                "end_time": "20260813093500",
            },
        )

        self.assertTrue(response["success"])
        self.assertEqual(
            self.history_downloads,
            [
                (
                    "600000.SH",
                    "1m",
                    "20260813093000",
                    "20260813093500",
                )
            ],
        )
        self.assertEqual(
            response["payload"]["bars"],
            [
                {
                    "time": "20260813093100",
                    "open": 10.0,
                    "high": 10.2,
                    "low": 9.9,
                    "close": 10.1,
                    "volume": 1200.0,
                }
            ],
        )

    def test_trading_disabled_never_calls_native_functions(self):
        native_call_count = [0]

        def forbidden_native_call(*arguments):
            native_call_count[0] += 1
            raise AssertionError("A disabled gateway called a trading API")

        self.gateway.passorder_function = forbidden_native_call
        self.gateway.cancel_function = forbidden_native_call
        place_response = self.process_request(
            "place_order",
            {
                "client_order_id": "client-1",
                "stock_code": "600000.SH",
                "order_type": "limit",
                "direction": "buy",
                "quantity": 100,
                "limit_price": 9.8,
            },
        )
        cancel_response = self.process_request(
            "cancel_order",
            {"order_id": "native-1"},
            request_id="request-2",
        )

        self.assertEqual(native_call_count[0], 0)
        self.assertEqual(place_response["error_code"], "TRADING_DISABLED")
        self.assertEqual(cancel_response["error_code"], "TRADING_DISABLED")

    def test_enabled_trading_maps_place_and_cancel_arguments(self):
        passorder_calls = []
        cancel_calls = []

        def passorder(*arguments):
            passorder_calls.append(arguments)

        def cancel(*arguments):
            cancel_calls.append(arguments)
            return True

        self.gateway.trading_enabled = True
        self.gateway.passorder_function = passorder
        self.gateway.cancel_function = cancel

        place_response = self.process_request(
            "place_order",
            {
                "client_order_id": "client-7",
                "stock_code": "000001.SZ",
                "order_type": "limit",
                "direction": "sell",
                "quantity": 300,
                "limit_price": 11.25,
                "strategy_name": "lean",
            },
        )
        cancel_response = self.process_request(
            "cancel_order",
            {"order_id": "native-7"},
            request_id="request-2",
        )

        self.assertEqual(
            passorder_calls[0],
            (
                24,
                1101,
                "test-account",
                "000001.SZ",
                11,
                11.25,
                300,
                "lean",
                1,
                "client-7",
                self.context_info,
            ),
        )
        self.assertEqual(
            place_response["payload"],
            {
                "accepted": True,
                "client_order_id": "client-7",
                "native_order_id": "",
            },
        )
        self.assertEqual(
            cancel_calls[0],
            ("native-7", "test-account", "STOCK", self.context_info),
        )
        self.assertEqual(
            cancel_response["payload"],
            {"canceled": True, "order_id": "native-7"},
        )

    def test_subscription_callback_and_unsubscribe_use_protocol_id(self):
        subscribe_response = self.process_request(
            "subscribe",
            {"stock_code": "000001.SZ"},
        )
        subscription_id = subscribe_response["payload"]["subscription_id"]
        native_subscription_id = int(subscription_id)
        quote_callback = self.context_info.subscriptions[
            native_subscription_id
        ][3]

        quote_callback(
            {
                "000001.SZ": {
                    1786584601123: {
                        "lastPrice": 10.25,
                        "volume": 1200,
                        "amount": 12300,
                        "bidPrice": [10.24, 10.23],
                        "askPrice": [10.25, 10.26],
                        "bidVol": [500, 400],
                        "askVol": [300, 200],
                    }
                }
            }
        )
        outgoing_target, quote_event = self.gateway.get_queued_outgoing_message()

        self.assertIsNone(outgoing_target)
        self.assertEqual(quote_event["operation"], "quote")
        self.assertEqual(
            quote_event["payload"],
            {
                "stock_code": "000001.SZ",
                "time": "1786584601123",
                "last_price": 10.25,
                "volume": 1200.0,
                "amount": 12300.0,
                "bid_price": 10.24,
                "ask_price": 10.25,
                "bid_volume": 500.0,
                "ask_volume": 300.0,
            },
        )

        unsubscribe_response = self.process_request(
            "unsubscribe",
            {"subscription_id": subscription_id},
            request_id="request-2",
        )
        self.assertEqual(
            unsubscribe_response["payload"],
            {
                "unsubscribed": True,
                "subscription_id": subscription_id,
            },
        )
        self.assertEqual(
            self.context_info.unsubscribed_ids,
            [native_subscription_id],
        )

    def test_callbacks_emit_normalized_events(self):
        self.gateway.order_callback(self.query_rows["ORDER"][0])
        unused_target, order_event = self.gateway.get_queued_outgoing_message()
        self.assertEqual(order_event["operation"], "order")
        self.assertEqual(order_event["payload"]["client_order_id"], "client-1")

        self.gateway.deal_callback(
            NativeRow(
                m_strInstrumentID="600000.SH",
                m_strOrderSysID="native-1",
                m_strTradeID="deal-1",
                m_nDirection=49,
                m_dPrice=9.9,
                m_nVolume=100,
                m_dTradeAmount=990,
                m_dComssion=1.25,
                m_strRemark="client-1",
                m_strTradeDate="20260813",
                m_strTradeTime="093101",
            )
        )
        unused_target, deal_event = self.gateway.get_queued_outgoing_message()
        self.assertEqual(deal_event["operation"], "deal")
        self.assertEqual(deal_event["payload"]["direction"], "sell")
        self.assertEqual(deal_event["payload"]["commission"], 1.25)

    def test_duplicate_request_returns_cached_response_without_second_query(self):
        first_response = self.process_request("query_account")
        second_response = self.process_request("query_account")

        self.assertIs(first_response, second_response)
        self.assertEqual(len(self.query_thread_identifiers), 1)

    def test_socket_thread_only_queues_then_handlebar_executes_qmt_api(self):
        main_thread_identifier = threading.get_ident()
        self.gateway.start()
        client_socket = socket.create_connection(
            ("127.0.0.1", self.gateway.bound_port),
            timeout=2.0,
        )
        client_socket.settimeout(2.0)
        try:
            encoded_request = json.dumps(
                request_message("socket-request", "query_account")
            ).encode("utf-8") + b"\n"
            client_socket.sendall(encoded_request)

            request_deadline = time.time() + 2.0
            while self.gateway._incoming_messages.empty():
                if time.time() >= request_deadline:
                    self.fail("Socket reader did not queue the request")
                time.sleep(0.01)

            self.assertEqual(self.query_thread_identifiers, [])
            self.gateway.handlebar()
            self.assertEqual(
                self.query_thread_identifiers,
                [main_thread_identifier],
            )

            received_bytes = b""
            response_message = None
            response_deadline = time.time() + 2.0
            while response_message is None:
                if time.time() >= response_deadline:
                    self.fail("Socket writer did not return the response")
                received_bytes += client_socket.recv(65536)
                while b"\n" in received_bytes:
                    line_bytes, received_bytes = received_bytes.split(b"\n", 1)
                    decoded_message = json.loads(line_bytes.decode("utf-8"))
                    if decoded_message.get("request_id") == "socket-request":
                        response_message = decoded_message
                        break

            self.assertTrue(response_message["success"])
            self.assertEqual(
                response_message["payload"]["accounts"][0]["available_cash"],
                12345.67,
            )
        finally:
            client_socket.close()

    def test_init_registers_periodic_request_pump_with_handlebar_fallback(self):
        original_load_config = self.gateway_module._load_config
        original_start = self.gateway_module.LeanQmtGateway.start
        self.gateway_module._load_config = lambda: {
            "account_id": "",
            "bind_host": "127.0.0.1",
            "bind_port": 0,
            "trading_enabled": False,
            "strategy_name": "test-strategy",
        }
        self.gateway_module.LeanQmtGateway.start = lambda gateway: None
        try:
            initialized_gateway = self.gateway_module.init(
                self.context_info,
                get_trade_detail_data_function=self.query_trade_detail,
                injected_account_id="test-account",
            )
            self.assertEqual(
                self.context_info.scheduled_callbacks,
                [
                    (
                        "qmt_gateway_timer_callback",
                        "500nMilliSecond",
                        "2000-01-01 00:00:00",
                        "SH",
                    )
                ],
            )
            self.assertFalse(initialized_gateway.trading_enabled)

            initialized_gateway.enqueue_received_message(
                None,
                request_message("timer-request", "query_account"),
            )
            self.gateway_module.qmt_gateway_timer_callback(self.context_info)
            unused_target, timer_response = (
                initialized_gateway.get_queued_outgoing_message()
            )
            self.assertEqual(timer_response["request_id"], "timer-request")

            initialized_gateway.enqueue_received_message(
                None,
                request_message("handlebar-request", "query_account"),
            )
            self.gateway_module.handlebar(self.context_info)
            unused_target, handlebar_response = (
                initialized_gateway.get_queued_outgoing_message()
            )
            self.assertEqual(
                handlebar_response["request_id"],
                "handlebar-request",
            )
        finally:
            self.gateway_module.stop(self.context_info)
            self.gateway_module._load_config = original_load_config
            self.gateway_module.LeanQmtGateway.start = original_start

    def test_init_keeps_handlebar_fallback_when_timer_registration_fails(self):
        original_load_config = self.gateway_module._load_config
        original_start = self.gateway_module.LeanQmtGateway.start
        self.gateway_module._load_config = lambda: {
            "account_id": "",
            "bind_host": "127.0.0.1",
            "bind_port": 0,
            "trading_enabled": False,
            "strategy_name": "test-strategy",
        }
        self.gateway_module.LeanQmtGateway.start = lambda gateway: None

        def failing_run_time(*arguments):
            raise RuntimeError("timer unavailable")

        self.context_info.run_time = failing_run_time
        try:
            initialized_gateway = self.gateway_module.init(
                self.context_info,
                get_trade_detail_data_function=self.query_trade_detail,
                injected_account_id="test-account",
            )
            initialized_gateway.enqueue_received_message(
                None,
                request_message("fallback-request", "query_account"),
            )
            self.gateway_module.handlebar(self.context_info)
            unused_target, response = (
                initialized_gateway.get_queued_outgoing_message()
            )
            self.assertEqual(response["request_id"], "fallback-request")
            self.assertTrue(response["success"])
        finally:
            self.gateway_module.stop(self.context_info)
            self.gateway_module._load_config = original_load_config
            self.gateway_module.LeanQmtGateway.start = original_start


if __name__ == "__main__":
    unittest.main()
