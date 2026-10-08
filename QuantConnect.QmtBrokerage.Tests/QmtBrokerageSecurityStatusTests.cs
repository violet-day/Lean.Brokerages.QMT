using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class QmtBrokerageSecurityStatusTests
    {
        [SetUp]
        public void SetUp()
        {
            Composer.Instance.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            Composer.Instance.Reset();
        }

        [Test]
        public void QueriesSecurityTradingStatusWithoutChangingLeanSecurity()
        {
            QmtMarket.RegisterIdentifier();
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "002860.SZ",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            using var gatewayClient = new QmtSecurityStatusTestGatewayClient(false);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider());

            var isTrading = brokerage.IsSecurityTrading(symbol);

            Assert.Multiple(() =>
            {
                Assert.That(isTrading, Is.False);
                Assert.That(
                    gatewayClient.RequestedOperation,
                    Is.EqualTo(QmtProtocol.Operations.QuerySecurityStatus));
                Assert.That(gatewayClient.RequestedStockCode, Is.EqualTo("002860.SZ"));
            });
        }

        [Test]
        public void CachesSuccessfulStatusForThirtySeconds()
        {
            QmtMarket.RegisterIdentifier();
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "002860.SZ",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            var initialUtcTime = new DateTime(2026, 10, 8, 6, 45, 0, DateTimeKind.Utc);
            var timeProvider = new QmtOrderTestTimeProvider(initialUtcTime);
            using var gatewayClient = new QmtSecurityStatusTestGatewayClient(false);
            using var brokerage = new QmtBrokerage(
                gatewayClient,
                new QmtOrderTestProvider(),
                timeProvider: timeProvider);

            Assert.That(brokerage.IsSecurityTrading(symbol), Is.False);
            gatewayClient.SetIsTrading(true);
            timeProvider.SetUtcNow(initialUtcTime.AddSeconds(29));

            Assert.That(brokerage.IsSecurityTrading(symbol), Is.False);
            timeProvider.SetUtcNow(initialUtcTime.AddSeconds(30));

            Assert.Multiple(() =>
            {
                Assert.That(brokerage.IsSecurityTrading(symbol), Is.True);
                Assert.That(gatewayClient.RequestCount, Is.EqualTo(2));
            });
        }

        [Test]
        public void FacadeReturnsRegisteredProviderStatus()
        {
            QmtMarket.RegisterIdentifier();
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "002860.SZ",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            Composer.Instance.AddPart<IQmtSecurityStatusProvider>(
                new FixedSecurityStatusProvider(false));

            var isTrading = QmtSecurityStatus.IsTrading(symbol);

            Assert.That(isTrading, Is.False);
        }

        [Test]
        public void FacadeReturnsNullWhenProviderIsUnavailable()
        {
            QmtMarket.RegisterIdentifier();
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "002860.SZ",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);

            var isTrading = QmtSecurityStatus.IsTrading(symbol);

            Assert.That(isTrading, Is.Null);
        }

        [Test]
        public void FacadeReturnsNullWhenProviderThrows()
        {
            QmtMarket.RegisterIdentifier();
            var symbol = new QmtSymbolMapper().GetLeanSymbol(
                "002860.SZ",
                SecurityType.Equity,
                QmtSymbolMapper.MarketName);
            Composer.Instance.AddPart<IQmtSecurityStatusProvider>(
                new ThrowingSecurityStatusProvider());

            var isTrading = QmtSecurityStatus.IsTrading(symbol);

            Assert.That(isTrading, Is.Null);
        }

        private sealed class FixedSecurityStatusProvider : IQmtSecurityStatusProvider
        {
            private readonly bool _isTrading;

            public FixedSecurityStatusProvider(bool isTrading)
            {
                _isTrading = isTrading;
            }

            public bool IsSecurityTrading(Symbol symbol)
            {
                return _isTrading;
            }
        }

        private sealed class ThrowingSecurityStatusProvider : IQmtSecurityStatusProvider
        {
            public bool IsSecurityTrading(Symbol symbol)
            {
                throw new InvalidOperationException("QMT status unavailable.");
            }
        }

        private sealed class QmtSecurityStatusTestGatewayClient : IQmtGatewayClient
        {
            private bool _isTrading;

            public bool IsConnected { get; private set; } = true;
            public QmtHelloPayload? ServerInformation { get; private set; } = new QmtHelloPayload
            {
                AccountId = "security-status-test",
                ServerName = "test-gateway"
            };
            public string RequestedOperation { get; private set; } = string.Empty;
            public string RequestedStockCode { get; private set; } = string.Empty;
            public int RequestCount { get; private set; }

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

            public QmtSecurityStatusTestGatewayClient(bool isTrading)
            {
                _isTrading = isTrading;
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
                RequestCount++;
                RequestedOperation = operation;
                RequestedStockCode = ((QmtStockCodeRequest)payload!).StockCode;
                return Task.FromResult(new QmtProtocolMessage
                {
                    MessageType = QmtProtocol.MessageTypes.Response,
                    RequestId = "query-security-status-request",
                    Operation = operation,
                    Success = true,
                    Payload = JObject.FromObject(new QmtQuerySecurityStatusPayload
                    {
                        StockCode = RequestedStockCode,
                        IsTrading = _isTrading
                    })
                });
            }

            public void SetIsTrading(bool isTrading)
            {
                _isTrading = isTrading;
            }

            public void Dispose()
            {
                Disconnect();
            }
        }
    }
}
