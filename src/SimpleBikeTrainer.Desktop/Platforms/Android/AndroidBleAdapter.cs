using Android.Bluetooth;
using AsyncAwaitBestPractices;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Runtime;
using Java.Util;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.Platforms;

internal sealed class AndroidBleAdapter : BleAdapter
{
    private static readonly string[] Permissions = [global::Android.Manifest.Permission.BluetoothScan, global::Android.Manifest.Permission.BluetoothConnect];

    // Asked for along with Bluetooth, so the ride's notification shows; a ride runs without it.
    private static readonly string[] OptionalPermissions =
        OperatingSystem.IsAndroidVersionAtLeast(33) ? [global::Android.Manifest.Permission.PostNotifications] : [];

    private readonly BluetoothAdapter _adapter;
    private readonly ScanListener _listener;
    private readonly Dictionary<string, AndroidPeripheral> _peripherals = new(StringComparer.OrdinalIgnoreCase);
    private BluetoothLeScanner? _scanner;

    private AndroidBleAdapter(BluetoothAdapter adapter)
    {
        _adapter = adapter;
        _listener = new ScanListener(this);
    }

    /// <summary>Asks for the Bluetooth permissions, and to turn Bluetooth on, the first time either is missing.</summary>
    public static async Task<BleAdapter> CreateAsync()
    {
        if (!await AndroidApp.RequestPermissionsAsync(Permissions, OptionalPermissions))
        {
            throw new InvalidOperationException(Strings.Android_BluetoothDenied);
        }

        var manager = (BluetoothManager?)AndroidApp.Context.GetSystemService(Context.BluetoothService);
        if (manager?.Adapter is not { } adapter)
        {
            throw new InvalidOperationException(CoreStrings.Ble_NoAdapter);
        }

        if (!await AndroidApp.RequestEnableAsync(BluetoothAdapter.ActionRequestEnable, () => adapter.IsEnabled))
        {
            throw new InvalidOperationException(Strings.Android_BluetoothOff);
        }

        return new AndroidBleAdapter(adapter);
    }

    public override Task StartScanAsync()
    {
        _scanner = _adapter.BluetoothLeScanner ?? throw new InvalidOperationException(Strings.Android_BluetoothOff);
        var settings = new ScanSettings.Builder().SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
        _scanner.StartScan(null, settings, _listener);
        return Task.CompletedTask;
    }

    public override Task StopScanAsync()
    {
        if (Interlocked.Exchange(ref _scanner, null) is { } scanner && _adapter.IsEnabled)
        {
            try
            {
                scanner.StopScan(_listener);
            }
            catch (Java.Lang.IllegalStateException)
            {
                // Bluetooth went off; the scan stopped with it.
            }
        }

        return Task.CompletedTask;
    }

    private void OnResult(ScanResult result)
    {
        if (result.Device is not { Address: { } address } device)
        {
            return;
        }

        var record = result.ScanRecord;
        var name = record?.DeviceName is { Length: > 0 } advertised ? advertised : device.Name;
        List<string> services = [.. record?.ServiceUuids?.Select(u => GattUuids.Normalize(u.Uuid?.ToString() ?? string.Empty)) ?? []];
        var manufacturerData = new Dictionary<ushort, byte[]>();
        if (record?.ManufacturerSpecificData is { } data)
        {
            for (var i = 0; i < data.Size(); i++)
            {
                var company = data.KeyAt(i);
                if (record.GetManufacturerSpecificData(company) is { } bytes)
                {
                    manufacturerData[(ushort)company] = bytes;
                }
            }
        }

        AndroidPeripheral peripheral;
        lock (_peripherals)
        {
            if (!_peripherals.TryGetValue(address, out var known))
            {
                known = _peripherals[address] = new AndroidPeripheral(device);
            }

            peripheral = known;
        }

        OnAdvertised(new BleAdvertisement(peripheral, address, name, services, manufacturerData, (short)result.Rssi));
    }

    private sealed class ScanListener(AndroidBleAdapter owner) : ScanCallback
    {
        public override void OnScanResult([GeneratedEnum] ScanCallbackType callbackType, ScanResult? result)
        {
            if (result is null)
            {
                return;
            }

            // An exception thrown back into Java would end the app.
            try
            {
                owner.OnResult(result);
            }
            catch (Exception e)
            {
                AndroidApp.Log(e, "Bluetooth scan result");
            }
        }

