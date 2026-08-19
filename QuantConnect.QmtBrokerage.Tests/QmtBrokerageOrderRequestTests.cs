using System;
using System.Globalization;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageOrderRequestTests
    {
        [Test]
        public void SendsLeanOrderIdAsClientOrderId()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider());
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "600000.SH",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            var order = new LimitOrder(
                symbol,
                100,
                10m,
                DateTime.UtcNow,
                string.Empty);
            typeof(Order).GetProperty(nameof(Order.Id))!.SetValue(order, 42);

            var result = brokerage.PlaceOrder(order);

            Assert.That(result, Is.True);
            Assert.That(gatewayClient.PlaceOrderRequest, Is.Not.Null);
            Assert.That(
                gatewayClient.PlaceOrderRequest!.ClientOrderId,
                Is.EqualTo(order.Id.ToString(CultureInfo.InvariantCulture)));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReturnsGatewayCancellationResult(bool cancellationSubmitted)
        {
            var gatewayClient = new QmtOrderTestGatewayClient(cancellationSubmitted);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider());
            var order = new MarketOrder(
                Symbol.Empty,
                100,
                DateTime.UtcNow);
            order.BrokerId.Add("native-order-1");

            var result = brokerage.CancelOrder(order);

            Assert.That(result, Is.EqualTo(cancellationSubmitted));
        }

        [Test]
        public void PublishesQmtRejectionReasonOnInvalidOrder()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider());
            OrderEvent? receivedOrderEvent = null;
            brokerage.OrdersStatusChanged += (_, orderEvents) =>
                receivedOrderEvent = orderEvents[0];

            gatewayClient.EmitOrderEvent(new QmtOrderEventPayload
            {
                StockCode = "600000.SH",
                OrderId = "native-order-1",
                ClientOrderId = "42",
                Status = 57,
                SubmitStatus = 52,
                ErrorId = 1001,
                ErrorMessage = "price outside limit",
                CallbackErrorMessage = "callback rejection",
                CancelInformation = "counter rejected order",
                Direction = "buy",
                OrderType = "limit",
                OriginalVolume = 100,
                LimitPrice = 10.5m
            });

            Assert.That(receivedOrderEvent, Is.Not.Null);
            Assert.That(receivedOrderEvent!.Status, Is.EqualTo(OrderStatus.Invalid));
            Assert.That(
                receivedOrderEvent.Message,
                Is.EqualTo(
                    "QMT error 1001: error_message=price outside limit; " +
                    "callback_error_message=callback rejection; " +
                    "cancel_information=counter rejected order"));
        }

        [Test]
        public void UsesRejectedSubmitStatusWhenOrderStatusIsUnknown()
        {
            var gatewayClient = new QmtOrderTestGatewayClient(cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider());
            OrderEvent? receivedOrderEvent = null;
            brokerage.OrdersStatusChanged += (_, orderEvents) =>
                receivedOrderEvent = orderEvents[0];

            gatewayClient.EmitOrderEvent(new QmtOrderEventPayload
            {
                StockCode = "600000.SH",
                OrderId = "native-order-2",
                ClientOrderId = "43",
                Status = 255,
                SubmitStatus = 52,
                ErrorMessage = "QMT rejected order",
                Direction = "buy",
                OrderType = "limit",
                OriginalVolume = 100,
                LimitPrice = 10.5m
            });

            Assert.That(receivedOrderEvent, Is.Not.Null);
            Assert.That(receivedOrderEvent!.Status, Is.EqualTo(OrderStatus.Invalid));
            Assert.That(receivedOrderEvent.Message, Is.EqualTo("error_message=QMT rejected order"));
        }
    }
}
