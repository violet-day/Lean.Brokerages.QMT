import ast
import unittest
from pathlib import Path


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


if __name__ == "__main__":
    unittest.main()
