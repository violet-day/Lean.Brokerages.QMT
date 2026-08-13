# coding: gbk
"""Stable Big QMT strategy entry.

Import this file into QMT once. The implementation lives in the Git checkout
and is reloaded every time QMT starts/restarts this strategy.
"""

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
    sys.modules[module_name] = module
    with open(source_path, "rb") as source_file:
        source_code = source_file.read()
    exec(compile(source_code, source_path, "exec"), module.__dict__)
    return module


_probe = _load_source(
    "lean_qmt_readonly_probe",
    os.path.join(
        REPOSITORY_PYTHON_DIRECTORY,
        "lean_qmt_readonly_probe.py",
    ),
)


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
