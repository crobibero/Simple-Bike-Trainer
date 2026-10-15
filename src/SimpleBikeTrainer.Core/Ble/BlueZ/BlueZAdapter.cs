using Linux.Bluetooth.Extensions;
using Linux.Bluetooth;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble.BlueZ;

internal sealed class BlueZAdapter : BleAdapter
{
    private readonly Adapter _adapter;
    private bool _discovering;

    private BlueZAdapter(Adapter adapter)
    {
        _adapter = adapter;
    }

    public static async Task<BleAdapter> CreateAsync(string? name)
    {
        Adapter? adapter;
        try
        {
            adapter = string.IsNullOrEmpty(name)
                ? (await BlueZManager.GetAdaptersAsync()).FirstOrDefault()
                : await BlueZManager.GetAdapterAsync(name);
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

        return new BlueZAdapter(adapter);
    }

    public override async Task StartScanAsync()
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

    public override async Task StopScanAsync()
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

    private async Task OnDeviceFoundAsync(Adapter sender, DeviceFoundEventArgs e)
    {
        try
        {
            var p = await e.Device.GetPropertiesAsync();
            var address = p.Address ?? string.Empty;
            var name = !string.IsNullOrEmpty(p.Name) ? p.Name : !string.IsNullOrEmpty(p.Alias) ? p.Alias : null;
            var manufacturerData = new Dictionary<ushort, byte[]>();
            foreach (var (company, value) in p.ManufacturerData ?? new Dictionary<ushort, object>())
            {
                if (value is byte[] bytes)
                {
                    manufacturerData[company] = bytes;
                }
            }

            // BlueZ keeps devices it saw earlier; an RSSI means it is advertising now.
            short? rssi = p.Rssi == 0 ? null : p.Rssi;
            OnAdvertised(new BleAdvertisement(new BlueZPeripheral(e.Device), address, name, p.UUIDs ?? [], manufacturerData, rssi));
        }
        catch
        {
            // The device vanished between the signal and the property read.
        }
    }
}

internal sealed class BlueZPeripheral(Device device) : BlePeripheral
{
    private bool _watching;

    public override async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_watching)
        {
            _watching = true;
            device.Disconnected += OnDisconnectedAsync;
        }

        await device.ConnectAsync().WaitAsync(timeout, cancellationToken);
        await device.WaitForPropertyValueAsync("ServicesResolved", true, timeout).WaitAsync(cancellationToken);
    }

    public override Task DisconnectAsync() => device.DisconnectAsync();

    public override async Task<bool> HasServiceAsync(string service) => await device.GetServiceAsync(service) is not null;

    public override async Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic)
    {
        if (await device.GetServiceAsync(service) is not { } svc || await svc.GetCharacteristicAsync(characteristic) is not { } c)
        {
            return null;
        }

        var flags = (await c.GetAllAsync()).Flags ?? [];
        return new BlueZCharacteristic(c, flags);
    }

    private Task OnDisconnectedAsync(Device sender, BlueZEventArgs e)
    {
        OnDisconnected();
        return Task.CompletedTask;
    }
}

internal sealed class BlueZCharacteristic(GattCharacteristic characteristic, string[] flags) : BleCharacteristic
{
    public override bool CanNotify => flags.Contains("notify") || flags.Contains("indicate");

    public override bool CanWriteWithoutResponse => flags.Contains("write-without-response");

    public override Task<byte[]> ReadAsync() => characteristic.ReadValueAsync(new Dictionary<string, object>());

    public override Task WriteAsync(byte[] value, bool withResponse) =>
        characteristic.WriteValueAsync(value, new Dictionary<string, object> { ["type"] = withResponse ? "request" : "command" });

    /// <summary>Linux.Bluetooth's Value event starts notifications unawaited and prints failures to stderr, so this starts them itself.</summary>
    public override async Task<IDisposable> SubscribeAsync(Action<byte[]> onValue)
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
        try
        {
            await characteristic.StartNotifyAsync();
        }
        catch
        {
            watcher.Dispose();
            throw;
        }

        return watcher;
    }
}
