using Linux.Bluetooth.Extensions;
using Linux.Bluetooth;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

public enum DeviceKind
{
    Other,
    Trainer,
    HeartRate,
    Shifter,
}

public sealed record DiscoveredDevice(Device Device, string Address, string Name, DeviceKind Kind, short? Rssi);

/// <summary>Finds trainers, heart rate monitors and Zwift controllers through BlueZ.</summary>
public sealed class BleScanner : IAsyncDisposable
{
    // Zwift controller types in the first byte of the manufacturer data: Play (2, 3, 0x0E), Ride (7, 8), Click (9),
    // Click V2 (0x0A, 0x0B).
    private static readonly HashSet<byte> ZwiftControllerTypes = [0x02, 0x03, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0E];

    private readonly Adapter _adapter;
    private readonly Dictionary<string, DiscoveredDevice> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private bool _discovering;

    private BleScanner(Adapter adapter)
    {
        _adapter = adapter;
    }

    public event Action<DiscoveredDevice>? DeviceFound;

    public IReadOnlyList<DiscoveredDevice> Found
    {
        get
        {
            lock (_lock)
            {
                return [.. _found.Values];
            }
        }
    }

    public static async Task<BleScanner> CreateAsync(string? adapterName = null)
    {
        Adapter? adapter;
        try
        {
            adapter = string.IsNullOrEmpty(adapterName)
                ? (await BlueZManager.GetAdaptersAsync()).FirstOrDefault()
                : await BlueZManager.GetAdapterAsync(adapterName);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(Loc.Format(CoreStrings.Ble_NoBlueZ, e.Message), e);
        }

        if (adapter is null)
        {
            throw new InvalidOperationException(CoreStrings.Ble_NoAdapter);
        }

        if (!await adapter.GetPoweredAsync())
        {
            await adapter.SetPoweredAsync(true);
        }

        return new BleScanner(adapter);
    }

    public async Task StartAsync()
    {
        _adapter.DeviceFound += OnDeviceFoundAsync;
        try
        {
            await _adapter.SetDiscoveryFilterAsync(new Dictionary<string, object> { ["Transport"] = "le" });
        }
        catch
        {
            // Older BlueZ versions reject some filters; an unfiltered scan still works.
        }

        await _adapter.StartDiscoveryAsync();
        _discovering = true;
    }

    public async Task StopAsync()
    {
        _adapter.DeviceFound -= OnDeviceFoundAsync;
        if (_discovering)
        {
            _discovering = false;
            try
            {
                await _adapter.StopDiscoveryAsync();
            }
            catch
            {
                // Discovery may already have stopped.
            }
        }
    }

    /// <summary>Waits until <paramref name="done"/> holds for the devices found so far, or the timeout passes.</summary>
    public async Task WaitForAsync(Func<IReadOnlyList<DiscoveredDevice>, bool> done, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !done(Found))
        {
            await Task.Delay(250, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    public static DeviceKind Classify(string? name, IEnumerable<string>? uuids, IDictionary<ushort, object>? manufacturerData)
    {
        var ids = new HashSet<string>(uuids ?? [], StringComparer.OrdinalIgnoreCase);
        name ??= string.Empty;

        // The trainer checks come first: a KICKR can advertise more than FTMS.
        if (name.StartsWith("KICKR", StringComparison.OrdinalIgnoreCase) || ids.Contains(GattUuids.FitnessMachine))
        {
            return DeviceKind.Trainer;
        }

        if (manufacturerData is not null
            && manufacturerData.TryGetValue(GattUuids.ZwiftManufacturerId, out var raw)
            && raw is byte[] { Length: > 0 } bytes
            && ZwiftControllerTypes.Contains(bytes[0]))
        {
            return DeviceKind.Shifter;
        }

        if (ids.Contains(GattUuids.ZwiftService) || ids.Contains(GattUuids.ZwiftRideService)
            || name.StartsWith("Zwift Click", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.Shifter;
        }

        return ids.Contains(GattUuids.HeartRate) ? DeviceKind.HeartRate : DeviceKind.Other;
    }

    private async Task OnDeviceFoundAsync(Adapter sender, DeviceFoundEventArgs e)
    {
        try
        {
            var p = await e.Device.GetPropertiesAsync();
            var address = p.Address ?? string.Empty;
            var name = !string.IsNullOrEmpty(p.Name) ? p.Name : !string.IsNullOrEmpty(p.Alias) ? p.Alias : address;
            var kind = Classify(name, p.UUIDs, p.ManufacturerData);
            if (kind == DeviceKind.Other)
            {
                return;
            }

            // BlueZ keeps devices it saw earlier; an RSSI means it is advertising now.
            short? rssi = p.Rssi == 0 ? null : p.Rssi;
            var device = new DiscoveredDevice(e.Device, address, name, kind, rssi);
            lock (_lock)
            {
                _found[address] = device;
            }

            DeviceFound?.Invoke(device);
        }
        catch
        {
            // The device vanished between the signal and the property read.
        }
    }
}
