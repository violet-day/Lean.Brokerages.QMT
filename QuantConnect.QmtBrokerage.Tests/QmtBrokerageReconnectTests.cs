using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageReconnectTests
    {
        private static readonly TimeSpan TestReconnectInterval = TimeSpan.FromMilliseconds(10);

        [Test]
        public void UsesBoundedProductionBackoff()
        {
            var delays = Enumerable.Range(1, 9)
                .Select(attemptNumber => QmtBrokerage.GetDefaultReconnectDelay(attemptNumber).TotalSeconds)
                .ToArray();

            Assert.That(delays, Is.EqualTo(new double[] { 1, 2, 5, 10, 20, 30, 60, 60, 60 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => QmtBrokerage.GetDefaultReconnectDelay(0));
        }

        [Test]
        public void RetriesOnceRestoresStateAndSubscriptionsBeforeReportingReconnect()
        {
            using var gatewayClient = new QmtReconnectTestGatewayClient(connectFailuresBeforeSuccess: 1);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                reconnectInterval: TestReconnectInterval);
            var subscriptionConfiguration = CreateSubscriptionConfiguration();
            using var enumerator = brokerage.Subscribe(subscriptionConfiguration, (_, _) => { });
            var messages = new ConcurrentQueue<BrokerageMessageEvent>();
            brokerage.Message += (_, message) => messages.Enqueue(message);

            gatewayClient.EmitDisconnected();
            gatewayClient.EmitDisconnected();

            Assert.That(
                SpinWait.SpinUntil(
                    () => messages.Any(message => message.Type == BrokerageMessageType.Reconnect),
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "Timed reconnect did not complete.");
            brokerage.Unsubscribe(subscriptionConfiguration);

            Assert.Multiple(() =>
            {
                Assert.That(gatewayClient.ConnectAttempts, Is.EqualTo(2));
                Assert.That(
                    gatewayClient.SuccessfulRecoveryOperations,
                    Is.EqualTo(new[]
                    {
                        QmtProtocol.Operations.QueryAccount,
                        QmtProtocol.Operations.QueryPositions,
                        QmtProtocol.Operations.QueryOrders,
                        QmtProtocol.Operations.Subscribe
                    }));
                Assert.That(gatewayClient.UnsubscribedSubscriptionIds, Is.EqualTo(new[] { "subscription-2" }));
                Assert.That(messages.Count(message => message.Type == BrokerageMessageType.Reconnect), Is.EqualTo(1));
                Assert.That(messages.Count(message => message.Type == BrokerageMessageType.Disconnect), Is.EqualTo(1));
                Assert.That(
                    messages.Single(message => message.Type == BrokerageMessageType.Reconnect).Message,
                    Does.Contain(" second(s); restored 1 subscription(s)"));
                Assert.That(brokerage.IsConnected, Is.True);
            });

            gatewayClient.EmitDisconnected();
            Assert.That(
                SpinWait.SpinUntil(
                    () => messages.Count(message => message.Type == BrokerageMessageType.Reconnect) == 2,
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "A second outage did not recover.");
            Assert.Multiple(() =>
            {
                Assert.That(gatewayClient.ConnectAttempts, Is.EqualTo(3));
                Assert.That(
                    messages.Last(message => message.Type == BrokerageMessageType.Reconnect).Message,
                    Does.Contain("after 1 attempt(s)"));
            });
        }

        [Test]
        public void RecoveryFailureAdvancesToNextAttemptBeforeReportingReconnect()
        {
            using var gatewayClient = new QmtReconnectTestGatewayClient(recoveryFailuresBeforeSuccess: 1);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                reconnectInterval: TestReconnectInterval);
            var messages = new ConcurrentQueue<BrokerageMessageEvent>();
            brokerage.Message += (_, message) => messages.Enqueue(message);

            gatewayClient.EmitDisconnected();

            Assert.That(
                SpinWait.SpinUntil(
                    () => messages.Any(message => message.Type == BrokerageMessageType.Reconnect),
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "Reconnect did not continue after account recovery failed.");
            Assert.Multiple(() =>
            {
                Assert.That(gatewayClient.ConnectAttempts, Is.EqualTo(2));
                Assert.That(messages.Count(message => message.Type == BrokerageMessageType.Reconnect), Is.EqualTo(1));
                Assert.That(
                    messages.Single(message => message.Type == BrokerageMessageType.Reconnect).Message,
                    Does.Contain("after 2 attempt(s)"));
            });
        }

        [Test]
        public void SubscriptionRecoveryFailureAdvancesToNextAttemptBeforeReportingReconnect()
        {
            using var gatewayClient = new QmtReconnectTestGatewayClient(
                subscriptionRecoveryFailuresBeforeSuccess: 1);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                reconnectInterval: TestReconnectInterval);
            using var enumerator = brokerage.Subscribe(CreateSubscriptionConfiguration(), (_, _) => { });
            var messages = new ConcurrentQueue<BrokerageMessageEvent>();
            brokerage.Message += (_, message) => messages.Enqueue(message);

            gatewayClient.EmitDisconnected();

            Assert.That(
                SpinWait.SpinUntil(
                    () => messages.Any(message => message.Type == BrokerageMessageType.Reconnect),
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "Reconnect did not continue after subscription recovery failed.");
            Assert.Multiple(() =>
            {
                Assert.That(gatewayClient.ConnectAttempts, Is.EqualTo(2));
                Assert.That(messages.Count(message => message.Type == BrokerageMessageType.Reconnect), Is.EqualTo(1));
                Assert.That(
                    messages.Single(message => message.Type == BrokerageMessageType.Reconnect).Message,
                    Does.Contain("after 2 attempt(s)"));
            });
        }

        [Test]
        public void RemainsDisconnectedUntilSubscriptionRestorationCompletes()
        {
            using var gatewayClient = new QmtReconnectTestGatewayClient(blockRestoredSubscription: true);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                reconnectInterval: TestReconnectInterval);
            using var enumerator = brokerage.Subscribe(CreateSubscriptionConfiguration(), (_, _) => { });
            var reconnectReported = 0;
            brokerage.Message += (_, message) =>
            {
                if (message.Type == BrokerageMessageType.Reconnect)
                {
                    Interlocked.Exchange(ref reconnectReported, 1);
                }
            };

            gatewayClient.EmitDisconnected();

            Assert.That(
                gatewayClient.RestoredSubscriptionStarted.Wait(TimeSpan.FromSeconds(2)),
                Is.True,
                "Reconnect did not reach subscription restoration.");
            try
            {
                Assert.Multiple(() =>
                {
                    Assert.That(brokerage.IsConnected, Is.False);
                    Assert.That(Volatile.Read(ref reconnectReported), Is.Zero);
                    Assert.Throws<QmtGatewayException>(() => brokerage.PlaceOrder(
                        new MarketOrder(Symbol.Empty, 100, DateTime.UtcNow)));
                });
            }
            finally
            {
                gatewayClient.CompleteRestoredSubscription();
            }

            Assert.That(
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref reconnectReported) == 1,
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "Reconnect was not reported after subscription restoration.");
        }

        [Test]
        public void ExplicitDisconnectCancelsTimedReconnect()
        {
            using var gatewayClient = new QmtReconnectTestGatewayClient(connectFailuresBeforeSuccess: int.MaxValue);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                reconnectInterval: TestReconnectInterval);

            gatewayClient.EmitDisconnected();
            Assert.That(
                SpinWait.SpinUntil(() => gatewayClient.ConnectAttempts >= 2, TimeSpan.FromSeconds(2)),
                Is.True,
                "Reconnect attempts did not start.");

            brokerage.Disconnect();
            var attemptsAfterDisconnect = gatewayClient.ConnectAttempts;
            Thread.Sleep(TimeSpan.FromMilliseconds(80));

            Assert.That(gatewayClient.ConnectAttempts, Is.EqualTo(attemptsAfterDisconnect));
        }

        private static SubscriptionDataConfig CreateSubscriptionConfiguration()
        {
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "600000.SH",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            return new SubscriptionDataConfig(
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
        }

        private sealed class QmtReconnectTestGatewayClient : IQmtGatewayClient
        {
            private readonly int _connectFailuresBeforeSuccess;
            private readonly int _recoveryFailuresBeforeSuccess;
            private readonly int _subscriptionRecoveryFailuresBeforeSuccess;
            private readonly bool _blockRestoredSubscription;
            private readonly TaskCompletionSource<QmtProtocolMessage> _restoredSubscriptionCompletion =
                new TaskCompletionSource<QmtProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _connectAttempts;
            private int _recoveryAttempts;
            private int _subscriptionCount;

            public bool IsConnected { get; private set; } = true;
            public QmtHelloPayload? ServerInformation { get; private set; } = CreateServerInformation();
            public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
            public ConcurrentQueue<string> SuccessfulRecoveryOperations { get; } = new ConcurrentQueue<string>();
            public ConcurrentQueue<string> UnsubscribedSubscriptionIds { get; } = new ConcurrentQueue<string>();
            public ManualResetEventSlim RestoredSubscriptionStarted { get; } = new ManualResetEventSlim(false);

            public event EventHandler<QmtGatewayMessageEventArgs>? EventReceived
            {
                add { }
                remove { }
            }
            public event EventHandler<QmtGatewayDisconnectedEventArgs>? Disconnected;

            public QmtReconnectTestGatewayClient(
                int connectFailuresBeforeSuccess = 0,
                int recoveryFailuresBeforeSuccess = 0,
                int subscriptionRecoveryFailuresBeforeSuccess = 0,
                bool blockRestoredSubscription = false)
            {
                _connectFailuresBeforeSuccess = connectFailuresBeforeSuccess;
                _recoveryFailuresBeforeSuccess = recoveryFailuresBeforeSuccess;
                _subscriptionRecoveryFailuresBeforeSuccess = subscriptionRecoveryFailuresBeforeSuccess;
                _blockRestoredSubscription = blockRestoredSubscription;
            }

            public void Connect()
            {
                ConnectAsync().GetAwaiter().GetResult();
            }

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var connectAttempt = Interlocked.Increment(ref _connectAttempts);
                if (connectAttempt <= _connectFailuresBeforeSuccess)
                {
                    throw new IOException("Test Gateway connection failed.");
                }

                IsConnected = true;
                ServerInformation = CreateServerInformation();
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
                cancellationToken.ThrowIfCancellationRequested();
                if (operation == QmtProtocol.Operations.Subscribe)
                {
                    var subscriptionNumber = Interlocked.Increment(ref _subscriptionCount);
                    var response = CreateResponse(
                        operation,
                        new QmtSubscribePayload
                        {
                            Subscribed = true,
                            SubscriptionId = $"subscription-{subscriptionNumber}",
                            StockCode = ((QmtStockCodeRequest)payload!).StockCode
                        });
                    if (subscriptionNumber > 1)
                    {
                        if (subscriptionNumber - 1 <= _subscriptionRecoveryFailuresBeforeSuccess)
                        {
                            throw new IOException("Test subscription recovery failed.");
                        }
                        SuccessfulRecoveryOperations.Enqueue(operation);
                        if (_blockRestoredSubscription)
                        {
                            RestoredSubscriptionStarted.Set();
                            return _restoredSubscriptionCompletion.Task;
                        }
                    }
                    return Task.FromResult(response);
                }

                if (operation == QmtProtocol.Operations.Unsubscribe)
                {
                    UnsubscribedSubscriptionIds.Enqueue(((QmtUnsubscribeRequest)payload!).SubscriptionId);
                    return Task.FromResult(CreateResponse(
                        operation,
                        new QmtUnsubscribePayload { Unsubscribed = true }));
                }

                if (operation == QmtProtocol.Operations.QueryAccount &&
                    Interlocked.Increment(ref _recoveryAttempts) <= _recoveryFailuresBeforeSuccess)
                {
                    throw new IOException("Test account recovery failed.");
                }

                SuccessfulRecoveryOperations.Enqueue(operation);
                return operation switch
                {
                    QmtProtocol.Operations.QueryAccount => Task.FromResult(CreateResponse(
                        operation,
                        new QmtQueryAccountPayload())),
                    QmtProtocol.Operations.QueryPositions => Task.FromResult(CreateResponse(
                        operation,
                        new QmtQueryPositionsPayload())),
                    QmtProtocol.Operations.QueryOrders => Task.FromResult(CreateResponse(
                        operation,
                        new QmtQueryOrdersPayload())),
                    _ => throw new InvalidOperationException($"Unexpected operation: {operation}")
                };
            }

            public void EmitDisconnected()
            {
                IsConnected = false;
                ServerInformation = null;
                Disconnected?.Invoke(
                    this,
                    new QmtGatewayDisconnectedEventArgs(
                        new QmtGatewayException("Test Gateway connection was lost.")));
            }

            public void CompleteRestoredSubscription()
            {
                _restoredSubscriptionCompletion.TrySetResult(CreateResponse(
                    QmtProtocol.Operations.Subscribe,
                    new QmtSubscribePayload
                    {
                        Subscribed = true,
                        SubscriptionId = "subscription-2",
                        StockCode = "600000.SH"
                    }));
            }

            public void Dispose()
            {
                Disconnect();
                RestoredSubscriptionStarted.Dispose();
            }

            private static QmtHelloPayload CreateServerInformation()
            {
                return new QmtHelloPayload
                {
                    AccountId = "reconnect-test",
                    ServerName = "test-gateway"
                };
            }

            private static QmtProtocolMessage CreateResponse(string operation, object payload)
            {
                return new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Response,
                    RequestId = "reconnect-request",
                    Operation = operation,
                    Success = true,
                    Payload = JObject.FromObject(payload)
                };
            }
        }
    }
}
