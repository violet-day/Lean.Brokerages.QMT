using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.Qmt.Tests
{
    internal sealed class QmtFakeGatewayServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentBag<Task> _requestTasks = new ConcurrentBag<Task>();
        private readonly Task _acceptTask;
        private TcpClient? _tcpClient;
        private StreamWriter? _writer;

        public int Port { get; }
        public Func<QmtProtocolMessage, Task<QmtProtocolMessage?>> RequestHandler { get; set; }

        public QmtFakeGatewayServer(string accountId = "86033767", bool tradingEnabled = false)
        {
            RequestHandler = request => Task.FromResult<QmtProtocolMessage?>(
                CreateSuccessfulResponse(
                    request,
                    request.Operation == QmtProtocol.Operations.Hello
                        ? JObject.FromObject(new QmtHelloPayload
                        {
                            ServerName = "fake-qmt-gateway",
                            AccountId = accountId,
                            TradingEnabled = tradingEnabled
                        })
                        : new JObject()));

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptClientAsync(_cancellationTokenSource.Token);
        }

        public async Task SendEventAsync(string operation, object payload)
        {
            await WaitForWriterAsync().ConfigureAwait(false);
            await SendMessageAsync(new QmtProtocolMessage
            {
                MessageType = QmtProtocol.MessageTypes.Event,
                Operation = operation,
                Payload = JObject.FromObject(payload)
            }).ConfigureAwait(false);
        }

        public void CloseClientConnection()
        {
            _tcpClient?.Dispose();
        }

        public static QmtProtocolMessage CreateSuccessfulResponse(QmtProtocolMessage request, JObject? payload = null)
        {
            return new QmtProtocolMessage
            {
                MessageType = QmtProtocol.MessageTypes.Response,
                RequestId = request.RequestId,
                Operation = request.Operation,
                Success = true,
                Payload = payload ?? new JObject()
            };
        }

        public static QmtProtocolMessage CreateFailedResponse(
            QmtProtocolMessage request,
            string errorCode,
            string errorMessage)
        {
            return new QmtProtocolMessage
            {
                MessageType = QmtProtocol.MessageTypes.Response,
                RequestId = request.RequestId,
                Operation = request.Operation,
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                Payload = new JObject()
            };
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _tcpClient?.Dispose();
            _listener.Stop();
            _writer?.Dispose();
            _writeLock.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private async Task AcceptClientAsync(CancellationToken cancellationToken)
        {
            try
            {
                _tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var networkStream = _tcpClient.GetStream();
                using var reader = new StreamReader(networkStream, new UTF8Encoding(false), false, 4096, true);
                _writer = new StreamWriter(networkStream, new UTF8Encoding(false), 4096, true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                while (!cancellationToken.IsCancellationRequested)
                {
                    var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (requestLine == null)
                    {
                        return;
                    }

                    var request = JsonConvert.DeserializeObject<QmtProtocolMessage>(requestLine)
                        ?? throw new InvalidDataException("The client sent an empty JSON request.");
                    var requestTask = HandleRequestAsync(request);
                    _requestTasks.Add(requestTask);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task HandleRequestAsync(QmtProtocolMessage request)
        {
            var response = await RequestHandler(request).ConfigureAwait(false);
            if (response != null)
            {
                await SendMessageAsync(response).ConfigureAwait(false);
            }
        }

        private async Task SendMessageAsync(QmtProtocolMessage message)
        {
            var writer = _writer ?? throw new InvalidOperationException("No fake QMT Gateway client is connected.");
            await _writeLock.WaitAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(JsonConvert.SerializeObject(message, Formatting.None)).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task WaitForWriterAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (_writer == null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            if (_writer == null)
            {
                throw new TimeoutException("The fake QMT Gateway client did not connect.");
            }
        }
    }
}
