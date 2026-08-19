using System;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageOrderValidationTests
    {
        [TestCase(0)]
        [TestCase(0.5)]
        public void RejectsInvalidMarketOrderQuantity(decimal quantity)
        {
            var gatewayClient = new QmtOrderTestGatewayClient();
            using var brokerage = new QmtBrokerage(gatewayClient, new QmtOrderTestProvider());
            BrokerageMessageEvent? message = null;
            brokerage.Message += (_, brokerageMessage) => message = brokerageMessage;
            var order = new MarketOrder(
                GetSymbol(),
                quantity,
                DateTime.UtcNow,
                properties: new QmtOrderProperties
                {
                    MarketOrderStyle = QmtMarketOrderStyle.LatestPrice
                });

            var accepted = brokerage.PlaceOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(message?.Code, Is.EqualTo("UnsupportedOrder"));
                Assert.That(gatewayClient.PlaceOrderRequest, Is.Null);
            });
        }

        [Test]
        public void RejectsUnsupportedOrderUpdate()
        {
            using var brokerage = new QmtBrokerage(
                new QmtOrderTestGatewayClient(),
                new QmtOrderTestProvider());
            BrokerageMessageEvent? message = null;
            brokerage.Message += (_, brokerageMessage) => message = brokerageMessage;
            var order = new LimitOrder(GetSymbol(), 100m, 1m, DateTime.UtcNow);

            var accepted = brokerage.UpdateOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(message?.Code, Is.EqualTo("UpdateNotSupported"));
            });
        }

        [Test]
        public void RejectsCancellationWithoutNativeOrderId()
        {
            using var brokerage = new QmtBrokerage(
                new QmtOrderTestGatewayClient(),
                new QmtOrderTestProvider());
            BrokerageMessageEvent? message = null;
            brokerage.Message += (_, brokerageMessage) => message = brokerageMessage;
            var order = new LimitOrder(GetSymbol(), 100m, 1m, DateTime.UtcNow);

            var accepted = brokerage.CancelOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(message?.Code, Is.EqualTo("MissingBrokerageOrderId"));
            });
        }

        [Test]
        public void ThrowsMarketClosedForLimitOrderBeforeGateway()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(isSimulation: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                timeProvider: new QmtOrderTestTimeProvider(
                    new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc)));
            var order = new LimitOrder(GetSymbol(), 100m, 1m, DateTime.UtcNow);

            var exception = Assert.Throws<QmtOrderSubmissionException>(() => brokerage.PlaceOrder(order));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.ErrorCode, Is.EqualTo("MarketClosed"));
                Assert.That(gatewayClient.PlaceOrderRequest, Is.Null);
            });
        }

        private static Symbol GetSymbol()
        {
            return new QmtSymbolMapper().GetLeanSymbol(
                "600000.SH",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
        }
    }
}
