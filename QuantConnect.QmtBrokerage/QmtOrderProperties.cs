using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// QMT-specific values supplied for one order.
    /// </summary>
    public sealed class QmtOrderProperties : OrderProperties
    {
        /// <summary>
        /// Selects the QMT price type used for a market order.
        /// </summary>
        public QmtMarketOrderStyle? MarketOrderStyle { get; set; }

        public override IOrderProperties Clone()
        {
            return (QmtOrderProperties)MemberwiseClone();
        }
    }
}
