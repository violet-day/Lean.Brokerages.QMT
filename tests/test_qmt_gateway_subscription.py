import gc
import importlib.util
import inspect
import os
import queue
import tempfile
import unittest
from unittest.mock import Mock
import weakref
from pathlib import Path


class QmtGatewaySubscriptionTests(unittest.TestCase):
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
            "lean_qmt_gateway_subscription_test",
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

    def test_retains_native_quote_callback_until_unsubscribe(self):
        callback_reference = None

        def subscribe_quote(*arguments):
            nonlocal callback_reference
            callback_reference = weakref.ref(arguments[3])
            return 42

        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="subscription-test",
            subscribe_quote_function=subscribe_quote,
            unsubscribe_quote_function=lambda _: True,
        )

        response = gateway._subscribe({"stock_code": "600000.SH"})
        gc.collect()

        self.assertEqual("42", response["subscription_id"])
        self.assertIsNotNone(callback_reference())
        callback_reference()(
            {
                "time": "20260824144500",
                "lastPrice": 10.5,
                "volume": 1000,
                "bidPrice": [10.49],
                "askPrice": [10.51],
                "bidVol": [100],
                "askVol": [200],
            }
        )
        _, event = gateway.get_queued_outgoing_message()
        self.assertEqual("quote", event["operation"])
        self.assertEqual(10.5, event["payload"]["last_price"])

        self.assertTrue(gateway._unsubscribe("42"))
        gc.collect()
        self.assertIsNone(callback_reference())

    def test_accepts_installed_entry_history_query_argument(self):
        self.assertIn(
            "get_history_trade_detail_data_function",
            inspect.signature(self.gateway_module.init).parameters,
        )

    def test_polls_current_full_tick_when_native_callback_is_silent(self):
        current_last_price = [10.5]
        current_volume = [1000]

        def get_full_tick(stock_codes):
            return {
                stock_code: {
                    "time": "20260824144500",
                    "lastPrice": current_last_price[0],
                    "volume": current_volume[0],
                    "bidPrice": [10.49],
                    "askPrice": [10.51],
                    "bidVol": [100],
                    "askVol": [200],
                }
                for stock_code in stock_codes
            }

        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="subscription-test",
            get_full_tick_function=get_full_tick,
            subscribe_quote_function=lambda *arguments: arguments[0],
            unsubscribe_quote_function=lambda _: True,
        )
        gateway._subscribe({"stock_code": "600000.SH"})
        gateway._next_quote_poll_at = 0

        gateway._poll_quote_snapshots_if_due()

        _, event = gateway.get_queued_outgoing_message()
        self.assertEqual("quote", event["operation"])
        self.assertEqual("600000.SH", event["payload"]["stock_code"])
        self.assertEqual(10.5, event["payload"]["last_price"])
        self.assertRegex(event["payload"]["time"], r"^\d{14}$")

        gateway._next_quote_poll_at = 0
        gateway._poll_quote_snapshots_if_due()
        with self.assertRaises(queue.Empty):
            gateway.get_queued_outgoing_message()

        current_last_price[0] = 10.6
        current_volume[0] = 1200
        gateway._next_quote_poll_at = 0
        gateway._poll_quote_snapshots_if_due()
        _, changed_event = gateway.get_queued_outgoing_message()
        self.assertEqual(10.6, changed_event["payload"]["last_price"])
        self.assertEqual(120_000, changed_event["payload"]["volume"])
        self.assertEqual("shares", changed_event["payload"]["volume_unit"])

    def test_replays_latest_snapshot_when_a_new_client_reuses_subscription(self):
        native_subscribe_calls = []

        def get_full_tick(stock_codes):
            return {
                stock_code: {
                    "time": "20260824144500",
                    "lastPrice": 10.5,
                    "volume": 1000,
                }
                for stock_code in stock_codes
            }

        def subscribe_quote(*arguments):
            native_subscribe_calls.append(arguments[0])
            return 42

        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="subscription-test",
            get_full_tick_function=get_full_tick,
            subscribe_quote_function=subscribe_quote,
            unsubscribe_quote_function=lambda _: True,
        )
        first_response = gateway._subscribe({"stock_code": "600000.SH"})
        gateway._next_quote_poll_at = 0
        gateway._poll_quote_snapshots_if_due()
        gateway.get_queued_outgoing_message()
        gateway._next_quote_poll_at = float("inf")

        reused_response = gateway._subscribe({"stock_code": "600000.SH"})

        self.assertEqual(first_response, reused_response)
        self.assertEqual(["600000.SH"], native_subscribe_calls)
        self.assertNotIn(
            "600000.SH",
            gateway._last_polled_quote_signature_by_stock_code,
        )
        self.assertLess(gateway._next_quote_poll_at, float("inf"))

        gateway._next_quote_poll_at = 0
        gateway._poll_quote_snapshots_if_due()
        _, replayed_event = gateway.get_queued_outgoing_message()
        self.assertEqual("quote", replayed_event["operation"])
        self.assertEqual("600000.SH", replayed_event["payload"]["stock_code"])
        self.assertEqual(10.5, replayed_event["payload"]["last_price"])

    def test_does_not_publish_daily_history_as_a_realtime_quote(self):
        daily_history_query = Mock()
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="subscription-test",
            get_market_data_function=daily_history_query,
            subscribe_quote_function=lambda *arguments: arguments[0],
            unsubscribe_quote_function=lambda _: True,
        )
        gateway._subscribe({"stock_code": "600000.SH"})

        gateway._poll_quote_snapshots_if_due()

        daily_history_query.assert_not_called()
        with self.assertRaises(queue.Empty):
            gateway.get_queued_outgoing_message()

    def test_prioritizes_response_over_quote_event_backlog(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="subscription-test",
        )
        for stock_code_number in range(300):
            gateway._publish_event(
                "quote",
                {
                    "stock_code": "%06d.SZ" % stock_code_number,
                    "last_price": 10.5,
                },
            )
        client_socket = object()
        response_message = gateway._process_request(
            {
                "protocol_version": 1,
                "message_type": "request",
                "request_id": "response-priority-test",
                "operation": "hello",
                "payload": {"account_id": "subscription-test"},
            }
        )
        gateway._queue_response(client_socket, response_message)

        queued_target_socket, queued_message = (
            gateway.get_queued_outgoing_message()
        )
        self.assertIs(client_socket, queued_target_socket)
        self.assertEqual("response", queued_message["message_type"])
        self.assertEqual(
            "response-priority-test",
            queued_message["request_id"],
        )

    def test_only_event_enabled_clients_receive_gateway_events(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="subscription-test",
        )
        event_client_socket = object()
        query_client_socket = object()

        gateway._process_request(
            {
                "protocol_version": 1,
                "message_type": "request",
                "request_id": "event-client-hello",
                "operation": "hello",
                "payload": {
                    "account_id": "subscription-test",
                    "receive_events": True,
                },
            },
            event_client_socket,
        )
        gateway._process_request(
            {
                "protocol_version": 1,
                "message_type": "request",
                "request_id": "query-client-hello",
                "operation": "hello",
                "payload": {"account_id": "subscription-test"},
            },
            query_client_socket,
        )

        self.assertEqual({event_client_socket}, gateway._event_client_sockets)
        gateway._remove_client(event_client_socket)
        self.assertEqual(set(), gateway._event_client_sockets)

    def test_rejects_non_boolean_event_delivery_request(self):
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="subscription-test",
        )

        response = gateway._process_request(
            {
                "protocol_version": 1,
                "message_type": "request",
                "request_id": "invalid-event-client-hello",
                "operation": "hello",
                "payload": {
                    "account_id": "subscription-test",
                    "receive_events": "true",
                },
            }
        )

        self.assertFalse(response["success"])
        self.assertEqual("INVALID_REQUEST", response["error_code"])


if __name__ == "__main__":
    unittest.main()
