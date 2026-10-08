import datetime
from enum import IntEnum
import typing

import QuantConnect.Brokerages
import QuantConnect.Interfaces
import QuantConnect.Orders
import QuantConnect
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

    def __init__(
        self,
        commission_rate: float = ...,
        minimum_commission: float = ...,
    ) -> None: ...


class QmtSecurityStatus:
    """Provides a strategy-facing QMT security-status query."""

    @staticmethod
    def is_trading(
        symbol: QuantConnect.Symbol,
    ) -> typing.Optional[bool]: ...


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


class QmtEquityMarketRules:
    """Defines exchange and board-specific rules for QMT A-share equities."""

    @staticmethod
    def get_minimum_buy_order_quantity(symbol: QuantConnect.Symbol) -> float: ...

    @staticmethod
    def get_daily_price_limit_percentage(symbol: QuantConnect.Symbol) -> float: ...


class QmtSymbolMapper(QuantConnect.Brokerages.ISymbolMapper):
    """Maps LEAN equity symbols to QMT stock codes."""

    MARKET_NAME: str
    registered_market_name: str

    def __init__(self) -> None: ...

    def get_brokerage_symbol(self, symbol: QuantConnect.Symbol) -> str: ...

    def get_lean_symbol_from_exchange(
        self, ticker: str, exchange: str
    ) -> QuantConnect.Symbol: ...

    def get_lean_symbol(
        self,
        brokerage_symbol: str,
        security_type: QuantConnect.SecurityType,
        market: str,
        expiration_date: typing.Union[datetime.datetime, datetime.date] = ...,
        strike: float = 0,
        option_right: QuantConnect.OptionRight = 0,
    ) -> QuantConnect.Symbol: ...


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


class QmtOrderEventStore:
    """Rebuilds current-position opening times from persisted QMT deals."""

    FILE_PATH: str

    @staticmethod
    def get_invested_at(
        symbol: QuantConnect.Symbol,
    ) -> typing.Optional[DateTime]: ...
