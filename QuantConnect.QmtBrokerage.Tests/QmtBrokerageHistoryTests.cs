using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class QmtBrokerageHistoryTests
    {
        [Test]
        public void UsesQmtMinuteTimestampAsTradeBarEndTime()
        {
            var historyRequest = CreateHistoryRequest();
            using var gatewayClient = new QmtHistoryTestGatewayClient(new[]
            {
                new QmtHistoryBar
                {
                    Time = "20260921093100",
                    Open = 21.96m,
                    High = 22.13m,
                    Low = 21.89m,
                    Close = 21.99m,
                    Volume = 261m
                },
                new QmtHistoryBar
                {
                    Time = "20260921150000",
                    Open = 23.24m,
                    High = 23.24m,
                    Low = 23.24m,
                    Close = 23.24m,
                    Volume = 226m
                }
            });
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider());

            var bars = brokerage.GetHistory(historyRequest).Cast<TradeBar>().ToList();

            Assert.Multiple(() =>
            {
                Assert.That(bars, Has.Count.EqualTo(2));
                Assert.That(bars[0].Time, Is.EqualTo(new DateTime(2026, 9, 21, 9, 30, 0)));
                Assert.That(bars[0].EndTime, Is.EqualTo(new DateTime(2026, 9, 21, 9, 31, 0)));
                Assert.That(bars[1].Time, Is.EqualTo(new DateTime(2026, 9, 21, 14, 59, 0)));
                Assert.That(bars[1].EndTime, Is.EqualTo(new DateTime(2026, 9, 21, 15, 0, 0)));
            });
        }

        [Test]
        public void LogsEmptyHistoryButNotSuccessfulHistory()
        {
            var originalLogHandler = Log.LogHandler;
            using var logHandler = new QueueLogHandler();
            Log.LogHandler = logHandler;
            try
            {
                using (var emptyGatewayClient = new QmtHistoryTestGatewayClient(Array.Empty<QmtHistoryBar>()))
                using (var emptyHistoryBrokerage = new QmtBrokerage(emptyGatewayClient, new QmtOrderTestProvider()))
                {
                    Assert.That(emptyHistoryBrokerage.GetHistory(CreateHistoryRequest()), Is.Empty);
                }
                Assert.That(
                    logHandler.Logs.Count(entry => entry.Message.Contains("QmtBrokerage.GetHistory(): status=empty")),
                    Is.EqualTo(1));

                while (logHandler.Logs.TryDequeue(out _))
                {
                }

                using (var successfulGatewayClient = new QmtHistoryTestGatewayClient(new[]
                {
                    new QmtHistoryBar
                    {
                        Time = "20260921093100",
                        Open = 21.96m,
                        High = 22.13m,
                        Low = 21.89m,
                        Close = 21.99m,
                        Volume = 261m
                    }
                }))
                using (var successfulHistoryBrokerage = new QmtBrokerage(successfulGatewayClient, new QmtOrderTestProvider()))
                {
                    Assert.That(successfulHistoryBrokerage.GetHistory(CreateHistoryRequest()).Count(), Is.EqualTo(1));
                }
                Assert.That(
                    logHandler.Logs.Any(entry => entry.Message.Contains("QmtBrokerage.GetHistory(): status=ok")),
                    Is.False);
            }
            finally
            {
                Log.LogHandler = originalLogHandler;
            }
        }

        private static HistoryRequest CreateHistoryRequest()
        {
            QmtMarket.RegisterIdentifier();
            var symbolIdentifier = SecurityIdentifier.GenerateEquity(
                SecurityIdentifier.DefaultDate,
                "002983.SZ",
                QmtSymbolMapper.MarketName);
            var symbol = new Symbol(symbolIdentifier, "002983.SZ");
            return new HistoryRequest(
                new DateTime(2026, 9, 21, 9, 30, 0),
                new DateTime(2026, 9, 21, 15, 0, 0),
                typeof(TradeBar),
                symbol,
                Resolution.Minute,
                SecurityExchangeHours.AlwaysOpen(TimeZones.Shanghai),
                TimeZones.Shanghai,
                null,
                false,
                false,
                DataNormalizationMode.Raw,
                TickType.Trade);
        }

        private sealed class QmtHistoryTestGatewayClient : IQmtGatewayClient
        {
            private readonly List<QmtHistoryBar> _historyBars;

            public bool IsConnected { get; private set; } = true;
            public QmtHelloPayload? ServerInformation { get; private set; } = new QmtHelloPayload
            {
                AccountId = "history-test",
                ServerName = "test-gateway"
            };

            public event EventHandler<QmtGatewayMessageEventArgs>? EventReceived
            {
                add { }
                remove { }
            }

            public event EventHandler<QmtGatewayDisconnectedEventArgs>? Disconnected
            {
                add { }
                remove { }
            }

            public QmtHistoryTestGatewayClient(IEnumerable<QmtHistoryBar> historyBars)
            {
                _historyBars = historyBars.ToList();
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
                if (operation != QmtProtocol.Operations.QueryHistory)
                {
                    throw new InvalidOperationException($"Unexpected operation: {operation}");
                }

                return Task.FromResult(new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Response,
                    RequestId = "query-history-request",
                    Operation = operation,
                    Success = true,
                    Payload = JObject.FromObject(new QmtQueryHistoryPayload
                    {
                        VolumeUnit = "shares",
                        Bars = _historyBars
                    })
                });
            }

            public void Dispose()
            {
                Disconnect();
            }
        }
    }
}
