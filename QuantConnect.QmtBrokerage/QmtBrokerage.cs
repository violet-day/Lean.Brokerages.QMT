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
using HistoryRequest = QuantConnect.Data.HistoryRequest;

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
        private readonly QmtSymbolMapper _symbolMapper;
        private readonly ITimeProvider _timeProvider;
        private QmtAccountProperties? _accountProperties;
        private readonly ConcurrentDictionary<Symbol, SubscriptionState> _subscriptions =
            new ConcurrentDictionary<Symbol, SubscriptionState>();
        private readonly Dictionary<Symbol, CumulativeVolumeState> _cumulativeVolumeBySymbol =
            new Dictionary<Symbol, CumulativeVolumeState>();
        private readonly object _cumulativeVolumeLock = new object();
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

        public QmtAccountProperties AccountProperties => _accountProperties ??
            throw new QmtGatewayException("QMT account properties are unavailable before the Gateway handshake.");

        public QmtBrokerage(
            IQmtGatewayClient gatewayClient,
            IOrderProvider orderProvider,
            QmtSymbolMapper? symbolMapper = null,
            ITimeProvider? timeProvider = null)
            : base("QMT")
        {
            _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
            _orderProvider = orderProvider ?? throw new ArgumentNullException(nameof(orderProvider));
            _symbolMapper = symbolMapper ?? new QmtSymbolMapper();
            _timeProvider = timeProvider ?? RealTimeProvider.Instance;
            if (_gatewayClient.ServerInformation != null)
            {
                _accountProperties = new QmtAccountProperties(
                    _gatewayClient.ServerInformation.IsSimulation);
            }
            AccountBaseCurrency = "CNY";
            _gatewayClient.EventReceived += HandleGatewayEvent;
            _gatewayClient.Disconnected += HandleGatewayDisconnected;
        }

        public override void Connect()
        {
            ThrowIfDisposed();
            Log.Trace("QmtBrokerage.Connect(): stage=connect status=start");
            _gatewayClient.Connect();
            var serverInformation = _gatewayClient.ServerInformation ??
                throw new QmtGatewayProtocolException("QMT Gateway connected without hello server information.");
            _accountProperties = new QmtAccountProperties(serverInformation.IsSimulation);
            Log.Trace(
                $"QmtBrokerage.Connect(): stage=connect status=ok account_id={serverInformation.AccountId} " +
                $"server={serverInformation.ServerName} is_simulation={serverInformation.IsSimulation.ToString().ToLowerInvariant()} " +
                "market_order_style=order-property");
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

        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            EnsureConnected();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Symbol.SecurityType != SecurityType.Equity ||
                !string.Equals(request.Symbol.ID.Market, QmtSymbolMapper.MarketName, StringComparison.OrdinalIgnoreCase) ||
                request.TickType != TickType.Trade ||
                request.DataNormalizationMode != DataNormalizationMode.Raw ||
                (request.Resolution != Resolution.Minute && request.Resolution != Resolution.Daily))
            {
                Log.Trace(
                    $"QmtBrokerage.GetHistory(): status=unsupported symbol={request.Symbol.Value} " +
                    $"market={request.Symbol.ID.Market} resolution={request.Resolution} tick_type={request.TickType} " +
                    $"normalization={request.DataNormalizationMode}");
                return Enumerable.Empty<BaseData>();
            }

            var period = request.Resolution == Resolution.Daily ? "1d" : "1m";
            var response = SendRequest(QmtProtocol.Operations.QueryHistory, new QmtHistoryRequest
            {
                StockCode = _symbolMapper.GetBrokerageSymbol(request.Symbol),
                Period = period,
                StartTime = request.StartTimeLocal.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                EndTime = request.EndTimeLocal.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            });
            var bars = response.ToPayload<QmtQueryHistoryPayload>().Bars
                .Select(bar => (BaseData)new TradeBar(
                    ParseQmtTime(bar.Time),
                    request.Symbol,
                    bar.Open,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    bar.Volume,
                    request.Resolution == Resolution.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(1)))
                .OrderBy(bar => bar.Time)
                .ToList();
            Log.Trace(
                $"QmtBrokerage.GetHistory(): status=ok symbol={request.Symbol.Value} resolution={request.Resolution} " +
                $"bars={bars.Count} start={request.StartTimeLocal:O} end={request.EndTimeLocal:O} " +
                $"first={(bars.Count == 0 ? "" : bars[0].Time.ToString("O"))} " +
                $"last={(bars.Count == 0 ? "" : bars[^1].Time.ToString("O"))}");
            return bars;
        }

        public override bool PlaceOrder(Order order)
        {
            if (order == null)
            {
                throw new ArgumentNullException(nameof(order));
            }
            EnsureConnected();

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
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                QmtMarketOrderSubmission? marketOrderSubmission = null;
                if (order.Type == OrderType.Market)
                {
                    if (order.Properties is not QmtOrderProperties qmtOrderProperties ||
                        !qmtOrderProperties.MarketOrderStyle.HasValue)
                    {
                        Log.Trace(
                            $"QmtBrokerage.PlaceOrder(): status=unsupported lean_order_id={order.Id} " +
                            $"symbol={brokerageSymbol} reason=missing-market-order-style");
                        OnMessage(new BrokerageMessageEvent(
                            BrokerageMessageType.Warning,
                            "MissingMarketOrderStyle",
                            "QMT market orders require QmtOrderProperties.MarketOrderStyle."));
                        return false;
                    }

                    try
                    {
                        marketOrderSubmission = QmtMarketOrderStyleResolver.Resolve(
                            qmtOrderProperties.MarketOrderStyle.Value,
                            QmtSecurityCode.Parse(brokerageSymbol).Exchange);
                    }
                    catch (ArgumentException exception)
                    {
                        Log.Trace(
                            $"QmtBrokerage.PlaceOrder(): status=unsupported lean_order_id={order.Id} " +
                            $"symbol={brokerageSymbol} market_order_style=" +
                            $"{qmtOrderProperties.MarketOrderStyle.Value}");
                        OnMessage(new BrokerageMessageEvent(
                            BrokerageMessageType.Warning,
                            "UnsupportedMarketOrderStyle",
                            exception.Message));
                        return false;
                    }
                }
                else if (order.Properties is QmtOrderProperties { MarketOrderStyle: not null })
                {
                    Log.Trace(
                        $"QmtBrokerage.PlaceOrder(): status=unsupported lean_order_id={order.Id} " +
                        $"symbol={brokerageSymbol} reason=market-order-style-on-limit-order");
                    OnMessage(new BrokerageMessageEvent(
                        BrokerageMessageType.Warning,
                        "UnexpectedMarketOrderStyle",
                        "QmtOrderProperties.MarketOrderStyle can be used only with a market order."));
                    return false;
                }

                var utcTime = _timeProvider.GetUtcNow();
                if (!AccountProperties.IsOrderSubmissionAllowed(utcTime))
                {
                    var chinaTime = utcTime.ConvertFromUtc(TimeZones.Shanghai);
                    Log.Trace(
                        $"QmtBrokerage.PlaceOrder(): status=market-closed lean_order_id={order.Id} " +
                        $"trading_environment=simulation china_time={chinaTime:O}");
                    throw new QmtOrderSubmissionException(
                        "MarketClosed",
                        "The QMT simulation account accepts orders only on weekdays from 10:00 to 17:00 Asia/Shanghai.");
                }

                var response = SendRequest(QmtProtocol.Operations.PlaceOrder, new QmtPlaceOrderRequest
                {
                    ClientOrderId = clientOrderId,
                    StockCode = brokerageSymbol,
                    OrderType = order.Type == OrderType.Market ? "market" : "limit",
                    Direction = order.Direction == OrderDirection.Buy ? "buy" : "sell",
                    Quantity = Math.Abs(order.Quantity),
                    LimitPrice = order is LimitOrder limitOrder ? limitOrder.LimitPrice : null,
                    MarketOrderStyle = marketOrderSubmission?.Style ?? string.Empty,
                    QmtPriceType = marketOrderSubmission?.PriceType,
                    QmtPrice = marketOrderSubmission?.Price
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
                    $"qmt_passorder_result={result.PassOrderResult} " +
                    $"symbol={order.Symbol.Value} type={order.Type} direction={order.Direction} quantity={Math.Abs(order.Quantity)}" +
                    (marketOrderSubmission.HasValue
                        ? $" market_order_style={marketOrderSubmission.Value.Style} " +
                            $"qmt_price_type={marketOrderSubmission.Value.PriceType} " +
                            $"qmt_price={marketOrderSubmission.Value.Price.ToStringInvariant()}"
                        : string.Empty));
                return true;
            }
            catch (QmtOrderSubmissionException)
            {
                throw;
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
                var response = SendRequest(
                    QmtProtocol.Operations.CancelOrder,
                    new QmtCancelOrderRequest { OrderId = nativeOrderId });
                var result = response.ToPayload<QmtCancelOrderPayload>();
                var canceled = result.Canceled;
                Log.Trace(
                    $"QmtBrokerage.CancelOrder(): status={(canceled ? "ok" : "rejected")} " +
                    $"lean_order_id={order.Id} native_order_id={nativeOrderId}");
                if (!canceled)
                {
                    OnMessage(new BrokerageMessageEvent(
                        BrokerageMessageType.Warning,
                        "CancelRejected",
                        $"QMT did not submit the cancellation for LEAN order {order.Id}."));
                }
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
            return subscriptionState.CreateEnumerator(dataConfig.TickType, newDataAvailableHandler);
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
            var tradeQuantity = GetIncrementalTradeQuantity(symbol, localTime, quote.Volume);
            var tradeTick = new Tick(
                localTime,
                symbol,
                string.Empty,
                string.Empty,
                tradeQuantity,
                quote.LastPrice);
            var quoteTick = new Tick(
                localTime,
                symbol,
                quote.BidVolume,
                quote.BidPrice,
                quote.AskVolume,
                quote.AskPrice)
            {
                Value = quote.LastPrice
            };
            subscriptionState.Publish(tradeTick);
            subscriptionState.Publish(quoteTick);
            Log.Trace(
                $"QmtBrokerage.HandleQuote(): status=published symbol={symbol.Value} time={localTime:O} " +
                $"last={quote.LastPrice} cumulative_volume={quote.Volume} trade_quantity={tradeQuantity}");
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
            var orderMessage = GetOrderEventMessage(orderUpdate);
            if (status == OrderStatus.None && orderUpdate.SubmitStatus == 52)
            {
                status = OrderStatus.Invalid;
                Log.Trace(
                    $"QmtBrokerage.HandleOrder(): status=order_rejected_from_submit_status " +
                    $"lean_order_id={leanOrderId.Value} native_order_id={orderUpdate.OrderId} " +
                    $"qmt_order_status={orderUpdate.Status} qmt_submit_status={orderUpdate.SubmitStatus} " +
                    $"error_id={orderUpdate.ErrorId}");
            }
            else if (status == OrderStatus.None)
            {
                Log.Error(
                    $"QmtBrokerage.HandleOrder(): status=unsupported_qmt_order_status " +
                    $"lean_order_id={leanOrderId.Value} native_order_id={orderUpdate.OrderId} " +
                    $"client_order_id={orderUpdate.ClientOrderId} qmt_order_status={orderUpdate.Status} " +
                    $"qmt_submit_status={orderUpdate.SubmitStatus} error_id={orderUpdate.ErrorId}");
                ProcessPendingDeals(orderUpdate.OrderId);
                return;
            }

            if (orderUpdate.SubmitStatus == 53 || orderUpdate.SubmitStatus == 54)
            {
                var requestName = orderUpdate.SubmitStatus == 53 ? "cancellation" : "update";
                OnMessage(new BrokerageMessageEvent(
                    BrokerageMessageType.Warning,
                    orderUpdate.SubmitStatus == 53 ? "CancelRejected" : "UpdateRejected",
                    $"QMT rejected the {requestName} for LEAN order {leanOrderId.Value}: {orderMessage}"));
            }

            if (status == OrderStatus.PartiallyFilled || status == OrderStatus.Filled)
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
                orderMessage));
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

        private static string GetOrderEventMessage(QmtOrderEventPayload orderUpdate)
        {
            var rawMessages = new List<string>();
            if (!string.IsNullOrWhiteSpace(orderUpdate.ErrorMessage))
            {
                rawMessages.Add($"error_message={orderUpdate.ErrorMessage.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(orderUpdate.CallbackErrorMessage))
            {
                rawMessages.Add($"callback_error_message={orderUpdate.CallbackErrorMessage.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(orderUpdate.CancelInformation))
            {
                rawMessages.Add($"cancel_information={orderUpdate.CancelInformation.Trim()}");
            }
            if (rawMessages.Count == 0)
            {
                return orderUpdate.Remark;
            }

            var statusInformation = string.Join("; ", rawMessages);
            return orderUpdate.ErrorId == 0
                ? statusInformation
                : $"QMT error {orderUpdate.ErrorId}: {statusInformation}";
        }

        private static DateTime ParseQmtTime(string value)
        {
            var normalizedValue = (value ?? string.Empty).Trim();
            var exactFormats = new[]
            {
                "yyyyMMddHHmmssfff",
                "yyyyMMddHHmmss",
                "yyyyMMdd"
            };
            if (DateTime.TryParseExact(
                normalizedValue,
                exactFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exactTime))
            {
                return DateTime.SpecifyKind(exactTime, DateTimeKind.Unspecified);
            }
            if (long.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTime))
            {
                try
                {
                    var utcTime = normalizedValue.Length >= 13
                        ? DateTimeOffset.FromUnixTimeMilliseconds(unixTime).UtcDateTime
                        : DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
                    return utcTime.ConvertFromUtc(TimeZones.Shanghai);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
            if (DateTime.TryParse(normalizedValue, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedTime))
            {
                return DateTime.SpecifyKind(parsedTime, DateTimeKind.Unspecified);
            }
            return DateTime.UtcNow.ConvertFromUtc(TimeZones.Shanghai);
        }

        private decimal GetIncrementalTradeQuantity(Symbol symbol, DateTime localTime, decimal cumulativeVolume)
        {
            lock (_cumulativeVolumeLock)
            {
                if (!_cumulativeVolumeBySymbol.TryGetValue(symbol, out var state) ||
                    state.TradingDate != localTime.Date ||
                    cumulativeVolume < state.CumulativeVolume)
                {
                    _cumulativeVolumeBySymbol[symbol] = new CumulativeVolumeState(localTime.Date, cumulativeVolume);
                    return 0m;
                }

                var incrementalVolume = cumulativeVolume - state.CumulativeVolume;
                state.CumulativeVolume = cumulativeVolume;
                return incrementalVolume;
            }
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

            public IEnumerator<BaseData> CreateEnumerator(TickType tickType, EventHandler newDataAvailableHandler)
            {
                var enumeratorId = Interlocked.Increment(ref _nextEnumeratorId);
                var enumerator = new MarketDataEnumerator(
                    tickType,
                    newDataAvailableHandler,
                    () => _enumerators.TryRemove(enumeratorId, out _));
                _enumerators[enumeratorId] = enumerator;
                return enumerator;
            }

            public void Publish(BaseData data)
            {
                foreach (var enumerator in _enumerators.Values)
                {
                    if (data is Tick tick && tick.TickType == enumerator.TickType)
                    {
                        enumerator.Enqueue(data.Clone());
                    }
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

            public TickType TickType { get; }

            public BaseData Current { get; private set; } = null!;
            object IEnumerator.Current => Current;

            public MarketDataEnumerator(
                TickType tickType,
                EventHandler? newDataAvailableHandler,
                Action removeEnumerator)
            {
                TickType = tickType;
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
                if (_queue.TryTake(out var nextData))
                {
                    Current = nextData;
                    return true;
                }

                Current = null!;
                return !_queue.IsCompleted;
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

        private sealed class CumulativeVolumeState
        {
            public DateTime TradingDate { get; }
            public decimal CumulativeVolume { get; set; }

            public CumulativeVolumeState(DateTime tradingDate, decimal cumulativeVolume)
            {
                TradingDate = tradingDate;
                CumulativeVolume = cumulativeVolume;
            }
        }
    }
}
