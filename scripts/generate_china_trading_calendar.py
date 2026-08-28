#!/usr/bin/env python3
"""Generate the versioned China A-share trading calendar used by QmtMarket."""

import argparse
from datetime import date, datetime
import json
from pathlib import Path


DEFAULT_START_DATE = date(2000, 1, 1)
DEFAULT_END_DATE = date(date.today().year, 12, 31)
DEFAULT_OUTPUT_PATH = (
    Path(__file__).resolve().parents[1]
    / "QuantConnect.QmtBrokerage"
    / "Resources"
    / "china-trading-calendar.json"
)


def parse_date(value):
    return datetime.strptime(value, "%Y-%m-%d").date()


def normalize_trading_date(value):
    if isinstance(value, (int, float)):
        value = datetime.fromtimestamp(value / 1000 if value > 10_000_000_000 else value)
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value

    date_text = str(value).strip().replace("-", "")[:8]
    return datetime.strptime(date_text, "%Y%m%d").date()


def load_qmt_trading_days(start_date, end_date):
    from xtquant import xtdata

    trading_dates = xtdata.get_trading_calendar(
        "SH",
        start_date.strftime("%Y%m%d"),
        end_date.strftime("%Y%m%d"),
    )
    return "qmt-xtdata:SH", [
        normalize_trading_date(trading_date)
        for trading_date in trading_dates
    ]


def load_exchange_calendar_trading_days(start_date, end_date):
    import exchange_calendars
    import importlib.metadata

    exchange_calendar = exchange_calendars.get_calendar(
        "XSHG",
        start=start_date.isoformat(),
        end=end_date.isoformat(),
    )
    source_version = importlib.metadata.version("exchange-calendars")
    return f"exchange-calendars:{source_version}:XSHG", [
        trading_session.date()
        for trading_session in exchange_calendar.sessions
    ]


def validate_calendar_resource(resource_path, current_date):
    try:
        calendar_payload = json.loads(resource_path.read_text(encoding="utf-8"))
        coverage_start = parse_date(calendar_payload["coverage_start"])
        coverage_end = parse_date(calendar_payload["coverage_end"])
        trading_days = [
            parse_date(trading_day)
            for trading_day in calendar_payload["trading_days"]
        ]
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        raise RuntimeError(
            f"The QMT trading calendar resource is invalid: {resource_path}"
        ) from error

    if calendar_payload.get("market") != "SH":
        raise RuntimeError("The QMT trading calendar market must be SH.")
    if not calendar_payload.get("source"):
        raise RuntimeError("The QMT trading calendar source is missing.")
    if coverage_end < coverage_start:
        raise RuntimeError("The QMT trading calendar coverage range is invalid.")
    if trading_days != sorted(set(trading_days)):
        raise RuntimeError("The QMT trading days must be sorted and unique.")
    if not trading_days or any(
        trading_day < coverage_start
        or trading_day > coverage_end
        or trading_day.weekday() >= 5
        for trading_day in trading_days
    ):
        raise RuntimeError("The QMT trading calendar contains invalid trading days.")
    if current_date < coverage_start or current_date > coverage_end:
        raise RuntimeError(
            "The QMT trading calendar does not cover "
            f"{current_date}; available range is {coverage_start} through {coverage_end}."
        )

    return calendar_payload, coverage_start, coverage_end, len(trading_days)


def main():
    argument_parser = argparse.ArgumentParser()
    argument_parser.add_argument("--start", type=parse_date, default=DEFAULT_START_DATE)
    argument_parser.add_argument("--end", type=parse_date, default=DEFAULT_END_DATE)
    argument_parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT_PATH)
    argument_parser.add_argument("--check", action="store_true")
    arguments = argument_parser.parse_args()
    if arguments.check:
        calendar_payload, coverage_start, coverage_end, trading_day_count = (
            validate_calendar_resource(arguments.output, date.today())
        )
        print(
            "[qmt-calendar] status=ok "
            f"source={calendar_payload['source']} start={coverage_start} "
            f"end={coverage_end} trading_days={trading_day_count} "
            f"current_date={date.today()} resource={arguments.output}"
        )
        return

    if arguments.end < arguments.start:
        argument_parser.error("--end must be on or after --start")

    try:
        source, trading_days = load_qmt_trading_days(arguments.start, arguments.end)
    except ImportError:
        source, trading_days = load_exchange_calendar_trading_days(
            arguments.start,
            arguments.end,
        )

    sorted_trading_days = sorted(set(trading_days))
    if not sorted_trading_days:
        raise RuntimeError("The trading calendar source returned no trading days.")
    if sorted_trading_days[0] < arguments.start or sorted_trading_days[-1] > arguments.end:
        raise RuntimeError("The trading calendar source returned dates outside the requested range.")

    calendar_payload = {
        "market": "SH",
        "source": source,
        "coverage_start": arguments.start.isoformat(),
        "coverage_end": arguments.end.isoformat(),
        "trading_days": [trading_day.isoformat() for trading_day in sorted_trading_days],
    }
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(
        json.dumps(calendar_payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    validate_calendar_resource(arguments.output, date.today())
    print(
        "[qmt-calendar] status=generated "
        f"source={source} start={arguments.start} end={arguments.end} "
        f"trading_days={len(sorted_trading_days)} output={arguments.output}"
    )


if __name__ == "__main__":
    main()
