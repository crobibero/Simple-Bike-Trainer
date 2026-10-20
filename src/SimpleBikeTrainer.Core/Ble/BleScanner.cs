namespace SimpleBikeTrainer.Ble;

public sealed class BleScanner : IDeviceScanner
{
    // Zwift controller types in the first byte of the manufacturer data: Play (2, 3, 0x0E), Ride (7, 8), Click (9),
    // Click V2 (0x0A, 0x0B).
    private static readonly HashSet<byte> ZwiftControllerTypes = [0x02, 0x03, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0E];

    private readonly BleAdapter _adapter;
    private readonly Dictionary<string, DiscoveredDevice> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Heard> _heard = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    internal BleScanner(BleAdapter adapter)
    {
        _adapter = adapter;
        _adapter.Advertised += OnAdvertised;
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

    public static async Task<BleScanner> CreateAsync(string? adapterName = null) => new(await BleAdapter.OpenAsync(adapterName));

    public Task StartAsync(CancellationToken cancellationToken = default) => _adapter.StartScanAsync();

    public Task StopAsync() => _adapter.StopScanAsync();

    public async ValueTask DisposeAsync()
    {
        _adapter.Advertised -= OnAdvertised;
        await _adapter.DisposeAsync();
    }

    public static DeviceKind Classify(string? name, IEnumerable<string>? uuids, IReadOnlyDictionary<ushort, byte[]>? manufacturerData)
    {
        var ids = new HashSet<string>(uuids ?? [], StringComparer.OrdinalIgnoreCase);
        name ??= string.Empty;

        // The trainer checks come first: a KICKR can advertise more than FTMS.
        if (name.StartsWith("KICKR", StringComparison.OrdinalIgnoreCase) || ids.Contains(GattUuids.FitnessMachine))
        {
            return DeviceKind.Trainer;
        }

        if (manufacturerData is not null
            && manufacturerData.TryGetValue(GattUuids.ZwiftManufacturerId, out var bytes)
            && bytes is { Length: > 0 }
            && ZwiftControllerTypes.Contains(bytes[0]))
        {
            return DeviceKind.Shifter;
        }

        if (ids.Contains(GattUuids.ZwiftService) || ids.Contains(GattUuids.ZwiftRideService)
            || name.StartsWith("Zwift Click", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.Shifter;
        }

        // A trainer without FTMS that offers Cycling Power lands here too: it can still give power, if not take control.
        return ids.Contains(GattUuids.HeartRate) ? DeviceKind.HeartRate
            : ids.Contains(GattUuids.CyclingPower) ? DeviceKind.PowerMeter
            : ids.Contains(GattUuids.CyclingSpeedCadence) ? DeviceKind.CadenceSensor
            : DeviceKind.Other;
    }

    private void OnAdvertised(BleAdvertisement ad)
    {
        DiscoveredDevice device;
        lock (_lock)
        {
            // Windows and macOS report the name, services and manufacturer data in separate advertisements.
            if (!_heard.TryGetValue(ad.Address, out var heard))
            {
                heard = _heard[ad.Address] = new Heard();
            }

            heard.Name = ad.Name ?? heard.Name;
            heard.Services.UnionWith(ad.Services);
            foreach (var (company, data) in ad.ManufacturerData)
            {
                heard.ManufacturerData[company] = data;
            }

            var kind = Classify(heard.Name, heard.Services, heard.ManufacturerData);
            if (kind == DeviceKind.Other)
            {
                return;
            }

            device = new DiscoveredDevice(ad.Peripheral, DeviceId.Bluetooth(ad.Address), heard.Name ?? ad.Address, kind, ad.Rssi);
            _found[ad.Address] = device;
        }

        DeviceFound?.Invoke(device);
    }

    private sealed class Heard
    {
        public string? Name { get; set; }

        public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<ushort, byte[]> ManufacturerData { get; } = [];
    }
}
