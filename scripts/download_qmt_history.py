#!/usr/bin/env python3
"""Download one day of QMT minute history into the LEAN data layout."""

import argparse
import json
import os
import re
import socket
import sys
import tempfile
import uuid
import zipfile
from datetime import datetime, timedelta, timezone
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path


PROTOCOL_VERSION = 1
SHANGHAI_TIMEZONE = timezone(timedelta(hours=8))
QMT_TICKER_PATTERN = re.compile(r"^[0-9]{6}\.(SH|SZ|BJ)$")


def default_lean_data_root():
    configured_data_root = os.environ.get("LEAN_DATA_ROOT")
    if configured_data_root:
        return Path(configured_data_root).expanduser()
    if os.name == "nt":
        return Path.home() / "lean_project" / "data"
    return Path.home() / "Workspace" / "quant" / "lean-project" / "data"


def default_gateway_host():
    configured_gateway_host = os.environ.get("QMT_GATEWAY_HOST")
    if configured_gateway_host:
        return configured_gateway_host
    return "127.0.0.1" if os.name == "nt" else "192.168.50.135"


def parse_arguments():
    argument_parser = argparse.ArgumentParser(
        description="Download one day of QMT minute history in LEAN/QC format."
    )
    argument_parser.add_argument(
        "--ticker",
        required=True,
        help="QMT stock code with exchange suffix, for example 600000.SH.",
    )
    argument_parser.add_argument(
        "--date",
        required=True,
        help="Trading date in YYYYMMDD or YYYY-MM-DD format.",
    )
    argument_parser.add_argument(
        "--host",
        default=default_gateway_host(),
        help="QMT Gateway host; defaults to the Windows QMT host on macOS.",
    )
    argument_parser.add_argument(
        "--port",
        type=int,
        default=int(os.environ.get("QMT_GATEWAY_PORT", "17890")),
        help="QMT Gateway port (default: 17890).",
    )
    argument_parser.add_argument(
        "--data-root",
        type=Path,
        default=default_lean_data_root(),
        help="LEAN data root; defaults to the local LEAN data directory.",
    )
    argument_parser.add_argument(
        "--timeout",
        type=int,
        default=120,
        help="Gateway request timeout in seconds (default: 120).",
    )
    return argument_parser.parse_args()


def normalize_ticker(ticker):
    normalized_ticker = str(ticker or "").strip().upper()
    if not QMT_TICKER_PATTERN.fullmatch(normalized_ticker):
        raise ValueError(
            "ticker must contain six digits and an SH, SZ, or BJ suffix"
        )
    return normalized_ticker


def parse_trading_date(date_text):
    normalized_date_text = str(date_text or "").strip().replace("-", "")
    try:
        return datetime.strptime(normalized_date_text, "%Y%m%d").date()
    except ValueError as error:
        raise ValueError("date must use YYYYMMDD or YYYY-MM-DD") from error


def parse_qmt_time(time_text):
    normalized_time_text = str(time_text or "").strip()
    exact_format_by_length = {
        17: "%Y%m%d%H%M%S%f",
        14: "%Y%m%d%H%M%S",
        8: "%Y%m%d",
    }
    exact_format = exact_format_by_length.get(len(normalized_time_text))
    if exact_format is not None:
        try:
            return datetime.strptime(normalized_time_text, exact_format)
        except ValueError:
            pass

    if normalized_time_text.isdigit():
        unix_time = int(normalized_time_text)
        if len(normalized_time_text) >= 13:
            unix_time /= 1000
        return datetime.fromtimestamp(unix_time, timezone.utc).astimezone(
            SHANGHAI_TIMEZONE
        ).replace(tzinfo=None)

    try:
        parsed_time = datetime.fromisoformat(normalized_time_text)
    except ValueError as error:
        raise ValueError(
            "unsupported QMT history time: %s" % normalized_time_text
        ) from error
    if parsed_time.tzinfo is not None:
        parsed_time = parsed_time.astimezone(SHANGHAI_TIMEZONE).replace(tzinfo=None)
    return parsed_time


def scaled_price(price_value):
    price = Decimal(str(price_value))
    if price <= 0:
        raise ValueError("history prices must be positive")
    return int((price * Decimal("10000")).quantize(Decimal("1"), ROUND_HALF_UP))


