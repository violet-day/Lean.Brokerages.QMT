import importlib.util
import unittest
from pathlib import Path


class ReconstructQmtOrderArchiveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        script_path = (
            Path(__file__).resolve().parents[1]
            / "scripts"
            / "reconstruct_qmt_order_archive.py"
        )
        module_specification = importlib.util.spec_from_file_location(
            "reconstruct_qmt_order_archive_test",
            script_path,
        )
        cls.module = importlib.util.module_from_spec(module_specification)
        module_specification.loader.exec_module(cls.module)

    def test_reconstructs_filled_order_from_gateway_callbacks(self):
        orders = self.module.reconstruct_orders(
            [
                "2026-08-25T10:14:10 [lean_qmt_gateway] place_order_submitted client_order_id=7 direction=buy market_order_style=latest-price order_type=market passorder_result=0 price=-1.0 price_type=5 quantity=100 stock_code=600745.SH strategy_name=ATopGainerGateway",
                "2026-08-25T10:14:10 [lean_qmt_gateway] order_event order_id= status=50 stock_code=600745.SH",
                "2026-08-25T10:14:10 [lean_qmt_gateway] order_event order_id=2035 status=50 stock_code=600745.SH",
                "2026-08-25T10:14:11 [lean_qmt_gateway] order_event order_id=2035 status=56 stock_code=600745.SH",
            ],
            "LeanQmtGateway",
        )

        self.assertEqual(1, len(orders))
        self.assertEqual("2035", orders[0]["order_id"])
        self.assertEqual(100, orders[0]["traded_volume"])
        self.assertEqual("ATopGainerGateway", orders[0]["strategy_name"])


if __name__ == "__main__":
    unittest.main()
