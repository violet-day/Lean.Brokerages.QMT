using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    [TestFixture]
    public class QmtBrokerageSecurityStatusTests
    {
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

        private sealed class QmtSecurityStatusTestGatewayClient : IQmtGatewayClient
        {
            private readonly bool _isTrading;

            public bool IsConnected { get; private set; } = true;
            public QmtHelloPayload? ServerInformation { get; private set; } = new QmtHelloPayload
            {
                AccountId = "security-status-test",
                ServerName = "test-gateway"
            };
            public string RequestedOperation { get; private set; } = string.Empty;
            public string RequestedStockCode { get; private set; } = string.Empty;

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

            public void Dispose()
            {
                Disconnect();
            }
        }
    }
}
