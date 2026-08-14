# coding: gbk
"""Stable QMT strategy entry for the LEAN QMT Gateway."""

import os
import sys


REPOSITORY_PYTHON_DIRECTORY = (
    r"C:\Users\nemo\lean\Lean.Brokerages.QMT\qmt_python"
)


if not os.path.isdir(REPOSITORY_PYTHON_DIRECTORY):
    raise RuntimeError(
        "Lean.Brokerages.QMT python directory does not exist: %s"
        % REPOSITORY_PYTHON_DIRECTORY
    )

if REPOSITORY_PYTHON_DIRECTORY not in sys.path:
    sys.path.insert(0, REPOSITORY_PYTHON_DIRECTORY)


def _load_source(module_name, source_path):
    module = type(sys)(module_name)
    module.__file__ = source_path
    module.module_directory = os.path.dirname(os.path.abspath(source_path))
    sys.modules[module_name] = module
    with open(source_path, "rb") as source_file:
        source_code = source_file.read()
    exec(compile(source_code, source_path, "exec"), module.__dict__)
    return module


_gateway_module = _load_source(
    "lean_qmt_gateway",
    os.path.join(
        REPOSITORY_PYTHON_DIRECTORY,
        "lean_qmt_gateway.py",
    ),
)


def _injected_account_id():
    for variable_name in ("account", "account_id", "accountID", "accid"):
        value = globals().get(variable_name)
        if value:
            return str(value)
    return ""


def _injected_function(function_name):
    function = globals().get(function_name)
    if callable(function):
        return function
    return None


def init(ContextInfo):
    return _gateway_module.init(
        ContextInfo,
        get_trade_detail_data_function=_injected_function(
            "get_trade_detail_data"
        ),
        passorder_function=_injected_function("passorder"),
        cancel_function=_injected_function("cancel"),
        down_history_data_function=_injected_function(
            "down_history_data"
        ),
        injected_account_id=_injected_account_id(),
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
