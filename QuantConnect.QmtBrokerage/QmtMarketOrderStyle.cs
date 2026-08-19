using System;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Selects how a LEAN market order is submitted through QMT.
    /// </summary>
    public enum QmtMarketOrderStyle
    {
        LatestPrice,
        FiveLevelImmediateOrCancel,
        FiveLevelImmediateToLimit,
        CounterpartyBest,
        OwnBest,
        ImmediateOrCancel,
        FillOrKill
    }

    /// <summary>
    /// Resolves QMT market-order styles to exchange-specific protocol values.
    /// </summary>
    public static class QmtMarketOrderStyleResolver
    {
        public const string LatestPriceProtocolValue = "latest-price";
        public const string FiveLevelImmediateOrCancelProtocolValue = "five-level-immediate-or-cancel";
        public const string FiveLevelImmediateToLimitProtocolValue = "five-level-immediate-to-limit";
        public const string CounterpartyBestProtocolValue = "counterparty-best";
        public const string OwnBestProtocolValue = "own-best";
        public const string ImmediateOrCancelProtocolValue = "immediate-or-cancel";
        public const string FillOrKillProtocolValue = "fill-or-kill";

        public static string GetProtocolValue(QmtMarketOrderStyle marketOrderStyle)
        {
            return marketOrderStyle switch
            {
                QmtMarketOrderStyle.LatestPrice => LatestPriceProtocolValue,
                QmtMarketOrderStyle.FiveLevelImmediateOrCancel => FiveLevelImmediateOrCancelProtocolValue,
                QmtMarketOrderStyle.FiveLevelImmediateToLimit => FiveLevelImmediateToLimitProtocolValue,
                QmtMarketOrderStyle.CounterpartyBest => CounterpartyBestProtocolValue,
                QmtMarketOrderStyle.OwnBest => OwnBestProtocolValue,
                QmtMarketOrderStyle.ImmediateOrCancel => ImmediateOrCancelProtocolValue,
                QmtMarketOrderStyle.FillOrKill => FillOrKillProtocolValue,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(marketOrderStyle),
                    marketOrderStyle,
                    "Unsupported QMT market-order style.")
            };
        }

        public static QmtMarketOrderSubmission Resolve(
            QmtMarketOrderStyle marketOrderStyle,
            QmtExchange exchange)
        {
            var protocolValue = GetProtocolValue(marketOrderStyle);
            return marketOrderStyle switch
            {
                QmtMarketOrderStyle.LatestPrice =>
                    new QmtMarketOrderSubmission(protocolValue, 5, -1m),
                QmtMarketOrderStyle.FiveLevelImmediateOrCancel => exchange switch
                {
                    QmtExchange.Shanghai or QmtExchange.Beijing =>
                        new QmtMarketOrderSubmission(protocolValue, 42, 0m),
                    QmtExchange.Shenzhen =>
                        new QmtMarketOrderSubmission(protocolValue, 47, 0m),
                    _ => throw UnsupportedExchange(marketOrderStyle, exchange)
                },
                QmtMarketOrderStyle.FiveLevelImmediateToLimit => exchange switch
                {
                    QmtExchange.Shanghai or QmtExchange.Beijing =>
                        new QmtMarketOrderSubmission(protocolValue, 43, 0m),
                    _ => throw UnsupportedExchange(marketOrderStyle, exchange)
                },
                QmtMarketOrderStyle.CounterpartyBest =>
                    new QmtMarketOrderSubmission(protocolValue, 44, 0m),
                QmtMarketOrderStyle.OwnBest =>
                    new QmtMarketOrderSubmission(protocolValue, 45, 0m),
                QmtMarketOrderStyle.ImmediateOrCancel when exchange == QmtExchange.Shenzhen =>
                    new QmtMarketOrderSubmission(protocolValue, 46, 0m),
                QmtMarketOrderStyle.FillOrKill when exchange == QmtExchange.Shenzhen =>
                    new QmtMarketOrderSubmission(protocolValue, 48, 0m),
                _ => throw UnsupportedExchange(marketOrderStyle, exchange)
            };
        }

        private static ArgumentException UnsupportedExchange(
            QmtMarketOrderStyle marketOrderStyle,
            QmtExchange exchange)
        {
            return new ArgumentException(
                $"QMT market-order style '{GetProtocolValue(marketOrderStyle)}' is not supported on {exchange}.");
        }
    }

    /// <summary>
    /// The exchange-specific QMT values used to submit one market order.
    /// </summary>
    public readonly struct QmtMarketOrderSubmission
    {
        public string Style { get; }
        public int PriceType { get; }
        public decimal Price { get; }

        public QmtMarketOrderSubmission(string style, int priceType, decimal price)
        {
            Style = style;
            PriceType = priceType;
            Price = price;
        }
    }
}
