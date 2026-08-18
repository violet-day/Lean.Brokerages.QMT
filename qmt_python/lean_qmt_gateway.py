# coding: gbk
"""QMT embedded-Python gateway for the LEAN brokerage integration.

The socket threads only move NDJSON messages between sockets and queues. QMT
API calls are deliberately executed by ``handlebar`` or by native QMT callback
threads.
"""

import json
import math
import os
import queue
import socket
import sys
import threading
import time


module_directory = globals().get(
    "module_directory",
    os.path.dirname(os.path.abspath(globals().get("__file__", ""))),
)


PROTOCOL_VERSION = 1
DEFAULT_BIND_HOST = "127.0.0.1"
DEFAULT_BIND_PORT = 17890
DEFAULT_STRATEGY_NAME = "LeanQmtGateway"
ACCOUNT_TYPE = "STOCK"
LOG_PREFIX = "[lean_qmt_gateway]"
RUNTIME_LOG_PATH = os.environ.get(
    "QMT_GATEWAY_RUNTIME_LOG_PATH",
    os.path.join(
        os.path.expanduser("~"),
        "lean_logs",
        "broker",
        "qmt-gateway-runtime.log",
    ),
)
MAXIMUM_RUNTIME_LOG_BYTES = 5 * 1024 * 1024
RUNTIME_LOG_BACKUP_COUNT = 3
MAXIMUM_MESSAGE_BYTES = 1024 * 1024
MAXIMUM_REQUESTS_PER_HANDLEBAR = 100
MAXIMUM_CACHED_RESPONSES = 512
REQUEST_PUMP_CALLBACK_NAME = "qmt_gateway_timer_callback"
REQUEST_PUMP_PERIOD = "500nMilliSecond"
REQUEST_PUMP_START_TIME = "2000-01-01 00:00:00"
REQUEST_PUMP_MARKET = "SH"
NETWORK_THREAD_HEARTBEAT_TIMEOUT_SECONDS = 5.0
NETWORK_RECOVERY_RETRY_SECONDS = 5.0
_runtime_log_lock = threading.Lock()


class _RequestError(Exception):
    def __init__(self, error_code, error_message):
        Exception.__init__(self, error_message)
        self.error_code = error_code
        self.error_message = error_message


def _load_source(module_name, source_path):
    module = type(sys)(module_name)
    module.__file__ = source_path
    sys.modules[module_name] = module
    with open(source_path, "rb") as source_file:
        source_code = source_file.read()
    exec(compile(source_code, source_path, "exec"), module.__dict__)
    return module


def _rotate_runtime_log_if_needed(incoming_byte_count):
    if not os.path.isfile(RUNTIME_LOG_PATH):
        return
    if (
        os.path.getsize(RUNTIME_LOG_PATH) + incoming_byte_count
        <= MAXIMUM_RUNTIME_LOG_BYTES
    ):
        return

    for backup_number in range(RUNTIME_LOG_BACKUP_COUNT, 0, -1):
        if backup_number == 1:
            source_path = RUNTIME_LOG_PATH
        else:
            source_path = "%s.%d" % (
                RUNTIME_LOG_PATH,
                backup_number - 1,
            )
        destination_path = "%s.%d" % (RUNTIME_LOG_PATH, backup_number)
        if not os.path.isfile(source_path):
            continue
        if os.path.isfile(destination_path):
            os.remove(destination_path)
        os.rename(source_path, destination_path)


def _log(message, **fields):
    parts = [time.strftime("%Y-%m-%dT%H:%M:%S"), LOG_PREFIX, str(message)]
    for field_name in sorted(fields):
        parts.append("%s=%s" % (field_name, fields[field_name]))
    log_line = " ".join(parts)
    encoded_log_line = (log_line + "\n").encode("utf-8", "replace")
    try:
        runtime_log_directory = os.path.dirname(RUNTIME_LOG_PATH)
        if runtime_log_directory and not os.path.isdir(runtime_log_directory):
            os.makedirs(runtime_log_directory)
        with _runtime_log_lock:
            try:
                _rotate_runtime_log_if_needed(len(encoded_log_line))
            except Exception:
                pass
            with open(RUNTIME_LOG_PATH, "ab") as runtime_log_file:
                runtime_log_file.write(encoded_log_line)
    except Exception:
        pass
    print(log_line)


_log(
    "module_loaded",
    module_directory=module_directory,
    python_version=sys.version.replace(" ", "_"),
)


def _load_config():
    config_path = os.path.join(
        module_directory,
        "qmt_local_config.py",
    )
    local_config = None
    if os.path.isfile(config_path):
        local_config = _load_source("qmt_local_config", config_path)

    def config_value(name, default):
        if local_config is None:
            return default
        return getattr(local_config, name, default)

    bind_host = str(
        config_value("GATEWAY_BIND_HOST", DEFAULT_BIND_HOST)
        or DEFAULT_BIND_HOST
    ).strip()
    allow_remote_clients = bool(
        config_value("GATEWAY_ALLOW_REMOTE_CLIENTS", False)
    )
    if bind_host not in ("127.0.0.1", "localhost") and not allow_remote_clients:
        raise RuntimeError(
            "GATEWAY_ALLOW_REMOTE_CLIENTS must be True for a non-loopback bind"
        )

    return {
        "account_id": str(config_value("ACCOUNT_ID", "") or "").strip(),
        "bind_host": bind_host,
        "bind_port": int(
            config_value("GATEWAY_BIND_PORT", DEFAULT_BIND_PORT)
            or DEFAULT_BIND_PORT
        ),
        "strategy_name": str(
            config_value("GATEWAY_STRATEGY_NAME", DEFAULT_STRATEGY_NAME)
            or DEFAULT_STRATEGY_NAME
        ).strip(),
    }


