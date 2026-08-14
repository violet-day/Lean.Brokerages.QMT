import os
import sys
import tempfile
import types
import unittest
from pathlib import Path


REPOSITORY_DIRECTORY = Path(__file__).resolve().parents[1]
ENTRY_SOURCE_PATH = (
    REPOSITORY_DIRECTORY / "qmt_python" / "qmt_gateway_entry.py"
)


def gateway_source(gateway_version, fail_initialization=False):
    return """
GATEWAY_VERSION = {gateway_version!r}
FAIL_INITIALIZATION = {fail_initialization!r}


def _log(message, **fields):
    pass


def init(context_info, register_request_pump=True, **arguments):
    context_info.initializations.append(
        (GATEWAY_VERSION, register_request_pump)
    )
    if FAIL_INITIALIZATION:
        raise RuntimeError("candidate initialization failed")
    if register_request_pump:
        context_info.run_time(
            "qmt_gateway_timer_callback",
            "500nMilliSecond",
            "2000-01-01 00:00:00",
            "SH",
        )
    context_info.active_gateway_version = GATEWAY_VERSION
    return object()


def stop(context_info):
    context_info.stops.append(GATEWAY_VERSION)
    context_info.active_gateway_version = None


def handlebar(context_info):
    context_info.handled_versions.append(GATEWAY_VERSION)


def qmt_gateway_timer_callback(context_info):
    context_info.handled_versions.append(GATEWAY_VERSION)


def account_callback(context_info, account_info):
    pass


def order_callback(context_info, order_info):
    pass


def deal_callback(context_info, deal_info):
    pass


def position_callback(context_info, position_info):
    pass


def order_error_callback(context_info, order_args, error_message):
    pass
""".format(
        gateway_version=gateway_version,
        fail_initialization=fail_initialization,
    )


class FakeContextInfo:
    def __init__(self):
        self.active_gateway_version = None
        self.handled_versions = []
        self.initializations = []
        self.scheduled_callbacks = []
        self.stops = []

    def run_time(self, function_name, period, start_time, market):
        self.scheduled_callbacks.append(
            (function_name, period, start_time, market)
        )


class QmtGatewayEntryTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.gateway_source_path = (
            Path(self.temporary_directory.name) / "lean_qmt_gateway.py"
        )
        self.source_modification_time = 2000000000
        self.write_gateway_source("initial")
        self.previous_gateway_module = sys.modules.get("lean_qmt_gateway")

        entry_module = types.ModuleType("qmt_gateway_entry_test")
        entry_module.REPOSITORY_PYTHON_DIRECTORY = self.temporary_directory.name
        entry_source = ENTRY_SOURCE_PATH.read_bytes()
        exec(
            compile(entry_source, str(ENTRY_SOURCE_PATH), "exec"),
            entry_module.__dict__,
        )
        self.entry_module = entry_module
        self.context_info = FakeContextInfo()
        self.entry_module.init(self.context_info)

    def tearDown(self):
        if self.previous_gateway_module is None:
            sys.modules.pop("lean_qmt_gateway", None)
        else:
            sys.modules["lean_qmt_gateway"] = self.previous_gateway_module
        self.temporary_directory.cleanup()

    def write_gateway_source(
        self,
        gateway_version,
        fail_initialization=False,
        invalid_source=False,
    ):
        if invalid_source:
            source_text = "def invalid syntax"
        else:
            source_text = gateway_source(
                gateway_version,
                fail_initialization=fail_initialization,
            )
        self.gateway_source_path.write_text(source_text)
        self.source_modification_time += 2
        os.utime(
            str(self.gateway_source_path),
            (
                self.source_modification_time,
                self.source_modification_time,
            ),
        )

    def test_unchanged_source_does_not_reload(self):
        self.entry_module.qmt_gateway_timer_callback(self.context_info)

        self.assertEqual(
            self.context_info.initializations,
            [("initial", True)],
        )
        self.assertEqual(self.context_info.stops, [])
        self.assertEqual(self.context_info.handled_versions, ["initial"])

    def test_changed_source_reloads_once_without_registering_another_timer(self):
        self.write_gateway_source("reloaded")

        self.entry_module.qmt_gateway_timer_callback(self.context_info)
        self.entry_module.qmt_gateway_timer_callback(self.context_info)

        self.assertEqual(
            self.context_info.initializations,
            [("initial", True), ("reloaded", False)],
        )
        self.assertEqual(self.context_info.stops, ["initial"])
        self.assertEqual(
            self.context_info.handled_versions,
            ["reloaded", "reloaded"],
        )
        self.assertEqual(len(self.context_info.scheduled_callbacks), 1)
        self.assertEqual(self.context_info.active_gateway_version, "reloaded")

    def test_load_failure_keeps_current_gateway_running(self):
        self.write_gateway_source("invalid", invalid_source=True)

        self.entry_module.qmt_gateway_timer_callback(self.context_info)

        self.assertEqual(
            self.context_info.initializations,
            [("initial", True)],
        )
        self.assertEqual(self.context_info.stops, [])
        self.assertEqual(self.context_info.handled_versions, ["initial"])
        self.assertEqual(self.context_info.active_gateway_version, "initial")

    def test_initialization_failure_rolls_back_current_gateway(self):
        self.write_gateway_source("broken", fail_initialization=True)

        self.entry_module.qmt_gateway_timer_callback(self.context_info)

        self.assertEqual(
            self.context_info.initializations,
            [
                ("initial", True),
                ("broken", False),
                ("initial", False),
            ],
        )
        self.assertEqual(self.context_info.stops, ["initial", "broken"])
        self.assertEqual(self.context_info.handled_versions, ["initial"])
        self.assertEqual(self.context_info.active_gateway_version, "initial")
        self.assertEqual(len(self.context_info.scheduled_callbacks), 1)


if __name__ == "__main__":
    unittest.main()
