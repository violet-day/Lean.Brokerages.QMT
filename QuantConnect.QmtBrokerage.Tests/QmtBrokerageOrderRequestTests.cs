using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
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

        [Test]
        public void PublishesDistinctNativeStatusesAndSuppressesExactDuplicates()
        {
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "600354.SH",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            var order = new LimitOrder(
                symbol,
                100,
                10.86m,
                DateTime.UtcNow,
                string.Empty)
            {
                Status = OrderStatus.New
            };
            typeof(Order).GetProperty(nameof(Order.Id))!.SetValue(order, 2);
            var gatewayClient = new QmtOrderTestGatewayClient(cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(order));
            var receivedOrderEvents = new List<OrderEvent>();
            BrokerageOrderIdChangedEvent? orderIdChangedEvent = null;
            brokerage.OrdersStatusChanged += (_, orderEvents) =>
            {
                receivedOrderEvents.AddRange(orderEvents);
                order.Status = orderEvents[^1].Status;
            };
            brokerage.OrderIdChanged += (_, eventArguments) => orderIdChangedEvent = eventArguments;

            gatewayClient.EmitOrderEvent(new QmtOrderEventPayload
            {
                StockCode = "600354.SH",
                ClientOrderId = "2",
                Status = 49,
                SubmitStatus = 51,
                Direction = "buy",
                Remark = "2"
            });
            gatewayClient.EmitOrderEvent(new QmtOrderEventPayload
            {
                StockCode = "600354.SH",
                OrderId = "1795",
                ClientOrderId = "2",
                Status = 50,
                SubmitStatus = 51,
                Direction = "buy",
                Remark = "2"
            });
            gatewayClient.EmitOrderEvent(new QmtOrderEventPayload
            {
                StockCode = "600354.SH",
                OrderId = "1795",
                ClientOrderId = "2",
                Status = 50,
                SubmitStatus = 51,
                Direction = "buy",
                Remark = "2"
            });

            Assert.Multiple(() =>
            {
                Assert.That(receivedOrderEvents.Select(orderEvent => orderEvent.Status),
                    Is.EqualTo(new[] { OrderStatus.Submitted, OrderStatus.Submitted }));
                Assert.That(
                    ParseQmtOrderEventMessage(receivedOrderEvents[0]).Value<int>("qmt_order_status"),
                    Is.EqualTo(49));
                Assert.That(
                    ParseQmtOrderEventMessage(receivedOrderEvents[1]).Value<int>("qmt_order_status"),
                    Is.EqualTo(50));
                Assert.That(orderIdChangedEvent?.OrderId, Is.EqualTo(2));
                Assert.That(orderIdChangedEvent?.BrokerId, Is.EqualTo(new[] { "1795" }));
            });
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

        [TestCase(OrderStatus.Canceled)]
        [TestCase(OrderStatus.Filled)]
        [TestCase(OrderStatus.Invalid)]
        public void RejectsCancellationForClosedOrder(OrderStatus orderStatus)
        {
            var gatewayClient = new QmtOrderTestGatewayClient(
                cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider());
            BrokerageMessageEvent? message = null;
            brokerage.Message += (_, brokerageMessage) => message = brokerageMessage;
            var order = new MarketOrder(
                Symbol.Empty,
                100,
                DateTime.UtcNow)
            {
                Status = orderStatus
            };
            order.BrokerId.Add("native-order-1");

            var result = brokerage.CancelOrder(order);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(gatewayClient.CancelOrderRequest, Is.Null);
                Assert.That(message?.Code, Is.EqualTo("CancelNotAllowed"));
            });
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
                ParseQmtOrderEventMessage(receivedOrderEvent).Value<string>("error_message"),
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
            Assert.That(
                ParseQmtOrderEventMessage(receivedOrderEvent).Value<string>("error_message"),
                Is.EqualTo("error_message=QMT rejected order"));
        }

        private static JObject ParseQmtOrderEventMessage(OrderEvent orderEvent)
        {
            const string messagePrefix = "QMT_EVENT_V1 ";
            Assert.That(orderEvent.Message, Does.StartWith(messagePrefix));
            return JObject.Parse(orderEvent.Message[messagePrefix.Length..]);
        }
    }
}