def _attribute(value, attribute_names, default=""):
    for attribute_name in attribute_names:
        try:
            if isinstance(value, dict):
                attribute_value = value[attribute_name]
            else:
                attribute_value = getattr(value, attribute_name)
        except Exception:
            continue
        if attribute_value is not None:
            return attribute_value
    return default


def _number(value, default=0.0):
    try:
        converted_value = float(value)
    except Exception:
        return default
    if not math.isfinite(converted_value):
        return default
    return converted_value


def _integer(value, default=0):
    try:
        return int(value)
    except Exception:
        return default


def _first_number(value):
    if isinstance(value, (list, tuple)):
        if not value:
            return 0.0
        return _number(value[0])
    return _number(value)


def _rows(value):
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return list(value)
    return [value]


def _direction(value):
    numeric_direction = _integer(value, -1)
    if numeric_direction == 48:
        return "buy"
    if numeric_direction == 49:
        return "sell"

    direction_text = str(value or "").strip().lower()
    if direction_text in ("buy", "sell"):
        return direction_text
    return direction_text


def _order_type(value):
    numeric_order_type = _integer(value, -1)
    if numeric_order_type == 49:
        return "market"
    if numeric_order_type == 50:
        return "limit"

    order_type_text = str(value or "").strip().lower()
    if order_type_text in ("market", "limit"):
        return order_type_text
    return order_type_text


def _normalize_account(account_info):
    return {
        "available_cash": _number(
            _attribute(account_info, ("m_dAvailable", "available_cash"), 0)
        ),
    }


def _normalize_position(position_info):
    return {
        "stock_code": str(
            _attribute(
                position_info,
                ("m_strInstrumentID", "stock_code"),
                "",
            )
            or ""
        ),
        "volume": _number(
            _attribute(position_info, ("m_nVolume", "volume"), 0)
        ),
        "open_price": _number(
            _attribute(position_info, ("m_dOpenPrice", "open_price"), 0)
        ),
        "last_price": _number(
            _attribute(position_info, ("m_dLastPrice", "last_price"), 0)
        ),
        "market_value": _number(
            _attribute(
                position_info,
                ("m_dMarketValue", "market_value"),
                0,
            )
        ),
    }


def _normalize_order(order_info):
    insert_date = str(
        _attribute(order_info, ("m_strInsertDate", "insert_date"), "") or ""
    )
    insert_time = str(
        _attribute(order_info, ("m_strInsertTime", "insert_time"), "") or ""
    )
    timestamp = (insert_date + " " + insert_time).strip()
    remark = str(
        _attribute(order_info, ("m_strRemark", "remark"), "") or ""
    )
    return {
        "stock_code": str(
            _attribute(
                order_info,
                ("m_strInstrumentID", "stock_code"),
                "",
            )
            or ""
        ),
        "order_id": str(
            _attribute(
                order_info,
                ("m_strOrderSysID", "order_id"),
                "",
            )
            or ""
        ),
        "client_order_id": remark,
        "direction": _direction(
            _attribute(order_info, ("m_nDirection", "direction"), "")
        ),
        "order_type": _order_type(
            _attribute(
                order_info,
                ("m_nOrderPriceType", "order_type"),
                "",
            )
        ),
        "status": _integer(
            _attribute(order_info, ("m_nOrderStatus", "status"), 255),
            255,
        ),
        "original_volume": _number(
            _attribute(
                order_info,
                ("m_nVolumeTotalOriginal", "original_volume"),
                0,
            )
        ),
        "traded_volume": _number(
            _attribute(
                order_info,
                ("m_nVolumeTraded", "traded_volume"),
                0,
            )
        ),
        "limit_price": _number(
            _attribute(order_info, ("m_dLimitPrice", "limit_price"), 0)
        ),
        "traded_price": _number(
            _attribute(order_info, ("m_dTradedPrice", "traded_price"), 0)
        ),
        "submit_status": _integer(
            _attribute(
                order_info,
                ("m_nOrderSubmitStatus", "submit_status"),
                -1,
            ),
            -1,
        ),
        "error_id": _integer(
            _attribute(order_info, ("m_nErrorID", "error_id"), 0),
            0,
        ),
        "error_message": str(
            _attribute(order_info, ("m_strErrorMsg", "error_message"), "")
            or ""
        ),
        "cancel_information": str(
            _attribute(
                order_info,
                ("m_strCancelInfo", "cancel_information"),
                "",
            )
            or ""
        ),
        "remark": remark,
        "time": timestamp,
    }


def _normalize_deal(deal_info):
    trade_date = str(
        _attribute(deal_info, ("m_strTradeDate", "trade_date"), "") or ""
    )
    trade_time = str(
        _attribute(deal_info, ("m_strTradeTime", "trade_time"), "") or ""
    )
    timestamp = (trade_date + " " + trade_time).strip()
    return {
        "stock_code": str(
            _attribute(
                deal_info,
                ("m_strInstrumentID", "stock_code"),
                "",
            )
            or ""
        ),
        "order_id": str(
            _attribute(
                deal_info,
                ("m_strOrderSysID", "order_id"),
                "",
            )
            or ""
        ),
        "deal_id": str(
            _attribute(deal_info, ("m_strTradeID", "deal_id"), "") or ""
        ),
        "direction": _direction(
            _attribute(deal_info, ("m_nDirection", "direction"), "")
        ),
        "price": _number(_attribute(deal_info, ("m_dPrice", "price"), 0)),
        "volume": _number(_attribute(deal_info, ("m_nVolume", "volume"), 0)),
        "amount": _number(
            _attribute(deal_info, ("m_dTradeAmount", "amount"), 0)
        ),
        "commission": _number(
            _attribute(deal_info, ("m_dComssion", "commission"), 0)
        ),
        "remark": str(
            _attribute(deal_info, ("m_strRemark", "remark"), "") or ""
        ),
        "time": timestamp,
    }


