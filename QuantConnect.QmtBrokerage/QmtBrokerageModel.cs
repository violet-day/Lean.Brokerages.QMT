using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Defines the capabilities supported by the QMT brokerage MVP.
    /// </summary>
    public sealed class QmtBrokerageModel : DefaultBrokerageModel
    {
        private static readonly HashSet<OrderType> SupportedOrderTypes = new HashSet<OrderType>
        {
            OrderType.Market,
            OrderType.Limit
        };

        public override IReadOnlyDictionary<SecurityType, string> DefaultMarkets { get; } =
            DefaultMarketMap
                .ToDictionary(entry => entry.Key, entry =>
                    entry.Key == SecurityType.Equity ? QmtSymbolMapper.RegisteredMarketName : entry.Value)
                .ToReadOnlyDictionary();

        public QmtBrokerageModel()
            : base(AccountType.Cash)
        {
        }

        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            if (security == null)
            {
                throw new ArgumentNullException(nameof(security));
            }

            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            if (security.Type != SecurityType.Equity ||
                !string.Equals(security.Symbol.ID.Market, QmtSymbolMapper.MarketName, StringComparison.OrdinalIgnoreCase))
            {
                message = new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "UnsupportedSecurity",
                    "QMT MVP supports only A-share equities in the china market.");
                return false;
            }

            if (!SupportedOrderTypes.Contains(order.Type))
            {
                message = new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "UnsupportedOrderType",
                    $"QMT MVP supports Market and Limit orders, not {order.Type}.");
                return false;
            }

            if (order.Quantity == 0 || order.Quantity != decimal.Truncate(order.Quantity))
            {
                message = new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "InvalidQuantity",
                    "QMT A-share order quantity must be a non-zero whole number of shares.");
                return false;
            }

            message = null;
            return true;
        }

        public override bool CanUpdateOrder(
            Security security,
            Order order,
            UpdateOrderRequest request,
            out BrokerageMessageEvent message)
        {
            message = new BrokerageMessageEvent(
                BrokerageMessageType.Warning,
                "UpdateNotSupported",
                "QMT MVP does not support modifying an order. Cancel it and submit a new order.");
            return false;
        }

        public override decimal GetLeverage(Security security)
        {
            return 1m;
        }
    }
}
