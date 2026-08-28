import importlib.util
import os
import tempfile
import time
import unittest
from pathlib import Path


class QmtGatewayRuntimeLogTests(unittest.TestCase):
    def test_writes_one_bounded_log_group_per_date(self):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"

        with tempfile.TemporaryDirectory() as temporary_directory:
            runtime_log_path = Path(temporary_directory) / "gateway.log"
            expired_runtime_log_path = (
                Path(temporary_directory) / "gateway-2000-01-01.log"
            )
            expired_runtime_log_path.write_text("expired", encoding="utf-8")
            previous_runtime_log_path = os.environ.get(
                "QMT_GATEWAY_RUNTIME_LOG_PATH"
            )
            os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = str(runtime_log_path)
            module_specification = importlib.util.spec_from_file_location(
                "lean_qmt_gateway_runtime_log_test",
                module_path,
            )
            gateway_module = importlib.util.module_from_spec(module_specification)
            try:
                module_specification.loader.exec_module(gateway_module)
            finally:
                if previous_runtime_log_path is None:
                    del os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"]
                else:
                    os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = (
                        previous_runtime_log_path
                    )

            current_log_date = time.strftime("%Y-%m-%d")
            dated_runtime_log_path = (
                Path(temporary_directory)
                / ("gateway-%s.log" % current_log_date)
            )
            self.assertTrue(dated_runtime_log_path.is_file())
            self.assertIn(
                "module_loaded",
                dated_runtime_log_path.read_text(encoding="utf-8"),
            )
            self.assertFalse(runtime_log_path.exists())
            self.assertFalse(expired_runtime_log_path.exists())


if __name__ == "__main__":
    unittest.main()
