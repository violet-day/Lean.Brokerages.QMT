using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtGatewayClientTests
    {
        [Test]
        [Timeout(5000)]
        public async Task ConnectsAndValidatesHelloHandshake()
        {
            using var gatewayServer = new QmtFakeGatewayServer(tradingEnabled: false);
            using var gatewayClient = CreateClient(gatewayServer);

            await gatewayClient.ConnectAsync();

            Assert.That(gatewayClient.IsConnected, Is.True);
            Assert.That(gatewayClient.ServerInformation?.ServerName, Is.EqualTo("fake-qmt-gateway"));
            Assert.That(gatewayClient.ServerInformation?.AccountId, Is.EqualTo("86033767"));
            Assert.That(gatewayClient.ServerInformation?.TradingEnabled, Is.False);
        }

        [Test]
        [Timeout(5000)]
        public void RejectsHelloWithoutTradingSafetyState()
        {
            using var gatewayServer = new QmtFakeGatewayServer();
            gatewayServer.RequestHandler = request => Task.FromResult<QmtProtocolMessage?>(
                QmtFakeGatewayServer.CreateSuccessfulResponse(
                    request,
                    new JObject
                    {
                        ["server_name"] = "fake-qmt-gateway",
                        ["account_id"] = "86033767"
                    }));
            using var gatewayClient = CreateClient(gatewayServer);

            var exception = Assert.ThrowsAsync<QmtGatewayProtocolException>(() => gatewayClient.ConnectAsync());

            Assert.That(exception?.Message, Does.Contain("trading_enabled"));
            Assert.That(gatewayClient.IsConnected, Is.False);
        }

        [Test]
        [Timeout(5000)]
        public void RejectsGatewayForDifferentAccount()
        {
            using var gatewayServer = new QmtFakeGatewayServer(accountId: "different-account");
            using var gatewayClient = CreateClient(gatewayServer);

            var exception = Assert.ThrowsAsync<QmtGatewayProtocolException>(() => gatewayClient.ConnectAsync());

            Assert.That(exception?.Message, Does.Contain("account mismatch"));
            Assert.That(gatewayClient.IsConnected, Is.False);
        }

        [Test]
        [Timeout(5000)]
        public async Task CorrelatesConcurrentResponsesByRequestId()
        {
            using var gatewayServer = new QmtFakeGatewayServer();
            gatewayServer.RequestHandler = async request =>
            {
                if (request.Operation == QmtProtocol.Operations.Hello)
                {
                    return QmtFakeGatewayServer.CreateSuccessfulResponse(
                        request,
                        JObject.FromObject(new QmtHelloPayload
                        {
                            ServerName = "fake-qmt-gateway",
                            AccountId = "86033767",
                            TradingEnabled = false
                        }));
                }

                if (request.Operation == QmtProtocol.Operations.QueryAccount)
                {
                    await Task.Delay(100);
                    return QmtFakeGatewayServer.CreateSuccessfulResponse(
                        request,
                        JObject.FromObject(new QmtQueryAccountPayload
                        {
                            Accounts = new List<QmtAccountSnapshot>
                            {
                                new QmtAccountSnapshot { AvailableCash = 12345.67m }
                            }
                        }));
                }

                return QmtFakeGatewayServer.CreateSuccessfulResponse(
                    request,
                    JObject.FromObject(new QmtQueryPositionsPayload
                    {
                        Positions = new List<QmtPositionSnapshot>
                        {
                            new QmtPositionSnapshot { StockCode = "600000.SH", Volume = 100 }
                        }
                    }));
            };
            using var gatewayClient = CreateClient(gatewayServer);
            await gatewayClient.ConnectAsync();

            var accountTask = gatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryAccount);
            var positionsTask = gatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryPositions);
            await Task.WhenAll(accountTask, positionsTask);

            Assert.That(
                accountTask.Result.ToPayload<QmtQueryAccountPayload>().Accounts[0].AvailableCash,
                Is.EqualTo(12345.67m));
            Assert.That(
                positionsTask.Result.ToPayload<QmtQueryPositionsPayload>().Positions[0].StockCode,
                Is.EqualTo("600000.SH"));
        }

        [Test]
        [Timeout(5000)]
        public async Task DispatchesGatewayEventsWithRawPayload()
        {
            using var gatewayServer = new QmtFakeGatewayServer();
            using var gatewayClient = CreateClient(gatewayServer);
            var receivedEvent = new TaskCompletionSource<QmtProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            gatewayClient.EventReceived += (_, eventArguments) => receivedEvent.TrySetResult(eventArguments.Message);
            await gatewayClient.ConnectAsync();

            await gatewayServer.SendEventAsync(
                QmtProtocol.Operations.Quote,
                new { stock_code = "000001.SZ", last_price = 10.25m });
            var quoteMessage = await receivedEvent.Task;

            Assert.That(quoteMessage.MessageType, Is.EqualTo(QmtProtocol.MessageTypes.Event));
            Assert.That(quoteMessage.Operation, Is.EqualTo(QmtProtocol.Operations.Quote));
            Assert.That(quoteMessage.Payload.Value<string>("stock_code"), Is.EqualTo("000001.SZ"));
            Assert.That(quoteMessage.Payload.Value<decimal>("last_price"), Is.EqualTo(10.25m));
        }

        [Test]
        [Timeout(5000)]
        public async Task UnsubscribesWithGatewaySubscriptionId()
        {
            using var gatewayServer = new QmtFakeGatewayServer();
            var defaultRequestHandler = gatewayServer.RequestHandler;
            QmtProtocolMessage? unsubscribeRequest = null;
            gatewayServer.RequestHandler = request =>
            {
                if (request.Operation == QmtProtocol.Operations.Subscribe)
                {
                    return Task.FromResult<QmtProtocolMessage?>(QmtFakeGatewayServer.CreateSuccessfulResponse(
                        request,
                        JObject.FromObject(new QmtSubscribePayload
                        {
                            Subscribed = true,
                            SubscriptionId = "37",
                            StockCode = "000001.SZ"
                        })));
                }

                if (request.Operation == QmtProtocol.Operations.Unsubscribe)
                {
                    unsubscribeRequest = request;
                    return Task.FromResult<QmtProtocolMessage?>(QmtFakeGatewayServer.CreateSuccessfulResponse(
                        request,
                        JObject.FromObject(new QmtUnsubscribePayload
                        {
                            Unsubscribed = true,
                            SubscriptionId = "37"
                        })));
                }

                return defaultRequestHandler(request);
            };
            using var gatewayClient = CreateClient(gatewayServer);
            await gatewayClient.ConnectAsync();

            var subscribeResponse = await gatewayClient.SendRequestAsync(
                QmtProtocol.Operations.Subscribe,
                new QmtStockCodeRequest { StockCode = "000001.SZ" });
            var subscription = subscribeResponse.ToPayload<QmtSubscribePayload>();
            var unsubscribeResponse = await gatewayClient.SendRequestAsync(
                QmtProtocol.Operations.Unsubscribe,
                new QmtUnsubscribeRequest { SubscriptionId = subscription.SubscriptionId });

            Assert.That(unsubscribeRequest?.Payload.Value<string>("subscription_id"), Is.EqualTo("37"));
            Assert.That(unsubscribeRequest?.Payload["stock_code"], Is.Null);
            Assert.That(unsubscribeResponse.ToPayload<QmtUnsubscribePayload>().Unsubscribed, Is.True);
        }

        [Test]
        [Timeout(5000)]
        public async Task SurfacesGatewayOperationErrors()
        {
            using var gatewayServer = new QmtFakeGatewayServer();
            var defaultRequestHandler = gatewayServer.RequestHandler;
            gatewayServer.RequestHandler = request =>
            {
                if (request.Operation == QmtProtocol.Operations.PlaceOrder)
                {
                    return Task.FromResult<QmtProtocolMessage?>(QmtFakeGatewayServer.CreateFailedResponse(
                        request,
                        "TRADING_DISABLED",
                        "Trading is disabled by configuration."));
                }

                return defaultRequestHandler(request);
            };
            using var gatewayClient = CreateClient(gatewayServer);
            await gatewayClient.ConnectAsync();

            var exception = Assert.ThrowsAsync<QmtGatewayRequestException>(() => gatewayClient.SendRequestAsync(
                QmtProtocol.Operations.PlaceOrder,
                new QmtPlaceOrderRequest { ClientOrderId = "42", StockCode = "600000.SH" }));

            Assert.That(exception?.ErrorCode, Is.EqualTo("TRADING_DISABLED"));
            Assert.That(exception?.Message, Does.Contain("Trading is disabled"));
        }

        [Test]
        [Timeout(5000)]
        public async Task TimesOutWhenGatewayDoesNotRespond()
        {
            using var gatewayServer = new QmtFakeGatewayServer();
            var defaultRequestHandler = gatewayServer.RequestHandler;
            gatewayServer.RequestHandler = request => request.Operation == QmtProtocol.Operations.QueryOrders
                ? Task.FromResult<QmtProtocolMessage?>(null)
                : defaultRequestHandler(request);
            using var gatewayClient = CreateClient(gatewayServer, TimeSpan.FromMilliseconds(100));
            await gatewayClient.ConnectAsync();

            Assert.ThrowsAsync<QmtGatewayTimeoutException>(
                () => gatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryOrders));
        }

        [Test]
        [Timeout(5000)]
        public async Task FailsPendingRequestsAndRaisesDisconnectedEvent()
        {
            using var gatewayServer = new QmtFakeGatewayServer();
            var defaultRequestHandler = gatewayServer.RequestHandler;
            gatewayServer.RequestHandler = request => request.Operation == QmtProtocol.Operations.QueryOrders
                ? Task.FromResult<QmtProtocolMessage?>(null)
                : defaultRequestHandler(request);
            using var gatewayClient = CreateClient(gatewayServer, TimeSpan.FromSeconds(2));
            var disconnected = new TaskCompletionSource<QmtGatewayDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            gatewayClient.Disconnected += (_, eventArguments) => disconnected.TrySetResult(eventArguments);
            await gatewayClient.ConnectAsync();

            var pendingRequest = gatewayClient.SendRequestAsync(QmtProtocol.Operations.QueryOrders);
            gatewayServer.CloseClientConnection();

            Assert.ThrowsAsync<QmtGatewayException>(async () => await pendingRequest);
            var disconnectedEvent = await disconnected.Task;
            Assert.That(gatewayClient.IsConnected, Is.False);
            Assert.That(disconnectedEvent.Exception, Is.Not.Null);
        }

        private static QmtGatewayClient CreateClient(
            QmtFakeGatewayServer gatewayServer,
            TimeSpan? requestTimeout = null)
        {
            return new QmtGatewayClient(
                "127.0.0.1",
                gatewayServer.Port,
                "86033767",
                requestTimeout ?? TimeSpan.FromSeconds(1));
        }
    }
}