def _quote_row(stock_code, quote_data):
    quote_row = quote_data
    inferred_time = ""

    if isinstance(quote_data, dict):
        nested_stock_row = quote_data.get(stock_code)
        if isinstance(nested_stock_row, dict):
            quote_row = nested_stock_row

        if isinstance(quote_row, dict) and not any(
            field_name in quote_row
            for field_name in ("lastPrice", "last_price", "time", "stime")
        ):
            nested_rows = []
            for nested_time, nested_value in quote_row.items():
                if isinstance(nested_value, dict):
                    nested_rows.append((nested_time, nested_value))
            if nested_rows:
                nested_rows.sort(key=lambda item: str(item[0]))
                inferred_time, quote_row = nested_rows[-1]

    if not isinstance(quote_row, dict):
        quote_row = {}
    return quote_row, inferred_time


def _normalize_quote(stock_code, quote_data):
    quote_row, inferred_time = _quote_row(stock_code, quote_data)
    raw_cumulative_volume = _number(
        _attribute(quote_row, ("pvolume", "raw_volume"), 0)
    )
    cumulative_volume = raw_cumulative_volume or _number(
        _attribute(quote_row, ("volume",), 0)
    )
    return {
        "stock_code": stock_code,
        "time": str(
            _attribute(
                quote_row,
                ("time", "stime"),
                inferred_time,
            )
            or ""
        ),
        "last_price": _number(
            _attribute(quote_row, ("lastPrice", "last_price"), 0)
        ),
        "volume": cumulative_volume,
        "amount": _number(_attribute(quote_row, ("amount",), 0)),
        "bid_price": _first_number(
            _attribute(quote_row, ("bidPrice", "bid_price"), 0)
        ),
        "ask_price": _first_number(
            _attribute(quote_row, ("askPrice", "ask_price"), 0)
        ),
        "bid_volume": _first_number(
            _attribute(quote_row, ("bidVol", "bid_volume"), 0)
        ),
        "ask_volume": _first_number(
            _attribute(quote_row, ("askVol", "ask_volume"), 0)
        ),
    }


def _history_records(stock_code, history_data, field_names):
    if isinstance(history_data, dict):
        stock_history = history_data.get(stock_code)
        if stock_history is None:
            stock_history = history_data.get(stock_code.upper())
    else:
        stock_history = history_data

    if stock_history is None:
        return []

    iterrows_function = getattr(stock_history, "iterrows", None)
    if callable(iterrows_function):
        records = []
        for history_index, history_row in iterrows_function():
            to_dict_function = getattr(history_row, "to_dict", None)
            if callable(to_dict_function):
                history_row = to_dict_function()
            if not isinstance(history_row, dict):
                continue
            history_row = dict(history_row)
            if not history_row.get("time") and not history_row.get("stime"):
                history_row["time"] = history_index
            records.append(history_row)
        return records

    if isinstance(stock_history, (list, tuple)):
        records = []
        raw_field_names = ["stime"] + list(field_names)
        for history_row in stock_history:
            if isinstance(history_row, dict):
                records.append(history_row)
            elif isinstance(history_row, (list, tuple)):
                records.append(dict(zip(raw_field_names, history_row)))
        return records

    if isinstance(stock_history, dict):
        if any(
            field_name in stock_history
            for field_name in ("open", "high", "low", "close")
        ):
            column_field_names = [
                field_name
                for field_name, field_values in stock_history.items()
                if isinstance(field_values, (list, tuple))
            ]
            if column_field_names:
                record_count = max(
                    len(stock_history[field_name])
                    for field_name in column_field_names
                )
                records = []
                for record_index in range(record_count):
                    history_row = {}
                    for field_name, field_values in stock_history.items():
                        if isinstance(field_values, (list, tuple)):
                            if record_index < len(field_values):
                                history_row[field_name] = field_values[
                                    record_index
                                ]
                        else:
                            history_row[field_name] = field_values
                    records.append(history_row)
                return records
            return [stock_history]

        records = []
        for history_time, history_row in stock_history.items():
            if not isinstance(history_row, dict):
                continue
            history_row = dict(history_row)
            if not history_row.get("time") and not history_row.get("stime"):
                history_row["time"] = history_time
            records.append(history_row)
        return records

    return []


def _normalize_history_bar(history_row):
    if not isinstance(history_row, dict):
        return None
    close_price = _number(_attribute(history_row, ("close",), 0))
    if close_price <= 0:
        return None
    return {
        "time": str(
            _attribute(history_row, ("stime", "time"), "") or ""
        ),
        "open": _number(_attribute(history_row, ("open",), close_price)),
        "high": _number(_attribute(history_row, ("high",), close_price)),
        "low": _number(_attribute(history_row, ("low",), close_price)),
        "close": close_price,
        "volume": _number(_attribute(history_row, ("volume",), 0)),
    }


def _protocol_message(
    message_type,
    request_id,
    operation,
    success,
    error_code,
    error_message,
    payload,
):
    return {
        "protocol_version": PROTOCOL_VERSION,
        "message_type": message_type,
        "request_id": str(request_id or ""),
        "operation": str(operation or ""),
        "success": success,
        "error_code": str(error_code or ""),
        "error_message": str(error_message or ""),
        "payload": payload or {},
    }


