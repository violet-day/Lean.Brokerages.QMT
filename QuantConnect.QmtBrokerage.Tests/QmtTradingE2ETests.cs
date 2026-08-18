using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    [Explicit("Places and cancels a real order through the current QMT Gateway account.")]
    public class QmtTradingE2ETests
    {
        private static readonly object EvidenceLogLock = new object();
        private const string TradingStockCode = "600000.SH";
        private const int TradingQuantity = 100;
        private const decimal AutomaticLimitPriceMultiplier = 0.95m;
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();
        private TradingOrderProvider _orderProvider = null!;
        private QmtGatewayClient _gatewayClient = null!;
        private QmtBrokerage _brokerage = null!;

        [SetUp]
        public void Connect()
        {
            const string stage = "connect";
            WriteEvidence(stage, "start");
            try
            {
                var gatewayHost = Environment.GetEnvironmentVariable("QMT_TRADING_E2E_GATEWAY_HOST") ?? "127.0.0.1";
                var gatewayPortText = Environment.GetEnvironmentVariable("QMT_TRADING_E2E_GATEWAY_PORT") ?? "17890";
                Assert.That(int.TryParse(gatewayPortText, out var gatewayPort), Is.True);

                var dataFolder = RequiredEnvironmentVariable("QMT_TRADING_E2E_DATA_FOLDER");
                Environment.CurrentDirectory = TestContext.CurrentContext.TestDirectory;
                Config.Reset();
                Config.Set("data-folder", dataFolder);
                Config.Set("data-directory", dataFolder);
                Globals.Reset();
                MarketHoursDatabase.Reset();
                SymbolPropertiesDatabase.Reset();

                _orderProvider = new TradingOrderProvider();
                _gatewayClient = new QmtGatewayClient(
                    gatewayHost,
                    gatewayPort,
                    null,
                    TimeSpan.FromSeconds(10));
                _brokerage = new QmtBrokerage(
                    _gatewayClient,
                    _orderProvider);
                _brokerage.Connect();

                Assert.That(_brokerage.IsConnected, Is.True);
                var accountId = _gatewayClient.ServerInformation?.AccountId;
                Assert.That(accountId, Is.Not.Null.And.Not.Empty);
                WriteEvidence(
                    stage,
                    "ok",
                    $"account_id={accountId} account_source=gateway_hello");
            }
            catch (Exception exception)
            {
                WriteFailure(stage, exception);
                throw;
            }
        }

        [TearDown]
        public void Disconnect()
        {
            _brokerage?.Dispose();
        }

        [Test]
        [Timeout(180000)]
        public void PlacesAndCancelsLimitOrder()
        {
            var symbol = _symbolMapper.GetLeanSymbol(
                TradingStockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            WriteEvidence(
                "run",
                "start",
                $"stock_code={TradingStockCode} quantity={TradingQuantity} limit_price=automatic");
            var limitPrice = GetLimitPriceFromLatestQuote(symbol);
            var algorithm = new QCAlgorithm();
            var submitOrderRequest = new SubmitOrderRequest(
                OrderType.Limit,
                SecurityType.Equity,
                symbol,
                TradingQuantity,
                0m,
                limitPrice,
                DateTime.UtcNow,
                string.Empty);
            algorithm.Transactions.SetOrderId(submitOrderRequest);
            var order = (LimitOrder)Order.CreateOrder(submitOrderRequest);
            _orderProvider.Add(order);

            var receivedStatuses = new ConcurrentQueue<OrderStatus>();
            using var orderStatusChanged = new ManualResetEventSlim(false);
            EventHandler<BrokerageOrderIdChangedEvent> orderIdChangedHandler = (_, orderIdChangedEvent) =>
                _orderProvider.ApplyBrokerageOrderId(orderIdChangedEvent);
            EventHandler<List<OrderEvent>> orderStatusHandler = (_, orderEvents) =>
            {
                foreach (var orderEvent in orderEvents.Where(orderEvent => orderEvent.OrderId == order.Id))
                {
                    _orderProvider.ApplyOrderStatus(orderEvent.OrderId, orderEvent.Status);
                    receivedStatuses.Enqueue(orderEvent.Status);
                    orderStatusChanged.Set();
                    WriteEvidence(
                        "order-callback",
                        "ok",
                        $"lean_order_id={order.Id} status={orderEvent.Status}");
                }
            };
            _brokerage.OrderIdChanged += orderIdChangedHandler;
            _brokerage.OrdersStatusChanged += orderStatusHandler;

            var currentStage = "place-order";
            try
            {
                WriteEvidence(
                    currentStage,
                    "start",
                    $"lean_order_id={order.Id} stock_code={TradingStockCode} quantity={TradingQuantity} " +
                    $"limit_price={limitPrice.ToString(CultureInfo.InvariantCulture)}");
                Assert.That(_brokerage.PlaceOrder(order), Is.True, "QMT rejected the test limit order request.");
                Assert.That(
                    WaitForStatus(
                        receivedStatuses,
                        orderStatusChanged,
                        TimeSpan.FromSeconds(30),
                        OrderStatus.Submitted,
                        OrderStatus.Invalid,
                        OrderStatus.PartiallyFilled,
                        OrderStatus.Filled),
                    Is.EqualTo(OrderStatus.Submitted),
                    "The test order did not reach Submitted before a terminal or fill status.");
                var nativeOrderId = WaitForNativeOrderId(order, TimeSpan.FromSeconds(5));
                Assert.That(nativeOrderId, Is.Not.Null.And.Not.Empty, "QMT did not return a native order ID.");
                WriteEvidence(
                    currentStage,
                    "ok",
                    $"lean_order_id={order.Id} native_order_id={nativeOrderId} callback=Submitted");

                currentStage = "open-order-query";
                WriteEvidence(currentStage, "start", $"native_order_id={nativeOrderId}");
                var submittedOrderSnapshot = WaitForOrderSnapshot(
                    order.Id,
                    nativeOrderId!,
                    TimeSpan.FromSeconds(15),
                    orderSnapshot => QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status).IsOpen());
                Assert.That(submittedOrderSnapshot, Is.Not.Null, "The submitted order was not returned by query_orders.");
                WriteEvidence(
                    currentStage,
                    "ok",
                    $"native_order_id={nativeOrderId} status={submittedOrderSnapshot!.Status}");

                currentStage = "cancel-order";
                WriteEvidence(currentStage, "start", $"native_order_id={nativeOrderId}");
                Assert.That(_brokerage.CancelOrder(order), Is.True, "QMT rejected the test cancellation request.");
                Assert.That(
                    WaitForStatus(
                        receivedStatuses,
                        orderStatusChanged,
                        TimeSpan.FromSeconds(30),
                        OrderStatus.Canceled,
                        OrderStatus.Invalid,
                        OrderStatus.PartiallyFilled,
                        OrderStatus.Filled),
                    Is.EqualTo(OrderStatus.Canceled),
                    "The test order did not reach Canceled before a terminal or fill status.");
                WriteEvidence(currentStage, "ok", $"native_order_id={nativeOrderId} callback=Canceled");

                currentStage = "final-order-query";
                WriteEvidence(currentStage, "start", $"native_order_id={nativeOrderId}");
                var canceledOrderSnapshot = WaitForOrderSnapshot(
                    order.Id,
                    nativeOrderId!,
                    TimeSpan.FromSeconds(15),
                    orderSnapshot =>
                        QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status) == OrderStatus.Canceled);
                Assert.That(canceledOrderSnapshot, Is.Not.Null, "query_orders did not report the order as canceled.");
                Assert.That(
                    _brokerage.GetOpenOrders().Any(openOrder => openOrder.BrokerId.Contains(nativeOrderId!)),
                    Is.False,
                    "The canceled order is still returned as open.");
                WriteEvidence(
                    currentStage,
                    "ok",
                    $"native_order_id={nativeOrderId} status=Canceled open_order=false");
                WriteEvidence("complete", "ok", "place=ok submitted_callback=ok cancel=ok canceled_callback=ok final_query=ok");
            }
            catch (Exception exception)
            {
                WriteFailure(currentStage, exception);
                throw;
            }
            finally
            {
                TryCancelRemainingTestOrder(order);
                _brokerage.OrderIdChanged -= orderIdChangedHandler;
                _brokerage.OrdersStatusChanged -= orderStatusHandler;
            }
        }

        private decimal GetLimitPriceFromLatestQuote(Symbol symbol)
        {
            const string stage = "latest-quote";
            WriteEvidence(stage, "start", $"stock_code={TradingStockCode}");
            var subscriptionConfiguration = new SubscriptionDataConfig(
                typeof(Tick),
                symbol,
                Resolution.Tick,
                TimeZones.Shanghai,
                TimeZones.Shanghai,
                false,
                false,
                false,
                false,
                TickType.Trade);
            using var dataAvailable = new ManualResetEventSlim(false);
            try
            {
                using var enumerator = _brokerage.Subscribe(
                    subscriptionConfiguration,
                    (_, _) => dataAvailable.Set());
                Assert.That(enumerator, Is.Not.Null, "QMT did not create a quote subscription.");
                Assert.That(
                    dataAvailable.Wait(TimeSpan.FromSeconds(30)),
                    Is.True,
                    "QMT did not publish a latest quote within 30 seconds.");
                Assert.That(enumerator!.MoveNext(), Is.True, "The QMT quote subscription returned no data.");
                Assert.That(enumerator.Current, Is.TypeOf<Tick>());
                var latestPrice = enumerator.Current.Value;
                Assert.That(latestPrice, Is.GreaterThan(0m));
                var limitPrice = Math.Floor(latestPrice * AutomaticLimitPriceMultiplier * 100m) / 100m;
                Assert.That(limitPrice, Is.GreaterThan(0m));
                WriteEvidence(
                    stage,
                    "ok",
                    $"latest_price={latestPrice.ToString(CultureInfo.InvariantCulture)} " +
                    $"limit_price={limitPrice.ToString(CultureInfo.InvariantCulture)} " +
                    $"multiplier={AutomaticLimitPriceMultiplier}");
                return limitPrice;
            }
            catch (Exception exception)
            {
                WriteFailure(stage, exception);
                throw;
            }
            finally
            {
                _brokerage.Unsubscribe(subscriptionConfiguration);
            }
        }

        private string? WaitForNativeOrderId(Order order, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                var nativeOrderId = order.BrokerId.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(nativeOrderId))
                {
                    return nativeOrderId;
                }
                Thread.Sleep(100);
            }
            return null;
        }

        private QmtOrderSnapshot? WaitForOrderSnapshot(
            int leanOrderId,
            string nativeOrderId,
            TimeSpan timeout,
            Func<QmtOrderSnapshot, bool> predicate)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                var orderSnapshot = QueryOrders().FirstOrDefault(snapshot =>
                    string.Equals(snapshot.OrderId, nativeOrderId, StringComparison.Ordinal) ||
                    string.Equals(snapshot.ClientOrderId, leanOrderId.ToStringInvariant(), StringComparison.Ordinal));
                if (orderSnapshot != null && predicate(orderSnapshot))
                {
                    return orderSnapshot;
                }
                Thread.Sleep(250);
            }
            return null;
        }

        private List<QmtOrderSnapshot> QueryOrders()
        {
            return _gatewayClient
                .SendRequestAsync(QmtProtocol.Operations.QueryOrders)
                .GetAwaiter()
                .GetResult()
                .ToPayload<QmtQueryOrdersPayload>()
                .Orders;
        }

        private void TryCancelRemainingTestOrder(Order order)
        {
            try
            {
                var orderSnapshot = QueryOrders().FirstOrDefault(snapshot =>
                    string.Equals(snapshot.ClientOrderId, order.Id.ToStringInvariant(), StringComparison.Ordinal));
                if (orderSnapshot == null)
                {
                    WriteEvidence("cleanup", "ok", $"lean_order_id={order.Id} action=none order_found=false");
                    return;
                }

                var orderStatus = QmtOrderStatusMapper.GetLeanOrderStatus(orderSnapshot.Status);
                if (!orderStatus.IsOpen())
                {
                    WriteEvidence(
                        "cleanup",
                        "ok",
                        $"native_order_id={orderSnapshot.OrderId} action=none status={orderStatus}");
                    return;
                }

                _gatewayClient
                    .SendRequestAsync(
                        QmtProtocol.Operations.CancelOrder,
                        new QmtCancelOrderRequest { OrderId = orderSnapshot.OrderId })
                    .GetAwaiter()
                    .GetResult();
                WriteEvidence(
                    "cleanup",
                    "ok",
                    $"native_order_id={orderSnapshot.OrderId} action=cancel-submitted previous_status={orderStatus}");
            }
            catch (Exception exception)
            {
                WriteFailure("cleanup", exception);
            }
        }

        private static OrderStatus WaitForStatus(
            ConcurrentQueue<OrderStatus> receivedStatuses,
            ManualResetEventSlim orderStatusChanged,
            TimeSpan timeout,
            params OrderStatus[] expectedStatuses)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                while (receivedStatuses.TryDequeue(out var receivedStatus))
                {
                    if (expectedStatuses.Contains(receivedStatus))
                    {
                        return receivedStatus;
                    }
                }
                orderStatusChanged.Wait(TimeSpan.FromMilliseconds(250));
                orderStatusChanged.Reset();
            }
            return OrderStatus.None;
        }

        private static string RequiredEnvironmentVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            Assert.That(value, Is.Not.Null.And.Not.Empty, $"{name} is required.");
            return value!;
        }

        private static void WriteFailure(string stage, Exception exception)
        {
            var reason = exception.Message.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
            WriteEvidence(stage, "failed", $"error_type={exception.GetType().Name} reason=\"{reason}\"");
        }

        private static void WriteEvidence(string stage, string status, string details = "")
        {
            var message = $"[qmt-trading-e2e] stage={stage} status={status}";
            if (!string.IsNullOrWhiteSpace(details))
            {
                message += " " + details;
            }
            var line = $"{DateTimeOffset.Now:O} {message}";
            var evidenceLogPath = Environment.GetEnvironmentVariable("QMT_TRADING_E2E_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(evidenceLogPath))
            {
                lock (EvidenceLogLock)
                {
                    File.AppendAllText(evidenceLogPath, line + Environment.NewLine);
                }
            }
            TestContext.Progress.WriteLine(line);
        }

        private sealed class TradingOrderProvider : IOrderProvider
        {
            private readonly object _ordersLock = new object();
            private readonly List<Order> _orders = new List<Order>();
            public int OrdersCount
            {
                get
                {
                    lock (_ordersLock)
                    {
                        return _orders.Count;
                    }
                }
            }

            public void Add(Order order)
            {
                lock (_ordersLock)
                {
                    _orders.Add(order);
                }
            }

            public void ApplyBrokerageOrderId(BrokerageOrderIdChangedEvent orderIdChangedEvent)
            {
                lock (_ordersLock)
                {
                    var order = _orders.FirstOrDefault(existingOrder => existingOrder.Id == orderIdChangedEvent.OrderId);
                    if (order != null)
                    {
                        order.BrokerId.Clear();
                        order.BrokerId.AddRange(orderIdChangedEvent.BrokerId);
                    }
                }
            }

            public void ApplyOrderStatus(int orderId, OrderStatus orderStatus)
            {
                lock (_ordersLock)
                {
                    var order = _orders.FirstOrDefault(existingOrder => existingOrder.Id == orderId);
                    if (order != null)
                    {
                        order.Status = orderStatus;
                    }
                }
            }

            public Order GetOrderById(int orderId)
            {
                lock (_ordersLock)
                {
                    return _orders.FirstOrDefault(order => order.Id == orderId)!;
                }
            }

            public List<Order> GetOrdersByBrokerageId(string brokerageId)
            {
                lock (_ordersLock)
                {
                    return _orders
                        .Where(order => order.BrokerId.Contains(brokerageId))
                        .Select(order => order.Clone())
                        .ToList();
                }
            }

            public IEnumerable<OrderTicket> GetOrderTickets(Func<OrderTicket, bool>? filter = null)
            {
                throw new NotSupportedException();
            }

            public IEnumerable<OrderTicket> GetOpenOrderTickets(Func<OrderTicket, bool>? filter = null)
            {
                throw new NotSupportedException();
            }

            public OrderTicket GetOrderTicket(int orderId)
            {
                throw new NotSupportedException();
            }

            public IEnumerable<Order> GetOrders(Func<Order, bool>? filter = null)
            {
                lock (_ordersLock)
                {
                    return _orders
                        .Where(order => filter == null || filter(order))
                        .Select(order => order.Clone())
                        .ToList();
                }
            }

            public List<Order> GetOpenOrders(Func<Order, bool>? filter = null)
            {
                lock (_ordersLock)
                {
                    return _orders
                        .Where(order => order.Status.IsOpen() && (filter == null || filter(order)))
                        .Select(order => order.Clone())
                        .ToList();
                }
            }

            public ProjectedHoldings GetProjectedHoldings(Security security)
            {
                lock (_ordersLock)
                {
                    return new ProjectedHoldings(
                        security.Holdings.Quantity,
                        _orders
                            .Where(order => order.Symbol == security.Symbol && order.Status.IsOpen())
                            .Sum(order => order.Quantity));
                }
            }
        }
    }
}
