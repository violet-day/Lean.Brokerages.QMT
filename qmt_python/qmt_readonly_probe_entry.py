# coding: gbk
"""Stable Big QMT strategy entry.

Import this file into QMT once. The implementation lives in the Git checkout
and is reloaded every time QMT starts/restarts this strategy.
"""

import importlib
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

import lean_qmt_readonly_probe as _probe

_probe = importlib.reload(_probe)


def _injected_account_id():
    for variable_name in ("account", "account_id", "accountID", "accid"):
        value = globals().get(variable_name)
        if value:
            return str(value)
    return ""


def init(ContextInfo):
    try:
        trade_detail_query = get_trade_detail_data
    except NameError:
        trade_detail_query = None

    return _probe.init(
        ContextInfo,
        get_trade_detail_data_function=trade_detail_query,
        injected_account_id=_injected_account_id(),
    )


def handlebar(ContextInfo):
    return _probe.handlebar(ContextInfo)


def account_callback(ContextInfo, accountInfo):
    return _probe.account_callback(ContextInfo, accountInfo)


def order_callback(ContextInfo, orderInfo):
    return _probe.order_callback(ContextInfo, orderInfo)


def deal_callback(ContextInfo, dealInfo):
    return _probe.deal_callback(ContextInfo, dealInfo)


def position_callback(ContextInfo, positionInfo):
    return _probe.position_callback(ContextInfo, positionInfo)


def orderError_callback(ContextInfo, orderArgs, errorMessage):
    return _probe.order_error_callback(
        ContextInfo,
        orderArgs,
        errorMessage,
    )
