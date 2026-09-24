import importlib.util
import os
import tempfile
import unittest
from pathlib import Path


class QmtGatewayAccountSelectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repository_directory = Path(__file__).resolve().parents[1]
        module_path = repository_directory / "qmt_python" / "lean_qmt_gateway.py"
        cls.temporary_directory = tempfile.TemporaryDirectory()
        runtime_log_path = str(
            Path(cls.temporary_directory.name)
            / "qmt-gateway-account-selection.log"
        )
        previous_runtime_log_path = os.environ.get(
            "QMT_GATEWAY_RUNTIME_LOG_PATH"
        )
        os.environ["QMT_GATEWAY_RUNTIME_LOG_PATH"] = runtime_log_path
        module_specification = importlib.util.spec_from_file_location(
            "lean_qmt_gateway_account_selection_test",
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

    def test_classifies_configured_live_account(self):
        self.assertFalse(
            self.gateway_module._is_simulation_account("40800875")
        )

    def test_defaults_other_accounts_to_simulation(self):
        self.assertTrue(
            self.gateway_module._is_simulation_account("simulation-account")
        )

    def test_hello_exposes_account_classification(self):
        for account_id, expected_is_simulation in (
            ("40800875", False),
            ("simulation-account", True),
        ):
            with self.subTest(account_id=account_id):
                gateway = self.gateway_module.LeanQmtGateway(
                    context_info=object(),
                    account_id=account_id,
                )
                response = gateway._process_request(
                    {
                        "protocol_version": 1,
                        "message_type": "request",
                        "request_id": "account-classification-test",
                        "operation": "hello",
                        "payload": {"account_id": account_id},
                    }
                )

                self.assertTrue(response["success"])
                self.assertIs(
                    expected_is_simulation,
                    response["payload"]["is_simulation"],
                )


if __name__ == "__main__":
    unittest.main()
