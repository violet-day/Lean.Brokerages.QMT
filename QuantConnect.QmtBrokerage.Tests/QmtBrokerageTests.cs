using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageTests
    {
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();

        [Test]
        public void MapsAccountPositionsAndOpenOrders()
        {
            var gatewayClient = CreateConnectedGatewayClient(false);
            gatewayClient.Responses[QmtProtocol.Operations.QueryAccount] = Response(
                QmtProtocol.Operations.QueryAccount,
                new QmtQueryAccountPayload
                {
                    Accounts = new List<QmtAccountSnapshot> { new QmtAccountSnapshot { AvailableCash = 1234.56m } }
                });
            gatewayClient.Responses[QmtProtocol.Operations.QueryPositions] = Response(
                QmtProtocol.Operations.QueryPositions,
                new QmtQueryPositionsPayload
                {
                    Positions = new List<QmtPositionSnapshot>
                    {
                        new QmtPositionSnapshot
                        {
                            StockCode = "600000.SH",
                            Volume = 100m,
                            OpenPrice = 9.8m,
                            LastPrice = 10.1m,
                            MarketValue = 1010m
                        }
                    }
                });
            gatewayClient.Responses[QmtProtocol.Operations.QueryOrders] = Response(
                QmtProtocol.Operations.QueryOrders,
                new QmtQueryOrdersPayload
                {
                    Orders = new List<QmtOrderSnapshot>
                    {
                        new QmtOrderSnapshot
                        {
                            StockCode = "600000.SH",
                            OrderId = "native-open",
                            Direction = "buy",
                            OrderType = "limit",
                            Status = 50,
                            OriginalVolume = 100,
                            LimitPrice = 10m
                        },
                        new QmtOrderSnapshot
                        {
                            StockCode = "000001.SZ",
                            OrderId = "native-filled",
                            Direction = "sell",
                            OrderType = "market",
                            Status = 56,
                            OriginalVolume = 100
                        }
                    }
                });
            using var brokerage = new QmtBrokerage(gatewayClient, new FakeOrderProvider(), false);

            var cash = brokerage.GetCashBalance();
            var holdings = brokerage.GetAccountHoldings();
            var orders = brokerage.GetOpenOrders();

            Assert.AreEqual("CNY", brokerage.AccountBaseCurrency);
            Assert.AreEqual(1234.56m, cash.Single().Amount);
            Assert.AreEqual("CNY", cash.Single().Currency);
            Assert.AreEqual(100m, holdings.Single().Quantity);
            Assert.AreEqual("600000.SH", holdings.Single().Symbol.Value);
            Assert.AreEqual(1, orders.Count);
            Assert.AreEqual("native-open", orders[0].BrokerId.Single());
            Assert.AreEqual(OrderStatus.Submitted, orders[0].Status);
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void TradingRequiresLocalAndGatewayFlags(bool localTradingEnabled, bool gatewayTradingEnabled)
        {
            var gatewayClient = CreateConnectedGatewayClient(gatewayTradingEnabled);
            var symbol = _symbolMapper.GetLeanSymbol("600000.SH", SecurityType.Equity, QmtSymbolMapper.MarketName);
            using var brokerage = new QmtBrokerage(gatewayClient, new FakeOrderProvider(), localTradingEnabled);

            var placed = brokerage.PlaceOrder(new MarketOrder(symbol, 100, DateTime.UtcNow));

            Assert.IsFalse(placed);
            Assert.IsFalse(gatewayClient.Requests.Any(request => request.Operation == QmtProtocol.Operations.PlaceOrder));
        }

        [Test]
        public void AcceptsOrderWithoutNativeIdThenMapsCallbackAndCancels()
        {
            var gatewayClient = CreateConnectedGatewayClient(true);
            gatewayClient.Responses[QmtProtocol.Operations.PlaceOrder] = Response(
                QmtProtocol.Operations.PlaceOrder,
                new QmtPlaceOrderPayload
                {
                    Accepted = true,
                    ClientOrderId = "7",
                    NativeOrderId = string.Empty
                });
            gatewayClient.Responses[QmtProtocol.Operations.CancelOrder] = Response(
                QmtProtocol.Operations.CancelOrder,
                new { canceled = true, order_id = "native-123" });
            var symbol = _symbolMapper.GetLeanSymbol("600000.SH", SecurityType.Equity, QmtSymbolMapper.MarketName);
            var order = CreateOrder(7, OrderType.Limit, symbol, -100m, 10.25m);
            using var brokerage = new QmtBrokerage(gatewayClient, new FakeOrderProvider(order), true);
            var orderIdChanges = new List<BrokerageOrderIdChangedEvent>();
            brokerage.OrderIdChanged += (_, orderIdChangedEvent) => orderIdChanges.Add(orderIdChangedEvent);

            Assert.IsTrue(brokerage.PlaceOrder(order));
            Assert.AreEqual(0, orderIdChanges.Count);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Order, new QmtOrderEventPayload
            {
                StockCode = "600000.SH",
                OrderId = "native-123",
                ClientOrderId = "7",
                Direction = "sell",
                OrderType = "limit",
                Status = 50,
                OriginalVolume = 100m,
                LimitPrice = 10.25m
            });
            Assert.AreEqual(1, orderIdChanges.Count);
            Assert.AreEqual(7, orderIdChanges[0].OrderId);
            Assert.AreEqual("native-123", orderIdChanges[0].BrokerId.Single());

            // LEAN applies BrokerageOrderIdChangedEvent to the tracked order.
            order.BrokerId.Add("native-123");
            Assert.IsTrue(brokerage.CancelOrder(order));

            var placeRequest = gatewayClient.Requests.Single(request => request.Operation == QmtProtocol.Operations.PlaceOrder);
            var payload = JObject.FromObject(placeRequest.Payload).ToObject<QmtPlaceOrderRequest>();
            Assert.AreEqual("600000.SH", payload.StockCode);
            Assert.AreEqual("sell", payload.Direction);
            Assert.AreEqual("limit", payload.OrderType);
            Assert.AreEqual(100m, payload.Quantity);
            Assert.AreEqual(10.25m, payload.LimitPrice);
            Assert.IsTrue(gatewayClient.Requests.Any(request => request.Operation == QmtProtocol.Operations.CancelOrder));
        }

        [Test]
        public void ConvertsQuoteAndDealEventsWithoutNetwork()
        {
            var gatewayClient = CreateConnectedGatewayClient(false);
            gatewayClient.Responses[QmtProtocol.Operations.Subscribe] = Response(
                QmtProtocol.Operations.Subscribe,
                new { subscribed = true, subscription_id = "71", stock_code = "000001.SZ" });
            gatewayClient.Responses[QmtProtocol.Operations.Unsubscribe] = Response(
                QmtProtocol.Operations.Unsubscribe,
                new { unsubscribed = true, subscription_id = "71" });
            var symbol = _symbolMapper.GetLeanSymbol("000001.SZ", SecurityType.Equity, QmtSymbolMapper.MarketName);
            var order = CreateOrder(42, OrderType.Market, symbol, 100m);
            order.BrokerId.Add("native-456");
            var orderProvider = new FakeOrderProvider(order);
            using var brokerage = new QmtBrokerage(gatewayClient, orderProvider, false);
            var orderEvents = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);
            var dataConfig = new SubscriptionDataConfig(
                typeof(Tick),
                symbol,
                Resolution.Tick,
                TimeZones.Shanghai,
                TimeZones.Shanghai,
                false,
                false,
                false,
                tickType: TickType.Quote);
            using var enumerator = brokerage.Subscribe(dataConfig, null);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Quote, new QmtQuoteEventPayload
            {
                StockCode = "000001.SZ",
                Time = "2026-08-13T09:30:01.123+08:00",
                LastPrice = 10.25m,
                BidPrice = 10.24m,
                AskPrice = 10.25m,
                BidVolume = 500m,
                AskVolume = 300m,
                Volume = 1200m
            });
            Assert.IsTrue(enumerator.MoveNext());
            var tick = (Tick)enumerator.Current;
            Assert.AreEqual(10.25m, tick.LastPrice);
            Assert.AreEqual(10.24m, tick.BidPrice);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Order, new QmtOrderEventPayload
            {
                StockCode = "000001.SZ",
                OrderId = "native-456",
                ClientOrderId = "42",
                Direction = "buy",
                OrderType = "market",
                Status = 50,
                OriginalVolume = 100m
            });
            Assert.AreEqual(1, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, orderEvents[0].Status);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Deal, new QmtDealEventPayload
            {
                StockCode = "000001.SZ",
                OrderId = "native-456",
                DealId = "deal-1",
                Direction = "buy",
                Volume = 20m,
                Price = 10.25m,
                Commission = 1.25m
            });
            Assert.AreEqual(2, orderEvents.Count);
            Assert.AreEqual(OrderStatus.PartiallyFilled, orderEvents[1].Status);
            Assert.AreEqual(20m, orderEvents[1].FillQuantity);
            Assert.AreEqual(10.25m, orderEvents[1].FillPrice);
            Assert.AreEqual(1.25m, orderEvents[1].OrderFee.Value.Amount);
            Assert.AreEqual("CNY", orderEvents[1].OrderFee.Value.Currency);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Deal, new QmtDealEventPayload
            {
                StockCode = "000001.SZ",
                OrderId = "native-456",
                DealId = "deal-1",
                Direction = "buy",
                Volume = 20m,
                Price = 10.25m,
                Commission = 1.25m
            });
            Assert.AreEqual(2, orderEvents.Count);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Order, new QmtOrderEventPayload
            {
                StockCode = "000001.SZ",
                OrderId = "native-456",
                ClientOrderId = "42",
                Direction = "buy",
                OrderType = "market",
                Status = 56,
                OriginalVolume = 100m
            });
            Assert.AreEqual(2, orderEvents.Count);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Deal, new QmtDealEventPayload
            {
                StockCode = "000001.SZ",
                OrderId = "native-456",
                DealId = "deal-2",
                Direction = "buy",
                Volume = 80m,
                Price = 10.26m,
                Commission = 2.5m
            });
            Assert.AreEqual(3, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Filled, orderEvents[2].Status);
            Assert.AreEqual(80m, orderEvents[2].FillQuantity);

            brokerage.Unsubscribe(dataConfig);
            var unsubscribeRequest = gatewayClient.Requests.Single(
                request => request.Operation == QmtProtocol.Operations.Unsubscribe);
            Assert.AreEqual(
                "71",
                JObject.FromObject(unsubscribeRequest.Payload).ToObject<QmtUnsubscribeRequest>().SubscriptionId);
        }

        [Test]
        public void ProcessesDealAfterLateOrderIdBinding()
        {
            var gatewayClient = CreateConnectedGatewayClient(false);
            var symbol = _symbolMapper.GetLeanSymbol("600000.SH", SecurityType.Equity, QmtSymbolMapper.MarketName);
            var order = CreateOrder(84, OrderType.Market, symbol, 100m);
            using var brokerage = new QmtBrokerage(gatewayClient, new FakeOrderProvider(order), false);
            var orderEvents = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

            var deal = new QmtDealEventPayload
            {
                StockCode = "600000.SH",
                OrderId = "late-native-id",
                DealId = "late-deal-id",
                Direction = "buy",
                Volume = 100m,
                Price = 10m
            };
            gatewayClient.RaiseEvent(QmtProtocol.Operations.Deal, deal);
            Assert.AreEqual(0, orderEvents.Count);

            gatewayClient.RaiseEvent(QmtProtocol.Operations.Order, new QmtOrderEventPayload
            {
                StockCode = "600000.SH",
                OrderId = "late-native-id",
                ClientOrderId = "84",
                Direction = "buy",
                OrderType = "market",
                Status = 50,
                OriginalVolume = 100m
            });

            Assert.AreEqual(2, orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.Filled, orderEvents[1].Status);
        }

        private static FakeGatewayClient CreateConnectedGatewayClient(bool tradingEnabled)
        {
            return new FakeGatewayClient
            {
                IsConnected = true,
                ServerInformation = new QmtHelloPayload
                {
                    ServerName = "fake-qmt-gateway",
                    AccountId = "test-account",
                    TradingEnabled = tradingEnabled
                }
            };
        }

        private static QmtProtocolMessage Response(string operation, object payload)
        {
            return new QmtProtocolMessage
            {
                MessageType = QmtProtocol.MessageTypes.Response,
                Operation = operation,
                Success = true,
                Payload = JObject.FromObject(payload)
            };
        }

        private static Order CreateOrder(int orderId, OrderType orderType, Symbol symbol, decimal quantity, decimal limitPrice = 0m)
        {
            var request = new SubmitOrderRequest(
                orderType,
                SecurityType.Equity,
                symbol,
                quantity,
                0m,
                limitPrice,
                DateTime.UtcNow,
                string.Empty);
            var orderIdProperty = typeof(OrderRequest).GetProperty(nameof(OrderRequest.OrderId));
            orderIdProperty.SetValue(request, orderId);
            return Order.CreateOrder(request);
        }

        private sealed class FakeGatewayClient : IQmtGatewayClient
        {
            public bool IsConnected { get; set; }
            public QmtHelloPayload ServerInformation { get; set; }
            public Dictionary<string, QmtProtocolMessage> Responses { get; } =
                new Dictionary<string, QmtProtocolMessage>();
            public List<(string Operation, object Payload)> Requests { get; } =
                new List<(string Operation, object Payload)>();

            public event EventHandler<QmtGatewayMessageEventArgs> EventReceived;
            public event EventHandler<QmtGatewayDisconnectedEventArgs> Disconnected;

            public void Connect()
            {
                IsConnected = true;
            }

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                Connect();
                return Task.CompletedTask;
            }

            public void Disconnect()
            {
                IsConnected = false;
                Disconnected?.Invoke(this, new QmtGatewayDisconnectedEventArgs(null));
            }

            public Task<QmtProtocolMessage> SendRequestAsync(
                string operation,
                object payload = null,
                CancellationToken cancellationToken = default)
            {
                Requests.Add((operation, payload));
                if (!Responses.TryGetValue(operation, out var response))
                {
                    throw new InvalidOperationException($"No fake response for operation '{operation}'.");
                }
                return Task.FromResult(response);
            }

            public void RaiseEvent(string operation, object payload)
            {
                EventReceived?.Invoke(this, new QmtGatewayMessageEventArgs(new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Event,
                    Operation = operation,
                    Payload = JObject.FromObject(payload)
                }));
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeOrderProvider : IOrderProvider
        {
            private readonly List<Order> _orders;

            public int OrdersCount => _orders.Count;

            public FakeOrderProvider(params Order[] orders)
            {
                _orders = orders.ToList();
            }

            public Order GetOrderById(int orderId) => _orders.FirstOrDefault(order => order.Id == orderId);
            public List<Order> GetOrdersByBrokerageId(string brokerageId) =>
                _orders.Where(order => order.BrokerId.Contains(brokerageId)).ToList();
            public IEnumerable<OrderTicket> GetOrderTickets(Func<OrderTicket, bool> filter = null) =>
                Enumerable.Empty<OrderTicket>();
            public IEnumerable<OrderTicket> GetOpenOrderTickets(Func<OrderTicket, bool> filter = null) =>
                Enumerable.Empty<OrderTicket>();
            public OrderTicket GetOrderTicket(int orderId) => null;
            public IEnumerable<Order> GetOrders(Func<Order, bool> filter = null) =>
                filter == null ? _orders : _orders.Where(filter);
            public List<Order> GetOpenOrders(Func<Order, bool> filter = null) =>
                GetOrders(filter).Where(order => order.Status.IsOpen()).ToList();
            public ProjectedHoldings GetProjectedHoldings(Security security) => default;
        }
    }
}
