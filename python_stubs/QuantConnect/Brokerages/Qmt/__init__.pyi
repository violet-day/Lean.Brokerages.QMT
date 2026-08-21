from enum import IntEnum
import typing

import QuantConnect.Brokerages
import QuantConnect.Interfaces
import QuantConnect.Orders


class QmtMarketOrderStyle(IntEnum):
    """Selects how a LEAN market order is submitted through QMT."""

    LATEST_PRICE = 0
    FIVE_LEVEL_IMMEDIATE_OR_CANCEL = 1
    FIVE_LEVEL_IMMEDIATE_TO_LIMIT = 2
    COUNTERPARTY_BEST = 3
    OWN_BEST = 4
    IMMEDIATE_OR_CANCEL = 5
    FILL_OR_KILL = 6


class QmtBrokerageModel(QuantConnect.Brokerages.DefaultBrokerageModel):
    """Defines the capabilities supported by the QMT brokerage."""

    def __init__(self) -> None: ...


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
