"""Gateway integration contracts that do not require proprietary QMT.

These tests use the real TCP server, NDJSON protocol, and normalization. Only
the QMT-provided function is replaced. Real QMT API availability and account
data remain the responsibility of read-only E2E.
"""

import importlib.util
import json
import os
import socket
import tempfile
import time
import unittest
from pathlib import Path


ORDER_DATE = time.strftime("%Y%m%d", time.localtime())


class NativeAccount:
    m_strAccountID = "account-1"
    m_dAvailable = 12345.67
    m_dBalance = 23456.78
    m_strStatus = "connected"


class NativeOrder:
    m_strExchangeID = "SZ"
    m_strInstrumentID = "000001"
    m_strOrderSysID = "broker-order-1"
    m_strRemark = "lean-order-17"
    m_strStrategyName = "LeanQmtGateway"
    m_nDirection = 48
    m_nOrderPriceType = 49
    m_nOrderStatus = 48
    m_nVolumeTotalOriginal = 100
    m_nVolumeTraded = 0
    m_dLimitPrice = 0
    m_dTradedPrice = 11.23
    m_strInsertDate = ORDER_DATE
    m_strInsertTime = "100102"


class NativeTradeDetailQuery:
    def __init__(self):
        self.calls = []

    def __call__(self, account_id, account_type, detail_type, strategy_name=""):
        self.calls.append(
            (account_id, account_type, detail_type, strategy_name)
        )
        if detail_type == "ACCOUNT":
            return [NativeAccount()]
        if detail_type == "ORDER":
            return [NativeOrder()]
        return []


class GatewaySocketClient:
    def __init__(self, gateway):
        self.gateway = gateway
        self.socket = socket.create_connection(
            (gateway.bind_host, gateway.bound_port),
            timeout=1,
        )
        self.socket.settimeout(0.02)
        self.received_bytes = b""
        self.next_request_number = 1

    def close(self):
        self.socket.close()

    def request(self, operation, payload):
        request_id = "contract-%d" % self.next_request_number
        self.next_request_number += 1
        request_message = {
            "protocol_version": 1,
            "message_type": "request",
            "request_id": request_id,
            "operation": operation,
            "payload": payload,
        }
        self.socket.sendall(
            (json.dumps(request_message, separators=(",", ":")) + "\n").encode(
                "utf-8"
            )
        )

        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            self.gateway.handlebar()
            try:
                self.received_bytes += self.socket.recv(65536)
            except socket.timeout:
                continue
            while b"\n" in self.received_bytes:
                line_bytes, self.received_bytes = self.received_bytes.split(
                    b"\n",
                    1,
                )
                if not line_bytes.strip():
                    continue
                response_message = json.loads(line_bytes.decode("utf-8"))
                if response_message.get("message_type") == "event":
                    continue
                if response_message.get("request_id") == request_id:
                    return response_message
        self.fail("Gateway did not return %s within two seconds." % operation)

    def fail(self, message):
        raise AssertionError(message)


class QmtGatewayContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary_directory = tempfile.TemporaryDirectory()
        gateway_path = (
            Path(__file__).resolve().parents[1]
            / "qmt_python"
            / "lean_qmt_gateway.py"
        )
        previous_runtime_log_path = os.environ.get(
            "QMT_GATEWAY_RUNTIME_LOG_PATH"
        )
        os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = str(
            Path(cls.temporary_directory.name) / "qmt-contract.log"
        )
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_contract_under_test",
            gateway_path,
        )
        cls.gateway_module = importlib.util.module_from_spec(
            module_specification
        )
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

    def test_native_account_and_order_queries_survive_tcp_json(self):
        native_trade_detail_query = NativeTradeDetailQuery()
        first_gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
            get_trade_detail_data_function=native_trade_detail_query,
            bind_host="127.0.0.1",
            bind_port=0,
        )
        first_gateway.start()
        first_client = GatewaySocketClient(first_gateway)
        try:
            hello_response = first_client.request(
                "hello",
                {"account_id": ""},
            )
            account_response = first_client.request("query_account", {})
            queried_account_id = account_response["payload"]["account_id"]
            first_order_response = first_client.request(
                "query_orders",
                {"account_id": queried_account_id},
            )
            second_order_response = first_client.request(
                "query_orders",
                {"account_id": queried_account_id},
            )
        finally:
            first_client.close()
            first_gateway.stop()

        self.assertTrue(hello_response["success"])
        self.assertEqual("account-1", hello_response["payload"]["account_id"])
        self.assertTrue(account_response["success"])
        self.assertEqual("account-1", queried_account_id)
        self.assertEqual(
            [{"available_cash": 12345.67}],
            account_response["payload"]["accounts"],
        )
        self.assertTrue(first_order_response["success"])
        self.assertTrue(second_order_response["success"])
        self.assertEqual(
            [
                ("account-1", "STOCK", "ACCOUNT", ""),
                ("account-1", "STOCK", "ORDER", ""),
                ("account-1", "STOCK", "ORDER", ""),
            ],
            native_trade_detail_query.calls,
        )

        submitted_order = second_order_response["payload"]["orders"][0]
        self.assertEqual(
            {
                "stock_code": "000001.SZ",
                "order_id": "broker-order-1",
                "client_order_id": "lean-order-17",
                "direction": "buy",
                "order_type": "market",
                "status": 48,
                "original_volume": 100.0,
                "traded_volume": 0.0,
                "limit_price": 0.0,
                "traded_price": 11.23,
                "submit_status": -1,
                "error_id": 0,
                "error_message": "",
                "cancel_information": "",
                "remark": "lean-order-17",
                "strategy_name": "LeanQmtGateway",
                "time": "%s 100102" % ORDER_DATE,
            },
            submitted_order,
        )

    def test_unknown_cash_balance_is_a_protocol_failure(self):
        empty_account_query = lambda *arguments: []
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
            get_trade_detail_data_function=empty_account_query,
            bind_host="127.0.0.1",
            bind_port=0,
        )
        gateway.start()
        client = GatewaySocketClient(gateway)
        try:
            response = client.request("query_account", {})
        finally:
            client.close()
            gateway.stop()

        self.assertFalse(response["success"])
        self.assertEqual(
            "QMT_ACCOUNT_DATA_UNAVAILABLE",
            response["error_code"],
        )
        self.assertEqual({}, response["payload"])

    def test_current_order_query_rejects_different_account(self):
        native_trade_detail_query = NativeTradeDetailQuery()
        gateway = self.gateway_module.LeanQmtGateway(
            context_info=object(),
            account_id="account-1",
            get_trade_detail_data_function=native_trade_detail_query,
            bind_host="127.0.0.1",
            bind_port=0,
        )
        gateway.start()
        client = GatewaySocketClient(gateway)
        try:
            response = client.request(
                "query_orders",
                {"account_id": "different-account"},
            )
        finally:
            client.close()
            gateway.stop()

        self.assertFalse(response["success"])
        self.assertEqual("ACCOUNT_MISMATCH", response["error_code"])
        self.assertEqual([], native_trade_detail_query.calls)


if __name__ == "__main__":
    unittest.main()
