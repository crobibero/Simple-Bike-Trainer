using Linux.Bluetooth.Extensions;
using Linux.Bluetooth;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

/// <summary>
/// A connection to one BLE device that reconnects itself. Subclasses set up their characteristics in
/// <see cref="SetupAsync"/>, which runs after every connect.
/// </summary>
public abstract class BleConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _watchers = [];
    private Task? _reconnect;
    private volatile bool _connected;
    private volatile int _battery = -1;

    protected BleConnection(Device device, string name, string address)
    {
        Device = device;
        Name = name;
        Address = address;
    }

    public string Name { get; }

    public string Address { get; }

    public bool IsConnected => _connected;

    /// <summary>Connecting, disconnected, or why the last reconnect failed; null while connected.</summary>
    public string? Status { get; private set; } = CoreStrings.Ble_Connecting;

    /// <summary>The level from the standard Battery Service, when the device has one.</summary>
    public virtual int? Battery => _battery < 0 ? null : _battery;

    protected Device Device { get; }

    protected CancellationToken Stopping => _cts.Token;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        Device.Disconnected += OnDisconnectedAsync;
        await ConnectOnceAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        Device.Disconnected -= OnDisconnectedAsync;
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

        try
        {
            await OnStoppingAsync();
        }
        catch
        {
            // Best effort: the device may already be gone.
        }

        DisposeWatchers();
        try
        {
            await Device.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Already disconnected.
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    protected abstract Task SetupAsync(CancellationToken cancellationToken);

    /// <summary>Runs before disconnecting, e.g. to hand trainer control back.</summary>
    protected virtual Task OnStoppingAsync() => Task.CompletedTask;

    protected async Task<GattCharacteristic> GetCharacteristicAsync(string service, string characteristic)
    {
        var svc = await Device.GetServiceAsync(service)
            ?? throw new InvalidOperationException($"{Name} has no service {service}");
        return await svc.GetCharacteristicAsync(characteristic)
            ?? throw new InvalidOperationException($"{Name} has no characteristic {characteristic}");
    }

    protected async Task<bool> HasServiceAsync(string service)
    {
        try
        {
            return await Device.GetServiceAsync(service) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Subscribes to notifications or indications. Linux.Bluetooth's Value event starts notifications without
    /// awaiting them and prints failures to stderr, so this watches the property and starts them itself.
    /// </summary>
    protected async Task SubscribeAsync(GattCharacteristic characteristic, Action<byte[]> onValue)
    {
        var watcher = await characteristic.WatchPropertiesAsync(changes =>
        {
            foreach (var (key, value) in changes.Changed)
            {
                if (key == "Value" && value is byte[] bytes)
                {
                    onValue(bytes);
                }
            }
        });
        _watchers.Add(watcher);
        await characteristic.StartNotifyAsync();
    }

    protected static Task WriteAsync(GattCharacteristic characteristic, byte[] value, bool withResponse) =>
        characteristic.WriteValueAsync(value, new Dictionary<string, object> { ["type"] = withResponse ? "request" : "command" });

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        Status = CoreStrings.Ble_Connecting;
        DisposeWatchers();
        await Device.ConnectAsync().WaitAsync(ConnectTimeout, cancellationToken);
        await Device.WaitForPropertyValueAsync("ServicesResolved", true, ConnectTimeout).WaitAsync(cancellationToken);
        await SetupAsync(cancellationToken);
        await WatchBatteryAsync();
        _connected = true;
        Status = null;
    }

    /// <summary>Reads the Battery Service's level, and follows it when the device notifies changes.</summary>
    private async Task WatchBatteryAsync()
    {
        try
        {
            if (!await HasServiceAsync(GattUuids.Battery))
            {
                return;
            }

            var level = await GetCharacteristicAsync(GattUuids.Battery, GattUuids.BatteryLevel);
            OnBatteryLevel(await level.ReadValueAsync(new Dictionary<string, object>()));
            if ((await level.GetAllAsync()).Flags.Contains("notify"))
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
            _battery = Math.Min((int)level, 100);
        }
    }

    private Task OnDisconnectedAsync(Device sender, BlueZEventArgs e)
    {
        _connected = false;
        Status = CoreStrings.Ble_Disconnected;
        if (!_cts.IsCancellationRequested && _reconnect is not { IsCompleted: false })
        {
            _reconnect = Task.Run(ReconnectLoopAsync);
        }

        return Task.CompletedTask;
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
                Status = Loc.Format(CoreStrings.Ble_Reconnecting, e.Message);
            }
        }
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();
    }
}
