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
    /// Parses configured QMT market-order styles and resolves exchange-specific QMT price types.
    /// </summary>
    public static class QmtMarketOrderStyleResolver
    {
        public const string LatestPriceConfigurationValue = "latest-price";
        public const string FiveLevelImmediateOrCancelConfigurationValue = "five-level-immediate-or-cancel";
        public const string FiveLevelImmediateToLimitConfigurationValue = "five-level-immediate-to-limit";
        public const string CounterpartyBestConfigurationValue = "counterparty-best";
        public const string OwnBestConfigurationValue = "own-best";
        public const string ImmediateOrCancelConfigurationValue = "immediate-or-cancel";
        public const string FillOrKillConfigurationValue = "fill-or-kill";

        public static bool TryParse(string? value, out QmtMarketOrderStyle marketOrderStyle)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case LatestPriceConfigurationValue:
                    marketOrderStyle = QmtMarketOrderStyle.LatestPrice;
                    return true;
                case FiveLevelImmediateOrCancelConfigurationValue:
                    marketOrderStyle = QmtMarketOrderStyle.FiveLevelImmediateOrCancel;
                    return true;
                case FiveLevelImmediateToLimitConfigurationValue:
                    marketOrderStyle = QmtMarketOrderStyle.FiveLevelImmediateToLimit;
                    return true;
                case CounterpartyBestConfigurationValue:
                    marketOrderStyle = QmtMarketOrderStyle.CounterpartyBest;
                    return true;
                case OwnBestConfigurationValue:
                    marketOrderStyle = QmtMarketOrderStyle.OwnBest;
                    return true;
                case ImmediateOrCancelConfigurationValue:
                    marketOrderStyle = QmtMarketOrderStyle.ImmediateOrCancel;
                    return true;
                case FillOrKillConfigurationValue:
                    marketOrderStyle = QmtMarketOrderStyle.FillOrKill;
                    return true;
                default:
                    marketOrderStyle = default;
                    return false;
            }
        }

        public static string GetConfigurationValue(QmtMarketOrderStyle marketOrderStyle)
        {
            return marketOrderStyle switch
            {
                QmtMarketOrderStyle.LatestPrice => LatestPriceConfigurationValue,
                QmtMarketOrderStyle.FiveLevelImmediateOrCancel => FiveLevelImmediateOrCancelConfigurationValue,
                QmtMarketOrderStyle.FiveLevelImmediateToLimit => FiveLevelImmediateToLimitConfigurationValue,
                QmtMarketOrderStyle.CounterpartyBest => CounterpartyBestConfigurationValue,
                QmtMarketOrderStyle.OwnBest => OwnBestConfigurationValue,
                QmtMarketOrderStyle.ImmediateOrCancel => ImmediateOrCancelConfigurationValue,
                QmtMarketOrderStyle.FillOrKill => FillOrKillConfigurationValue,
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
            var configurationValue = GetConfigurationValue(marketOrderStyle);
            return marketOrderStyle switch
            {
                QmtMarketOrderStyle.LatestPrice =>
                    new QmtMarketOrderSubmission(configurationValue, 5, -1m),
                QmtMarketOrderStyle.FiveLevelImmediateOrCancel => exchange switch
                {
                    QmtExchange.Shanghai or QmtExchange.Beijing =>
                        new QmtMarketOrderSubmission(configurationValue, 42, 0m),
                    QmtExchange.Shenzhen =>
                        new QmtMarketOrderSubmission(configurationValue, 47, 0m),
                    _ => throw UnsupportedExchange(marketOrderStyle, exchange)
                },
                QmtMarketOrderStyle.FiveLevelImmediateToLimit => exchange switch
                {
                    QmtExchange.Shanghai or QmtExchange.Beijing =>
                        new QmtMarketOrderSubmission(configurationValue, 43, 0m),
                    _ => throw UnsupportedExchange(marketOrderStyle, exchange)
                },
                QmtMarketOrderStyle.CounterpartyBest =>
                    new QmtMarketOrderSubmission(configurationValue, 44, 0m),
                QmtMarketOrderStyle.OwnBest =>
                    new QmtMarketOrderSubmission(configurationValue, 45, 0m),
                QmtMarketOrderStyle.ImmediateOrCancel when exchange == QmtExchange.Shenzhen =>
                    new QmtMarketOrderSubmission(configurationValue, 46, 0m),
                QmtMarketOrderStyle.FillOrKill when exchange == QmtExchange.Shenzhen =>
                    new QmtMarketOrderSubmission(configurationValue, 48, 0m),
                _ => throw UnsupportedExchange(marketOrderStyle, exchange)
            };
        }

        private static ArgumentException UnsupportedExchange(
            QmtMarketOrderStyle marketOrderStyle,
            QmtExchange exchange)
        {
            return new ArgumentException(
                $"QMT market-order style '{GetConfigurationValue(marketOrderStyle)}' is not supported on {exchange}.");
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
