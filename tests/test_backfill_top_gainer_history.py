import sys
import tempfile
import unittest
import zipfile
from datetime import date
from pathlib import Path


SCRIPTS_DIRECTORY = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS_DIRECTORY))

from backfill_top_gainer_history import (
    REPAIR_STATE_VERSION,
    download_job_is_complete,
    load_completed_repairs,
    repair_state_line,
    target_zip_path,
)


class BackfillTopGainerHistoryTests(unittest.TestCase):
    def write_valid_minute_archive(self, data_root, target_date, ticker):
        archive_path = target_zip_path(data_root, target_date, ticker)
        archive_path.parent.mkdir(parents=True)
        rows = []
        for row_number in range(241):
            milliseconds_from_midnight = 34_200_000 + row_number * 80_000
            rows.append(
                "%s,100000,100000,100000,100000,100"
                % milliseconds_from_midnight
            )
        with zipfile.ZipFile(archive_path, "w") as archive:
            archive.writestr(
                "%s_%s_trade.csv"
                % (target_date.strftime("%Y%m%d"), ticker.split(".", 1)[0]),
                "\n".join(rows) + "\n",
            )
        return archive_path

    def test_repair_mode_requires_a_completed_repair_checkpoint(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            data_root = Path(temporary_directory)
            target_date = date(2026, 9, 30)
            ticker = "300765.SZ"
            self.write_valid_minute_archive(data_root, target_date, ticker)

            self.assertTrue(
                download_job_is_complete(
                    data_root,
                    target_date,
                    ticker,
                    should_repair_local_data=False,
                    completed_repairs=set(),
                )
            )
            self.assertFalse(
                download_job_is_complete(
                    data_root,
                    target_date,
                    ticker,
                    should_repair_local_data=True,
                    completed_repairs=set(),
                )
            )
            self.assertTrue(
                download_job_is_complete(
                    data_root,
                    target_date,
                    ticker,
                    should_repair_local_data=True,
                    completed_repairs={(target_date, ticker)},
                )
            )

    def test_repair_checkpoint_uses_the_current_conversion_version(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            repair_state_path = Path(temporary_directory) / "repair-state.tsv"
            repair_state_path.write_text(
                "old-version\t20260929\t600000.SH\n"
                + repair_state_line(date(2026, 9, 30), "300765.SZ"),
                encoding="utf-8",
            )

            completed_repairs = load_completed_repairs(repair_state_path)

        self.assertEqual(
            {(date(2026, 9, 30), "300765.SZ")},
            completed_repairs,
        )
        self.assertTrue(
            repair_state_line(date(2026, 9, 30), "300765.SZ").startswith(
                REPAIR_STATE_VERSION + "\t"
            )
        )


if __name__ == "__main__":
    unittest.main()
