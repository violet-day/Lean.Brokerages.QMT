import importlib.util
import unittest
from pathlib import Path


class QmtGatewayAccountSelectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_account_selection_test",
            module_path,
        )
        cls.gateway_module = importlib.util.module_from_spec(module_specification)
        module_specification.loader.exec_module(cls.gateway_module)

    def test_uses_qmt_injected_account(self):
        self.assertEqual(
            "injected-account",
            self.gateway_module._select_account_id("", "injected-account"),
        )

    def test_uses_configured_account_when_qmt_does_not_inject_one(self):
        self.assertEqual(
            "configured-account",
            self.gateway_module._select_account_id("configured-account", ""),
        )

    def test_rejects_conflicting_accounts(self):
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            self.gateway_module._select_account_id(
                "configured-account",
                "injected-account",
            )

    def test_detects_simulation_qmt_runtime(self):
        simulation_runtime_path = (
            "C:\\Program Files (x86)\\Broker QMT\\"
            "\u4ea4\u6613\u7aef\u6a21\u62df\\bin.x64\\XtItClient.exe"
        )
        self.assertTrue(
            self.gateway_module._is_simulation_runtime(
                [simulation_runtime_path]
            )
        )

    def test_does_not_classify_live_qmt_runtime_as_simulation(self):
        self.assertFalse(
            self.gateway_module._is_simulation_runtime(
                [r"C:\Program Files (x86)\Broker QMT\bin.x64\XtItClient.exe"]
            )
        )


if __name__ == "__main__":
    unittest.main()
