#!/usr/bin/env python3
"""Backfill T and T+1 QMT minute history for A Top Gainer screens."""

import argparse
import json
import sys
import time
import zipfile
from datetime import date, datetime, time as clock_time
from pathlib import Path
from zoneinfo import ZoneInfo

from tqdm.auto import tqdm

from download_qmt_history import (
    QmtGatewayApiClient,
    default_gateway_host,
    default_lean_data_root,
    history_bars_with_share_volume,
    parse_trading_date,
    quantconnect_minute_rows,
    write_quantconnect_minute_zip,
)


DEFAULT_START_DATE = date(2026, 8, 1)
DEFAULT_SCREEN_ROOT = Path(
    "/Users/Nemo/Workspace/quant/lean-project/storage/screen/a_top_gainer"
)
DEFAULT_CALENDAR_PATH = (
    Path(__file__).resolve().parents[1]
    / "QuantConnect.QmtBrokerage"
    / "Resources"
    / "china-trading-calendar.json"
)
SHANGHAI_TIMEZONE = ZoneInfo("Asia/Shanghai")
MARKET_DATA_READY_TIME = clock_time(15, 30)
REPAIR_STATE_VERSION = "shares-v1"


class InvalidHistoryError(RuntimeError):
    """Raised when returned history is unsafe to write."""


def parse_arguments():
    argument_parser = argparse.ArgumentParser(
        description=(
            "Backfill T and available T+1 minute history for A Top Gainer "
            "screens in chronological order."
        )
    )
    argument_parser.add_argument(
        "--start",
        type=parse_trading_date,
        default=DEFAULT_START_DATE,
        help="First screen date (default: 2026-08-01).",
    )
    argument_parser.add_argument(
        "--end",
        type=parse_trading_date,
        help="Last completed trading date; defaults to the latest available date.",
    )
    argument_parser.add_argument(
        "--screen-root",
        type=Path,
        default=DEFAULT_SCREEN_ROOT,
        help="A Top Gainer screen CSV root.",
    )
    argument_parser.add_argument(
        "--calendar",
        type=Path,
        default=DEFAULT_CALENDAR_PATH,
        help="China trading calendar JSON path.",
    )
    argument_parser.add_argument(
        "--data-root",
        type=Path,
        default=default_lean_data_root(),
        help="LEAN data root.",
    )
    argument_parser.add_argument(
        "--host",
        default=default_gateway_host(),
        help="Big QMT Gateway host.",
    )
    argument_parser.add_argument(
        "--port",
        type=int,
        default=17890,
        help="Big QMT Gateway port (default: 17890).",
    )
    argument_parser.add_argument(
        "--timeout",
        type=int,
        default=180,
        help="Gateway request timeout in seconds (default: 180).",
    )
    argument_parser.add_argument(
        "--plan",
        action="store_true",
        help="Show the resumable work plan without contacting QMT.",
    )
    argument_parser.add_argument(
        "--repair-local-data",
        action="store_true",
        help=(
            "Redownload every screen job once with the current QMT conversion "
            "logic; completed repairs are persisted for resumable reruns."
        ),
    )
    argument_parser.add_argument(
        "--repair-state",
        type=Path,
        help=(
            "Repair checkpoint path; defaults to "
            "<data-root>/qmt-a-top-gainer-repair-state.tsv."
        ),
    )
    return argument_parser.parse_args()


def ticker_from_stock_code(stock_code):
    if stock_code.startswith("6"):
        return stock_code + ".SH"
    if stock_code.startswith(("0", "3")):
        return stock_code + ".SZ"
    if stock_code.startswith(("4", "8", "9")):
        return stock_code + ".BJ"
    raise ValueError("cannot determine exchange for stock code %s" % stock_code)


def load_trading_days(calendar_path):
    calendar_payload = json.loads(calendar_path.read_text(encoding="utf-8"))
    trading_days = [
        date.fromisoformat(trading_day)
        for trading_day in calendar_payload["trading_days"]
    ]
    if trading_days != sorted(set(trading_days)):
        raise ValueError("China trading calendar must be sorted and unique")
    return trading_days


