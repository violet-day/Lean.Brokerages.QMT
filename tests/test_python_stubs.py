import ast
from pathlib import Path
import unittest


REPOSITORY_DIRECTORY = Path(__file__).resolve().parents[1]
STUB_PACKAGE_DIRECTORY = (
    REPOSITORY_DIRECTORY
    / "python_stubs"
    / "QuantConnect"
    / "Brokerages"
    / "Qmt"
)


class PythonStubTests(unittest.TestCase):
    def test_stub_exposes_strategy_types_with_python_names(self):
        stub_tree = ast.parse(
            (STUB_PACKAGE_DIRECTORY / "__init__.pyi").read_text(encoding="utf-8")
        )
        classes_by_name = {
            node.name: node for node in stub_tree.body if isinstance(node, ast.ClassDef)
        }

        self.assertEqual(
            {
                "QmtBrokerageModel",
                "QmtMarketOrderStyle",
                "QmtOrderProperties",
            },
            set(classes_by_name),
        )

        enum_member_names = {
            target.id
            for statement in classes_by_name["QmtMarketOrderStyle"].body
            if isinstance(statement, ast.Assign)
            for target in statement.targets
            if isinstance(target, ast.Name)
        }
        self.assertEqual(
            {
                "LATEST_PRICE",
                "FIVE_LEVEL_IMMEDIATE_OR_CANCEL",
                "FIVE_LEVEL_IMMEDIATE_TO_LIMIT",
                "COUNTERPARTY_BEST",
                "OWN_BEST",
                "IMMEDIATE_OR_CANCEL",
                "FILL_OR_KILL",
            },
            enum_member_names,
        )

        order_property_method_names = {
            node.name
            for node in classes_by_name["QmtOrderProperties"].body
            if isinstance(node, ast.FunctionDef)
        }
        self.assertEqual(
            {"__init__", "clone", "market_order_style"},
            order_property_method_names,
        )

    def test_runtime_loader_references_qmt_assembly(self):
        loader_tree = ast.parse(
            (STUB_PACKAGE_DIRECTORY / "__init__.py").read_text(encoding="utf-8")
        )
        referenced_assemblies = [
            node.args[0].value
            for node in ast.walk(loader_tree)
            if isinstance(node, ast.Call)
            and isinstance(node.func, ast.Name)
            and node.func.id == "AddReference"
            and len(node.args) == 1
            and isinstance(node.args[0], ast.Constant)
            and isinstance(node.args[0].value, str)
        ]

        self.assertEqual(["QuantConnect.Brokerages.Qmt"], referenced_assemblies)

    def test_packaging_does_not_define_parent_quantconnect_packages(self):
        self.assertFalse(
            (REPOSITORY_DIRECTORY / "python_stubs" / "QuantConnect" / "__init__.py").exists()
        )
        self.assertFalse(
            (
                REPOSITORY_DIRECTORY
                / "python_stubs"
                / "QuantConnect"
                / "Brokerages"
                / "__init__.py"
            ).exists()
        )
        self.assertTrue((STUB_PACKAGE_DIRECTORY / "py.typed").exists())


if __name__ == "__main__":
    unittest.main()