class LeanQmtGateway(object):
    def __init__(
        self,
        context_info,
        account_id,
        get_trade_detail_data_function=None,
        passorder_function=None,
        cancel_function=None,
        down_history_data_function=None,
        get_market_data_function=None,
        subscribe_quote_function=None,
        unsubscribe_quote_function=None,
        bind_host=DEFAULT_BIND_HOST,
        bind_port=DEFAULT_BIND_PORT,
        strategy_name=DEFAULT_STRATEGY_NAME,
    ):
        self.context_info = context_info
        self.account_id = str(account_id or "").strip()
        self.get_trade_detail_data_function = get_trade_detail_data_function
        self.passorder_function = passorder_function
        self.cancel_function = cancel_function
        self.down_history_data_function = down_history_data_function
        self.get_market_data_function = get_market_data_function
        self.subscribe_quote_function = subscribe_quote_function
        self.unsubscribe_quote_function = unsubscribe_quote_function
        self.bind_host = str(bind_host or DEFAULT_BIND_HOST)
        self.bind_port = int(bind_port)
        self.bound_port = None
        self.strategy_name = str(strategy_name or DEFAULT_STRATEGY_NAME)

        self._incoming_messages = queue.Queue()
        self._outgoing_messages = queue.Queue()
        self._stop_event = threading.Event()
        self._server_socket = None
        self._accept_thread = None
        self._sender_thread = None
        self._accept_loop_last_active_at = 0.0
        self._next_network_recovery_at = 0.0
        self._client_sockets = []
        self._client_threads = []
        self._client_lock = threading.Lock()
        self._subscriptions_by_protocol_id = {}
        self._protocol_ids_by_stock_code = {}
        self._cached_responses = {}
        self._cached_response_ids = []

    @property
    def is_running(self):
        accept_thread_is_alive = (
            self._accept_thread is not None
            and self._accept_thread.is_alive()
        )
        sender_thread_is_alive = (
            self._sender_thread is not None
            and self._sender_thread.is_alive()
        )
        accept_loop_is_active = (
            self._accept_loop_last_active_at > 0
            and time.monotonic() - self._accept_loop_last_active_at
            <= NETWORK_THREAD_HEARTBEAT_TIMEOUT_SECONDS
        )
        return (
            self._server_socket is not None
            and accept_thread_is_alive
            and sender_thread_is_alive
            and accept_loop_is_active
        )

    def start(self):
        if self.is_running:
            return

        server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server_socket.bind((self.bind_host, self.bind_port))
            server_socket.listen(5)
            server_socket.settimeout(0.25)
        except Exception:
            server_socket.close()
            raise
        self._server_socket = server_socket
        self.bound_port = server_socket.getsockname()[1]
        self._stop_event.clear()
        self._accept_loop_last_active_at = time.monotonic()

        self._accept_thread = threading.Thread(
            target=self._accept_connections,
            name="lean-qmt-gateway-accept",
        )
        self._accept_thread.daemon = True
        self._accept_thread.start()

        self._sender_thread = threading.Thread(
            target=self._send_messages,
            name="lean-qmt-gateway-send",
        )
        self._sender_thread.daemon = True
        self._sender_thread.start()
        _log(
            "server_started",
            bind_host=self.bind_host,
            bind_port=self.bound_port,
        )

    def recover_network_server_if_needed(self):
        if self.is_running:
            self._next_network_recovery_at = 0.0
            return True

        current_time = time.monotonic()
        if current_time < self._next_network_recovery_at:
            return False
        self._next_network_recovery_at = (
            current_time + NETWORK_RECOVERY_RETRY_SECONDS
        )

        _log(
            "server_recovery_start",
            accept_thread_alive=(
                self._accept_thread is not None
                and self._accept_thread.is_alive()
            ),
            sender_thread_alive=(
                self._sender_thread is not None
                and self._sender_thread.is_alive()
            ),
        )
        self._stop_network_server()
        try:
            self.start()
        except Exception as error:
            _log("server_recovery_failed", error=repr(error))
            return False

        self._next_network_recovery_at = 0.0
        _log("server_recovery_ok", bind_port=self.bound_port)
        return True

    def stop(self):
        for protocol_subscription_id in list(
            self._subscriptions_by_protocol_id.keys()
        ):
            try:
                self._unsubscribe(protocol_subscription_id)
            except Exception as error:
                _log(
                    "unsubscribe_failed_on_stop",
                    error=repr(error),
                    subscription_id=protocol_subscription_id,
                )

        was_started = self._accept_thread is not None or self._server_socket is not None
        self._stop_network_server()
        if was_started:
            _log("server_stopped")

    def _stop_network_server(self):
        self._stop_event.set()
        server_socket = self._server_socket
        self._server_socket = None
        if server_socket is not None:
            try:
                server_socket.close()
            except Exception:
                pass

        with self._client_lock:
            client_sockets = list(self._client_sockets)
            self._client_sockets = []
        for client_socket in client_sockets:
            self._close_socket(client_socket)

        self._outgoing_messages.put((None, None))
        network_threads = [self._accept_thread, self._sender_thread]
        network_threads.extend(self._client_threads)
        for network_thread in network_threads:
            if network_thread is not None and network_thread.is_alive():
                network_thread.join(1.0)
        self._accept_thread = None
        self._sender_thread = None
        self._client_threads = []
        self._accept_loop_last_active_at = 0.0
        self.bound_port = None
        self._incoming_messages = queue.Queue()
        self._outgoing_messages = queue.Queue()

    def handlebar(self):
        if not self.recover_network_server_if_needed():
            return

        processed_request_count = 0
        while processed_request_count < MAXIMUM_REQUESTS_PER_HANDLEBAR:
            try:
                client_socket, request_message = self._incoming_messages.get_nowait()
            except queue.Empty:
                break

            response_message = self._process_request(request_message)
            self._outgoing_messages.put((client_socket, response_message))
            processed_request_count += 1

        if processed_request_count:
            _log("requests_processed", count=processed_request_count)

    def account_callback(self, account_info):
        payload = _normalize_account(account_info)
        status = str(
            _attribute(account_info, ("m_strStatus", "status"), "") or ""
        )
        if status:
            payload["status"] = status
        self._publish_event("account", payload)
        _log("account_event", status=status)

    def order_callback(self, order_info):
        payload = _normalize_order(order_info)
        self._publish_event("order", payload)
        _log(
            "order_event",
            order_id=payload["order_id"],
            status=payload["status"],
            stock_code=payload["stock_code"],
        )

    def deal_callback(self, deal_info):
        payload = _normalize_deal(deal_info)
        self._publish_event("deal", payload)
        _log(
            "deal_event",
            deal_id=payload["deal_id"],
            order_id=payload["order_id"],
            stock_code=payload["stock_code"],
        )

    def position_callback(self, position_info):
        payload = _normalize_position(position_info)
        self._publish_event("position", payload)
        _log(
            "position_event",
            stock_code=payload["stock_code"],
            volume=payload["volume"],
        )

    def order_error_callback(self, order_args, error_message):
        payload = _normalize_order(order_args)
        callback_error_message = str(error_message or "").strip()
        if callback_error_message:
            payload["error_message"] = callback_error_message
        self._publish_event("order", payload)
        _log(
            "order_error_event",
            error_id=payload["error_id"],
            error_message=payload["error_message"],
            order_id=payload["order_id"],
            status=payload["status"],
            submit_status=payload["submit_status"],
        )

    def enqueue_received_message(self, client_socket, request_message):
        """Queue a decoded request; exposed for deterministic boundary tests."""
        self._incoming_messages.put((client_socket, request_message))

    def get_queued_outgoing_message(self):
        """Return one queued message; intended for tests before ``start``."""
        return self._outgoing_messages.get_nowait()

    def _accept_connections(self):
        while not self._stop_event.is_set():
            self._accept_loop_last_active_at = time.monotonic()
            server_socket = self._server_socket
            if server_socket is None:
                break
            try:
                client_socket, client_address = server_socket.accept()
            except socket.timeout:
                continue
            except Exception as error:
                if not self._stop_event.is_set():
                    _log("accept_failed", error=repr(error))
                break

            client_socket.settimeout(0.25)
            with self._client_lock:
                self._client_sockets.append(client_socket)
            client_thread = threading.Thread(
                target=self._receive_messages,
                args=(client_socket,),
                name="lean-qmt-gateway-receive",
            )
            client_thread.daemon = True
            client_thread.start()
            self._client_threads.append(client_thread)
            self._publish_event(
                "connection",
                {
                    "connected": True,
                    "remote_address": str(client_address[0]),
                },
            )
            _log("client_connected", remote_address=client_address[0])

    def _receive_messages(self, client_socket):
        received_bytes = b""
        while not self._stop_event.is_set():
            try:
                next_bytes = client_socket.recv(65536)
            except socket.timeout:
                continue
            except Exception:
                break
            if not next_bytes:
                break

            received_bytes += next_bytes
            if len(received_bytes) > MAXIMUM_MESSAGE_BYTES:
                self._queue_network_error(
                    client_socket,
                    "MESSAGE_TOO_LARGE",
                    "NDJSON message exceeds the maximum size.",
                )
                break

            while b"\n" in received_bytes:
                line_bytes, received_bytes = received_bytes.split(b"\n", 1)
                if not line_bytes.strip():
                    continue
                try:
                    request_message = json.loads(line_bytes.decode("utf-8"))
                except Exception as error:
                    _log("invalid_json", error=repr(error))
                    self._queue_network_error(
                        client_socket,
                        "INVALID_JSON",
                        "Message is not valid UTF-8 JSON.",
                    )
                    continue
                self._incoming_messages.put((client_socket, request_message))

        self._remove_client(client_socket)

    def _send_messages(self):
        while not self._stop_event.is_set():
            try:
                target_socket, message = self._outgoing_messages.get(timeout=0.25)
            except queue.Empty:
                continue
            if message is None:
                continue

            try:
                encoded_message = (
                    json.dumps(
                        message,
                        ensure_ascii=False,
                        separators=(",", ":"),
                        allow_nan=False,
                    ).encode("utf-8")
                    + b"\n"
                )
            except Exception as error:
                _log("serialize_failed", error=repr(error))
                continue

            if target_socket is None:
                with self._client_lock:
                    target_sockets = list(self._client_sockets)
            else:
                target_sockets = [target_socket]

            for client_socket in target_sockets:
                try:
                    client_socket.sendall(encoded_message)
                except Exception:
                    self._remove_client(client_socket)

    def _queue_network_error(self, client_socket, error_code, error_message):
        response_message = _protocol_message(
            "response",
            "",
            "",
            False,
            error_code,
            error_message,
            {},
        )
        self._outgoing_messages.put((client_socket, response_message))

    def _remove_client(self, client_socket):
        removed = False
        with self._client_lock:
            if client_socket in self._client_sockets:
                self._client_sockets.remove(client_socket)
                removed = True
        self._close_socket(client_socket)
        if removed:
            _log("client_disconnected")

    @staticmethod
    def _close_socket(client_socket):
        try:
            client_socket.shutdown(socket.SHUT_RDWR)
        except Exception:
            pass
        try:
            client_socket.close()
        except Exception:
            pass

    def _process_request(self, request_message):
        if not isinstance(request_message, dict):
            return _protocol_message(
                "response",
                "",
                "",
                False,
                "INVALID_REQUEST",
                "Request must be a JSON object.",
                {},
            )

        request_id = str(request_message.get("request_id") or "")
        operation = str(request_message.get("operation") or "")
        cached_response = self._cached_responses.get(request_id)
        if request_id and cached_response is not None:
            _log("duplicate_request", operation=operation, request_id=request_id)
            return cached_response

        try:
            self._validate_request_envelope(request_message)
            payload = request_message.get("payload") or {}
            response_payload = self._execute_operation(operation, payload)
            response_message = _protocol_message(
                "response",
                request_id,
                operation,
                True,
                "",
                "",
                response_payload,
            )
        except _RequestError as error:
            response_message = _protocol_message(
                "response",
                request_id,
                operation,
                False,
                error.error_code,
                error.error_message,
                {},
            )
            _log(
                "request_rejected",
                error_code=error.error_code,
                operation=operation,
                request_id=request_id,
            )
        except Exception as error:
            response_message = _protocol_message(
                "response",
                request_id,
                operation,
                False,
                "QMT_API_ERROR",
                str(error),
                {},
            )
            _log(
                "request_failed",
                error=repr(error),
                operation=operation,
                request_id=request_id,
            )

        if request_id:
            self._cache_response(request_id, response_message)
        return response_message

    @staticmethod
    def _validate_request_envelope(request_message):
        if request_message.get("protocol_version") != PROTOCOL_VERSION:
            raise _RequestError(
                "UNSUPPORTED_PROTOCOL_VERSION",
                "Only QMT Gateway protocol version 1 is supported.",
            )
        if request_message.get("message_type") != "request":
            raise _RequestError(
                "INVALID_REQUEST",
                "message_type must be request.",
            )
        if not request_message.get("request_id"):
            raise _RequestError(
                "INVALID_REQUEST",
                "request_id is required.",
            )
        if not request_message.get("operation"):
            raise _RequestError(
                "INVALID_REQUEST",
                "operation is required.",
            )
        payload = request_message.get("payload")
        if payload is not None and not isinstance(payload, dict):
            raise _RequestError(
                "INVALID_REQUEST",
                "payload must be a JSON object.",
            )

    def _execute_operation(self, operation, payload):
        if operation == "hello":
            requested_account_id = str(payload.get("account_id") or "").strip()
            if requested_account_id and requested_account_id != self.account_id:
                raise _RequestError(
                    "ACCOUNT_MISMATCH",
                    "Gateway account does not match the requested account.",
                )
            return {
                "server_name": "lean-qmt-gateway",
                "account_id": self.account_id,
            }
        if operation == "query_account":
            return {
                "accounts": [
                    _normalize_account(account_info)
                    for account_info in self._query_trade_detail("ACCOUNT")
                ]
            }
        if operation == "query_positions":
            return {
                "positions": [
                    _normalize_position(position_info)
                    for position_info in self._query_trade_detail("POSITION")
                ]
            }
        if operation == "query_orders":
            return {
                "orders": [
                    _normalize_order(order_info)
                    for order_info in self._query_trade_detail("ORDER")
                ]
            }
        if operation == "query_history":
            return self._query_history(payload)
        if operation == "place_order":
            return self._place_order(payload)
        if operation == "cancel_order":
            return self._cancel_order(payload)
        if operation == "subscribe":
            return self._subscribe(payload)
        if operation == "unsubscribe":
            return self._unsubscribe_request(payload)
        raise _RequestError(
            "UNSUPPORTED_OPERATION",
            "Unsupported operation: %s" % operation,
        )

    def _query_history(self, payload):
        stock_code = str(payload.get("stock_code") or "").strip().upper()
        period = str(payload.get("period") or "").strip().lower()
        start_time = str(payload.get("start_time") or "").strip()
        end_time = str(payload.get("end_time") or "").strip()
        self._validate_stock_code(stock_code)
        if period not in ("1m", "1d"):
            raise _RequestError(
                "UNSUPPORTED_HISTORY_PERIOD",
                "QMT history supports only 1m and 1d periods.",
            )
        if not callable(self.get_market_data_function):
            raise _RequestError(
                "QMT_API_UNAVAILABLE",
                "ContextInfo.get_market_data_ex is unavailable.",
            )

        started_at = time.time()
        if callable(self.down_history_data_function):
            history_download_started_at = time.time()
            _log(
                "history_download_start",
                end_time=end_time,
                period=period,
                start_time=start_time,
                stock_code=stock_code,
            )
            history_download_result = self.down_history_data_function(
                stock_code,
                period,
                start_time,
                end_time,
            )
            _log(
                "history_download_complete",
                elapsed_ms=int(
                    (time.time() - history_download_started_at) * 1000
                ),
                result=repr(history_download_result),
                stock_code=stock_code,
            )
        else:
            _log("history_download_unavailable", stock_code=stock_code)
        history_field_names = ["open", "high", "low", "close", "volume"]
        history_data = self.get_market_data_function(
            fields=history_field_names,
            stock_code=[stock_code],
            period=period,
            start_time=start_time,
            end_time=end_time,
            count=-1,
            dividend_type="none",
            fill_data=True,
            subscribe=False,
        )
        history_records = _history_records(
            stock_code,
            history_data,
            history_field_names,
        )
        _log(
            "history_data_received",
            raw_type=type(history_data).__name__,
            records=len(history_records),
            sample=repr(history_records[0])[:500] if history_records else "",
            stock_code=stock_code,
        )
        bars = []
        for history_row in history_records:
            normalized_bar = _normalize_history_bar(history_row)
            if normalized_bar is not None:
                bars.append(normalized_bar)
        bars.sort(key=lambda history_bar: history_bar["time"])
        _log(
            "history_query_ok",
            bars=len(bars),
            elapsed_ms=int((time.time() - started_at) * 1000),
            end_time=end_time,
            first_time=bars[0]["time"] if bars else "",
            last_time=bars[-1]["time"] if bars else "",
            period=period,
            start_time=start_time,
            stock_code=stock_code,
        )
        return {"bars": bars}

    def _query_trade_detail(self, detail_type):
        if not self.account_id:
            raise _RequestError(
                "ACCOUNT_NOT_CONFIGURED",
                "QMT account is not configured.",
            )
        if not callable(self.get_trade_detail_data_function):
            raise _RequestError(
                "QMT_API_UNAVAILABLE",
                "get_trade_detail_data is unavailable.",
            )

        started_at = time.time()
        try:
            try:
                result = self.get_trade_detail_data_function(
                    self.account_id,
                    ACCOUNT_TYPE,
                    detail_type,
                    "",
                )
            except TypeError:
                result = self.get_trade_detail_data_function(
                    self.account_id,
                    ACCOUNT_TYPE,
                    detail_type,
                )
        except Exception:
            _log("query_failed", detail_type=detail_type)
            raise

        result_rows = _rows(result)
        if detail_type == "ACCOUNT" and not result_rows:
            account_query_probes = (
                (
                    "uppercase-three-arguments",
                    (self.account_id, ACCOUNT_TYPE, detail_type),
                ),
                (
                    "lowercase-four-arguments",
                    (
                        self.account_id,
                        ACCOUNT_TYPE.lower(),
                        detail_type.lower(),
                        "",
                    ),
                ),
                (
                    "lowercase-three-arguments",
                    (
                        self.account_id,
                        ACCOUNT_TYPE.lower(),
                        detail_type.lower(),
                    ),
                ),
            )
            for probe_name, probe_arguments in account_query_probes:
                try:
                    probe_result = self.get_trade_detail_data_function(
                        *probe_arguments
                    )
                    _log(
                        "account_query_probe",
                        probe=probe_name,
                        raw_type=type(probe_result).__name__,
                        rows=len(_rows(probe_result)),
                    )
                except Exception as probe_error:
                    _log(
                        "account_query_probe_failed",
                        error=repr(probe_error),
                        probe=probe_name,
                    )
        _log(
            "query_ok",
            detail_type=detail_type,
            elapsed_ms=int((time.time() - started_at) * 1000),
            rows=len(result_rows),
        )
        return result_rows

    def _place_order(self, payload):
        if not callable(self.passorder_function):
            raise _RequestError("QMT_API_UNAVAILABLE", "passorder is unavailable.")

        client_order_id = str(payload.get("client_order_id") or "").strip()
        stock_code = str(payload.get("stock_code") or "").strip().upper()
        order_type = str(payload.get("order_type") or "").strip().lower()
        direction = str(payload.get("direction") or "").strip().lower()
        quantity = _number(payload.get("quantity"), -1)
        if not client_order_id:
            raise _RequestError("INVALID_REQUEST", "client_order_id is required.")
        self._validate_stock_code(stock_code)
        if direction not in ("buy", "sell"):
            raise _RequestError(
                "INVALID_REQUEST",
                "direction must be buy or sell.",
            )
        if order_type not in ("market", "limit"):
            raise _RequestError(
                "INVALID_REQUEST",
                "order_type must be market or limit.",
            )
        if quantity <= 0 or quantity != int(quantity):
            raise _RequestError(
                "INVALID_REQUEST",
                "quantity must be a positive whole number.",
            )

        operation_type = 23 if direction == "buy" else 24
        price_type = 5
        model_price = -1
        if order_type == "limit":
            model_price = _number(payload.get("limit_price"), -1)
            if model_price <= 0:
                raise _RequestError(
                    "INVALID_REQUEST",
                    "limit_price must be positive for a limit order.",
                )
            price_type = 11

        strategy_name = str(
            payload.get("strategy_name") or self.strategy_name
        ).strip()
        self.passorder_function(
            operation_type,
            1101,
            self.account_id,
            stock_code,
            price_type,
            model_price,
            int(quantity),
            strategy_name,
            1,
            client_order_id,
            self.context_info,
        )
        _log(
            "place_order_submitted",
            client_order_id=client_order_id,
            direction=direction,
            order_type=order_type,
            quantity=int(quantity),
            stock_code=stock_code,
        )
        return {
            "accepted": True,
            "client_order_id": client_order_id,
            "native_order_id": "",
        }

    def _cancel_order(self, payload):
        if not callable(self.cancel_function):
            raise _RequestError("QMT_API_UNAVAILABLE", "cancel is unavailable.")

        order_id = str(payload.get("order_id") or "").strip()
        if not order_id:
            raise _RequestError("INVALID_REQUEST", "order_id is required.")
        canceled = bool(
            self.cancel_function(
                order_id,
                self.account_id,
                ACCOUNT_TYPE,
                self.context_info,
            )
        )
        _log("cancel_order_submitted", canceled=canceled, order_id=order_id)
        return {"canceled": canceled, "order_id": order_id}

    def _subscribe(self, payload):
        if not callable(self.subscribe_quote_function):
            raise _RequestError(
                "QMT_API_UNAVAILABLE",
                "ContextInfo.subscribe_quote is unavailable.",
            )
        stock_code = str(payload.get("stock_code") or "").strip().upper()
        self._validate_stock_code(stock_code)

        existing_protocol_id = self._protocol_ids_by_stock_code.get(stock_code)
        if existing_protocol_id is not None:
            return {
                "subscribed": True,
                "subscription_id": existing_protocol_id,
                "stock_code": stock_code,
            }

        def quote_callback(quote_data):
            self._quote_callback(stock_code, quote_data)

        native_subscription_id = self.subscribe_quote_function(
            stock_code,
            "tick",
            "none",
            quote_callback,
        )
        protocol_subscription_id = str(native_subscription_id)
        if not protocol_subscription_id:
            raise _RequestError(
                "QMT_SUBSCRIPTION_FAILED",
                "QMT did not return a subscription identifier.",
            )
        self._subscriptions_by_protocol_id[protocol_subscription_id] = (
            native_subscription_id,
            stock_code,
        )
        self._protocol_ids_by_stock_code[stock_code] = protocol_subscription_id
        _log(
            "subscribed",
            stock_code=stock_code,
            subscription_id=protocol_subscription_id,
        )
        return {
            "subscribed": True,
            "subscription_id": protocol_subscription_id,
            "stock_code": stock_code,
        }

    def _unsubscribe_request(self, payload):
        protocol_subscription_id = str(
            payload.get("subscription_id") or ""
        ).strip()
        if not protocol_subscription_id:
            raise _RequestError(
                "INVALID_REQUEST",
                "subscription_id is required.",
            )
        unsubscribed = self._unsubscribe(protocol_subscription_id)
        return {
            "unsubscribed": unsubscribed,
            "subscription_id": protocol_subscription_id,
        }

    def _unsubscribe(self, protocol_subscription_id):
        subscription = self._subscriptions_by_protocol_id.get(
            protocol_subscription_id
        )
        if subscription is None:
            return False
        if not callable(self.unsubscribe_quote_function):
            raise _RequestError(
                "QMT_API_UNAVAILABLE",
                "ContextInfo.unsubscribe_quote is unavailable.",
            )

        native_subscription_id, stock_code = subscription
        unsubscribe_result = self.unsubscribe_quote_function(
            native_subscription_id
        )
        unsubscribed = unsubscribe_result is not False
        if unsubscribed:
            del self._subscriptions_by_protocol_id[protocol_subscription_id]
            self._protocol_ids_by_stock_code.pop(stock_code, None)
        _log(
            "unsubscribed",
            stock_code=stock_code,
            subscription_id=protocol_subscription_id,
            unsubscribed=unsubscribed,
        )
        return unsubscribed

    @staticmethod
    def _validate_stock_code(stock_code):
        stock_code_parts = stock_code.split(".")
        if (
            len(stock_code_parts) != 2
            or len(stock_code_parts[0]) != 6
            or not stock_code_parts[0].isdigit()
            or stock_code_parts[1] not in ("SH", "SZ", "BJ")
        ):
            raise _RequestError(
                "INVALID_REQUEST",
                "stock_code must use six digits and an SH, SZ, or BJ suffix.",
            )

    def _quote_callback(self, stock_code, quote_data):
        payload = _normalize_quote(stock_code, quote_data)
        self._publish_event("quote", payload)
        _log(
            "quote_event",
            last_price=payload["last_price"],
            stock_code=stock_code,
        )

    def _publish_event(self, operation, payload):
        event_message = _protocol_message(
            "event",
            "",
            operation,
            True,
            "",
            "",
            payload,
        )
        self._outgoing_messages.put((None, event_message))

    def _cache_response(self, request_id, response_message):
        self._cached_responses[request_id] = response_message
        self._cached_response_ids.append(request_id)
        while len(self._cached_response_ids) > MAXIMUM_CACHED_RESPONSES:
            oldest_request_id = self._cached_response_ids.pop(0)
            self._cached_responses.pop(oldest_request_id, None)