def latest_completed_trading_day(trading_days, requested_end_date):
    shanghai_now = datetime.now(SHANGHAI_TIMEZONE)
    last_eligible_date = min(requested_end_date or shanghai_now.date(), shanghai_now.date())
    completed_trading_days = [
        trading_day
        for trading_day in trading_days
        if trading_day < last_eligible_date
        or (
            trading_day == last_eligible_date
            and (
                trading_day < shanghai_now.date()
                or shanghai_now.time() >= MARKET_DATA_READY_TIME
            )
        )
    ]
    if not completed_trading_days:
        raise ValueError("trading calendar contains no completed trading day")
    return completed_trading_days[-1]


def build_download_jobs(
    screen_root,
    start_date,
    completed_through_date,
    trading_days,
):
    trading_day_position = {
        trading_day: position
        for position, trading_day in enumerate(trading_days)
    }
    download_jobs = set()
    ignored_screen_files = []

    for screen_file in sorted(screen_root.glob("*/*.csv")):
        try:
            screen_date = datetime.strptime(screen_file.stem, "%y%m%d").date()
        except ValueError:
            continue
        if not start_date <= screen_date <= completed_through_date:
            continue
        if screen_date not in trading_day_position:
            ignored_screen_files.append(screen_file)
            continue

        target_dates = [screen_date]
        next_trading_day_position = trading_day_position[screen_date] + 1
        if next_trading_day_position < len(trading_days):
            next_trading_day = trading_days[next_trading_day_position]
            if next_trading_day <= completed_through_date:
                target_dates.append(next_trading_day)

        for screen_line in screen_file.read_text(encoding="utf-8").splitlines():
            if not screen_line.strip():
                continue
            screen_fields = screen_line.split(",", 2)
            if len(screen_fields) < 2:
                raise ValueError("invalid screen row in %s" % screen_file)
            ticker = ticker_from_stock_code(screen_fields[1].strip())
            for target_date in target_dates:
                download_jobs.add((target_date, ticker))

    return sorted(download_jobs), ignored_screen_files


def minute_row_validation_error(quantconnect_rows):
    if not 200 <= len(quantconnect_rows) <= 300:
        return "unexpected minute count: %s" % len(quantconnect_rows)

    try:
        parsed_rows = [
            [int(field_value) for field_value in row.split(",")]
            for row in quantconnect_rows
        ]
    except ValueError:
        return "minute data contains a non-integer field"
    if any(len(row) != 6 for row in parsed_rows):
        return "minute row does not contain six fields"

    timestamps = [row[0] for row in parsed_rows]
    if timestamps != sorted(set(timestamps)):
        return "minute timestamps are duplicated or out of order"
    if timestamps[0] > 10 * 60 * 60 * 1000:
        return "morning session data is missing"
    if timestamps[-1] < (14 * 60 + 50) * 60 * 1000:
        return "closing session data is missing"

    for _, open_price, high_price, low_price, close_price, volume in parsed_rows:
        if min(open_price, high_price, low_price, close_price) <= 0:
            return "minute data contains a non-positive price"
        if high_price < max(open_price, low_price, close_price):
            return "minute high price is inconsistent"
        if low_price > min(open_price, high_price, close_price):
            return "minute low price is inconsistent"
        if volume < 0:
            return "minute data contains negative volume"
    if not any(row[5] > 0 for row in parsed_rows):
        return "all minute volumes are zero; likely suspended fill data"
    return ""


def existing_zip_is_valid(zip_path):
    if not zip_path.exists():
        return False
    try:
        with zipfile.ZipFile(zip_path) as zip_file:
            if zip_file.testzip() is not None:
                return False
            zip_entry_names = zip_file.namelist()
            if len(zip_entry_names) != 1:
                return False
            quantconnect_rows = (
                zip_file.read(zip_entry_names[0]).decode("utf-8").splitlines()
            )
        return not minute_row_validation_error(quantconnect_rows)
    except (OSError, UnicodeDecodeError, zipfile.BadZipFile):
        return False


