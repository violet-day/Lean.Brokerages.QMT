using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt
{
    /// <summary>
    /// LEAN brokerage and live-data adapter for the QMT Python Gateway.
    /// </summary>
    [BrokerageFactory(typeof(QmtBrokerageFactory))]
    public sealed class QmtBrokerage : Brokerage, IDataQueueHandler
    {
        private readonly IQmtGatewayClient _gatewayClient;
        private readonly IOrderProvider _orderProvider;
        private readonly bool _localTradingEnabled;
        private readonly QmtSymbolMapper _symbolMapper;
        private readonly ConcurrentDictionary<Symbol, SubscriptionState> _subscriptions =
            new ConcurrentDictionary<Symbol, SubscriptionState>();
        private readonly ConcurrentDictionary<string, int> _leanOrderIdsByClientOrderId =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _leanOrderIdsByNativeOrderId =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _processedDealIds =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, decimal> _filledQuantityByLeanOrderId =
            new ConcurrentDictionary<int, decimal>();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<QmtDealEventPayload>> _pendingDealsByNativeOrderId =
            new ConcurrentDictionary<string, ConcurrentQueue<QmtDealEventPayload>>(StringComparer.Ordinal);
        private readonly object _pendingDealsLock = new object();
        private int _isDisposed;

        public override bool IsConnected => _gatewayClient.IsConnected;

        public QmtBrokerage(
            IQmtGatewayClient gatewayClient,
            IOrderProvider orderProvider,
            bool localTradingEnabled,
            QmtSymbolMapper? symbolMapper = null)
            : base("QMT")
        {
            _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
            _orderProvider = orderProvider ?? throw new ArgumentNullException(nameof(orderProvider));
            _localTradingEnabled = localTradingEnabled;
            _symbolMapper = symbolMapper ?? new QmtSymbolMapper();
            _gatewayClient.EventReceived += HandleGatewayEvent;
            _gatewayClient.Disconnected += HandleGatewayDisconnected;
        }

        public override void Connect()
        {
            ThrowIfDisposed();
            Log.Trace($"QmtBrokerage.Connect(): stage=connect status=start local_trading_enabled={_localTradingEnabled}");
            _gatewayClient.Connect();
            var serverInformation = _gatewayClient.ServerInformation ??
                throw new QmtGatewayProtocolException("QMT Gateway connected without hello server information.");
            Log.Trace(
                $"QmtBrokerage.Connect(): stage=connect status=ok account_id={serverInformation.AccountId} " +
                $"local_trading_enabled={_localTradingEnabled} gateway_trading_enabled={serverInformation.TradingEnabled}");
        }

        public override void Disconnect()
        {
            _gatewayClient.Disconnect();
            Log.Trace("QmtBrokerage.Disconnect(): status=ok");
        }

        public override List<CashAmount> GetCashBalance()
        {
            EnsureConnected();
            var response = SendRequest(QmtProtocol.Operations.QueryAccount);
            var accounts = response.ToPayload<QmtQueryAccountPayload>().Accounts;
            Log.Trace($"QmtBrokerage.GetCashBalance(): status=ok accounts={accounts.Count}");
            return accounts.Select(account => new CashAmount(account.AvailableCash, "CNY")).ToList();
        }

        public override List<Holding> GetAccountHoldings()
        {
            EnsureConnected();
            var response = SendRequest(QmtProtocol.Operations.QueryPositions);
            var positions = response.ToPayload<QmtQueryPositionsPayload>().Positions;
            var holdings = positions
                .Where(position => position.Volume != 0)
                .Select(position => new Holding
                {
                    Symbol = _symbolMapper.GetLeanSymbol(
                        position.StockCode,
                        SecurityType.Equity,
                        QmtSymbolMapper.MarketName),
                    Quantity = position.Volume,
                    AveragePrice = position.OpenPrice,
                    MarketPrice = position.LastPrice,
                    MarketValue = position.MarketValue,
                    CurrencySymbol = Currencies.GetCurrencySymbol("CNY"),
                    ConversionRate = 1m
                })
                .ToList();
            Log.Trace($"QmtBrokerage.GetAccountHoldings(): status=ok holdings={holdings.Count}");
            return holdings;
        }

        public override List<Order> GetOpenOrders()
        {
            EnsureConnected();
            var response = SendRequest(QmtProtocol.Operations.QueryOrders);
            var snapshots = response.ToPayload<QmtQueryOrdersPayload>().Orders;
            var orders = snapshots
                .Where(snapshot => QmtOrderStatusMapper.GetLeanOrderStatus(snapshot.Status).IsOpen())
                .Select(CreateLeanOrder)
                .Where(order => order != null)
                .Cast<Order>()
                .ToList();
            Log.Trace($"QmtBrokerage.GetOpenOrders(): status=ok open_orders={orders.Count}");
            return orders;
        }

        public override bool PlaceOrder(Order order)
        {
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            if (!CanTrade("place_order"))
            {
                return false;
            }

            if (order.SecurityType != SecurityType.Equity ||
                (order.Type != OrderType.Market && order.Type != OrderType.Limit) ||
                order.Quantity == 0 || order.Quantity != decimal.Truncate(order.Quantity))
            {
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "UnsupportedOrder",
                    "QMT MVP accepts only whole-share A-share Market and Limit orders."));
                return false;
            }

            try
            {
                var clientOrderId = order.Id.ToStringInvariant();
                var response = SendRequest(QmtProtocol.Operations.PlaceOrder, new QmtPlaceOrderRequest
                {
                    ClientOrderId = clientOrderId,
                    StockCode = _symbolMapper.GetBrokerageSymbol(order.Symbol),
                    OrderType = order.Type == OrderType.Market ? "market" : "limit",
                    Direction = order.Direction == OrderDirection.Buy ? "buy" : "sell",
                    Quantity = Math.Abs(order.Quantity),
                    LimitPrice = order is LimitOrder limitOrder ? limitOrder.LimitPrice : null
                });
                var result = response.ToPayload<QmtPlaceOrderPayload>();
                if (!result.Accepted)
                {
                    OnMessage(new BrokerageMessageEvent(
                        BrokerageMessageType.Warning,
                        "OrderRejected",
                        $"QMT Gateway did not accept LEAN order {order.Id}."));
                    return false;
                }

                _leanOrderIdsByClientOrderId[clientOrderId] = order.Id;
                if (!string.IsNullOrWhiteSpace(result.NativeOrderId))
                {
                    RegisterNativeOrderId(result.NativeOrderId, order.Id);
                }
                Log.Trace(
                    $"QmtBrokerage.PlaceOrder(): status=accepted lean_order_id={order.Id} " +
                    $"native_order_id={(string.IsNullOrWhiteSpace(result.NativeOrderId) ? "pending" : result.NativeOrderId)} " +
                    $"symbol={order.Symbol.Value} type={order.Type} direction={order.Direction} quantity={Math.Abs(order.Quantity)}");
                return true;
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"QmtBrokerage.PlaceOrder(): status=error lean_order_id={order.Id}");
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "PlaceOrderFailed", exception.Message));
                return false;
            }
        }

        public override bool UpdateOrder(Order order)
        {
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning,
                "UpdateNotSupported",
                "QMT MVP does not support modifying an order. Cancel it and submit a new order."));
            return false;
        }

        public override bool CancelOrder(Order order)
        {
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }

            if (!CanTrade("cancel_order"))
            {
                return false;
            }

            var nativeOrderId = order.BrokerId.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(nativeOrderId))
            {
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    "MissingBrokerageOrderId",
                    $"LEAN order {order.Id} does not have a QMT order ID."));
                return false;
            }

            try
            {
                SendRequest(
                    QmtProtocol.Operations.CancelOrder,
                    new QmtCancelOrderRequest { OrderId = nativeOrderId });
                var canceled = true;
                Log.Trace(
                    $"QmtBrokerage.CancelOrder(): status={(canceled ? "ok" : "rejected")} " +
                    $"lean_order_id={order.Id} native_order_id={nativeOrderId}");
                return canceled;
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"QmtBrokerage.CancelOrder(): status=error lean_order_id={order.Id}");
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "CancelOrderFailed", exception.Message));
                return false;
            }
        }

        public IEnumerator<BaseData>? Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (dataConfig == null)
            {
                throw new ArgumentNullException(nameof(dataConfig));
            }

            if (!CanSubscribe(dataConfig))
            {
                return null;
            }

            EnsureConnected();
            var subscriptionState = _subscriptions.GetOrAdd(
                dataConfig.Symbol,
                symbol =>
                {
                    var response = SendRequest(
                        QmtProtocol.Operations.Subscribe,
                        new QmtStockCodeRequest { StockCode = _symbolMapper.GetBrokerageSymbol(symbol) });
                    var result = response.ToPayload<QmtSubscribePayload>();
                    if (!result.Subscribed || string.IsNullOrWhiteSpace(result.SubscriptionId))
                    {
                        throw new QmtGatewayProtocolException($"QMT Gateway did not subscribe {symbol.Value}.");
                    }
                    Log.Trace(
                        $"QmtBrokerage.Subscribe(): status=ok symbol={symbol.Value} " +
                        $"subscription_id={result.SubscriptionId}");
                    return new SubscriptionState(result.SubscriptionId);
                });
            Interlocked.Increment(ref subscriptionState.ReferenceCount);
            return subscriptionState.CreateEnumerator(newDataAvailableHandler);
        }

        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            if (dataConfig == null || !_subscriptions.TryGetValue(dataConfig.Symbol, out var subscriptionState))
            {
                return;
            }

            if (Interlocked.Decrement(ref subscriptionState.ReferenceCount) > 0)
            {
                return;
            }

            if (_subscriptions.TryRemove(dataConfig.Symbol, out var removedState))
            {
                try
                {
                    SendRequest(
                        QmtProtocol.Operations.Unsubscribe,
                        new QmtUnsubscribeRequest { SubscriptionId = removedState.SubscriptionId });
                    Log.Trace(
                        $"QmtBrokerage.Unsubscribe(): status=ok symbol={dataConfig.Symbol.Value} " +
                        $"subscription_id={removedState.SubscriptionId}");
                }
                finally
                {
                    removedState.Dispose();
                }
            }
        }

        public void SetJob(LiveNodePacket job)
        {
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            {
                return;
            }

            _gatewayClient.EventReceived -= HandleGatewayEvent;
            _gatewayClient.Disconnected -= HandleGatewayDisconnected;
            foreach (var subscriptionState in _subscriptions.Values)
            {
                subscriptionState.Dispose();
            }
            _subscriptions.Clear();
            _gatewayClient.Dispose();
            base.Dispose();
        }

        private QmtProtocolMessage SendRequest(string operation, object? payload = null)
        {
            return _gatewayClient.SendRequestAsync(operation, payload).GetAwaiter().GetResult();
        }

        private Order? CreateLeanOrder(QmtOrderSnapshot snapshot)
        {
            try
            {
                var symbol = _symbolMapper.GetLeanSymbol(
                    snapshot.StockCode,
                    SecurityType.Equity,
                    QmtSymbolMapper.MarketName);
                var quantitySign = string.Equals(snapshot.Direction, "sell", StringComparison.OrdinalIgnoreCase) ? -1m : 1m;
                var quantity = snapshot.OriginalVolume * quantitySign;
                Order order = string.Equals(snapshot.OrderType, "market", StringComparison.OrdinalIgnoreCase)
                    ? new MarketOrder(symbol, quantity, DateTime.UtcNow)
                    : new LimitOrder(symbol, quantity, snapshot.LimitPrice, DateTime.UtcNow);
                order.BrokerId.Add(snapshot.OrderId);
                order.Status = QmtOrderStatusMapper.GetLeanOrderStatus(snapshot.Status);
                return order;
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"QmtBrokerage.CreateLeanOrder(): status=skip native_order_id={snapshot.OrderId}");
                return null;
            }
        }

        private void HandleGatewayEvent(object? sender, QmtGatewayMessageEventArgs eventArgs)
        {
            try
            {
                switch (eventArgs.Message.Operation)
                {
                    case QmtProtocol.Operations.Quote:
                        HandleQuote(eventArgs.Message.ToPayload<QmtQuoteEventPayload>());
                        break;
                    case QmtProtocol.Operations.Order:
                        HandleOrder(eventArgs.Message.ToPayload<QmtOrderEventPayload>());
                        break;
                    case QmtProtocol.Operations.Deal:
                        HandleDeal(eventArgs.Message.ToPayload<QmtDealEventPayload>());
                        break;
                    case QmtProtocol.Operations.Account:
                    case QmtProtocol.Operations.Position:
                        Log.Trace($"QmtBrokerage.HandleGatewayEvent(): operation={eventArgs.Message.Operation} status=received");
                        break;
                    default:
                        Log.Trace($"QmtBrokerage.HandleGatewayEvent(): operation={eventArgs.Message.Operation} status=ignored");
                        break;
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"QmtBrokerage.HandleGatewayEvent(): operation={eventArgs.Message.Operation} status=error");
            }
        }

        private void HandleQuote(QmtQuoteEventPayload quote)
        {
            var symbol = _symbolMapper.GetLeanSymbol(
                quote.StockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            if (!_subscriptions.TryGetValue(symbol, out var subscriptionState))
            {
                return;
            }

            var localTime = ParseQmtTime(quote.Time);
            var tick = new Tick(
                localTime,
                symbol,
                quote.BidVolume,
                quote.BidPrice,
                quote.AskVolume,
                quote.AskPrice)
            {
                Value = quote.LastPrice,
                Quantity = quote.Volume
            };
            subscriptionState.Publish(tick);
        }

        private void HandleOrder(QmtOrderEventPayload orderUpdate)
        {
            var leanOrderId = ResolveLeanOrderId(
                orderUpdate.OrderId,
                orderUpdate.ClientOrderId);
            if (!leanOrderId.HasValue)
            {
                Log.Trace(
                    $"QmtBrokerage.HandleOrder(): status=unmatched native_order_id={orderUpdate.OrderId} " +
                    $"client_order_id={orderUpdate.ClientOrderId}");
                return;
            }

            var leanOrder = _orderProvider.GetOrderById(leanOrderId.Value);
            if (!string.IsNullOrWhiteSpace(orderUpdate.OrderId))
            {
                RegisterNativeOrderId(orderUpdate.OrderId, leanOrderId.Value);
            }
            var symbol = leanOrder?.Symbol ?? _symbolMapper.GetLeanSymbol(
                orderUpdate.StockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            var direction = leanOrder?.Direction ?? ParseDirection(orderUpdate.Direction);
            var status = QmtOrderStatusMapper.GetLeanOrderStatus(orderUpdate.Status);
            if (status == OrderStatus.PartiallyFilled || status == OrderStatus.Filled || status == OrderStatus.None)
            {
                ProcessPendingDeals(orderUpdate.OrderId);
                Log.Trace(
                    $"QmtBrokerage.HandleOrder(): status=deferred_to_deal lean_order_id={leanOrderId.Value} " +
                    $"native_order_id={orderUpdate.OrderId} order_status={status}");
                return;
            }
            OnOrderEvent(new OrderEvent(
                leanOrderId.Value,
                symbol,
                DateTime.UtcNow,
                status,
                direction,
                0m,
                0m,
                OrderFee.Zero,
                orderUpdate.Remark));
            Log.Trace(
                $"QmtBrokerage.HandleOrder(): status=ok lean_order_id={leanOrderId.Value} " +
                $"native_order_id={orderUpdate.OrderId} order_status={status}");
            ProcessPendingDeals(orderUpdate.OrderId);
        }

        private void HandleDeal(QmtDealEventPayload deal, bool queueIfUnmatched = true)
        {
            var leanOrderId = ResolveLeanOrderId(deal.OrderId, null);
            if (!leanOrderId.HasValue && queueIfUnmatched && !string.IsNullOrWhiteSpace(deal.OrderId))
            {
                lock (_pendingDealsLock)
                {
                    leanOrderId = ResolveLeanOrderId(deal.OrderId, null);
                    if (!leanOrderId.HasValue)
                    {
                        _pendingDealsByNativeOrderId.GetOrAdd(
                            deal.OrderId,
                            _ => new ConcurrentQueue<QmtDealEventPayload>()).Enqueue(deal);
                    }
                }
            }

            if (!leanOrderId.HasValue)
            {
                Log.Trace($"QmtBrokerage.HandleDeal(): status=unmatched native_order_id={deal.OrderId} deal_id={deal.DealId}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(deal.DealId) && !_processedDealIds.TryAdd(deal.DealId, 0))
            {
                Log.Trace($"QmtBrokerage.HandleDeal(): status=duplicate deal_id={deal.DealId}");
                return;
            }

            var leanOrder = _orderProvider.GetOrderById(leanOrderId.Value);
            var symbol = leanOrder?.Symbol ?? _symbolMapper.GetLeanSymbol(
                deal.StockCode,
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            var direction = leanOrder?.Direction ?? ParseDirection(deal.Direction);
            var signedFillQuantity = direction == OrderDirection.Sell ? -Math.Abs(deal.Volume) : Math.Abs(deal.Volume);
            var cumulativeFilledQuantity = _filledQuantityByLeanOrderId.AddOrUpdate(
                leanOrderId.Value,
                Math.Abs(deal.Volume),
                (_, existingFilledQuantity) => existingFilledQuantity + Math.Abs(deal.Volume));
            var fillStatus = leanOrder != null && cumulativeFilledQuantity >= leanOrder.AbsoluteQuantity
                ? OrderStatus.Filled
                : OrderStatus.PartiallyFilled;
            OnOrderEvent(new OrderEvent(
                leanOrderId.Value,
                symbol,
                DateTime.UtcNow,
                fillStatus,
                direction,
                deal.Price,
                signedFillQuantity,
                new OrderFee(new CashAmount(deal.Commission, "CNY")),
                $"QMT deal {deal.DealId}"));
            Log.Trace(
                $"QmtBrokerage.HandleDeal(): status=ok lean_order_id={leanOrderId.Value} native_order_id={deal.OrderId} " +
                $"deal_id={deal.DealId} fill_quantity={signedFillQuantity} cumulative_fill_quantity={cumulativeFilledQuantity} " +
                $"fill_price={deal.Price} commission={deal.Commission}");
        }

        private void ProcessPendingDeals(string nativeOrderId)
        {
            if (string.IsNullOrWhiteSpace(nativeOrderId))
            {
                return;
            }

            ConcurrentQueue<QmtDealEventPayload>? pendingDeals;
            lock (_pendingDealsLock)
            {
                if (!_pendingDealsByNativeOrderId.TryRemove(nativeOrderId, out pendingDeals))
                {
                    return;
                }
            }

            while (pendingDeals.TryDequeue(out var pendingDeal))
            {
                HandleDeal(pendingDeal, false);
            }
        }

        private int? ResolveLeanOrderId(string nativeOrderId, string? clientOrderId)
        {
            if (!string.IsNullOrWhiteSpace(nativeOrderId) &&
                _leanOrderIdsByNativeOrderId.TryGetValue(nativeOrderId, out var leanOrderId))
            {
                return leanOrderId;
            }

            if (!string.IsNullOrWhiteSpace(nativeOrderId))
            {
                var brokerageOrders = _orderProvider.GetOrdersByBrokerageId(nativeOrderId);
                var brokerageOrder = brokerageOrders?.FirstOrDefault();
                if (brokerageOrder != null)
                {
                    _leanOrderIdsByNativeOrderId[nativeOrderId] = brokerageOrder.Id;
                    return brokerageOrder.Id;
                }
            }

            if (!string.IsNullOrWhiteSpace(clientOrderId) &&
                _leanOrderIdsByClientOrderId.TryGetValue(clientOrderId, out leanOrderId))
            {
                return leanOrderId;
            }

            return int.TryParse(clientOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out leanOrderId)
                ? leanOrderId
                : null;
        }

        private void RegisterNativeOrderId(string nativeOrderId, int leanOrderId)
        {
            if (!_leanOrderIdsByNativeOrderId.TryAdd(nativeOrderId, leanOrderId))
            {
                return;
            }

            OnOrderIdChangedEvent(new BrokerageOrderIdChangedEvent
            {
                OrderId = leanOrderId,
                BrokerId = new List<string> { nativeOrderId }
            });
            Log.Trace(
                $"QmtBrokerage.RegisterNativeOrderId(): status=ok lean_order_id={leanOrderId} " +
                $"native_order_id={nativeOrderId}");
        }

        private bool CanTrade(string operation)
        {
            if (!IsConnected)
            {
                OnMessage(BrokerageMessageEvent.Disconnected($"Cannot execute {operation}: QMT Gateway is disconnected."));
                return false;
            }

            var gatewayTradingEnabled = _gatewayClient.ServerInformation?.TradingEnabled == true;
            if (_localTradingEnabled && gatewayTradingEnabled)
            {
                return true;
            }

            Log.Trace(
                $"QmtBrokerage.CanTrade(): status=blocked operation={operation} local_trading_enabled={_localTradingEnabled} " +
                $"gateway_trading_enabled={gatewayTradingEnabled}");
            OnMessage(new BrokerageMessageEvent(
                BrokerageMessageType.Warning,
                "TradingDisabled",
                "QMT trading is disabled. Both qmt-trading-enabled and the Gateway trading flag must be true."));
            return false;
        }

        private static bool CanSubscribe(SubscriptionDataConfig dataConfig)
        {
            return dataConfig.Symbol.SecurityType == SecurityType.Equity &&
                string.Equals(dataConfig.Symbol.ID.Market, QmtSymbolMapper.MarketName, StringComparison.OrdinalIgnoreCase) &&
                (dataConfig.TickType == TickType.Trade || dataConfig.TickType == TickType.Quote);
        }

        private static OrderDirection ParseDirection(string direction)
        {
            return string.Equals(direction, "sell", StringComparison.OrdinalIgnoreCase)
                ? OrderDirection.Sell
                : OrderDirection.Buy;
        }

        private static DateTime ParseQmtTime(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedTime))
            {
                return DateTime.SpecifyKind(parsedTime, DateTimeKind.Unspecified);
            }
            return DateTime.UtcNow.ConvertFromUtc(TimeZones.Shanghai);
        }

        private void HandleGatewayDisconnected(object? sender, QmtGatewayDisconnectedEventArgs eventArgs)
        {
            var reason = eventArgs.Exception?.Message ?? "The QMT Gateway connection closed.";
            Log.Trace($"QmtBrokerage.HandleGatewayDisconnected(): status=disconnected reason={reason}");
            OnMessage(BrokerageMessageEvent.Disconnected(reason));
        }

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!IsConnected)
            {
                throw new QmtGatewayException("The QMT Gateway is not connected.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _isDisposed) == 1)
            {
                throw new ObjectDisposedException(nameof(QmtBrokerage));
            }
        }

        private sealed class SubscriptionState : IDisposable
        {
            private readonly ConcurrentDictionary<int, MarketDataEnumerator> _enumerators =
                new ConcurrentDictionary<int, MarketDataEnumerator>();
            private int _nextEnumeratorId;
            public int ReferenceCount;
            public string SubscriptionId { get; }

            public SubscriptionState(string subscriptionId)
            {
                SubscriptionId = subscriptionId;
            }

            public IEnumerator<BaseData> CreateEnumerator(EventHandler newDataAvailableHandler)
            {
                var enumeratorId = Interlocked.Increment(ref _nextEnumeratorId);
                var enumerator = new MarketDataEnumerator(
                    newDataAvailableHandler,
                    () => _enumerators.TryRemove(enumeratorId, out _));
                _enumerators[enumeratorId] = enumerator;
                return enumerator;
            }

            public void Publish(BaseData data)
            {
                foreach (var enumerator in _enumerators.Values)
                {
                    enumerator.Enqueue(data.Clone());
                }
            }

            public void Dispose()
            {
                foreach (var enumerator in _enumerators.Values)
                {
                    enumerator.Dispose();
                }
                _enumerators.Clear();
            }
        }

        private sealed class MarketDataEnumerator : IEnumerator<BaseData>
        {
            private readonly BlockingCollection<BaseData> _queue = new BlockingCollection<BaseData>();
            private readonly EventHandler? _newDataAvailableHandler;
            private readonly Action _removeEnumerator;
            private int _isDisposed;

            public BaseData Current { get; private set; } = null!;
            object IEnumerator.Current => Current;

            public MarketDataEnumerator(EventHandler? newDataAvailableHandler, Action removeEnumerator)
            {
                _newDataAvailableHandler = newDataAvailableHandler;
                _removeEnumerator = removeEnumerator;
            }

            public void Enqueue(BaseData data)
            {
                if (Volatile.Read(ref _isDisposed) == 1)
                {
                    return;
                }
                _queue.Add(data);
                _newDataAvailableHandler?.Invoke(this, EventArgs.Empty);
            }

            public bool MoveNext()
            {
                try
                {
                    Current = _queue.Take();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    Current = null!;
                    return false;
                }
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
                {
                    return;
                }
                _queue.CompleteAdding();
                _removeEnumerator();
                _queue.Dispose();
            }
        }
    }
}
