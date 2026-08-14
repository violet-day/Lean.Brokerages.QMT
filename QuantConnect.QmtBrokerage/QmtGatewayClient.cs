using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Qmt
{
    public interface IQmtGatewayClient : IDisposable
    {
        bool IsConnected { get; }
        QmtHelloPayload? ServerInformation { get; }
        event EventHandler<QmtGatewayMessageEventArgs>? EventReceived;
        event EventHandler<QmtGatewayDisconnectedEventArgs>? Disconnected;
        void Connect();
        Task ConnectAsync(CancellationToken cancellationToken = default);
        void Disconnect();
        Task<QmtProtocolMessage> SendRequestAsync(
            string operation,
            object? payload = null,
            CancellationToken cancellationToken = default);
    }

    public sealed class QmtGatewayMessageEventArgs : EventArgs
    {
        public QmtProtocolMessage Message { get; }

        public QmtGatewayMessageEventArgs(QmtProtocolMessage message)
        {
            Message = message;
        }
    }

    public sealed class QmtGatewayDisconnectedEventArgs : EventArgs
    {
        public Exception? Exception { get; }

        public QmtGatewayDisconnectedEventArgs(Exception? exception)
        {
            Exception = exception;
        }
    }

    /// <summary>
    /// Persistent NDJSON-over-TCP client for the QMT Python Gateway.
    /// </summary>
    public sealed class QmtGatewayClient : IQmtGatewayClient
    {
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings();

        private readonly string _host;
        private readonly int _port;
        private readonly string _expectedAccountId;
        private readonly TimeSpan _requestTimeout;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests =
            new ConcurrentDictionary<string, PendingRequest>();

        private TcpClient? _tcpClient;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _readerCancellationTokenSource;
        private int _isConnected;
        private int _isDisposed;
        private int _disconnectNotificationSent;

        public bool IsConnected => Volatile.Read(ref _isConnected) == 1;
        public QmtHelloPayload? ServerInformation { get; private set; }

        public event EventHandler<QmtGatewayMessageEventArgs>? EventReceived;
        public event EventHandler<QmtGatewayDisconnectedEventArgs>? Disconnected;

        public QmtGatewayClient(
            string host,
            int port,
            string expectedAccountId,
            TimeSpan? requestTimeout = null)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("A QMT Gateway host is required.", nameof(host));
            }

            if (port < 1 || port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "The QMT Gateway port must be between 1 and 65535.");
            }

            if (string.IsNullOrWhiteSpace(expectedAccountId))
            {
                throw new ArgumentException("An expected QMT account ID is required.", nameof(expectedAccountId));
            }

            _host = host;
            _port = port;
            _expectedAccountId = expectedAccountId;
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
            if (_requestTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(requestTimeout), "The request timeout must be positive.");
            }
        }

        public void Connect()
        {
            ConnectAsync().GetAwaiter().GetResult();
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsConnected)
                {
                    return;
                }

                Log.Trace($"QmtGatewayClient.Connect(): stage=tcp-connect status=start host={_host} port={_port}");
                CloseTransport();
                Interlocked.Exchange(ref _disconnectNotificationSent, 0);

                var tcpClient = new TcpClient();
                try
                {
                    await tcpClient.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
                    var networkStream = tcpClient.GetStream();
                    _tcpClient = tcpClient;
                    _reader = new StreamReader(networkStream, new UTF8Encoding(false), false, 4096, true);
                    _writer = new StreamWriter(networkStream, new UTF8Encoding(false), 4096, true)
                    {
                        AutoFlush = true,
                        NewLine = "\n"
                    };
                    var readerCancellationTokenSource = new CancellationTokenSource();
                    _readerCancellationTokenSource = readerCancellationTokenSource;
                    _ = ReadMessagesAsync(readerCancellationTokenSource, readerCancellationTokenSource.Token);

                    var helloResponse = await SendRequestAsync(
                        QmtProtocol.Operations.Hello,
                        new QmtHelloRequest { AccountId = _expectedAccountId },
                        cancellationToken).ConfigureAwait(false);
                    if (helloResponse.Payload.Property("account_id", StringComparison.Ordinal) == null ||
                        helloResponse.Payload.Property("trading_enabled", StringComparison.Ordinal) == null)
                    {
                        throw new QmtGatewayProtocolException(
                            "The QMT Gateway hello response must contain account_id and trading_enabled.");
                    }

                    var serverInformation = helloResponse.ToPayload<QmtHelloPayload>();
                    if (!string.Equals(serverInformation.AccountId, _expectedAccountId, StringComparison.Ordinal))
                    {
                        throw new QmtGatewayProtocolException(
                            $"QMT Gateway account mismatch. Expected '{_expectedAccountId}', received '{serverInformation.AccountId}'.");
                    }

                    ServerInformation = serverInformation;
                    Interlocked.Exchange(ref _isConnected, 1);
                    Log.Trace(
                        $"QmtGatewayClient.Connect(): stage=hello status=ok server={serverInformation.ServerName} " +
                        $"account_id={serverInformation.AccountId} trading_enabled={serverInformation.TradingEnabled}");
                }
                catch
                {
                    tcpClient.Dispose();
                    CloseTransport();
                    throw;
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task<QmtProtocolMessage> SendRequestAsync(
            string operation,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(operation))
            {
                throw new ArgumentException("A QMT Gateway operation is required.", nameof(operation));
            }

            var writer = _writer;
            if (writer == null)
            {
                throw new QmtGatewayException("The QMT Gateway transport is not connected.");
            }

            var requestId = Guid.NewGuid().ToString("N");
            var request = QmtProtocolMessage.CreateRequest(requestId, operation, payload);
            var pendingRequest = new PendingRequest();
            if (!_pendingRequests.TryAdd(requestId, pendingRequest))
            {
                throw new QmtGatewayException($"Could not register QMT Gateway request '{requestId}'.");
            }

            try
            {
                var requestJson = JsonConvert.SerializeObject(request, Formatting.None, SerializerSettings);
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Log.Trace($"QmtGatewayClient.SendRequestAsync(): stage=send operation={operation} request_id={requestId}");
                    await writer.WriteLineAsync(requestJson).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }

                var timeoutTask = Task.Delay(_requestTimeout, cancellationToken);
                var completedTask = await Task.WhenAny(pendingRequest.Completion.Task, timeoutTask).ConfigureAwait(false);
                if (completedTask != pendingRequest.Completion.Task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new QmtGatewayTimeoutException(operation, _requestTimeout);
                }

                var response = await pendingRequest.Completion.Task.ConfigureAwait(false);
                if (!string.Equals(response.Operation, operation, StringComparison.Ordinal))
                {
                    throw new QmtGatewayProtocolException(
                        $"QMT Gateway response operation mismatch for request '{requestId}'. Expected '{operation}', received '{response.Operation}'.");
                }

                if (response.Success != true)
                {
                    throw new QmtGatewayRequestException(operation, response.ErrorCode, response.ErrorMessage);
                }

                Log.Trace($"QmtGatewayClient.SendRequestAsync(): stage=response status=ok operation={operation} request_id={requestId}");
                return response;
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        public void Disconnect()
        {
            Interlocked.Exchange(ref _disconnectNotificationSent, 1);
            Interlocked.Exchange(ref _isConnected, 0);
            ServerInformation = null;
            CloseTransport();
            FailPendingRequests(new QmtGatewayException("The QMT Gateway client disconnected."));
            Log.Trace("QmtGatewayClient.Disconnect(): status=ok");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            {
                return;
            }

            Disconnect();
            _connectionLock.Dispose();
            _writeLock.Dispose();
        }

        private async Task ReadMessagesAsync(
            CancellationTokenSource readerCancellationTokenSource,
            CancellationToken cancellationToken)
        {
            Exception? disconnectException = null;
            try
            {
                var reader = _reader ?? throw new QmtGatewayException("The QMT Gateway reader was not initialized.");
                while (!cancellationToken.IsCancellationRequested)
                {
                    var messageLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (messageLine == null)
                    {
                        throw new IOException("The QMT Gateway closed the TCP connection.");
                    }

                    QmtProtocolMessage message;
                    try
                    {
                        message = JsonConvert.DeserializeObject<QmtProtocolMessage>(messageLine, SerializerSettings)
                            ?? throw new QmtGatewayProtocolException("The QMT Gateway sent an empty JSON message.");
                        ValidateMessage(message);
                    }
                    catch (Exception exception) when (exception is JsonException || exception is QmtGatewayProtocolException)
                    {
                        Log.Error(exception, "QmtGatewayClient.ReadMessagesAsync(): stage=parse status=failed");
                        continue;
                    }

                    if (message.MessageType == QmtProtocol.MessageTypes.Response)
                    {
                        if (message.RequestId != null && _pendingRequests.TryGetValue(message.RequestId, out var pendingRequest))
                        {
                            pendingRequest.Completion.TrySetResult(message);
                        }
                        else
                        {
                            Log.Error(
                                $"QmtGatewayClient.ReadMessagesAsync(): stage=dispatch status=unknown-request request_id={message.RequestId}");
                        }

                        continue;
                    }

                    try
                    {
                        EventReceived?.Invoke(this, new QmtGatewayMessageEventArgs(message));
                    }
                    catch (Exception exception)
                    {
                        Log.Error(exception, $"QmtGatewayClient.ReadMessagesAsync(): stage=event-handler operation={message.Operation}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                disconnectException = new QmtGatewayException("The QMT Gateway connection was lost.", exception);
                Log.Error(exception, "QmtGatewayClient.ReadMessagesAsync(): stage=read status=disconnected");
            }
            finally
            {
                if (ReferenceEquals(_readerCancellationTokenSource, readerCancellationTokenSource))
                {
                    Interlocked.Exchange(ref _isConnected, 0);
                    FailPendingRequests(disconnectException ?? new QmtGatewayException("The QMT Gateway reader stopped."));
                    NotifyDisconnected(disconnectException);
                }
            }
        }

        private static void ValidateMessage(QmtProtocolMessage message)
        {
            if (message.ProtocolVersion != QmtProtocol.Version)
            {
                throw new QmtGatewayProtocolException(
                    $"Unsupported QMT Gateway protocol version '{message.ProtocolVersion}'. Expected '{QmtProtocol.Version}'.");
            }

            if (message.MessageType != QmtProtocol.MessageTypes.Response &&
                message.MessageType != QmtProtocol.MessageTypes.Event)
            {
                throw new QmtGatewayProtocolException($"Unexpected QMT Gateway message type '{message.MessageType}'.");
            }

            if (string.IsNullOrWhiteSpace(message.Operation))
            {
                throw new QmtGatewayProtocolException("The QMT Gateway message has no operation.");
            }

            message.Payload ??= new Newtonsoft.Json.Linq.JObject();

            if (message.MessageType == QmtProtocol.MessageTypes.Response && string.IsNullOrWhiteSpace(message.RequestId))
            {
                throw new QmtGatewayProtocolException("The QMT Gateway response has no request ID.");
            }
        }

        private void FailPendingRequests(Exception exception)
        {
            foreach (var pendingRequestPair in _pendingRequests)
            {
                pendingRequestPair.Value.Completion.TrySetException(exception);
            }
        }

        private void NotifyDisconnected(Exception? exception)
        {
            if (Interlocked.Exchange(ref _disconnectNotificationSent, 1) == 1)
            {
                return;
            }

            try
            {
                Disconnected?.Invoke(this, new QmtGatewayDisconnectedEventArgs(exception));
            }
            catch (Exception handlerException)
            {
                Log.Error(handlerException, "QmtGatewayClient.NotifyDisconnected(): stage=event-handler");
            }
        }

        private void CloseTransport()
        {
            _readerCancellationTokenSource?.Cancel();
            _tcpClient?.Dispose();
            _reader?.Dispose();
            _writer?.Dispose();
            _readerCancellationTokenSource?.Dispose();
            _tcpClient = null;
            _reader = null;
            _writer = null;
            _readerCancellationTokenSource = null;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _isDisposed) == 1)
            {
                throw new ObjectDisposedException(nameof(QmtGatewayClient));
            }
        }

        private sealed class PendingRequest
        {
            public TaskCompletionSource<QmtProtocolMessage> Completion { get; } =
                new TaskCompletionSource<QmtProtocolMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
