from enum import IntEnum
import typing

import QuantConnect.Brokerages
import QuantConnect.Interfaces
import QuantConnect.Orders
from System import DateTime


class QmtMarketOrderStyle(IntEnum):
    """Selects how a LEAN market order is submitted through QMT."""

    LATEST_PRICE = 0
    FIVE_LEVEL_IMMEDIATE_OR_CANCEL = 1
    FIVE_LEVEL_IMMEDIATE_TO_LIMIT = 2
    COUNTERPARTY_BEST = 3
    OWN_BEST = 4
    IMMEDIATE_OR_CANCEL = 5
    FILL_OR_KILL = 6


class QmtBrokerageModel(
    QuantConnect.Brokerages.DefaultBrokerageModel,
    QuantConnect.Interfaces.IAccountCurrencyProvider,
):
    """Defines the capabilities supported by the QMT brokerage."""

    @property
    def account_currency(self) -> str: ...

    def __init__(self) -> None: ...


class QmtMarket:
    """Registers the China A-share market metadata required by LEAN."""

    NAME: str
    ACCOUNT_CURRENCY: str
    calendar_coverage_start: DateTime
    calendar_coverage_end: DateTime
    calendar_source: str

    @staticmethod
    def register_metadata() -> None: ...

    @staticmethod
    def is_trading_day(date: DateTime) -> bool: ...

    @staticmethod
    def is_market_open(local_time: DateTime) -> bool: ...

    @staticmethod
    def ensure_calendar_covers(date: DateTime) -> None: ...


class QmtOrderProperties(QuantConnect.Orders.OrderProperties):
    """QMT-specific values supplied for one order."""

    @property
    def market_order_style(self) -> typing.Optional[QmtMarketOrderStyle]: ...

    @market_order_style.setter
    def market_order_style(
        self, value: typing.Optional[QmtMarketOrderStyle]
    ) -> None: ...

    def __init__(self) -> None: ...

    def clone(self) -> QuantConnect.Interfaces.IOrderProperties: ...
