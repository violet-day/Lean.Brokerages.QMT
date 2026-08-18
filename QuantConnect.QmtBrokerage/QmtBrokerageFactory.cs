using System;
using System.Collections.Generic;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// Creates the QMT brokerage and registers it as the live data queue handler.
    /// </summary>
    public sealed class QmtBrokerageFactory : BrokerageFactory
    {
        public QmtBrokerageFactory()
            : base(typeof(QmtBrokerage))
        {
            QmtMarket.RegisterMetadata();
        }

        public override Dictionary<string, string> BrokerageData => new Dictionary<string, string>
        {
            { "qmt-gateway-host", Config.Get("qmt-gateway-host", "127.0.0.1") },
            { "qmt-gateway-port", Config.Get("qmt-gateway-port", "17890") },
            { "qmt-account-id", Config.Get("qmt-account-id") },
            { "qmt-request-timeout", Config.Get("qmt-request-timeout", "10") },
            {
                "qmt-market-order-style",
                Config.Get(
                    "qmt-market-order-style",
                    QmtMarketOrderStyleResolver.LatestPriceConfigurationValue)
            },
            {
                "qmt-trading-environment",
                Config.Get(
                    "qmt-trading-environment",
                    QmtTradingEnvironmentResolver.LiveConfigurationValue)
            }
        };

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider)
        {
            return new QmtBrokerageModel();
        }

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (algorithm == null)
            {
                throw new ArgumentNullException(nameof(algorithm));
            }

            var errors = new List<string>();
            var host = Read<string>(job.BrokerageData, "qmt-gateway-host", errors);
            var port = Read<int>(job.BrokerageData, "qmt-gateway-port", errors);
            var accountId = Read<string>(job.BrokerageData, "qmt-account-id", errors);
            var requestTimeoutSeconds = Read<int>(job.BrokerageData, "qmt-request-timeout", errors);
            var marketOrderStyleText = Read<string>(job.BrokerageData, "qmt-market-order-style", errors);
            var marketOrderStyle = QmtMarketOrderStyle.LatestPrice;
            var tradingEnvironmentText = Read<string>(job.BrokerageData, "qmt-trading-environment", errors);
            var tradingEnvironment = QmtTradingEnvironment.Live;

            if (requestTimeoutSeconds <= 0)
            {
                errors.Add("qmt-request-timeout must be greater than zero seconds.");
            }
            if (!QmtMarketOrderStyleResolver.TryParse(marketOrderStyleText, out marketOrderStyle))
            {
                errors.Add(
                    $"qmt-market-order-style '{marketOrderStyleText}' is invalid. " +
                    "Use latest-price, five-level-immediate-or-cancel, five-level-immediate-to-limit, " +
                    "counterparty-best, own-best, immediate-or-cancel, or fill-or-kill.");
            }
            if (!QmtTradingEnvironmentResolver.TryParse(tradingEnvironmentText, out tradingEnvironment))
            {
                errors.Add(
                    $"qmt-trading-environment '{tradingEnvironmentText}' is invalid. " +
                    "Use live or simulation.");
            }

            if (errors.Count != 0)
            {
                throw new ArgumentException(string.Join(Environment.NewLine, errors));
            }

            var gatewayClient = new QmtGatewayClient(
                host,
                port,
                accountId,
                TimeSpan.FromSeconds(requestTimeoutSeconds));
            var brokerage = new QmtBrokerage(
                gatewayClient,
                algorithm.Transactions,
                marketOrderStyle: marketOrderStyle,
                tradingEnvironment: tradingEnvironment);
            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);
            return brokerage;
        }

        public override void Dispose()
        {
        }
    }
}
