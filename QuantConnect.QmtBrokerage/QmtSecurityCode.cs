using System;
using System.Linq;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Parses and formats QMT stock codes such as 600000.SH and 000001.SZ.
    /// </summary>
    public readonly struct QmtSecurityCode : IEquatable<QmtSecurityCode>
    {
        public string Ticker { get; }
        public QmtExchange Exchange { get; }

        public QmtSecurityCode(string ticker, QmtExchange exchange)
        {
            if (ticker == null || ticker.Length != 6 || !ticker.All(char.IsDigit))
            {
                throw new ArgumentException("QMT stock tickers must contain exactly six digits.", nameof(ticker));
            }

            Ticker = ticker;
            Exchange = exchange;
        }

        public static QmtSecurityCode Parse(string brokerageSymbol)
        {
            if (string.IsNullOrWhiteSpace(brokerageSymbol))
            {
                throw new ArgumentException("A QMT brokerage symbol is required.", nameof(brokerageSymbol));
            }

            var symbolParts = brokerageSymbol.Split('.');
            if (symbolParts.Length != 2)
            {
                throw new ArgumentException("QMT stock codes must use the TICKER.EXCHANGE format.", nameof(brokerageSymbol));
            }

            var exchange = symbolParts[1].ToUpperInvariant() switch
            {
                "SH" => QmtExchange.Shanghai,
                "SZ" => QmtExchange.Shenzhen,
                "BJ" => QmtExchange.Beijing,
                _ => throw new ArgumentException("Unsupported QMT stock exchange suffix.", nameof(brokerageSymbol))
            };

            return new QmtSecurityCode(symbolParts[0], exchange);
        }

        public override string ToString()
        {
            var exchangeSuffix = Exchange switch
            {
                QmtExchange.Shanghai => "SH",
                QmtExchange.Shenzhen => "SZ",
                QmtExchange.Beijing => "BJ",
                _ => throw new ArgumentOutOfRangeException(nameof(Exchange), Exchange, "Unsupported QMT exchange.")
            };

            return $"{Ticker}.{exchangeSuffix}";
        }

        public bool Equals(QmtSecurityCode other)
        {
            return Ticker == other.Ticker && Exchange == other.Exchange;
        }

        public override bool Equals(object? value)
        {
            return value is QmtSecurityCode other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Ticker, Exchange);
        }
    }
}
