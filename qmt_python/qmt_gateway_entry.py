# coding: gbk
"""Stable QMT strategy entry for the LEAN QMT Gateway."""

import os
import sys


REPOSITORY_PYTHON_DIRECTORY = globals().get(
    "REPOSITORY_PYTHON_DIRECTORY",
    r"C:\Users\nemo\lean\Lean.Brokerages.QMT\qmt_python",
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
    with open(source_path, "rb") as source_file:
        source_code = source_file.read()
    exec(compile(source_code, source_path, "exec"), module.__dict__)
    sys.modules[module_name] = module
    return module


GATEWAY_SOURCE_PATH = os.path.join(
    REPOSITORY_PYTHON_DIRECTORY,
    "lean_qmt_gateway.py",
)


def _source_version(source_path):
    source_stat = os.stat(source_path)
    return (source_stat.st_mtime, source_stat.st_size)


_gateway_module = _load_source(
    "lean_qmt_gateway",
    GATEWAY_SOURCE_PATH,
)
_gateway_source_version = _source_version(GATEWAY_SOURCE_PATH)
_last_reload_source_version = _gateway_source_version
_reload_in_progress = False


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


def _log_entry(message, **fields):
    log_function = getattr(_gateway_module, "_log", None)
    if callable(log_function):
        log_function(message, **fields)
        return

    log_parts = ["[lean_qmt_gateway]", str(message)]
    for field_name in sorted(fields):
        log_parts.append("%s=%s" % (field_name, fields[field_name]))
    print(" ".join(log_parts))


def _initialize_gateway(gateway_module, ContextInfo, register_request_pump):
    return gateway_module.init(
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
        register_request_pump=register_request_pump,
    )


def _reload_gateway_if_changed(ContextInfo):
    global _gateway_module
    global _gateway_source_version
    global _last_reload_source_version
    global _reload_in_progress

    if _reload_in_progress:
        return False

    try:
        source_version = _source_version(GATEWAY_SOURCE_PATH)
    except Exception as error:
        _log_entry("hot_reload_stat_failed", error=repr(error))
        return False

    if source_version in (
        _gateway_source_version,
        _last_reload_source_version,
    ):
        return False

    _reload_in_progress = True
    _last_reload_source_version = source_version
    previous_gateway_module = _gateway_module
    try:
        try:
            next_gateway_module = _load_source(
                "lean_qmt_gateway",
                GATEWAY_SOURCE_PATH,
            )
        except Exception as error:
            sys.modules["lean_qmt_gateway"] = previous_gateway_module
            _log_entry("hot_reload_load_failed", error=repr(error))
            return False

        try:
            previous_gateway_module.stop(ContextInfo)
            initialized_gateway = _initialize_gateway(
                next_gateway_module,
                ContextInfo,
                register_request_pump=False,
            )
            if initialized_gateway is None:
                raise RuntimeError("Reloaded Gateway did not initialize")
        except Exception as error:
            try:
                next_gateway_module.stop(ContextInfo)
            except Exception as stop_error:
                _log_entry(
                    "hot_reload_candidate_stop_failed",
                    error=repr(stop_error),
                )

            sys.modules["lean_qmt_gateway"] = previous_gateway_module
            rollback_success = False
            try:
                rollback_success = _initialize_gateway(
                    previous_gateway_module,
                    ContextInfo,
                    register_request_pump=False,
                ) is not None
            except Exception as rollback_error:
                _log_entry(
                    "hot_reload_rollback_failed",
                    error=repr(rollback_error),
                )
            _log_entry(
                "hot_reload_initialize_failed",
                error=repr(error),
                rollback_success=rollback_success,
            )
            return False

        _gateway_module = next_gateway_module
        _gateway_source_version = source_version
        _log_entry(
            "hot_reload_complete",
            source_modification_time=source_version[0],
            source_size=source_version[1],
        )
        return True
    finally:
        _reload_in_progress = False


def init(ContextInfo):
    initialized_gateway = _initialize_gateway(
        _gateway_module,
        ContextInfo,
        register_request_pump=True,
    )
    _log_entry("hot_reload_enabled", source_path=GATEWAY_SOURCE_PATH)
    return initialized_gateway


def handlebar(ContextInfo):
    _reload_gateway_if_changed(ContextInfo)
    return _gateway_module.handlebar(ContextInfo)


def qmt_gateway_timer_callback(ContextInfo):
    _reload_gateway_if_changed(ContextInfo)
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
