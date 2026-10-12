using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public sealed class QmtBrokerageHoldingsReportTests
    {
        private string _temporaryDirectory = null!;
        private string _reportPath = null!;

        [SetUp]
        public void SetUp()
        {
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"qmt-holdings-report-{Guid.NewGuid():N}");
            _reportPath = Path.Combine(_temporaryDirectory, "qmt-holdings.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }

        [Test]
        public void InitialHoldingsQueryWritesSortedAtomicReportOutsideMarketHours()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromDays(1));

            brokerage.Connect();
            var holdings = brokerage.GetAccountHoldings();

            var report = JObject.Parse(File.ReadAllText(_reportPath));
            Assert.Multiple(() =>
            {
                Assert.That(holdings, Has.Count.EqualTo(2));
                Assert.That(
                    report.Properties().Select(property => property.Name),
                    Is.EqualTo(new[] { "000001.SZ", "600000.SH" }));
                Assert.That(report["000001.SZ"]!["available_volume"]!.Value<long>(), Is.EqualTo(80));
                Assert.That(report["000001.SZ"]!["volume"]!.Value<long>(), Is.EqualTo(100));
                Assert.That(report["600000.SH"]!["available_volume"]!.Value<long>(), Is.EqualTo(0));
                Assert.That(report["600000.SH"]!["volume"]!.Value<long>(), Is.EqualTo(200));
                Assert.That(report["300001.SZ"], Is.Null);
                Assert.That(
                    Directory.GetFiles(_temporaryDirectory, "*.tmp"),
                    Is.Empty);
            });
        }

        [TestCase(false, 1, 31)]
        [TestCase(true, 2, 1)]
        public void PeriodicReportQueriesInsideAccountMarketWindow(
            bool isSimulation,
            int utcHour,
            int utcMinute)
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, utcHour, utcMinute, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(10));

            brokerage.Connect();

            Assert.That(
                SpinWait.SpinUntil(
                    () => gatewayClient.PositionQueryCount > 0 && File.Exists(_reportPath),
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "The periodic holdings report did not run inside the market window.");
        }

        [TestCase(false, 0, 0)]
        [TestCase(true, 1, 31)]
        public void PeriodicReportSkipsClosedAndSimulationPreOpenWindows(
            bool isSimulation,
            int utcHour,
            int utcMinute)
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, utcHour, utcMinute, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(10));

            brokerage.Connect();
            Thread.Sleep(TimeSpan.FromMilliseconds(80));

            Assert.Multiple(() =>
            {
                Assert.That(gatewayClient.PositionQueryCount, Is.Zero);
                Assert.That(File.Exists(_reportPath), Is.False);
            });
        }

        [Test]
        public void DisconnectStopsPeriodicReport()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 1, 31, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(10));

            brokerage.Connect();
            Assert.That(
                SpinWait.SpinUntil(
                    () => gatewayClient.PositionQueryCount > 0,
                    TimeSpan.FromSeconds(2)),
                Is.True);

            brokerage.Disconnect();
            var queryCountAfterDisconnect = gatewayClient.PositionQueryCount;
            Thread.Sleep(TimeSpan.FromMilliseconds(80));

            Assert.That(
                gatewayClient.PositionQueryCount,
                Is.EqualTo(queryCountAfterDisconnect));
        }

        [Test]
        public void RepeatedConnectDoesNotStartASecondReportLoop()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions(),
                blockPositionQueries: true);
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 1, 31, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(10));

            brokerage.Connect();
            Assert.That(
                gatewayClient.PositionQueryStarted.Wait(TimeSpan.FromSeconds(2)),
                Is.True);
            try
            {
                brokerage.Connect();
                Thread.Sleep(TimeSpan.FromMilliseconds(80));

                Assert.That(gatewayClient.PositionQueryCount, Is.EqualTo(1));
            }
            finally
            {
                gatewayClient.CompletePositionQueries();
            }
        }

        [Test]
        public void DisposeStopsPeriodicReport()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions());
            var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 1, 31, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(10));

            brokerage.Connect();
            Assert.That(
                SpinWait.SpinUntil(
                    () => gatewayClient.PositionQueryCount > 0,
                    TimeSpan.FromSeconds(2)),
                Is.True);

            brokerage.Dispose();
            var queryCountAfterDispose = gatewayClient.PositionQueryCount;
            Thread.Sleep(TimeSpan.FromMilliseconds(80));

            Assert.That(
                gatewayClient.PositionQueryCount,
                Is.EqualTo(queryCountAfterDispose));
        }

        [Test]
        public void InvalidSnapshotPreservesLastValidReport()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromDays(1));

            brokerage.Connect();
            brokerage.GetAccountHoldings();
            var validReport = File.ReadAllText(_reportPath);
            gatewayClient.SetPositions(new[]
            {
                Position("000001.SZ", 100, 100),
                Position("000001.SZ", 200, 200)
            });

            brokerage.GetAccountHoldings();

            Assert.That(File.ReadAllText(_reportPath), Is.EqualTo(validReport));
        }

        [TestCase("-1", "0")]
        [TestCase("1.5", "0")]
        [TestCase("100", "101")]
        public void InvalidQuantityPreservesLastValidReport(
            string volume,
            string availableVolume)
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromDays(1));

            brokerage.Connect();
            brokerage.GetAccountHoldings();
            var validReport = File.ReadAllText(_reportPath);
            gatewayClient.SetPositions(new[]
            {
                Position(
                    "000001.SZ",
                    decimal.Parse(volume, System.Globalization.CultureInfo.InvariantCulture),
                    decimal.Parse(availableVolume, System.Globalization.CultureInfo.InvariantCulture))
            });

            brokerage.GetAccountHoldings();

            Assert.That(File.ReadAllText(_reportPath), Is.EqualTo(validReport));
        }

        [Test]
        [Platform("Win")]
        public void WriteFailurePreservesLastValidReportAndNextWriteRecovers()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromDays(1));

            brokerage.Connect();
            brokerage.GetAccountHoldings();
            var validReport = File.ReadAllText(_reportPath);
            gatewayClient.SetPositions(new[]
            {
                Position("000001.SZ", 300, 200)
            });

            using (new FileStream(
                _reportPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                brokerage.GetAccountHoldings();
                Assert.That(File.ReadAllText(_reportPath), Is.EqualTo(validReport));
            }

            brokerage.GetAccountHoldings();
            var recoveredReport = JObject.Parse(File.ReadAllText(_reportPath));
            Assert.Multiple(() =>
            {
                Assert.That(recoveredReport.Properties().Select(property => property.Name),
                    Is.EqualTo(new[] { "000001.SZ" }));
                Assert.That(
                    recoveredReport["000001.SZ"]!["available_volume"]!.Value<long>(),
                    Is.EqualTo(200));
                Assert.That(
                    recoveredReport["000001.SZ"]!["volume"]!.Value<long>(),
                    Is.EqualTo(300));
                Assert.That(Directory.GetFiles(_temporaryDirectory, "*.tmp"), Is.Empty);
            });
        }

        [Test]
        public void EmptySnapshotWritesEmptyObject()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                Array.Empty<QmtPositionSnapshot>());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromDays(1));

            brokerage.Connect();
            brokerage.GetAccountHoldings();

            Assert.That(JObject.Parse(File.ReadAllText(_reportPath)), Is.Empty);
        }

        [Test]
        public void PeriodicReportRecoversAfterQueryFailure()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions(),
                positionQueryFailures: 1);
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 1, 31, 0, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(10));

            brokerage.Connect();

            Assert.That(
                SpinWait.SpinUntil(
                    () => gatewayClient.PositionQueryCount >= 2 && File.Exists(_reportPath),
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "The holdings report loop did not recover after a query failure.");
        }

        [Test]
        public void ReconnectReusesRecoveredPositionsForImmediateReport()
        {
            using var gatewayClient = new HoldingsReportGatewayClient(
                isSimulation: false,
                CreatePositions());
            using var brokerage = CreateBrokerage(
                gatewayClient,
                new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromDays(1),
                TimeSpan.FromMilliseconds(10));

            brokerage.Connect();
            gatewayClient.EmitDisconnected();

            Assert.That(
                SpinWait.SpinUntil(
                    () => gatewayClient.PositionQueryCount == 1 && File.Exists(_reportPath),
                    TimeSpan.FromSeconds(2)),
                Is.True,
                "Reconnect did not write the recovered position snapshot.");
        }

        private QmtBrokerage CreateBrokerage(
            HoldingsReportGatewayClient gatewayClient,
            DateTime utcTime,
            TimeSpan reportInterval,
            TimeSpan? reconnectInterval = null)
        {
            return new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                symbolMapper: null,
                timeProvider: new QmtOrderTestTimeProvider(utcTime),
                reconnectInterval: reconnectInterval,
                holdingsReportPath: _reportPath,
                holdingsReportInterval: reportInterval);
        }

        private static IReadOnlyCollection<QmtPositionSnapshot> CreatePositions()
        {
            return new[]
            {
                Position("600000.SH", 200, 0),
                Position("300001.SZ", 0, 0),
                Position("000001.SZ", 100, 80)
            };
        }

        private static QmtPositionSnapshot Position(
            string stockCode,
            decimal volume,
            decimal availableVolume)
        {
            return new QmtPositionSnapshot
            {
                StockCode = stockCode,
                Volume = volume,
                AvailableVolume = availableVolume,
                OpenPrice = 10m,
                LastPrice = 10m,
                MarketValue = volume * 10m
            };
        }

        private sealed class HoldingsReportGatewayClient : IQmtGatewayClient
        {
            private readonly object _positionsLock = new object();
            private readonly bool _isSimulation;
            private readonly bool _blockPositionQueries;
            private List<QmtPositionSnapshot> _positions;
            private int _positionQueryFailures;
            private int _positionQueryCount;

            public bool IsConnected { get; private set; }
            public QmtHelloPayload? ServerInformation { get; private set; }
            public int PositionQueryCount => Volatile.Read(ref _positionQueryCount);
            public ManualResetEventSlim PositionQueryStarted { get; } = new ManualResetEventSlim(false);
            private ManualResetEventSlim PositionQueriesMayComplete { get; } = new ManualResetEventSlim(false);

            public event EventHandler<QmtGatewayMessageEventArgs>? EventReceived
            {
                add { }
                remove { }
            }

            public event EventHandler<QmtGatewayDisconnectedEventArgs>? Disconnected;

            public HoldingsReportGatewayClient(
                bool isSimulation,
                IEnumerable<QmtPositionSnapshot> positions,
                int positionQueryFailures = 0,
                bool blockPositionQueries = false)
            {
                _isSimulation = isSimulation;
                _blockPositionQueries = blockPositionQueries;
                _positions = positions.ToList();
                _positionQueryFailures = positionQueryFailures;
            }

            public void Connect()
            {
                IsConnected = true;
                ServerInformation = new QmtHelloPayload
                {
                    AccountId = "holdings-report-test",
                    ServerName = "test-gateway",
                    IsSimulation = _isSimulation
                };
            }

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Connect();
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
                if (operation == QmtProtocol.Operations.QueryAccount)
                {
                    return Task.FromResult(Response(
                        operation,
                        new QmtQueryAccountPayload()));
                }
                if (operation == QmtProtocol.Operations.QueryOrders)
                {
                    return Task.FromResult(Response(
                        operation,
                        new QmtQueryOrdersPayload()));
                }
                if (operation != QmtProtocol.Operations.QueryPositions)
                {
                    throw new InvalidOperationException($"Unexpected operation: {operation}");
                }

                Interlocked.Increment(ref _positionQueryCount);
                if (_blockPositionQueries)
                {
                    PositionQueryStarted.Set();
                    PositionQueriesMayComplete.Wait(cancellationToken);
                }
                if (Interlocked.Decrement(ref _positionQueryFailures) >= 0)
                {
                    throw new IOException("Test position query failed.");
                }

                List<QmtPositionSnapshot> positions;
                lock (_positionsLock)
                {
                    positions = _positions.ToList();
                }
                return Task.FromResult(Response(
                    operation,
                    new QmtQueryPositionsPayload
                    {
                        Positions = positions
                    }));
            }

            public void SetPositions(IEnumerable<QmtPositionSnapshot> positions)
            {
                lock (_positionsLock)
                {
                    _positions = positions.ToList();
                }
            }

            public void CompletePositionQueries()
            {
                PositionQueriesMayComplete.Set();
            }

            public void EmitDisconnected()
            {
                IsConnected = false;
                ServerInformation = null;
                Disconnected?.Invoke(
                    this,
                    new QmtGatewayDisconnectedEventArgs(
                        new IOException("Test Gateway disconnected.")));
            }

            public void Dispose()
            {
                Disconnect();
                PositionQueryStarted.Dispose();
                PositionQueriesMayComplete.Dispose();
            }

            private static QmtProtocolMessage Response(string operation, object payload)
            {
                return new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Response,
                    RequestId = "holdings-report-request",
                    Operation = operation,
                    Success = true,
                    Payload = JObject.FromObject(payload)
                };
            }
        }
    }
}
