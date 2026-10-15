using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

public sealed record BleAdvertisement(
    BlePeripheral Peripheral,
    string Address,
    string? Name,
    IReadOnlyCollection<string> Services,
    IReadOnlyDictionary<ushort, byte[]> ManufacturerData,
    short? Rssi);

public abstract class BleAdapter : IAsyncDisposable
{
    public event Action<BleAdvertisement>? Advertised;

    /// <summary>Opens the radio on a system this library has no Bluetooth code for: the Android app sets it at startup.</summary>
    internal static Func<string?, Task<BleAdapter>>? PlatformOpen { get; set; }

    /// <summary><paramref name="name"/> picks a BlueZ adapter such as "hci0"; other systems have one.</summary>
    public static async Task<BleAdapter> OpenAsync(string? name)
    {
        if (PlatformOpen is { } open)
        {
            return await open(name);
        }

        if (OperatingSystem.IsLinux())
        {
            return await BlueZ.BlueZAdapter.CreateAsync(name);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await CoreBluetooth.CoreBluetoothAdapter.CreateAsync();
        }

#if WINDOWS
        return await WinRt.WinRtAdapter.CreateAsync();
#else
        throw new PlatformNotSupportedException(CoreStrings.Ble_Unsupported);
#endif
    }

    public abstract Task StartScanAsync();

    public abstract Task StopScanAsync();

    public virtual async ValueTask DisposeAsync()
    {
        await StopScanAsync();
        GC.SuppressFinalize(this);
    }

    protected void OnAdvertised(BleAdvertisement advertisement) => Advertised?.Invoke(advertisement);
}

public abstract class BlePeripheral : IDeviceLink
{
    /// <summary>Raised when an established connection drops.</summary>
    public event Action? Disconnected;

    /// <summary>Connects and discovers the device's services.</summary>
    public abstract Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken);

    public abstract Task DisconnectAsync();

    public abstract Task<bool> HasServiceAsync(string service);

    /// <summary>A characteristic of a connected device, or null when the device does not have it.</summary>
    public abstract Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic);

    public IConnectableDevice Open(DiscoveredDevice found) => found.Kind switch
    {
        DeviceKind.Trainer => new FtmsTrainer(this, found.Name, found.Id),
        DeviceKind.HeartRate => new BleHeartRateMonitor(this, found.Name, found.Id),
        DeviceKind.Shifter => new ZwiftClick(this, found.Name, found.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(found)),
    };

    protected void OnDisconnected() => Disconnected?.Invoke();
}

public abstract class BleCharacteristic
{
    /// <summary>The characteristic sends notifications or indications.</summary>
    public abstract bool CanNotify { get; }

    public abstract bool CanWriteWithoutResponse { get; }

    public abstract Task<byte[]> ReadAsync();

    public abstract Task WriteAsync(byte[] value, bool withResponse);

    /// <summary>Turns on notifications or indications. Disposing the result stops <paramref name="onValue"/> being called.</summary>
    public abstract Task<IDisposable> SubscribeAsync(Action<byte[]> onValue);
}

/// <summary>Runs an action once, when disposed.</summary>
internal sealed class Unsubscriber(Action unsubscribe) : IDisposable
{
    private Action? _unsubscribe = unsubscribe;

    public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}
