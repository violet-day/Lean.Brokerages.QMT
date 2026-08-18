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


if __name__ == "__main__":
    unittest.main()
