using System;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Defines exchange and board-specific rules for QMT A-share equities.
    /// </summary>
    public static class QmtEquityMarketRules
    {
        private static readonly QmtSymbolMapper SymbolMapper = new QmtSymbolMapper();

        /// <summary>
        /// Gets the minimum quantity for a buy order in shares.
        /// </summary>
        public static decimal GetMinimumBuyOrderQuantity(Symbol symbol)
        {
            var securityCode = GetSecurityCode(symbol);
            return IsShanghaiScienceAndTechnologyInnovationBoard(securityCode) ? 200m : 100m;
        }

        /// <summary>
        /// Gets the board's base daily price limit percentage. Security-specific exceptions are outside this rule.
        /// </summary>
        public static decimal GetDailyPriceLimitPercentage(Symbol symbol)
        {
            var securityCode = GetSecurityCode(symbol);
            if (securityCode.Exchange == QmtExchange.Beijing)
            {
                return 0.30m;
            }

            if (IsShanghaiScienceAndTechnologyInnovationBoard(securityCode) ||
                IsShenzhenChiNextBoard(securityCode))
            {
                return 0.20m;
            }

            return 0.10m;
        }

        private static QmtSecurityCode GetSecurityCode(Symbol symbol)
        {
            return QmtSecurityCode.Parse(SymbolMapper.GetBrokerageSymbol(symbol));
        }

        private static bool IsShanghaiScienceAndTechnologyInnovationBoard(QmtSecurityCode securityCode)
        {
            return securityCode.Exchange == QmtExchange.Shanghai &&
                (securityCode.Ticker.StartsWith("688", StringComparison.Ordinal) ||
                    securityCode.Ticker.StartsWith("689", StringComparison.Ordinal));
        }

        private static bool IsShenzhenChiNextBoard(QmtSecurityCode securityCode)
        {
            return securityCode.Exchange == QmtExchange.Shenzhen &&
                (securityCode.Ticker.StartsWith("300", StringComparison.Ordinal) ||
                    securityCode.Ticker.StartsWith("301", StringComparison.Ordinal));
        }
    }
}
