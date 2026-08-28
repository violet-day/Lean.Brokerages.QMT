#!/usr/bin/env python3

import argparse
from collections import defaultdict, deque
from datetime import datetime
import json
from pathlib import Path
import re


PLACE_ORDER_PATTERN = re.compile(
    r"^(?P<time>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}) "
    r"\[lean_qmt_gateway\] place_order_submitted "
    r"(?P<fields>.*)$"
)
ORDER_EVENT_PATTERN = re.compile(
    r"^(?P<time>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}) "
    r"\[lean_qmt_gateway\] order_event "
    r"(?P<fields>.*)$"
)
FIELD_PATTERN = re.compile(r"(?P<name>[a-z_]+)=(?P<value>\S*)")


def parse_fields(field_text):
    return {
        field_match.group("name"): field_match.group("value")
        for field_match in FIELD_PATTERN.finditer(field_text)
    }


def reconstruct_orders(log_lines, default_strategy_name):
    pending_submissions_by_stock_code = defaultdict(deque)
    orders_by_broker_order_id = {}
    for log_line in log_lines:
        stripped_line = log_line.strip()
        placement_match = PLACE_ORDER_PATTERN.match(stripped_line)
        if placement_match:
            placement = parse_fields(placement_match.group("fields"))
            placement["time"] = placement_match.group("time")
            required_fields = (
                "client_order_id",
                "direction",
                "order_type",
                "quantity",
                "stock_code",
            )
            if not all(placement.get(field_name) for field_name in required_fields):
                continue
            pending_submissions_by_stock_code[placement["stock_code"]].append(
                placement
            )
            continue

        order_event_match = ORDER_EVENT_PATTERN.match(stripped_line)
        if not order_event_match:
            continue
        order_event = parse_fields(order_event_match.group("fields"))
        if not all(
            field_name in order_event
            for field_name in ("order_id", "status", "stock_code")
        ):
            continue
        broker_order_id = order_event["order_id"]
        if not broker_order_id:
            continue
        order = orders_by_broker_order_id.get(broker_order_id)
        if order is None:
            pending_submissions = pending_submissions_by_stock_code[
                order_event["stock_code"]
            ]
            if not pending_submissions:
                continue
            placement = pending_submissions.popleft()
            submitted_at = datetime.strptime(
                placement["time"],
                "%Y-%m-%dT%H:%M:%S",
            )
            requested_quantity = int(placement["quantity"])
            order = {
                "stock_code": placement["stock_code"],
                "order_id": broker_order_id,
                "client_order_id": placement["client_order_id"],
                "direction": placement["direction"],
                "order_type": placement["order_type"],
                "status": int(order_event["status"]),
                "original_volume": requested_quantity,
                "traded_volume": 0,
                "limit_price": 0,
                "traded_price": 0,
                "remark": placement["client_order_id"],
                "strategy_name": (
                    placement.get("strategy_name") or default_strategy_name
                ),
                "time": submitted_at.strftime("%Y%m%d %H%M%S"),
            }
            orders_by_broker_order_id[broker_order_id] = order
        order["status"] = int(order_event["status"])
        if order["status"] == 56:
            order["traded_volume"] = order["original_volume"]

    return sorted(
        orders_by_broker_order_id.values(),
        key=lambda order: (order["time"], order["order_id"]),
    )


def main():
    argument_parser = argparse.ArgumentParser()
    argument_parser.add_argument("--log", required=True, type=Path)
    argument_parser.add_argument("--account-id", required=True)
    argument_parser.add_argument("--date", required=True)
    argument_parser.add_argument("--output-directory", required=True, type=Path)
    argument_parser.add_argument(
        "--strategy-name",
        default="LeanQmtGateway",
    )
    argument_parser.add_argument("--force", action="store_true")
    argument_parser.add_argument("--merge", action="store_true")
    arguments = argument_parser.parse_args()
    datetime.strptime(arguments.date, "%Y%m%d")
    output_path = (
        arguments.output_directory / f"qmt-orders-{arguments.date}.json"
    )
    if output_path.exists() and not arguments.force and not arguments.merge:
        raise FileExistsError(f"Archive already exists: {output_path}")

    orders = reconstruct_orders(
        arguments.log.read_text(encoding="utf-8").splitlines(),
        arguments.strategy_name,
    )
    orders_for_date = [
        order for order in orders if order["time"].startswith(arguments.date)
    ]
    archive_source = "gateway_runtime_log_reconstructed"
    if arguments.merge and output_path.exists():
        existing_payload = json.loads(output_path.read_text(encoding="utf-8"))
        if str(existing_payload.get("account_id") or "") != arguments.account_id:
            raise ValueError("Existing archive account does not match.")
        merged_orders_by_order_id = {
            str(order.get("order_id") or ""): order
            for order in existing_payload.get("orders", [])
        }
        for reconstructed_order in orders_for_date:
            broker_order_id = reconstructed_order["order_id"]
            existing_order = merged_orders_by_order_id.get(broker_order_id)
            if existing_order is None:
                merged_orders_by_order_id[broker_order_id] = reconstructed_order
                continue
            merged_order = dict(reconstructed_order)
            merged_order.update(existing_order)
            if not merged_order.get("strategy_name"):
                merged_order["strategy_name"] = reconstructed_order[
                    "strategy_name"
                ]
            merged_orders_by_order_id[broker_order_id] = merged_order
        orders_for_date = sorted(
            merged_orders_by_order_id.values(),
            key=lambda order: (order.get("time", ""), order.get("order_id", "")),
        )
        archive_source = "daily_archive_with_gateway_log_enrichment"
    arguments.output_directory.mkdir(parents=True, exist_ok=True)
    temporary_output_path = output_path.with_suffix(".json.tmp")
    temporary_output_path.write_text(
        json.dumps(
            {
                "account_id": arguments.account_id,
                "archive_date": arguments.date,
                "source": archive_source,
                "orders": orders_for_date,
            },
            ensure_ascii=False,
            separators=(",", ":"),
        ),
        encoding="utf-8",
    )
    temporary_output_path.replace(output_path)
    print(
        f"[qmt-history-archive] status=ok date={arguments.date} "
        f"orders={len(orders_for_date)} "
        f"filled={sum(order['status'] == 56 for order in orders_for_date)} "
        f"path={output_path}"
    )


if __name__ == "__main__":
    main()
