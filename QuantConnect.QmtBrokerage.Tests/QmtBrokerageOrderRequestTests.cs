using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            var gatewayClient = new TestGatewayClient(cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new TestOrderProvider());
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "600000.SH",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            var order = new MarketOrder(
                symbol,
                100,
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

        [TestCase("600000.SH", true, "latest-price", 5, -1)]
        [TestCase("000001.SZ", true, "latest-price", 5, -1)]
        [TestCase("830799.BJ", true, "latest-price", 5, -1)]
        [TestCase("600000.SH", false, "five-level-immediate-or-cancel", 42, 0)]
        [TestCase("000001.SZ", false, "five-level-immediate-or-cancel", 47, 0)]
        [TestCase("830799.BJ", false, "five-level-immediate-or-cancel", 42, 0)]
        public void MapsAccountMarketOrderStyleToExchangePriceType(
            string stockCode,
            bool isSimulation,
            string expectedStyle,
            int expectedPriceType,
            int expectedPrice)
        {
            var exchange = QmtSecurityCode.Parse(stockCode).Exchange;
            var accountProperties = new QmtAccountProperties(isSimulation);

            var submission = QmtMarketOrderStyleResolver.Resolve(
                accountProperties.MarketOrderStyle,
                exchange);

            Assert.Multiple(() =>
            {
                Assert.That(submission.Style, Is.EqualTo(expectedStyle));
                Assert.That(submission.PriceType, Is.EqualTo(expectedPriceType));
                Assert.That(submission.Price, Is.EqualTo((decimal)expectedPrice));
            });
        }

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
        public void MapsQmtMarketOrderStyleToExchangePriceType(
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
        public void RejectsMarketOrderStyleUnsupportedByExchange(
            string stockCode,
            QmtMarketOrderStyle marketOrderStyle)
        {
            Assert.Throws<ArgumentException>(() => QmtMarketOrderStyleResolver.Resolve(
                marketOrderStyle,
                QmtSecurityCode.Parse(stockCode).Exchange));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReturnsGatewayCancellationResult(bool cancellationSubmitted)
        {
            var gatewayClient = new TestGatewayClient(cancellationSubmitted);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new TestOrderProvider());
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
            var gatewayClient = new TestGatewayClient(cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new TestOrderProvider());
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
                Is.EqualTo("QMT error 1001: counter rejected order"));
        }

        [Test]
        public void UsesRejectedSubmitStatusWhenOrderStatusIsUnknown()
        {
            var gatewayClient = new TestGatewayClient(cancellationSubmitted: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new TestOrderProvider());
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
            Assert.That(receivedOrderEvent.Message, Is.EqualTo("QMT rejected order"));
        }

        private sealed class TestOrderProvider : IOrderProvider
        {
            public int OrdersCount => 0;

            public Order? GetOrderById(int orderId)
            {
                return null;
            }

            public List<Order> GetOrdersByBrokerageId(string brokerageId)
            {
                return new List<Order>();
            }

            public IEnumerable<OrderTicket> GetOrderTickets(Func<OrderTicket, bool>? filter = null)
            {
                return Enumerable.Empty<OrderTicket>();
            }

            public IEnumerable<OrderTicket> GetOpenOrderTickets(Func<OrderTicket, bool>? filter = null)
            {
                return Enumerable.Empty<OrderTicket>();
            }

            public OrderTicket? GetOrderTicket(int orderId)
            {
                return null;
            }

            public IEnumerable<Order> GetOrders(Func<Order, bool>? filter = null)
            {
                return Enumerable.Empty<Order>();
            }

            public List<Order> GetOpenOrders(Func<Order, bool>? filter = null)
            {
                return new List<Order>();
            }

            public ProjectedHoldings? GetProjectedHoldings(Security security)
            {
                return null;
            }
        }

        private sealed class TestGatewayClient : IQmtGatewayClient
        {
            private readonly bool _cancellationSubmitted;

            public bool IsConnected { get; private set; } = true;
            public QmtPlaceOrderRequest? PlaceOrderRequest { get; private set; }
            public QmtHelloPayload? ServerInformation { get; private set; }

            public event EventHandler<QmtGatewayMessageEventArgs>? EventReceived;
            public event EventHandler<QmtGatewayDisconnectedEventArgs>? Disconnected
            {
                add { }
                remove { }
            }

            public TestGatewayClient(bool cancellationSubmitted, bool isSimulation = false)
            {
                _cancellationSubmitted = cancellationSubmitted;
                ServerInformation = new QmtHelloPayload
                {
                    AccountId = "order-request-test",
                    ServerName = "test-gateway",
                    IsSimulation = isSimulation
                };
            }

            public void Connect()
            {
                IsConnected = true;
            }

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                IsConnected = true;
                return Task.CompletedTask;
            }

            public void Disconnect()
            {
                IsConnected = false;
                ServerInformation = null;
            }

            public Task<QmtProtocolMessage> SendRequestAsync(
                string operation,
                object? payload = null,
                CancellationToken cancellationToken = default)
            {
                if (operation == QmtProtocol.Operations.PlaceOrder)
                {
                    PlaceOrderRequest = (QmtPlaceOrderRequest?)payload;
                    return Task.FromResult(new QmtProtocolMessage
                    {
                        MessageType = QmtProtocol.MessageTypes.Response,
                        RequestId = "place-request",
                        Operation = operation,
                        Success = true,
                        Payload = JObject.FromObject(new QmtPlaceOrderPayload
                        {
                            Accepted = true,
                            ClientOrderId = PlaceOrderRequest?.ClientOrderId ?? string.Empty,
                            NativeOrderId = string.Empty
                        })
                    });
                }

                if (operation == QmtProtocol.Operations.CancelOrder)
                {
                    return Task.FromResult(new QmtProtocolMessage
                    {
                        MessageType = QmtProtocol.MessageTypes.Response,
                        RequestId = "cancel-request",
                        Operation = operation,
                        Success = true,
                        Payload = JObject.FromObject(new QmtCancelOrderPayload
                        {
                            Canceled = _cancellationSubmitted,
                            OrderId = "native-order-1"
                        })
                    });
                }

                throw new InvalidOperationException($"Unexpected operation: {operation}");
            }

            public void EmitOrderEvent(QmtOrderEventPayload payload)
            {
                EventReceived?.Invoke(this, new QmtGatewayMessageEventArgs(new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Event,
                    Operation = QmtProtocol.Operations.Order,
                    Success = true,
                    Payload = JObject.FromObject(payload)
                }));
            }

            public void Dispose()
            {
                Disconnect();
            }
        }
    }
}
