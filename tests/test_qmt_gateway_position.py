import importlib.util
import os
import tempfile
import unittest
from pathlib import Path


class NativePosition:
    m_strExchangeID = "SH"
    m_strInstrumentID = "600000"
    m_nVolume = 300
    m_nCanUseVolume = 200
    m_dOpenPrice = 9.23
    m_dLastPrice = 9.25
    m_dMarketValue = 2775


class QmtGatewayPositionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"
        cls.temporary_directory = tempfile.TemporaryDirectory()
        runtime_log_path = str(
            Path(cls.temporary_directory.name) / "qmt-gateway-position.log"
        )
        previous_runtime_log_path = os.environ.get(
            "QMT_GATEWAY_RUNTIME_LOG_PATH"
        )
        os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = runtime_log_path
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_position_test",
            module_path,
        )
        cls.gateway_module = importlib.util.module_from_spec(module_specification)
        try:
            module_specification.loader.exec_module(cls.gateway_module)
        finally:
            if previous_runtime_log_path is None:
                del os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"]
            else:
                os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = (
                    previous_runtime_log_path
                )

    @classmethod
    def tearDownClass(cls):
        cls.temporary_directory.cleanup()

    def test_normalizes_total_and_available_position_volume(self):
        normalized_position = self.gateway_module._normalize_position(
            NativePosition()
        )

        self.assertEqual("600000.SH", normalized_position["stock_code"])
        self.assertEqual(300, normalized_position["volume"])
        self.assertEqual(200, normalized_position["available_volume"])


if __name__ == "__main__":
    unittest.main()
