using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

/// <summary>
/// A connection to one BLE device that reconnects itself. Subclasses set up their characteristics in
/// <see cref="SetupAsync"/>, which runs after every connect.
/// </summary>
public abstract class BleConnection : DeviceConnection
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _watchers = [];
    private Task? _reconnect;
    private volatile bool _connected;
    private volatile string? _status = CoreStrings.Ble_Connecting;

    protected BleConnection(BlePeripheral peripheral, string name, DeviceId id)
        : base(name, id)
    {
        Peripheral = peripheral;
    }

    public override bool IsConnected => _connected;

    /// <summary>Connecting, disconnected, or why the last reconnect failed; null while connected.</summary>
    public override string? Status => _status;

    protected BlePeripheral Peripheral { get; }

    protected CancellationToken Stopping => _cts.Token;

    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        Peripheral.Disconnected += OnDisconnected;
        await ConnectOnceAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        Peripheral.Disconnected -= OnDisconnected;
        if (_reconnect is { } reconnect)
        {
            // A reconnect part way through could subscribe after the watchers below are gone, so it stops first.
            try
            {
                await reconnect.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }
        }

        await TryStopAsync();
        DisposeWatchers();
        try
        {
            await Peripheral.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Already disconnected.
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    protected abstract Task SetupAsync(CancellationToken cancellationToken);

    protected async Task<BleCharacteristic> GetCharacteristicAsync(string service, string characteristic)
    {
        if (!await Peripheral.HasServiceAsync(service))
        {
            throw new InvalidOperationException($"{Name} has no service {service}");
        }

        return await Peripheral.GetCharacteristicAsync(service, characteristic)
            ?? throw new InvalidOperationException($"{Name} has no characteristic {characteristic}");
    }

    protected async Task<bool> HasServiceAsync(string service)
    {
        try
        {
            return await Peripheral.HasServiceAsync(service);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Subscribes to notifications or indications until the next reconnect or dispose.</summary>
    protected async Task SubscribeAsync(BleCharacteristic characteristic, Action<byte[]> onValue)
    {
        var subscription = await characteristic.SubscribeAsync(onValue);
        lock (_watchers)
        {
            _watchers.Add(subscription);
        }
    }

    protected static Task WriteAsync(BleCharacteristic characteristic, byte[] value, bool withResponse) =>
        characteristic.WriteAsync(value, withResponse);

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        _status = CoreStrings.Ble_Connecting;
        DisposeWatchers();
        await Peripheral.ConnectAsync(ConnectTimeout, cancellationToken);
        await SetupAsync(cancellationToken);
        await WatchBatteryAsync();
        _connected = true;
        _status = null;
    }

    private async Task WatchBatteryAsync()
    {
        try
        {
            if (!await HasServiceAsync(GattUuids.Battery))
            {
                return;
            }

            var level = await GetCharacteristicAsync(GattUuids.Battery, GattUuids.BatteryLevel);
            OnBatteryLevel(await level.ReadAsync());
            if (level.CanNotify)
            {
                await SubscribeAsync(level, OnBatteryLevel);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The battery is extra: a device that will not report it still rides.
        }
    }

    private void OnBatteryLevel(byte[] value)
    {
        if (value is [var level, ..])
        {
            SetBattery(level);
        }
    }

    private void OnDisconnected()
    {
        _connected = false;
        _status = CoreStrings.Ble_Disconnected;
        if (!_cts.IsCancellationRequested && _reconnect is not { IsCompleted: false })
        {
            _reconnect = Task.Run(ReconnectLoopAsync);
        }
    }

    private async Task ReconnectLoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_connected)
        {
            try
            {
                await Task.Delay(RetryDelay, _cts.Token);
                await ConnectOnceAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _status = Loc.Format(CoreStrings.Ble_Reconnecting, e.Message);
            }
        }
    }

    private void DisposeWatchers()
    {
        IDisposable[] watchers;
        lock (_watchers)
        {
            watchers = [.. _watchers];
            _watchers.Clear();
        }

        foreach (var w in watchers)
        {
            w.Dispose();
        }
    }
}