def quantconnect_minute_rows(history_bars, trading_date):
    bars_by_time = {}
    for history_bar in history_bars:
        if not isinstance(history_bar, dict):
            raise ValueError("QMT history bars must be JSON objects")
        bar_time = parse_qmt_time(history_bar.get("time"))
        if bar_time.date() == trading_date:
            bars_by_time[bar_time] = history_bar

    if not bars_by_time:
        raise ValueError(
            "QMT returned no minute bars for %s"
            % trading_date.strftime("%Y%m%d")
        )

    quantconnect_rows = []
    for bar_time in sorted(bars_by_time):
        history_bar = bars_by_time[bar_time]
        milliseconds_from_midnight = (
            ((bar_time.hour * 60 + bar_time.minute) * 60 + bar_time.second) * 1000
            + bar_time.microsecond // 1000
        )
        volume = Decimal(str(history_bar.get("volume", 0)))
        if volume < 0:
            raise ValueError("history volume must not be negative")
        quantconnect_rows.append(
            ",".join(
                str(field_value)
                for field_value in (
                    milliseconds_from_midnight,
                    scaled_price(history_bar.get("open")),
                    scaled_price(history_bar.get("high")),
                    scaled_price(history_bar.get("low")),
                    scaled_price(history_bar.get("close")),
                    int(volume.quantize(Decimal("1"), ROUND_HALF_UP)),
                )
            )
        )
    return quantconnect_rows


def write_quantconnect_minute_zip(
    quantconnect_rows,
    ticker,
    trading_date,
    lean_data_root,
):
    symbol = ticker.split(".", 1)[0].lower()
    date_key = trading_date.strftime("%Y%m%d")
    target_directory = (
        Path(lean_data_root).expanduser()
        / "equity"
        / "china"
        / "minute"
        / symbol
    )
    target_directory.mkdir(parents=True, exist_ok=True)
    target_zip_path = target_directory / (date_key + "_trade.zip")
    zip_entry_name = "%s_%s_trade.csv" % (date_key, symbol)
    csv_content = "\n".join(quantconnect_rows) + "\n"

    temporary_file_descriptor, temporary_zip_name = tempfile.mkstemp(
        prefix=".%s." % target_zip_path.name,
        suffix=".tmp",
        dir=str(target_directory),
    )
    os.close(temporary_file_descriptor)
    temporary_zip_path = Path(temporary_zip_name)
    try:
        with zipfile.ZipFile(
            temporary_zip_path,
            "w",
            compression=zipfile.ZIP_DEFLATED,
        ) as temporary_zip_file:
            temporary_zip_file.writestr(zip_entry_name, csv_content)
        with zipfile.ZipFile(temporary_zip_path, "r") as verification_zip_file:
            if verification_zip_file.testzip() is not None:
                raise zipfile.BadZipFile("generated QMT history ZIP is corrupted")
            if verification_zip_file.namelist() != [zip_entry_name]:
                raise zipfile.BadZipFile(
                    "generated QMT history ZIP has an invalid entry"
                )
        with temporary_zip_path.open("rb+") as temporary_zip_file:
            os.fsync(temporary_zip_file.fileno())
        os.replace(temporary_zip_path, target_zip_path)
    finally:
        temporary_zip_path.unlink(missing_ok=True)
    return target_zip_path