def target_zip_path(data_root, target_date, ticker):
    stock_code = ticker.split(".", 1)[0].lower()
    return (
        data_root
        / "equity"
        / "china"
        / "minute"
        / stock_code
        / (target_date.strftime("%Y%m%d") + "_trade.zip")
    )


def load_completed_repairs(repair_state_path):
    completed_repairs = set()
    if not repair_state_path.exists():
        return completed_repairs

    for line_number, repair_state_line in enumerate(
        repair_state_path.read_text(encoding="utf-8").splitlines(),
        start=1,
    ):
        if not repair_state_line.strip():
            continue
        repair_state_fields = repair_state_line.split("\t")
        if len(repair_state_fields) != 3:
            raise ValueError(
                "invalid repair state row %s in %s"
                % (line_number, repair_state_path)
            )
        repair_state_version, date_key, ticker = repair_state_fields
        if repair_state_version != REPAIR_STATE_VERSION:
            continue
        completed_repairs.add((parse_trading_date(date_key), ticker))
    return completed_repairs


def repair_state_line(target_date, ticker):
    return "%s\t%s\t%s\n" % (
        REPAIR_STATE_VERSION,
        target_date.strftime("%Y%m%d"),
        ticker,
    )


def download_job_is_complete(
    data_root,
    target_date,
    ticker,
    should_repair_local_data,
    completed_repairs,
):
    zip_path = target_zip_path(data_root, target_date, ticker)
    if not existing_zip_is_valid(zip_path):
        return False
    if not should_repair_local_data:
        return True
    return (target_date, ticker) in completed_repairs


class GatewayConnection:
    def __init__(self, host, port, timeout_seconds):
        self.host = host
        self.port = port
        self.timeout_seconds = timeout_seconds
        self.client = None

    def get(self):
        if self.client is None:
            self.client = QmtGatewayApiClient(
                self.host,
                self.port,
                self.timeout_seconds,
            )
            self.client.__enter__()
            hello_payload = self.client.request("hello", {"account_id": ""})
            if not str(hello_payload.get("account_id") or "").strip():
                self.close()
                raise RuntimeError("QMT Gateway hello returned no account_id")
        return self.client

    def close(self):
        if self.client is None:
            return
        try:
            self.client.__exit__(None, None, None)
        finally:
            self.client = None


