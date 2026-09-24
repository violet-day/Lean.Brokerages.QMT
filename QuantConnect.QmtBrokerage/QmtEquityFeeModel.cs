using System;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Models Guojin A-share commissions and statutory transaction charges.
    /// </summary>
    public sealed class QmtEquityFeeModel : FeeModel
    {
        public const decimal DefaultCommissionRate = 0.00023m;
        public const decimal DefaultMinimumCommission = 5m;
        public const decimal StampDutyRate = 0.0005m;
        public const decimal TransferFeeRate = 0.00001m;

        private readonly decimal _commissionRate;
        private readonly decimal _minimumCommission;

        public QmtEquityFeeModel(
            decimal commissionRate = DefaultCommissionRate,
            decimal minimumCommission = DefaultMinimumCommission)
        {
            if (commissionRate < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(commissionRate), "The commission rate cannot be negative.");
            }

            if (minimumCommission < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumCommission), "The minimum commission cannot be negative.");
            }

            _commissionRate = commissionRate;
            _minimumCommission = minimumCommission;
        }

        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            var security = parameters.Security;
            var order = parameters.Order;
            if (security.Type != SecurityType.Equity ||
                !string.Equals(security.Symbol.ID.Market, QmtSymbolMapper.MarketName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The QMT equity fee model supports only A-share equities.", nameof(parameters));
            }

            var tradeValue = Math.Abs(order.GetValue(security));
            var commission = Math.Max(tradeValue * _commissionRate, _minimumCommission);
            var stampDuty = order.Direction == OrderDirection.Sell
                ? tradeValue * StampDutyRate
                : 0m;
            var transferFee = tradeValue * TransferFeeRate;

            return new OrderFee(new CashAmount(
                commission + stampDuty + transferFee,
                QmtMarket.AccountCurrency));
        }
    }
}