class QmtGatewayApiClient:
    def __init__(self, host, port, timeout_seconds):
        self.host = host
        self.port = port
        self.timeout_seconds = timeout_seconds
        self.connection_socket = None
        self.response_reader = None
        self.request_writer = None

    def __enter__(self):
        self.connection_socket = socket.create_connection(
            (self.host, self.port),
            timeout=self.timeout_seconds,
        )
        self.connection_socket.settimeout(self.timeout_seconds)
        self.response_reader = self.connection_socket.makefile("r", encoding="utf-8")
        self.request_writer = self.connection_socket.makefile("w", encoding="utf-8")
        return self

    def __exit__(self, exception_type, exception, traceback):
        if self.request_writer is not None:
            self.request_writer.close()
        if self.response_reader is not None:
            self.response_reader.close()
        if self.connection_socket is not None:
            self.connection_socket.close()

    def request(self, operation, payload=None):
        request_id = uuid.uuid4().hex
        request_message = {
            "protocol_version": PROTOCOL_VERSION,
            "message_type": "request",
            "request_id": request_id,
            "operation": operation,
            "payload": payload or {},
        }
        self.request_writer.write(
            json.dumps(request_message, separators=(",", ":")) + "\n"
        )
        self.request_writer.flush()

        while True:
            response_line = self.response_reader.readline()
            if not response_line:
                raise ConnectionError("QMT Gateway closed the connection")
            try:
                response_message = json.loads(response_line)
            except json.JSONDecodeError as error:
                raise ValueError("QMT Gateway returned invalid JSON") from error
            if response_message.get("message_type") == "event":
                continue
            if response_message.get("request_id") != request_id:
                continue
            if response_message.get("protocol_version") != PROTOCOL_VERSION:
                raise RuntimeError("QMT Gateway protocol version mismatch")
            if response_message.get("operation") != operation:
                raise RuntimeError("QMT Gateway response operation mismatch")
            if response_message.get("success") is not True:
                raise RuntimeError(
                    "QMT Gateway %s failed: %s %s"
                    % (
                        operation,
                        response_message.get("error_code", ""),
                        response_message.get("error_message", ""),
                    )
                )
            response_payload = response_message.get("payload")
            if not isinstance(response_payload, dict):
                raise RuntimeError("QMT Gateway response payload must be an object")
            return response_payload


def write_evidence(stage, status, details=""):
    evidence_line = "[qmt-history] stage=%s status=%s" % (stage, status)
    if details:
        evidence_line += " " + details
    print(evidence_line, file=sys.stderr, flush=True)


def main():
    current_stage = "arguments"
    try:
        arguments = parse_arguments()
        ticker = normalize_ticker(arguments.ticker)
        trading_date = parse_trading_date(arguments.date)
        if arguments.timeout <= 0:
            raise ValueError("timeout must be positive")

        current_stage = "connect"
        write_evidence(
            current_stage,
            "start",
            "host=%s port=%s" % (arguments.host, arguments.port),
        )
        with QmtGatewayApiClient(
            arguments.host,
            arguments.port,
            arguments.timeout,
        ) as gateway_client:
            hello_payload = gateway_client.request("hello", {"account_id": ""})
            if not str(hello_payload.get("account_id") or "").strip():
                raise RuntimeError("QMT Gateway hello returned no account_id")
            write_evidence(
                current_stage,
                "ok",
                "account_discovered=true trading_enabled=%s"
                % str(bool(hello_payload.get("trading_enabled"))).lower(),
            )

            current_stage = "query-history"
            date_key = trading_date.strftime("%Y%m%d")
            write_evidence(
                current_stage,
                "start",
                "ticker=%s date=%s period=1m" % (ticker, date_key),
            )
            history_payload = gateway_client.request(
                "query_history",
                {
                    "stock_code": ticker,
                    "period": "1m",
                    "start_time": date_key + "000000",
                    "end_time": date_key + "235959",
                },
            )

        history_bars = history_payload.get("bars")
        if not isinstance(history_bars, list):
            raise RuntimeError("QMT query_history returned no bars list")
        quantconnect_rows = quantconnect_minute_rows(history_bars, trading_date)
        write_evidence(
            current_stage,
            "ok",
            "ticker=%s date=%s bars=%s" % (ticker, date_key, len(quantconnect_rows)),
        )

        current_stage = "write"
        target_zip_path = write_quantconnect_minute_zip(
            quantconnect_rows,
            ticker,
            trading_date,
            arguments.data_root,
        )
        write_evidence(
            current_stage,
            "ok",
            "bars=%s path=%s" % (len(quantconnect_rows), target_zip_path),
        )
        write_evidence("run", "ok", "path=%s" % target_zip_path)
        print(target_zip_path)
        return 0
    except Exception as error:
        failure_reason = str(error).replace("\r", " ").replace("\n", " ")
        write_evidence(
            "run",
            "failed",
            'failed_stage=%s reason="%s"' % (current_stage, failure_reason),
        )
        return 1


if __name__ == "__main__":
    sys.exit(main())
