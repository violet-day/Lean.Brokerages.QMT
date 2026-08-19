using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    internal sealed class QmtOrderTestProvider : IOrderProvider
    {
        public int OrdersCount => 0;
        public Order? GetOrderById(int orderId) => null;
        public List<Order> GetOrdersByBrokerageId(string brokerageId) => new();
        public IEnumerable<OrderTicket> GetOrderTickets(Func<OrderTicket, bool>? filter = null) =>
            Enumerable.Empty<OrderTicket>();
        public IEnumerable<OrderTicket> GetOpenOrderTickets(Func<OrderTicket, bool>? filter = null) =>
            Enumerable.Empty<OrderTicket>();
        public OrderTicket? GetOrderTicket(int orderId) => null;
        public IEnumerable<Order> GetOrders(Func<Order, bool>? filter = null) => Enumerable.Empty<Order>();
        public List<Order> GetOpenOrders(Func<Order, bool>? filter = null) => new();
        public ProjectedHoldings? GetProjectedHoldings(Security security) => null;
    }

    internal sealed class QmtOrderTestGatewayClient : IQmtGatewayClient
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

        public QmtOrderTestGatewayClient(
            bool cancellationSubmitted = true,
            bool isSimulation = false)
        {
            _cancellationSubmitted = cancellationSubmitted;
            ServerInformation = new QmtHelloPayload
            {
                AccountId = "order-test",
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
                        NativeOrderId = string.Empty,
                        PassOrderResult = "test-result"
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

    internal sealed class QmtOrderTestTimeProvider : ITimeProvider
    {
        private readonly DateTime _utcTime;

        public QmtOrderTestTimeProvider(DateTime utcTime)
        {
            _utcTime = utcTime;
        }

        public DateTime GetUtcNow()
        {
            return _utcTime;
        }
    }
}
