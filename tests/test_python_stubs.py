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
                "QmtEquityMarketRules",
                "QmtMarket",
                "QmtMarketOrderStyle",
                "QmtOrderEventStore",
                "QmtOrderProperties",
                "QmtSecurityStatus",
                "QmtSymbolMapper",
            },
            set(classes_by_name),
        )

        security_status_methods = {
            node.name: node
            for node in classes_by_name["QmtSecurityStatus"].body
            if isinstance(node, ast.FunctionDef)
        }
        self.assertEqual(
            {"is_trading"},
            set(security_status_methods),
        )
        self.assertEqual(
            ["staticmethod"],
            [
                ast.unparse(decorator)
                for decorator in security_status_methods["is_trading"].decorator_list
            ],
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

        brokerage_model_method_names = {
            node.name
            for node in classes_by_name["QmtBrokerageModel"].body
            if isinstance(node, ast.FunctionDef)
        }
        self.assertEqual(
            {"__init__", "account_currency"},
            brokerage_model_method_names,
        )
        self.assertEqual(
            {
                "QuantConnect.Brokerages.DefaultBrokerageModel",
                "QuantConnect.Interfaces.IAccountCurrencyProvider",
            },
            {
                ast.unparse(base)
                for base in classes_by_name["QmtBrokerageModel"].bases
            },
        )
        market_member_names = {
            statement.target.id
            for statement in classes_by_name["QmtMarket"].body
            if isinstance(statement, ast.AnnAssign)
            and isinstance(statement.target, ast.Name)
        }
        self.assertEqual(
            {
                "NAME",
                "ACCOUNT_CURRENCY",
                "calendar_coverage_start",
                "calendar_coverage_end",
                "calendar_source",
            },
            market_member_names,
        )
        market_method_names = {
            node.name
            for node in classes_by_name["QmtMarket"].body
            if isinstance(node, ast.FunctionDef)
        }
        self.assertEqual(
            {
                "register_metadata",
                "is_trading_day",
                "is_market_open",
                "ensure_calendar_covers",
            },
            market_method_names,
        )

        equity_market_rule_methods = {
            node.name: node
            for node in classes_by_name["QmtEquityMarketRules"].body
            if isinstance(node, ast.FunctionDef)
        }
        self.assertEqual(
            {
                "get_daily_price_limit_percentage",
                "get_minimum_buy_order_quantity",
            },
            set(equity_market_rule_methods),
        )
        self.assertTrue(
            all(
                [ast.unparse(decorator) for decorator in method.decorator_list]
                == ["staticmethod"]
                for method in equity_market_rule_methods.values()
            )
        )

        symbol_mapper_method_names = {
            node.name
            for node in classes_by_name["QmtSymbolMapper"].body
            if isinstance(node, ast.FunctionDef)
        }
        self.assertEqual(
            {
                "__init__",
                "get_brokerage_symbol",
                "get_lean_symbol",
                "get_lean_symbol_from_exchange",
            },
            symbol_mapper_method_names,
        )
        self.assertEqual(
            {"QuantConnect.Brokerages.ISymbolMapper"},
            {
                ast.unparse(base)
                for base in classes_by_name["QmtSymbolMapper"].bases
            },
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

        order_event_store_methods = {
            node.name: node
            for node in classes_by_name["QmtOrderEventStore"].body
            if isinstance(node, ast.FunctionDef)
        }
        self.assertEqual(
            {"get_invested_at"},
            set(order_event_store_methods),
        )
        self.assertEqual(
            ["staticmethod"],
            [
                ast.unparse(decorator)
                for decorator in order_event_store_methods[
                    "get_invested_at"
                ].decorator_list
            ],
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
