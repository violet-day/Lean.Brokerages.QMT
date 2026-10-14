import ast
import builtins
import sys
import unittest
from pathlib import Path
from unittest.mock import patch


class PythonCompatibilityTests(unittest.TestCase):
    def test_qmt_modules_use_python_36_syntax(self):
        repository_directory = Path(__file__).resolve().parents[1]

        for source_path in sorted(
            (repository_directory / "qmt_python").glob("*.py")
        ):
            with self.subTest(source_path=source_path.name):
                ast.parse(
                    source_path.read_text(),
                    filename=str(source_path),
                    feature_version=(3, 6),
                )

    def test_generated_gateway_entry_loads_when_file_io_is_forbidden(self):
        repository_directory = Path(__file__).resolve().parents[1]
        entry_source_path = (
            repository_directory / "qmt_python" / "qmt_gateway_entry.py"
        )
        entry_source = entry_source_path.read_text()
        entry_namespace = {
            "__builtins__": builtins,
            "__file__": str(entry_source_path),
            "__name__": "qmt_gateway_entry_file_io_test",
        }
        previous_gateway_module = sys.modules.get("lean_qmt_gateway")

        try:
            with patch(
                "builtins.open",
                side_effect=PermissionError("Forbidden FileIO"),
            ):
                exec(
                    compile(entry_source, str(entry_source_path), "exec"),
                    entry_namespace,
                )
        finally:
            if previous_gateway_module is None:
                sys.modules.pop("lean_qmt_gateway", None)
            else:
                sys.modules["lean_qmt_gateway"] = previous_gateway_module

        self.assertFalse(
            entry_namespace["_gateway_module"].RUNTIME_FILE_LOG_ENABLED
        )
        self.assertEqual(
            "0.0.0.0",
            entry_namespace["_gateway_module"]._load_config()["bind_host"],
        )


if __name__ == "__main__":
    unittest.main()
