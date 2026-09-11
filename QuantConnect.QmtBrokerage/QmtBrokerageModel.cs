using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Benchmarks;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Defines the capabilities supported by the QMT brokerage MVP.
    /// </summary>
    public sealed class QmtBrokerageModel : DefaultBrokerageModel, IAccountCurrencyProvider
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

        public string AccountCurrency => QmtMarket.AccountCurrency;

        public QmtBrokerageModel()
            : base(AccountType.Cash)
        {
            QmtMarket.RegisterMetadata();
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

            var minimumBuyOrderQuantity = QmtEquityMarketRules.GetMinimumBuyOrderQuantity(security.Symbol);
            if (order.Quantity > 0 && order.Quantity < minimumBuyOrderQuantity)
            {
                message = new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "InvalidQuantity",
                    $"QMT requires a minimum buy order quantity of {minimumBuyOrderQuantity} shares for {security.Symbol.Value}.");
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

        public override IFeeModel GetFeeModel(Security security)
        {
            return new ConstantFeeModel(0m, QmtMarket.AccountCurrency);
        }

        public override IBenchmark GetBenchmark(SecurityManager securities)
        {
            return new FuncBenchmark(_ => 0m);
        }
    }
}
