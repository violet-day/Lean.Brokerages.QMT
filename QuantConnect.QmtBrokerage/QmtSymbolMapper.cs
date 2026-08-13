using System;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Maps LEAN equity symbols to QMT stock codes.
    /// </summary>
    public sealed class QmtSymbolMapper : ISymbolMapper
    {
        public const string MarketName = QmtMarket.Name;
        public static string RegisteredMarketName => MarketName;

        static QmtSymbolMapper()
        {
            QmtMarket.RegisterIdentifier();
        }

        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol == null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }

            if (symbol.SecurityType != SecurityType.Equity ||
                !string.Equals(symbol.ID.Market, MarketName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("QMT supports only equities in the china market.", nameof(symbol));
            }

            if (symbol.Value.Contains("."))
            {
                return QmtSecurityCode.Parse(symbol.Value).ToString();
            }

            if (symbol.Value.Length != 6)
            {
                throw new ArgumentException("QMT equity tickers must contain six digits.", nameof(symbol));
            }

            var exchange = symbol.Value[0] switch
            {
                '5' or '6' or '9' => QmtExchange.Shanghai,
                '0' or '1' or '2' or '3' => QmtExchange.Shenzhen,
                '4' or '8' => QmtExchange.Beijing,
                _ => throw new ArgumentException(
                    "The QMT exchange cannot be inferred from this ticker. Use a ticker with .SH, .SZ, or .BJ.",
                    nameof(symbol))
            };

            return new QmtSecurityCode(symbol.Value, exchange).ToString();
        }

        public Symbol GetLeanSymbol(
            string brokerageSymbol,
            SecurityType securityType,
            string market,
            DateTime expirationDate = default,
            decimal strike = 0,
            OptionRight optionRight = 0)
        {
            if (securityType != SecurityType.Equity)
            {
                throw new ArgumentException("QMT supports only equity symbols.", nameof(securityType));
            }

            if (!string.Equals(market, MarketName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("QMT equity symbols must use the china market.", nameof(market));
            }

            var securityCode = QmtSecurityCode.Parse(brokerageSymbol);
            var securityIdentifier = SecurityIdentifier.GenerateEquity(
                SecurityIdentifier.DefaultDate,
                securityCode.Ticker,
                MarketName);
            return new Symbol(securityIdentifier, securityCode.ToString());
        }
    }
}
