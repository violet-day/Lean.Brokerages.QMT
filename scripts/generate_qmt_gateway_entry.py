#!/usr/bin/env python3
"""Generate the self-contained source pasted into the Big QMT editor."""

import argparse
from pathlib import Path


SOURCE_CHUNK_SIZE = 3000


def _generated_source(gateway_source):
    source_chunks = [
        gateway_source[index:index + SOURCE_CHUNK_SIZE]
        for index in range(0, len(gateway_source), SOURCE_CHUNK_SIZE)
    ]
    indented_source_chunks = "\n".join(
        "    %r" % source_chunk for source_chunk in source_chunks
    )
    return '''# coding: gbk
"""Self-contained QMT strategy entry. Generated; do not edit directly."""

import sys


GATEWAY_BIND_HOST = "0.0.0.0"
GATEWAY_BIND_PORT = 17890
GATEWAY_STRATEGY_NAME = "LeanQmtGateway"
GATEWAY_ACCOUNT_ID = ""

_GATEWAY_SOURCE = (
%s
)


def _gateway_configuration():
    return {
        "account_id": GATEWAY_ACCOUNT_ID,
        "bind_host": GATEWAY_BIND_HOST,
        "bind_port": GATEWAY_BIND_PORT,
        "strategy_name": GATEWAY_STRATEGY_NAME,
    }


def _injected_account_id(context_info):
    for value_source in (globals(), context_info):
        for variable_name in ("account", "account_id", "accountID", "accid"):
            if isinstance(value_source, dict):
                value = value_source.get(variable_name)
            else:
                value = getattr(value_source, variable_name, None)
            if value:
                return str(value)
    return ""


def _injected_function(function_name):
    function = globals().get(function_name)
    if not callable(function):
        builtin_namespace = globals().get("__builtins__")
        if isinstance(builtin_namespace, dict):
            function = builtin_namespace.get(function_name)
        else:
            function = getattr(builtin_namespace, function_name, None)
    if callable(function):
        return function
    return None


def _load_embedded_gateway():
    gateway_module = type(sys)("lean_qmt_gateway")
    gateway_module.__file__ = "<embedded lean_qmt_gateway.py>"
    gateway_module.module_directory = ""
    gateway_module.RUNTIME_FILE_LOG_ENABLED = False
    exec(
        compile(_GATEWAY_SOURCE, gateway_module.__file__, "exec"),
        gateway_module.__dict__,
    )
    gateway_module._load_config = _gateway_configuration
    sys.modules["lean_qmt_gateway"] = gateway_module
    return gateway_module


_gateway_module = _load_embedded_gateway()


def init(ContextInfo):
    return _gateway_module.init(
        ContextInfo,
        get_trade_detail_data_function=_injected_function(
            "get_trade_detail_data"
        ),
        passorder_function=_injected_function("passorder"),
        cancel_function=_injected_function("cancel"),
        down_history_data_function=_injected_function("down_history_data"),
        injected_account_id=_injected_account_id(ContextInfo),
    )


def handlebar(ContextInfo):
    return _gateway_module.handlebar(ContextInfo)


def qmt_gateway_timer_callback(ContextInfo):
    return _gateway_module.qmt_gateway_timer_callback(ContextInfo)


def stop(ContextInfo):
    return _gateway_module.stop(ContextInfo)


def account_callback(ContextInfo, accountInfo):
    return _gateway_module.account_callback(ContextInfo, accountInfo)


def order_callback(ContextInfo, orderInfo):
    return _gateway_module.order_callback(ContextInfo, orderInfo)


def deal_callback(ContextInfo, dealInfo):
    return _gateway_module.deal_callback(ContextInfo, dealInfo)


def position_callback(ContextInfo, positionInfo):
    return _gateway_module.position_callback(ContextInfo, positionInfo)


def orderError_callback(ContextInfo, orderArgs, errorMessage):
    return _gateway_module.order_error_callback(
        ContextInfo,
        orderArgs,
        errorMessage,
    )
''' % indented_source_chunks


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    arguments = parser.parse_args()

    repository_directory = Path(__file__).resolve().parents[1]
    gateway_source_path = (
        repository_directory / "qmt_python" / "lean_qmt_gateway.py"
    )
    gateway_entry_path = (
        repository_directory / "qmt_python" / "qmt_gateway_entry.py"
    )
    expected_source = _generated_source(gateway_source_path.read_text())

    if arguments.check:
        if gateway_entry_path.read_text() != expected_source:
            raise SystemExit("qmt_gateway_entry.py is stale")
        return

    gateway_entry_path.write_text(expected_source)


if __name__ == "__main__":
    main()
