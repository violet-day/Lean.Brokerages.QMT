using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageSubscriptionTests
    {
        [Test]
        public void RoutesQuoteByBrokerageStockCodeWhenLeanSymbolIdentifiersDiffer()
        {
            var subscriptionIdentifier = SecurityIdentifier.GenerateEquity(
                SecurityIdentifier.DefaultDate,
                "600000.SH",
                QmtSymbolMapper.MarketName);
            var subscriptionSymbol = new Symbol(subscriptionIdentifier, "600000.SH");
            var subscriptionConfiguration = new SubscriptionDataConfig(
                typeof(Tick),
                subscriptionSymbol,
                Resolution.Tick,
                TimeZones.Shanghai,
                TimeZones.Shanghai,
                false,
                false,
                false,
                false,
                TickType.Trade);
            using var gatewayClient = new QmtSubscriptionTestGatewayClient();
            using var brokerage = new QmtBrokerage(gatewayClient, new QmtOrderTestProvider());
            using var dataAvailable = new ManualResetEventSlim(false);
            using var enumerator = brokerage.Subscribe(
                subscriptionConfiguration,
                (_, _) => dataAvailable.Set());

            gatewayClient.EmitQuoteEvent(new QmtQuoteEventPayload
            {
                StockCode = "600000.SH",
                Time = "20260824150000",
                LastPrice = 9.22m,
                Volume = 1000m,
                VolumeUnit = "shares",
                BidPrice = 9.21m,
                AskPrice = 9.22m,
                BidVolume = 100m,
                AskVolume = 200m
            });

            Assert.Multiple(() =>
            {
                Assert.That(dataAvailable.Wait(TimeSpan.FromSeconds(1)), Is.True);
                Assert.That(enumerator, Is.Not.Null);
                Assert.That(enumerator!.MoveNext(), Is.True);
                Assert.That(enumerator.Current.Symbol, Is.EqualTo(subscriptionSymbol));
                Assert.That(enumerator.Current.Value, Is.EqualTo(9.22m));
                Assert.That(gatewayClient.SubscribedStockCodes, Is.EqualTo(new[] { "600000.SH" }));
            });
        }

        [Test]
        public void ProducesMinuteTradeBarForMinuteTradeBarSubscription()
        {
            var subscriptionIdentifier = SecurityIdentifier.GenerateEquity(
                SecurityIdentifier.DefaultDate,
                "600000.SH",
                QmtSymbolMapper.MarketName);
            var subscriptionSymbol = new Symbol(subscriptionIdentifier, "600000.SH");
            var subscriptionConfiguration = new SubscriptionDataConfig(
                typeof(TradeBar),
                subscriptionSymbol,
                Resolution.Minute,
                TimeZones.Shanghai,
                TimeZones.Shanghai,
                false,
                false,
                false,
                false,
                TickType.Trade);
            using var gatewayClient = new QmtSubscriptionTestGatewayClient();
            using var brokerage = new QmtBrokerage(gatewayClient, new QmtOrderTestProvider());
            using var dataAvailable = new ManualResetEventSlim(false);
            using var enumerator = brokerage.Subscribe(
                subscriptionConfiguration,
                (_, _) => dataAvailable.Set());

            gatewayClient.EmitQuoteEvent(new QmtQuoteEventPayload
            {
                StockCode = "600000.SH",
                Time = "20260824093001",
                LastPrice = 9.20m,
                Volume = 1000m,
                VolumeUnit = "shares"
            });
            gatewayClient.EmitQuoteEvent(new QmtQuoteEventPayload
            {
                StockCode = "600000.SH",
                Time = "20260824093030",
                LastPrice = 9.25m,
                Volume = 1300m,
                VolumeUnit = "shares"
            });
            gatewayClient.EmitQuoteEvent(new QmtQuoteEventPayload
            {
                StockCode = "600000.SH",
                Time = "20260824093101",
                LastPrice = 9.23m,
                Volume = 1500m,
                VolumeUnit = "shares"
            });

            Assert.Multiple(() =>
            {
                Assert.That(dataAvailable.Wait(TimeSpan.FromSeconds(1)), Is.True);
                Assert.That(enumerator, Is.Not.Null);
                Assert.That(enumerator!.MoveNext(), Is.True);
                Assert.That(enumerator.Current, Is.TypeOf<TradeBar>());
            });

            var tradeBar = (TradeBar)enumerator!.Current;
            Assert.Multiple(() =>
            {
                Assert.That(tradeBar.Symbol, Is.EqualTo(subscriptionSymbol));
                Assert.That(tradeBar.Time, Is.EqualTo(new DateTime(2026, 8, 24, 9, 30, 0)));
                Assert.That(tradeBar.EndTime, Is.EqualTo(new DateTime(2026, 8, 24, 9, 31, 0)));
                Assert.That(tradeBar.Open, Is.EqualTo(9.20m));
                Assert.That(tradeBar.High, Is.EqualTo(9.25m));
                Assert.That(tradeBar.Low, Is.EqualTo(9.20m));
                Assert.That(tradeBar.Close, Is.EqualTo(9.25m));
                Assert.That(tradeBar.Volume, Is.EqualTo(300m));
            });
        }

        private sealed class QmtSubscriptionTestGatewayClient : IQmtGatewayClient
        {
            public bool IsConnected { get; private set; } = true;
            public QmtHelloPayload? ServerInformation { get; private set; } = new QmtHelloPayload
            {
                AccountId = "subscription-test",
                ServerName = "test-gateway"
            };
            public List<string> SubscribedStockCodes { get; } = new List<string>();

            public event EventHandler<QmtGatewayMessageEventArgs>? EventReceived;
            public event EventHandler<QmtGatewayDisconnectedEventArgs>? Disconnected
            {
                add { }
                remove { }
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
                if (operation != QmtProtocol.Operations.Subscribe)
                {
                    throw new InvalidOperationException($"Unexpected operation: {operation}");
                }

                var subscribeRequest = (QmtStockCodeRequest)payload!;
                SubscribedStockCodes.Add(subscribeRequest.StockCode);
                return Task.FromResult(new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Response,
                    RequestId = "subscribe-request",
                    Operation = operation,
                    Success = true,
                    Payload = JObject.FromObject(new QmtSubscribePayload
                    {
                        Subscribed = true,
                        SubscriptionId = "subscription-1",
                        StockCode = subscribeRequest.StockCode
                    })
                });
            }

            public void EmitQuoteEvent(QmtQuoteEventPayload payload)
            {
                EventReceived?.Invoke(this, new QmtGatewayMessageEventArgs(new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Event,
                    Operation = QmtProtocol.Operations.Quote,
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