        public override void OnBatchScanResults(IList<ScanResult>? results)
        {
            foreach (var result in results ?? [])
            {
                OnScanResult(ScanCallbackType.AllMatches, result);
            }
        }

        public override void OnScanFailed([GeneratedEnum] ScanFailure errorCode) => AndroidApp.Log($"Bluetooth scan failed: {errorCode}");
    }
}

/// <summary>Android runs one GATT operation at a time, so each read, write and subscription waits its turn, then its callback.</summary>
internal sealed class AndroidPeripheral(BluetoothDevice device) : BlePeripheral
{
    private const int GattError = 133;

    private static readonly UUID ClientConfiguration = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;
    private static readonly byte[] EnableNotifications = [0x01, 0x00];
    private static readonly byte[] EnableIndications = [0x02, 0x00];
    private static readonly byte[] Disable = [0x00, 0x00];
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Action<byte[]>>> _subscribers = [];
    private BluetoothGatt? _gatt;
    private Listener? _listener;
    private TaskCompletionSource? _connecting;
    private TaskCompletionSource? _disconnecting;
    private TaskCompletionSource<byte[]?>? _pending;
    private Operation _pendingOperation;
    private bool _connected;

    public override async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ConnectOnceAsync(Remaining(deadline), cancellationToken);
                return;
            }
            catch (GattException e) when (e.Status == GattError && attempt < 3 && DateTime.UtcNow < deadline)
            {
                // Android's catch-all connection error, often gone on the next try.
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    public override async Task DisconnectAsync()
    {
        BluetoothGatt? gatt;
        TaskCompletionSource disconnecting;
        lock (_lock)
        {
            gatt = _gatt;
            _connected = false;
            disconnecting = _disconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (gatt is null)
        {
            return;
        }

        gatt.Disconnect();
        try
        {
            await disconnecting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Closing below ends the connection anyway.
        }

        lock (_lock)
        {
            if (_gatt == gatt)
            {
                _gatt = null;
                _listener = null;
            }

            _disconnecting = null;
        }

        Close(gatt);
    }

    public override Task<bool> HasServiceAsync(string service) => Task.FromResult(_gatt?.GetService(UUID.FromString(service)) is not null);

    public override Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic)
    {
        var found = _gatt?.GetService(UUID.FromString(service))?.GetCharacteristic(UUID.FromString(characteristic));
        return Task.FromResult<BleCharacteristic?>(found is null ? null : new AndroidCharacteristic(this, found));
    }

    internal async Task<byte[]> ReadAsync(BluetoothGattCharacteristic characteristic) =>
        await RunAsync(gatt => gatt.ReadCharacteristic(characteristic), Operation.Read) ?? [];

    internal Task WriteAsync(BluetoothGattCharacteristic characteristic, byte[] value, bool withResponse)
    {
        var type = withResponse ? GattWriteType.Default : GattWriteType.NoResponse;
        return RunAsync(
            gatt =>
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
                    return gatt.WriteCharacteristic(characteristic, value, (int)type) == (int)CurrentBluetoothStatusCodes.Success;
                }

                characteristic.WriteType = type;
                characteristic.SetValue(value);
                return gatt.WriteCharacteristic(characteristic);
            },
            Operation.Write);
    }

    internal async Task<IDisposable> SubscribeAsync(BluetoothGattCharacteristic characteristic, Action<byte[]> onValue)
    {
        var key = Key(characteristic);
        bool first;
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(key, out var handlers))
            {
                handlers = _subscribers[key] = [];
            }

            handlers.Add(onValue);
            first = handlers.Count == 1;
        }

        try
        {
            if (first)
            {
                await SetNotificationsAsync(characteristic, enable: true);
            }
        }
        catch
        {
            Unsubscribe(characteristic, key, onValue);
            throw;
        }

        return new Unsubscriber(() => Unsubscribe(characteristic, key, onValue));
    }

    private async Task ConnectOnceAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        // A reconnect starts from a new connection: Android's GATT objects do not survive a disconnect.
        if (Interlocked.Exchange(ref _gatt, null) is { } stale)
        {
            Close(stale);
        }

        var listener = new Listener(this);
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _listener = listener;
            _connecting = connecting;
            _subscribers.Clear();
        }

        var gatt = device.ConnectGatt(AndroidApp.Context, false, listener, BluetoothTransports.Le)
            ?? throw new InvalidOperationException(CoreStrings.Ble_Disconnected);
        lock (_lock)
        {
            _gatt = gatt;
        }

        try
        {
            await connecting.Task.WaitAsync(timeout, cancellationToken);

            // Before discovery: FTMS data over 20 bytes needs more than Android's default MTU, and a KICKR asked after discovery can drop the connection.
            try
            {
                await RunAsync(g => g.RequestMtu(247), Operation.Mtu, Remaining(deadline));
            }
            catch (GattException)
            {
            }

            await RunAsync(g => g.DiscoverServices(), Operation.Discovery, Remaining(deadline));

            // A shorter connection interval, for the Zwift Click's presses and the trainer's data.
            gatt.RequestConnectionPriority(GattConnectionPriority.High);
            lock (_lock)
            {
                _connected = true;
            }
        }
        catch
        {
            lock (_lock)
            {
                if (_gatt == gatt)
                {
                    _gatt = null;
                    _listener = null;
                }
            }

            Close(gatt);
            throw;
        }
    }

    private async Task<byte[]?> RunAsync(Func<BluetoothGatt, bool> start, Operation operation, TimeSpan? timeout = null)
    {
        await _turn.WaitAsync();
        try
        {
            var gatt = _gatt ?? throw new InvalidOperationException(CoreStrings.Ble_Disconnected);
            var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _pending = pending;
                _pendingOperation = operation;
            }

            if (!start(gatt))
            {
                throw new GattException(operation, -1);
            }

            return await pending.Task.WaitAsync(timeout ?? OperationTimeout);
        }
        finally
        {
            lock (_lock)
            {
                _pending = null;
            }

            _turn.Release();
        }
    }

    /// <summary>Turns notifications, or indications for a characteristic without them, on or off.</summary>
    private Task SetNotificationsAsync(BluetoothGattCharacteristic characteristic, bool enable)
    {
        var descriptor = characteristic.GetDescriptor(ClientConfiguration)
            ?? throw new InvalidOperationException($"{characteristic.Uuid} has no client configuration");
        var value = !enable ? Disable
            : characteristic.Properties.HasFlag(GattProperty.Notify) ? EnableNotifications
            : EnableIndications;
        return RunAsync(
            gatt =>
            {
                if (!gatt.SetCharacteristicNotification(characteristic, enable))
                {
                    return false;
                }

                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
                    return gatt.WriteDescriptor(descriptor, value) == (int)CurrentBluetoothStatusCodes.Success;
                }

                descriptor.SetValue(value);
                return gatt.WriteDescriptor(descriptor);
            },
            Operation.Subscribe);
    }

    private void Unsubscribe(BluetoothGattCharacteristic characteristic, string key, Action<byte[]> onValue)
    {
        bool last;
        lock (_lock)
        {
            last = _subscribers.TryGetValue(key, out var handlers) && handlers.Remove(onValue) && handlers.Count == 0;
            if (last)
            {
                _subscribers.Remove(key);
            }

            last &= _connected;
        }

        if (last)
        {
            // Best effort: the device may be going away.
            SetNotificationsAsync(characteristic, enable: false).SafeFireAndForget(e => AndroidApp.Log(e, "Bluetooth unsubscribe"));
        }
    }

    private static TimeSpan Remaining(DateTime deadline) => TimeSpan.FromTicks(Math.Max(0, (deadline - DateTime.UtcNow).Ticks));

    private static string Key(BluetoothGattCharacteristic c) => $"{c.Service?.Uuid}/{c.Uuid}/{c.InstanceId}";

    private static void Close(BluetoothGatt gatt)
    {
        try
        {
            gatt.Close();
        }
        catch (Java.Lang.Exception)
        {
            // Already closed.
        }
    }

    private void OnConnectionState(Listener listener, BluetoothGatt gatt, GattStatus status, ProfileState state)
    {
        TaskCompletionSource? connecting, disconnecting;
        TaskCompletionSource<byte[]?>? pending;
        bool dropped;
        lock (_lock)
        {
            if (listener != _listener)
            {
                return;
            }

            connecting = _connecting;
            if (state == ProfileState.Connected && status == GattStatus.Success)
            {
                _connecting = null;
                connecting?.TrySetResult();
                return;
            }

            if (state != ProfileState.Disconnected)
            {
                return;
            }

            _connecting = null;
            disconnecting = _disconnecting;
            _disconnecting = null;
            pending = _pending;
            dropped = _connected;
            _connected = false;
            if (_gatt == gatt)
            {
                _gatt = null;
            }

            _listener = null;
        }

        connecting?.TrySetException(new GattException(Operation.Connect, (int)status));
        pending?.TrySetException(new InvalidOperationException(CoreStrings.Ble_Disconnected));
        disconnecting?.TrySetResult();
        if (disconnecting is null)
        {
            Close(gatt);
        }

        if (dropped)
        {
            OnDisconnected();
        }
    }

    /// <summary>A callback arriving after its operation timed out is dropped, so it cannot end the next operation.</summary>
    private void OnDone(Listener listener, GattStatus status, byte[]? value, Operation operation)
    {
        TaskCompletionSource<byte[]?>? pending;
        lock (_lock)
        {
            pending = listener == _listener && _pendingOperation == operation ? _pending : null;
        }

        if (status == GattStatus.Success)
        {
            pending?.TrySetResult(value);
        }
        else
        {
            pending?.TrySetException(new GattException(operation, (int)status));
        }
    }

    private void OnChanged(Listener listener, BluetoothGattCharacteristic characteristic, byte[] value)
    {
        Action<byte[]>[] handlers;
        lock (_lock)
        {
            if (listener != _listener || !_subscribers.TryGetValue(Key(characteristic), out var list))
            {
                return;
            }

            handlers = [.. list];
        }

        foreach (var handler in handlers)
        {
            handler(value);
        }
    }

    private enum Operation
    {
        Connect,
        Discovery,
        Mtu,
        Read,
        Write,
        Subscribe,
    }

    /// <summary>A GATT operation Android refused or failed, with its status code; 133 is its catch-all error.</summary>
    private sealed class GattException(Operation operation, int status) : Exception($"Bluetooth {operation} failed (status {status})")
    {
        public int Status { get; } = status;
    }

    /// <summary>Android's callbacks for one connection, on Binder threads: an exception thrown back into Java would end the app.</summary>
    private sealed class Listener(AndroidPeripheral owner) : BluetoothGattCallback
    {
        public override void OnConnectionStateChange(BluetoothGatt? gatt, [GeneratedEnum] GattStatus status, [GeneratedEnum] ProfileState newState) =>
            Guard(() => owner.OnConnectionState(this, gatt!, status, newState));

        public override void OnServicesDiscovered(BluetoothGatt? gatt, [GeneratedEnum] GattStatus status) =>
            Guard(() => owner.OnDone(this, status, null, Operation.Discovery));

        public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, [GeneratedEnum] GattStatus status) =>
            Guard(() => owner.OnDone(this, status, null, Operation.Mtu));

        public override void OnCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value, [GeneratedEnum] GattStatus status) =>
            Guard(() => owner.OnDone(this, status, value, Operation.Read));

        public override void OnCharacteristicRead(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, [GeneratedEnum] GattStatus status)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var value = characteristic?.GetValue();
                Guard(() => owner.OnDone(this, status, value, Operation.Read));
            }
        }

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, [GeneratedEnum] GattStatus status) =>
            Guard(() => owner.OnDone(this, status, null, Operation.Write));

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, [GeneratedEnum] GattStatus status) =>
            Guard(() => owner.OnDone(this, status, null, Operation.Subscribe));

        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value) =>
            Guard(() => owner.OnChanged(this, characteristic, value));

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33) && characteristic?.GetValue() is { } value)
            {
                Guard(() => owner.OnChanged(this, characteristic, value));
            }
        }

        private static void Guard(Action callback)
        {
            try
            {
                callback();
            }
            catch (Exception e)
            {
                AndroidApp.Log(e, "Bluetooth callback");
            }
        }
    }
}

internal sealed class AndroidCharacteristic(AndroidPeripheral peripheral, BluetoothGattCharacteristic characteristic) : BleCharacteristic
{
    public override bool CanNotify => (characteristic.Properties & (GattProperty.Notify | GattProperty.Indicate)) != 0;

    public override bool CanWriteWithoutResponse => characteristic.Properties.HasFlag(GattProperty.WriteNoResponse);

    public override Task<byte[]> ReadAsync() => peripheral.ReadAsync(characteristic);

    public override Task WriteAsync(byte[] value, bool withResponse) => peripheral.WriteAsync(characteristic, value, withResponse);

    public override Task<IDisposable> SubscribeAsync(Action<byte[]> onValue) => peripheral.SubscribeAsync(characteristic, onValue);
}