_gateway = None


def init(
    context_info,
    get_trade_detail_data_function=None,
    passorder_function=None,
    cancel_function=None,
    down_history_data_function=None,
    injected_account_id="",
    register_request_pump=True,
):
    global _gateway

    try:
        config = _load_config()
    except Exception as error:
        _log("config_load_failed", error=repr(error))
        raise
    account_id = config["account_id"] or str(injected_account_id or "").strip()
    _log(
        "init_start",
        account_configured=bool(account_id),
        bind_host=config["bind_host"],
        bind_port=config["bind_port"],
    )
    if not account_id:
        _log("account_missing", action="create_qmt_local_config.py")
        return None

    set_account_function = getattr(context_info, "set_account", None)
    if not callable(set_account_function):
        _log("set_account_unavailable")
        return None
    _log("set_account_start", account_id=account_id)
    try:
        set_account_function(account_id)
    except Exception as error:
        _log("set_account_failed", account_id=account_id, error=repr(error))
        raise
    _log("set_account_ok", account_id=account_id)

    get_market_data_function = getattr(
        context_info,
        "get_market_data_ex_ori",
        None,
    )
    history_api_name = "get_market_data_ex_ori"
    if not callable(get_market_data_function):
        get_market_data_function = getattr(
            context_info,
            "get_market_data_ex",
            None,
        )
        history_api_name = "get_market_data_ex"
    _log(
        "history_api_selected",
        api=history_api_name,
        available=callable(get_market_data_function),
        download_available=callable(down_history_data_function),
    )

    _gateway = LeanQmtGateway(
        context_info=context_info,
        account_id=account_id,
        get_trade_detail_data_function=get_trade_detail_data_function,
        passorder_function=passorder_function,
        cancel_function=cancel_function,
        down_history_data_function=down_history_data_function,
        get_market_data_function=get_market_data_function,
        subscribe_quote_function=getattr(context_info, "subscribe_quote", None),
        unsubscribe_quote_function=getattr(
            context_info,
            "unsubscribe_quote",
            None,
        ),
        bind_host=config["bind_host"],
        bind_port=config["bind_port"],
        strategy_name=config["strategy_name"],
    )
    try:
        _gateway.start()
    except Exception as error:
        _log("server_start_failed", error=repr(error))
        _gateway = None
        raise
    run_time_function = getattr(context_info, "run_time", None)
    if register_request_pump and callable(run_time_function):
        try:
            run_time_function(
                REQUEST_PUMP_CALLBACK_NAME,
                REQUEST_PUMP_PERIOD,
                REQUEST_PUMP_START_TIME,
                REQUEST_PUMP_MARKET,
            )
            _log(
                "request_pump_registered",
                callback=REQUEST_PUMP_CALLBACK_NAME,
                period=REQUEST_PUMP_PERIOD,
            )
        except Exception as error:
            _log(
                "request_pump_registration_failed",
                error=repr(error),
                fallback="handlebar",
            )
    elif register_request_pump:
        _log("request_pump_unavailable", fallback="handlebar")
    _log("init_complete", account_id=account_id)
    return _gateway


def handlebar(context_info):
    if _gateway is not None:
        _gateway.handlebar()


def qmt_gateway_timer_callback(context_info):
    if _gateway is not None:
        _gateway.handlebar()


def stop(context_info):
    global _gateway
    if _gateway is not None:
        _gateway.stop()
        _gateway = None


def account_callback(context_info, account_info):
    if _gateway is not None:
        _gateway.account_callback(account_info)


def order_callback(context_info, order_info):
    if _gateway is not None:
        _gateway.order_callback(order_info)


def deal_callback(context_info, deal_info):
    if _gateway is not None:
        _gateway.deal_callback(deal_info)


def position_callback(context_info, position_info):
    if _gateway is not None:
        _gateway.position_callback(position_info)


def order_error_callback(context_info, order_args, error_message):
    if _gateway is not None:
        _gateway.order_error_callback(order_args, error_message)