def download_pending_jobs(
    arguments,
    pending_jobs,
    already_valid_count,
    repair_state_path,
):
    failure_log_path = arguments.data_root / "qmt-backfill-failures.tsv"
    failure_log_path.parent.mkdir(parents=True, exist_ok=True)
    gateway_connection = GatewayConnection(
        arguments.host,
        arguments.port,
        arguments.timeout,
    )
    successful_download_count = 0
    failed_download_count = 0
    was_interrupted = False
    repair_state_file = None

    try:
        if arguments.repair_local_data:
            repair_state_path.parent.mkdir(parents=True, exist_ok=True)
            repair_state_file = repair_state_path.open(
                "a",
                encoding="utf-8",
                buffering=1,
            )
        with failure_log_path.open(
            "w",
            encoding="utf-8",
            buffering=1,
        ) as failure_log_file:
            failure_log_file.write("date\tticker\treason\n")
            progress_bar = tqdm(
                pending_jobs,
                desc="QMT T/T+1",
                unit="file",
                dynamic_ncols=True,
                colour="green",
            )
            for target_date, ticker in progress_bar:
                date_key = target_date.strftime("%Y%m%d")
                job_completed = False
                failure_reason = ""

                for attempt_number in range(1, 4):
                    try:
                        history_payload = gateway_connection.get().request(
                            "query_history",
                            {
                                "stock_code": ticker,
                                "period": "1m",
                                "start_time": date_key + "000000",
                                "end_time": date_key + "235959",
                            },
                        )
                        history_bars = history_bars_with_share_volume(
                            history_payload
                        )
                        quantconnect_rows = quantconnect_minute_rows(
                            history_bars,
                            target_date,
                        )
                        validation_error = minute_row_validation_error(
                            quantconnect_rows
                        )
                        if validation_error:
                            raise InvalidHistoryError(validation_error)

                        write_quantconnect_minute_zip(
                            quantconnect_rows,
                            ticker,
                            target_date,
                            arguments.data_root,
                        )
                        if repair_state_file is not None:
                            repair_state_file.write(
                                repair_state_line(target_date, ticker)
                            )
                        successful_download_count += 1
                        job_completed = True
                        break
                    except InvalidHistoryError as error:
                        failure_reason = str(error)
                        break
                    except KeyboardInterrupt:
                        raise
                    except Exception as error:
                        failure_reason = str(error).replace("\n", " ")
                        gateway_connection.close()
                        if attempt_number < 3:
                            time.sleep(attempt_number * 2)

                if not job_completed:
                    failed_download_count += 1
                    failure_log_file.write(
                        "%s\t%s\t%s\n"
                        % (date_key, ticker, failure_reason)
                    )
                    tqdm.write(
                        "[qmt-backfill] stage=download status=failed "
                        "date=%s ticker=%s reason=%s"
                        % (date_key, ticker, failure_reason)
                    )

                progress_bar.set_postfix(
                    success=successful_download_count,
                    failed=failed_download_count,
                    refresh=False,
                )
    except KeyboardInterrupt:
        was_interrupted = True
        print(
            "\n[qmt-backfill] stage=run status=interrupted "
            "resume=rerun-same-command",
            file=sys.stderr,
        )
    finally:
        gateway_connection.close()
        if repair_state_file is not None:
            repair_state_file.close()

    final_status = (
        "interrupted"
        if was_interrupted
        else "ok" if failed_download_count == 0 else "failed"
    )
    print(
        "[qmt-backfill] stage=run status=%s skipped=%s success=%s "
        "failed=%s failure_log=%s repair_state=%s"
        % (
            final_status,
            already_valid_count,
            successful_download_count,
            failed_download_count,
            failure_log_path,
            repair_state_path if arguments.repair_local_data else "disabled",
        )
    )
    if was_interrupted:
        return 130
    return 1 if failed_download_count else 0


def main():
    arguments = parse_arguments()
    if arguments.timeout <= 0:
        raise ValueError("--timeout must be positive")
    if arguments.end is not None and arguments.end < arguments.start:
        raise ValueError("--end must be on or after --start")

    trading_days = load_trading_days(arguments.calendar)
    completed_through_date = latest_completed_trading_day(
        trading_days,
        arguments.end,
    )
    download_jobs, ignored_screen_files = build_download_jobs(
        arguments.screen_root,
        arguments.start,
        completed_through_date,
        trading_days,
    )
    repair_state_path = (
        arguments.repair_state
        if arguments.repair_state is not None
        else arguments.data_root / "qmt-a-top-gainer-repair-state.tsv"
    ).expanduser().resolve()
    completed_repairs = (
        load_completed_repairs(repair_state_path)
        if arguments.repair_local_data
        else set()
    )
    pending_jobs = [
        (target_date, ticker)
        for target_date, ticker in download_jobs
        if not download_job_is_complete(
            arguments.data_root,
            target_date,
            ticker,
            arguments.repair_local_data,
            completed_repairs,
        )
    ]
    already_valid_count = len(download_jobs) - len(pending_jobs)

    print(
        "[qmt-backfill] stage=plan status=ok start=%s completed_through=%s "
        "jobs=%s skipped=%s pending=%s ignored_screens=%s host=%s port=%s "
        "repair_local_data=%s repair_state=%s"
        % (
            arguments.start,
            completed_through_date,
            len(download_jobs),
            already_valid_count,
            len(pending_jobs),
            len(ignored_screen_files),
            arguments.host,
            arguments.port,
            arguments.repair_local_data,
            repair_state_path if arguments.repair_local_data else "disabled",
        )
    )
    if arguments.plan:
        return 0
    return download_pending_jobs(
        arguments,
        pending_jobs,
        already_valid_count,
        repair_state_path,
    )


if __name__ == "__main__":
    sys.exit(main())
