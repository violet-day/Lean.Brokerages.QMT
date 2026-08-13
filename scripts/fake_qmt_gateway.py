#!/usr/bin/env python3

import argparse
import datetime
import json
import socketserver
import sys
import threading
import time


PROTOCOL_VERSION = 1


def write_log(message):
    sys.stderr.write("[qmt-fake-gateway] {0}\n".format(message))
    sys.stderr.flush()


class FakeGatewayRequestHandler(socketserver.StreamRequestHandler):
    def handle(self):
        client_address = self.client_address[0]
        write_log("stage=connection status=accepted client={0}".format(client_address))
        while True:
            request_line = self.rfile.readline()
            if not request_line:
                write_log("stage=connection status=closed client={0}".format(client_address))
                return

            request = json.loads(request_line.decode("utf-8"))
            operation = request.get("operation", "")
            request_id = request.get("request_id")
            write_log("stage=request status=received operation={0} request_id={1}".format(
                operation,
                request_id))

            payload = self.server.build_payload(operation, request.get("payload") or {})
            response = {
                "protocol_version": PROTOCOL_VERSION,
                "message_type": "response",
                "request_id": request_id,
                "operation": operation,
                "success": True,
                "error_code": "",
                "error_message": "",
                "payload": payload,
            }
            self.write_message(response)

            if operation == "subscribe":
                time.sleep(0.5)
                self.write_quote_event(payload["stock_code"])

    def write_message(self, message):
        serialized_message = json.dumps(message, separators=(",", ":"), ensure_ascii=True)
        self.wfile.write(serialized_message.encode("utf-8") + b"\n")
        self.wfile.flush()

    def write_quote_event(self, stock_code):
        quote_event = {
            "protocol_version": PROTOCOL_VERSION,
            "message_type": "event",
            "request_id": None,
            "operation": "quote",
            "success": None,
            "error_code": "",
            "error_message": "",
            "payload": {
                "stock_code": stock_code,
                "time": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                "last_price": 10.0,
                "volume": 100,
                "amount": 1000.0,
                "bid_price": 9.99,
                "ask_price": 10.01,
                "bid_volume": 100,
                "ask_volume": 100,
            },
        }
        self.write_message(quote_event)
        write_log("stage=quote status=sent stock_code={0}".format(stock_code))


class FakeGatewayServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, server_address, account_id):
        socketserver.ThreadingTCPServer.__init__(self, server_address, FakeGatewayRequestHandler)
        self.account_id = account_id
        self.subscription_lock = threading.Lock()
        self.next_subscription_id = 0

    def build_payload(self, operation, request_payload):
        if operation == "hello":
            requested_account_id = request_payload.get("account_id", "")
            if requested_account_id != self.account_id:
                raise ValueError("unexpected fake account ID")
            return {
                "server_name": "fake-qmt-deployment-gateway",
                "account_id": self.account_id,
                "trading_enabled": False,
            }
        if operation == "query_account":
            return {"accounts": [{"available_cash": 100000.0}]}
        if operation == "query_positions":
            return {"positions": []}
        if operation == "query_orders":
            return {"orders": []}
        if operation == "subscribe":
            with self.subscription_lock:
                self.next_subscription_id += 1
                subscription_id = "fake-{0}".format(self.next_subscription_id)
            return {
                "subscribed": True,
                "subscription_id": subscription_id,
                "stock_code": request_payload["stock_code"],
            }
        if operation == "unsubscribe":
            return {
                "unsubscribed": True,
                "subscription_id": request_payload["subscription_id"],
            }
        if operation in ("place_order", "cancel_order"):
            raise ValueError("fake deployment Gateway rejects trading operations")
        raise ValueError("unsupported operation: {0}".format(operation))


def main():
    parser = argparse.ArgumentParser(description="Fake-only QMT deployment Gateway")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", default=17891, type=int)
    parser.add_argument("--account-id", default="deployment-test")
    arguments = parser.parse_args()

    server = FakeGatewayServer((arguments.host, arguments.port), arguments.account_id)
    write_log("stage=server status=listening host={0} port={1} trading_enabled=false".format(
        arguments.host,
        arguments.port))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.shutdown()
        server.server_close()
        write_log("stage=server status=stopped")


if __name__ == "__main__":
    main()
