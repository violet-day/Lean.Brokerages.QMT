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

namespace QuantConnect.Brokerages.Qmt.Tests.E2E.Infrastructure
{
    internal sealed class QmtTradingTestContext : IDisposable
    {
        private static readonly object EvidenceLogLock = new object();
        private const decimal NonMarketableBuyPriceMultiplier = 0.95m;
        private readonly QCAlgorithm _algorithm;
        private readonly TradingOrderProvider _orderProvider;
        private readonly List<Order> _createdOrders = new List<Order>();
        private readonly ConcurrentDictionary<int, ConcurrentQueue<OrderStatus>> _statusesByOrderId = new();
        private readonly ConcurrentDictionary<int, ManualResetEventSlim> _statusSignalsByOrderId = new();
        private readonly QmtSymbolMapper _symbolMapper = new QmtSymbolMapper();
        private bool _disposed;

        public const string TradingStockCode = "600000.SH";
        public const int TradingQuantity = 100;

        public QmtGatewayClient GatewayClient { get; }
        public QmtBrokerage Brokerage { get; }
        public Symbol TradingSymbol { get; }

        private QmtTradingTestContext(
            QCAlgorithm algorithm,
            TradingOrderProvider orderProvider,
            QmtGatewayClient gatewayClient,
            QmtBrokerage brokerage)
        {
            _algorithm = algorithm;
            _orderProvider = orderProvider;
            GatewayClient = gatewayClient;
            Brokerage = brokerage;
            TradingSymbol = _symbolMapper.GetLeanSymbol(
                TradingStockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            Brokerage.OrderIdChanged += HandleOrderIdChanged;
            Brokerage.OrdersStatusChanged += HandleOrderStatusChanged;
        }

        public static QmtTradingTestContext Connect(
            QmtMarketOrderStyle marketOrderStyle = QmtMarketOrderStyle.LatestPrice)
        {
            const string stage = "connect";
            QmtTradingTestContext? context = null;
            WriteCurrentTask();
            WriteEvidence(stage, "start");
            try
            {
                var expectedAccountId = RequiredEnvironmentVariable("QMT_TRADING_E2E_ACCOUNT_ID");
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

                var algorithm = new QCAlgorithm();
                var orderProvider = new TradingOrderProvider();
                var gatewayClient = new QmtGatewayClient(
                    gatewayHost,
                    gatewayPort,
                    expectedAccountId,
                    TimeSpan.FromSeconds(10));
                var brokerage = new QmtBrokerage(
                    gatewayClient,
                    orderProvider,
                    marketOrderStyle: marketOrderStyle);
                context = new QmtTradingTestContext(
                    algorithm,
                    orderProvider,
                    gatewayClient,
                    brokerage);
                brokerage.Connect();

                Assert.That(brokerage.IsConnected, Is.True);
                Assert.That(gatewayClient.ServerInformation?.AccountId, Is.EqualTo(expectedAccountId));
                WriteEvidence(
                    stage,
                    "ok",
                    $"account_id={expectedAccountId} account_match=true " +
                    $"market_order_style={QmtMarketOrderStyleResolver.GetConfigurationValue(marketOrderStyle)}");
                return context;
            }
            catch (Exception exception)
            {
                context?.Dispose();
                WriteFailure(stage, exception);
                throw;
            }
        }

        public static bool IsSimulationSessionOpen()
        {
            var chinaTime = DateTime.UtcNow.ConvertFromUtc(TimeZones.Shanghai);
            if (chinaTime.DayOfWeek == DayOfWeek.Saturday ||
                chinaTime.DayOfWeek == DayOfWeek.Sunday)
            {
                return false;
            }
            return chinaTime.TimeOfDay >= TimeSpan.FromHours(10) &&
                chinaTime.TimeOfDay < TimeSpan.FromHours(17);
        }

        public void Run(Action testCase)
        {
            WriteEvidence("case", "start");
            try
            {
                testCase();
                WriteEvidence("case-complete", "ok");
            }
            catch (Exception exception)
            {
                WriteFailure("case", exception);
                throw;
            }
        }

        public Order CreateMarketOrder(decimal quantity)
        {
            return CreateOrder(OrderType.Market, quantity, 0m);
        }

        public LimitOrder CreateLimitOrder(decimal quantity, decimal limitPrice)
        {
            return (LimitOrder)CreateOrder(OrderType.Limit, quantity, limitPrice);
        }

        public decimal GetNonMarketableBuyPrice()
        {
            const string stage = "latest-quote";
            WriteEvidence(stage, "start", $"stock_code={TradingStockCode}");
            var subscriptionConfiguration = new SubscriptionDataConfig(
                typeof(Tick),
                TradingSymbol,
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
                using var enumerator = Brokerage.Subscribe(
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
                var limitPrice = RoundDownToPriceStep(latestPrice * NonMarketableBuyPriceMultiplier);
                Assert.That(limitPrice, Is.GreaterThan(0m));
                WriteEvidence(
                    stage,
                    "ok",
                    $"latest_price={latestPrice.ToString(CultureInfo.InvariantCulture)} " +
                    $"limit_price={limitPrice.ToString(CultureInfo.InvariantCulture)}");
                return limitPrice;
            }
            finally
            {
                Brokerage.Unsubscribe(subscriptionConfiguration);
            }
        }

        public decimal GetNonMarketableBuyPriceFromDailyHistory()
        {
            var endTimeUtc = DateTime.UtcNow;
            var historyRequest = new HistoryRequest(
                endTimeUtc.AddDays(-30),
                endTimeUtc,
                typeof(TradeBar),
                TradingSymbol,
                Resolution.Daily,
                SecurityExchangeHours.AlwaysOpen(TimeZones.Shanghai),
                TimeZones.Shanghai,
                null,
                false,
                false,
                DataNormalizationMode.Raw,
                TickType.Trade);
            var latestClose = Brokerage
                .GetHistory(historyRequest)
                .OfType<TradeBar>()
                .OrderBy(bar => bar.EndTime)
                .LastOrDefault()?.Close ?? 0m;
            Assert.That(latestClose, Is.GreaterThan(0m), "QMT daily history did not provide a reference close.");
            return RoundDownToPriceStep(latestClose * NonMarketableBuyPriceMultiplier);
        }

        public OrderStatus WaitForStatus(
            Order order,
            TimeSpan timeout,
            params OrderStatus[] expectedStatuses)
        {
            var receivedStatuses = _statusesByOrderId[order.Id];
            var statusChanged = _statusSignalsByOrderId[order.Id];
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
                statusChanged.Wait(TimeSpan.FromMilliseconds(250));
                statusChanged.Reset();
            }
            return OrderStatus.None;
        }

        public string? WaitForNativeOrderId(Order order, TimeSpan timeout)
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

        public QmtOrderSnapshot? WaitForOrderSnapshot(
            Order order,
            TimeSpan timeout,
            Func<QmtOrderSnapshot, bool> predicate)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                var orderSnapshot = FindOrderSnapshot(order);
                if (orderSnapshot != null && predicate(orderSnapshot))
                {
                    return orderSnapshot;
                }
                Thread.Sleep(250);
            }
            return null;
        }

