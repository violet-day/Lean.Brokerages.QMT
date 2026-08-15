import importlib.util
import os
import socket
import tempfile
import time
import unittest
from pathlib import Path


class QmtGatewayNetworkRecoveryTests(unittest.TestCase):
    def test_gateway_rebuilds_failed_network_server(self):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"

        with tempfile.TemporaryDirectory() as temporary_directory:
            runtime_log_path = str(Path(temporary_directory) / "gateway.log")
            previous_runtime_log_path = os.environ.get(
                "QMT_GATEWAY_RUNTIME_LOG_PATH"
            )
            os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = runtime_log_path
            module_specification = importlib.util.spec_from_file_location(
                "lean_qmt_gateway_network_test",
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

            gateway = gateway_module.LeanQmtGateway(
                context_info=None,
                account_id="network-test",
                bind_port=0,
            )
            try:
                gateway.start()
                self._connect(gateway.bound_port)

                gateway._server_socket.close()
                self._wait_until_stopped(gateway)

                self.assertTrue(gateway.recover_network_server_if_needed())
                self._connect(gateway.bound_port)
            finally:
                gateway.stop()

    @staticmethod
    def _connect(port):
        client_socket = socket.create_connection(("127.0.0.1", port), timeout=2)
        client_socket.close()

    @staticmethod
    def _wait_until_stopped(gateway):
        deadline = time.monotonic() + 3
        while gateway.is_running and time.monotonic() < deadline:
            time.sleep(0.05)
        if gateway.is_running:
            raise AssertionError("Gateway network server did not stop")


if __name__ == "__main__":
    unittest.main()
