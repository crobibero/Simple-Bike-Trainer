using System.Net.Sockets;
using SimpleBikeTrainer.Ble;

namespace SimpleBikeTrainer.Dircon;

/// <summary>Direct Connect carries the same GATT services as Bluetooth, so <see cref="FtmsTrainer"/> drives it unchanged.</summary>
internal sealed class DirconPeripheral(string host, int port) : BlePeripheral
{
    public const int DefaultPort = 36866;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    // The trainer answers requests in order, so one goes out at a time and the next waits for its answer.
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, DirconCharacteristic>> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private Connection? _connection;
    private byte _sequence;

    public string Host { get; } = host;

    public int Port { get; } = port;

    public override async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(Host, Port, cts.Token);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"No answer from {Host}:{Port}");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var connection = new Connection(client);
        lock (_lock)
        {
            _connection = connection;
            _services.Clear();
            _subscriptions.Clear();
        }

        connection.Reader = Task.Run(() => ReadLoopAsync(connection), CancellationToken.None);
        try
        {
            var services = DirconMessage.ParseServices(await RequestAsync(DirconMessage.DiscoverServices, [], cts.Token));
            foreach (var service in services)
            {
                var payload = await RequestAsync(DirconMessage.DiscoverCharacteristics, DirconMessage.UuidPayload(service), cts.Token);
                var characteristics = DirconMessage.ParseCharacteristics(payload)
                    .ToDictionary(c => c.Uuid, c => new DirconCharacteristic(this, c.Uuid, c.Properties), StringComparer.OrdinalIgnoreCase);
                lock (_lock)
                {
                    _services[service] = characteristics;
                }
            }
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    public override async Task DisconnectAsync()
    {
        Connection? connection;
        lock (_lock)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection is not null)
        {
            connection.Close();
            if (connection.Reader is { } reader)
            {
                await reader;
            }
        }
    }

    public override Task<bool> HasServiceAsync(string service)
    {
        lock (_lock)
        {
            return Task.FromResult(_services.ContainsKey(GattUuids.Normalize(service)));
        }
    }

    public override Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic)
    {
        lock (_lock)
        {
            return Task.FromResult<BleCharacteristic?>(
                _services.TryGetValue(GattUuids.Normalize(service), out var characteristics)
                && characteristics.TryGetValue(GattUuids.Normalize(characteristic), out var found) ? found : null);
        }
    }

    internal async Task<byte[]> ReadAsync(string uuid) =>
        StripUuid(await RequestAsync(DirconMessage.ReadCharacteristic, DirconMessage.UuidPayload(uuid), CancellationToken.None));

    internal Task WriteAsync(string uuid, byte[] value) =>
        RequestAsync(DirconMessage.WriteCharacteristic, DirconMessage.UuidPayload(uuid, value), CancellationToken.None);

    internal async Task<IDisposable> SubscribeAsync(string uuid, Action<byte[]> onValue)
    {
        var subscription = new Subscription(onValue);
        Connection? connection;
        lock (_lock)
        {
            connection = _connection;
            _subscriptions[uuid] = subscription;
        }

        await RequestAsync(DirconMessage.EnableNotifications, DirconMessage.UuidPayload(uuid, 1), CancellationToken.None);
        return new Unsubscriber(() =>
        {
            lock (_lock)
            {
                // A reconnect starts a fresh table, and a later subscription to the same characteristic replaces this one.
                if (_connection != connection || !_subscriptions.TryGetValue(uuid, out var current) || current != subscription)
                {
                    return;
                }

                _subscriptions.Remove(uuid);
            }

            _ = RequestAsync(DirconMessage.EnableNotifications, DirconMessage.UuidPayload(uuid, 0), CancellationToken.None)
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        });
    }

    /// <summary>The value in a read response, which starts with the characteristic's UUID.</summary>
    private static byte[] StripUuid(byte[] payload) =>
        payload.Length >= DirconMessage.UuidLength ? payload[DirconMessage.UuidLength..] : payload;

    private async Task<byte[]> RequestAsync(byte type, byte[] payload, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken);
        Connection connection;
        try
        {
            lock (_lock)
            {
                connection = _connection ?? throw new InvalidOperationException($"{Host} is not connected");
            }

            var sequence = unchecked(++_sequence);
            var answer = new TaskCompletionSource<DirconMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Pending = new PendingRequest(sequence, answer);
            await connection.Stream.WriteAsync(new DirconMessage(type, sequence, DirconMessage.Success, payload).Encode(), cancellationToken);
            DirconMessage response;
            try
            {
                response = await answer.Task.WaitAsync(RequestTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                // An unanswered request means the link is gone: closing it starts the reconnect.
                connection.Close();
                throw new TimeoutException($"{Host} did not answer");
            }

            return response.Response == DirconMessage.Success && response.Type != DirconMessage.Error
                ? response.Payload
                : throw new InvalidOperationException($"{Host}: {DirconMessage.DescribeResponse(response.Response)}");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task ReadLoopAsync(Connection connection)
    {
        var header = new byte[DirconMessage.HeaderLength];
        try
        {
            while (true)
            {
                await connection.Stream.ReadExactlyAsync(header);
                var payload = new byte[DirconMessage.PayloadLength(header)];
                await connection.Stream.ReadExactlyAsync(payload);
                var message = DirconMessage.Decode(header, payload);
                if (message.Type == DirconMessage.Notification)
                {
                    OnNotification(connection, message.Payload);
                }
                else if (connection.Pending is { } pending && pending.Sequence == message.Sequence)
                {
                    connection.Pending = null;
                    pending.Answer.TrySetResult(message);
                }
            }
        }
        catch (Exception e)
        {
            connection.Pending?.Answer.TrySetException(new IOException($"{Host} closed the connection", e));
        }

        bool dropped;
        lock (_lock)
        {
            // Only a connection that ends on its own is a drop; DisconnectAsync clears it first.
            dropped = _connection == connection;
            if (dropped)
            {
                _connection = null;
            }
        }

        connection.Close();
        if (dropped)
        {
            OnDisconnected();
        }
    }

    private void OnNotification(Connection connection, byte[] payload)
    {
        if (payload.Length < DirconMessage.UuidLength)
        {
            return;
        }

        Subscription? subscription;
        lock (_lock)
        {
            if (_connection != connection)
            {
                return;
            }

            _subscriptions.TryGetValue(DirconMessage.ReadUuid(payload), out subscription);
        }

        subscription?.OnValue(payload[DirconMessage.UuidLength..]);
    }

    private sealed class Subscription(Action<byte[]> onValue)
    {
        public Action<byte[]> OnValue { get; } = onValue;
    }

    private sealed record PendingRequest(byte Sequence, TaskCompletionSource<DirconMessage> Answer);

    private sealed class Connection(TcpClient client)
    {
        private volatile PendingRequest? _pending;

        public NetworkStream Stream { get; } = client.GetStream();

        public Task? Reader { get; set; }

        public PendingRequest? Pending
        {
            get => _pending;
            set => _pending = value;
        }

        /// <summary>Disposing the socket ends a pending read with an exception, which ends the read loop.</summary>
        public void Close() => client.Dispose();
    }
}

internal sealed class DirconCharacteristic(DirconPeripheral peripheral, string uuid, byte properties) : BleCharacteristic
{
    public string Uuid { get; } = uuid;

    public override bool CanNotify => (properties & DirconMessage.PropertyNotify) != 0;

    // Every Direct Connect write is answered.
    public override bool CanWriteWithoutResponse => false;

    public override Task<byte[]> ReadAsync() => peripheral.ReadAsync(Uuid);

    public override Task WriteAsync(byte[] value, bool withResponse) => peripheral.WriteAsync(Uuid, value);

    public override Task<IDisposable> SubscribeAsync(Action<byte[]> onValue) => peripheral.SubscribeAsync(Uuid, onValue);
}