        public QmtOrderSnapshot? FindOrderSnapshot(Order order)
        {
            var nativeOrderId = order.BrokerId.FirstOrDefault();
            return QueryOrders().FirstOrDefault(snapshot =>
                (!string.IsNullOrWhiteSpace(nativeOrderId) &&
                    string.Equals(snapshot.OrderId, nativeOrderId, StringComparison.Ordinal)) ||
                string.Equals(
                    snapshot.ClientOrderId,
                    order.Id.ToStringInvariant(),
                    StringComparison.Ordinal));
        }

        public List<QmtOrderSnapshot> QueryOrders()
        {
            return GatewayClient
                .SendRequestAsync(QmtProtocol.Operations.QueryOrders)
                .GetAwaiter()
                .GetResult()
                .ToPayload<QmtQueryOrdersPayload>()
                .Orders;
        }

        public void WriteStage(string stage, string status, string details = "")
        {
            WriteEvidence(stage, status, details);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var order in _createdOrders)
            {
                TryCancelRemainingTestOrder(order);
            }
            Brokerage.OrderIdChanged -= HandleOrderIdChanged;
            Brokerage.OrdersStatusChanged -= HandleOrderStatusChanged;
            Brokerage.Dispose();
            foreach (var statusSignal in _statusSignalsByOrderId.Values)
            {
                statusSignal.Dispose();
            }
        }

        private Order CreateOrder(OrderType orderType, decimal quantity, decimal limitPrice)
        {
            var submitOrderRequest = new SubmitOrderRequest(
                orderType,
                SecurityType.Equity,
                TradingSymbol,
                quantity,
                0m,
                limitPrice,
                DateTime.UtcNow,
                string.Empty);
            _algorithm.Transactions.SetOrderId(submitOrderRequest);
            var order = Order.CreateOrder(submitOrderRequest);
            _orderProvider.Add(order);
            _createdOrders.Add(order);
            _statusesByOrderId[order.Id] = new ConcurrentQueue<OrderStatus>();
            _statusSignalsByOrderId[order.Id] = new ManualResetEventSlim(false);
            return order;
        }

        private void HandleOrderIdChanged(object? sender, BrokerageOrderIdChangedEvent eventArguments)
        {
            _orderProvider.ApplyBrokerageOrderId(eventArguments);
        }

        private void HandleOrderStatusChanged(object? sender, List<OrderEvent> orderEvents)
        {
            foreach (var orderEvent in orderEvents)
            {
                _orderProvider.ApplyOrderStatus(orderEvent.OrderId, orderEvent.Status);
                if (!_statusesByOrderId.TryGetValue(orderEvent.OrderId, out var receivedStatuses) ||
                    !_statusSignalsByOrderId.TryGetValue(orderEvent.OrderId, out var statusChanged))
                {
                    continue;
                }
                receivedStatuses.Enqueue(orderEvent.Status);
                statusChanged.Set();
                WriteEvidence(
                    "order-callback",
                    "ok",
                    $"lean_order_id={orderEvent.OrderId} status={orderEvent.Status}");
            }
        }

        private void TryCancelRemainingTestOrder(Order order)
        {
            try
            {
                var orderSnapshot = FindOrderSnapshot(order);
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
                GatewayClient
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

        private static decimal RoundDownToPriceStep(decimal price)
        {
            return Math.Floor(price * 100m) / 100m;
        }

        private static string RequiredEnvironmentVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            Assert.That(value, Is.Not.Null.And.Not.Empty, $"{name} is required.");
            return value!;
        }

        private static void WriteCurrentTask()
        {
            var taskPath = Environment.GetEnvironmentVariable("QMT_TRADING_E2E_TASK_PATH") ??
                "test-trading > trading-e2e";
            var className = TestContext.CurrentContext.Test.ClassName?.Split('.').Last() ?? "unknown-class";
            var testName = TestContext.CurrentContext.Test.MethodName ?? TestContext.CurrentContext.Test.Name;
            WriteLog($"[qmt-task] {taskPath} > {className} > {testName}");
        }

        private static void WriteFailure(string stage, Exception exception)
        {
            var reason = exception.Message.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
            WriteEvidence(stage, "failed", $"error_type={exception.GetType().Name} reason=\"{reason}\"");
        }

        private static void WriteEvidence(string stage, string status, string details = "")
        {
            var testName = TestContext.CurrentContext.Test.MethodName ?? TestContext.CurrentContext.Test.Name;
            var message = $"[qmt-trading-e2e] stage={stage} status={status} test={testName}";
            if (!string.IsNullOrWhiteSpace(details))
            {
                message += " " + details;
            }
            WriteLog(message);
        }

        private static void WriteLog(string message)
        {
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

            public void ApplyBrokerageOrderId(BrokerageOrderIdChangedEvent eventArguments)
            {
                lock (_ordersLock)
                {
                    var order = _orders.FirstOrDefault(existingOrder => existingOrder.Id == eventArguments.OrderId);
                    if (order == null)
                    {
                        return;
                    }
                    order.BrokerId.Clear();
                    order.BrokerId.AddRange(eventArguments.BrokerId);
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
