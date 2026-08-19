using System;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtMarketOrderTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void UsesOrderStyleRegardlessOfAccountType(bool isSimulation)
        {
            var gatewayClient = new QmtOrderTestGatewayClient(isSimulation: isSimulation);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                timeProvider: new TestTimeProvider(
                    new DateTime(2026, 8, 19, 4, 0, 0, DateTimeKind.Utc)));
            var order = CreateMarketOrder(
                "600000.SH",
                QmtMarketOrderStyle.FiveLevelImmediateToLimit);

            var accepted = brokerage.PlaceOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.True);
                Assert.That(gatewayClient.PlaceOrderRequest, Is.Not.Null);
                Assert.That(
                    gatewayClient.PlaceOrderRequest!.MarketOrderStyle,
                    Is.EqualTo("five-level-immediate-to-limit"));
                Assert.That(gatewayClient.PlaceOrderRequest.QmtPriceType, Is.EqualTo(43));
                Assert.That(gatewayClient.PlaceOrderRequest.QmtPrice, Is.EqualTo(0m));
            });
        }

        [TestCase("600000.SH", QmtMarketOrderStyle.LatestPrice, "latest-price", 5, -1)]
        [TestCase("000001.SZ", QmtMarketOrderStyle.LatestPrice, "latest-price", 5, -1)]
        [TestCase("830799.BJ", QmtMarketOrderStyle.LatestPrice, "latest-price", 5, -1)]
        [TestCase("600000.SH", QmtMarketOrderStyle.FiveLevelImmediateOrCancel, "five-level-immediate-or-cancel", 42, 0)]
        [TestCase("000001.SZ", QmtMarketOrderStyle.FiveLevelImmediateOrCancel, "five-level-immediate-or-cancel", 47, 0)]
        [TestCase("830799.BJ", QmtMarketOrderStyle.FiveLevelImmediateOrCancel, "five-level-immediate-or-cancel", 42, 0)]
        [TestCase("600000.SH", QmtMarketOrderStyle.FiveLevelImmediateToLimit, "five-level-immediate-to-limit", 43, 0)]
        [TestCase("830799.BJ", QmtMarketOrderStyle.FiveLevelImmediateToLimit, "five-level-immediate-to-limit", 43, 0)]
        [TestCase("600000.SH", QmtMarketOrderStyle.CounterpartyBest, "counterparty-best", 44, 0)]
        [TestCase("000001.SZ", QmtMarketOrderStyle.CounterpartyBest, "counterparty-best", 44, 0)]
        [TestCase("830799.BJ", QmtMarketOrderStyle.CounterpartyBest, "counterparty-best", 44, 0)]
        [TestCase("600000.SH", QmtMarketOrderStyle.OwnBest, "own-best", 45, 0)]
        [TestCase("000001.SZ", QmtMarketOrderStyle.OwnBest, "own-best", 45, 0)]
        [TestCase("830799.BJ", QmtMarketOrderStyle.OwnBest, "own-best", 45, 0)]
        [TestCase("000001.SZ", QmtMarketOrderStyle.ImmediateOrCancel, "immediate-or-cancel", 46, 0)]
        [TestCase("000001.SZ", QmtMarketOrderStyle.FillOrKill, "fill-or-kill", 48, 0)]
        public void MapsStyleToExchangePriceDetails(
            string stockCode,
            QmtMarketOrderStyle marketOrderStyle,
            string expectedStyle,
            int expectedPriceType,
            int expectedPrice)
        {
            var submission = QmtMarketOrderStyleResolver.Resolve(
                marketOrderStyle,
                QmtSecurityCode.Parse(stockCode).Exchange);

            Assert.Multiple(() =>
            {
                Assert.That(submission.Style, Is.EqualTo(expectedStyle));
                Assert.That(submission.PriceType, Is.EqualTo(expectedPriceType));
                Assert.That(submission.Price, Is.EqualTo((decimal)expectedPrice));
            });
        }

        [TestCase("000001.SZ", QmtMarketOrderStyle.FiveLevelImmediateToLimit)]
        [TestCase("600000.SH", QmtMarketOrderStyle.ImmediateOrCancel)]
        [TestCase("830799.BJ", QmtMarketOrderStyle.ImmediateOrCancel)]
        [TestCase("600000.SH", QmtMarketOrderStyle.FillOrKill)]
        [TestCase("830799.BJ", QmtMarketOrderStyle.FillOrKill)]
        public void RejectsStyleUnsupportedByExchange(
            string stockCode,
            QmtMarketOrderStyle marketOrderStyle)
        {
            Assert.Throws<ArgumentException>(() => QmtMarketOrderStyleResolver.Resolve(
                marketOrderStyle,
                QmtSecurityCode.Parse(stockCode).Exchange));
        }

        [Test]
        public void RejectsMissingOrderStyleBeforeGateway()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(isSimulation: false);
            using var brokerage = new QmtBrokerage(gatewayClient, new QmtOrderTestProvider());
            BrokerageMessageEvent? message = null;
            brokerage.Message += (_, brokerageMessage) => message = brokerageMessage;
            var order = CreateMarketOrder("600000.SH");

            var accepted = brokerage.PlaceOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(gatewayClient.PlaceOrderRequest, Is.Null);
                Assert.That(message?.Code, Is.EqualTo("MissingMarketOrderStyle"));
            });
        }

        [Test]
        public void ThrowsMarketClosedOutsideSimulationSessionBeforeGateway()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(isSimulation: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                timeProvider: new TestTimeProvider(
                    new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc)));
            var order = CreateMarketOrder("600000.SH", QmtMarketOrderStyle.LatestPrice);

            var exception = Assert.Throws<QmtOrderSubmissionException>(() => brokerage.PlaceOrder(order));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.ErrorCode, Is.EqualTo("MarketClosed"));
                Assert.That(gatewayClient.PlaceOrderRequest, Is.Null);
            });
        }

        [Test]
        public void RejectsStyleUnsupportedByExchangeBeforeGateway()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(isSimulation: false);
            using var brokerage = new QmtBrokerage(gatewayClient, new QmtOrderTestProvider());
            BrokerageMessageEvent? message = null;
            brokerage.Message += (_, brokerageMessage) => message = brokerageMessage;
            var order = CreateMarketOrder("600000.SH", QmtMarketOrderStyle.FillOrKill);

            var accepted = brokerage.PlaceOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(gatewayClient.PlaceOrderRequest, Is.Null);
                Assert.That(message?.Code, Is.EqualTo("UnsupportedMarketOrderStyle"));
            });
        }

        [Test]
        public void RejectsMarketOrderStyleOnLimitOrderBeforeGateway()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(isSimulation: false);
            using var brokerage = new QmtBrokerage(gatewayClient, new QmtOrderTestProvider());
            BrokerageMessageEvent? message = null;
            brokerage.Message += (_, brokerageMessage) => message = brokerageMessage;
            var symbol = GetSymbol("600000.SH");
            var order = new LimitOrder(
                symbol,
                100,
                10m,
                DateTime.UtcNow,
                properties: new QmtOrderProperties
                {
                    MarketOrderStyle = QmtMarketOrderStyle.LatestPrice
                });

            var accepted = brokerage.PlaceOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(gatewayClient.PlaceOrderRequest, Is.Null);
                Assert.That(message?.Code, Is.EqualTo("UnexpectedMarketOrderStyle"));
            });
        }

        [Test]
        public void ClonesOrderStyle()
        {
            var properties = new QmtOrderProperties
            {
                MarketOrderStyle = QmtMarketOrderStyle.CounterpartyBest
            };

            var clone = properties.Clone();

            Assert.That(clone, Is.TypeOf<QmtOrderProperties>());
            Assert.That(
                ((QmtOrderProperties)clone).MarketOrderStyle,
                Is.EqualTo(QmtMarketOrderStyle.CounterpartyBest));
        }

        private static MarketOrder CreateMarketOrder(
            string stockCode,
            QmtMarketOrderStyle? marketOrderStyle = null)
        {
            QmtOrderProperties? properties = null;
            if (marketOrderStyle.HasValue)
            {
                properties = new QmtOrderProperties
                {
                    MarketOrderStyle = marketOrderStyle.Value
                };
            }
            return new MarketOrder(
                GetSymbol(stockCode),
                100,
                DateTime.UtcNow,
                properties: properties);
        }

        private static Symbol GetSymbol(string stockCode)
        {
            return new QmtSymbolMapper().GetLeanSymbol(
                stockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
        }

        private sealed class TestTimeProvider : ITimeProvider
        {
            private readonly DateTime _utcTime;

            public TestTimeProvider(DateTime utcTime)
            {
                _utcTime = utcTime;
            }

            public DateTime GetUtcNow()
            {
                return _utcTime;
            }
        }
    }
}
