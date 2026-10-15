#if WINDOWS
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using SimpleBikeTrainer.Resources;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace SimpleBikeTrainer.Ble.WinRt;

internal sealed class WinRtAdapter : BleAdapter
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    private readonly Dictionary<ulong, WinRtPeripheral> _peripherals = [];

    private WinRtAdapter()
    {
    }

    public static async Task<BleAdapter> CreateAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsLowEnergySupported)
        {
            throw new InvalidOperationException(CoreStrings.Ble_NoAdapter);
        }

        if (await adapter.GetRadioAsync() is { State: not RadioState.On })
        {
            throw new InvalidOperationException(CoreStrings.Ble_RadioOff);
        }

        return new WinRtAdapter();
    }

    public override Task StartScanAsync()
    {
        _watcher.Received += OnReceived;
        _watcher.Start();
        return Task.CompletedTask;
    }

    public override Task StopScanAsync()
    {
        _watcher.Received -= OnReceived;
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            _watcher.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>A Bluetooth address as the other systems print it, e.g. "CE:D1:96:5E:7A:BB".</summary>
    internal static string FormatAddress(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Reverse().Select(i => ((byte)(address >> (i * 8))).ToString("X2", CultureInfo.InvariantCulture)));

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var ad = args.Advertisement;
        var manufacturerData = new Dictionary<ushort, byte[]>();
        foreach (var data in ad.ManufacturerData)
        {
            manufacturerData[data.CompanyId] = data.Data.ToArray();
        }

        WinRtPeripheral peripheral;
        lock (_peripherals)
        {
            if (!_peripherals.TryGetValue(args.BluetoothAddress, out peripheral!))
            {
                peripheral = _peripherals[args.BluetoothAddress] = new WinRtPeripheral(args.BluetoothAddress);
            }
        }

        OnAdvertised(new BleAdvertisement(
            peripheral,
            FormatAddress(args.BluetoothAddress),
            string.IsNullOrEmpty(ad.LocalName) ? null : ad.LocalName,
            [.. ad.ServiceUuids.Select(g => g.ToString("D"))],
            manufacturerData,
            args.RawSignalStrengthInDBm));
    }
}

/// <summary>Windows connects when the services are read, and keeps the link while a GATT session maintains it and the objects live.</summary>
internal sealed class WinRtPeripheral(ulong address) : BlePeripheral
{
    private readonly Dictionary<string, GattDeviceService> _services = new(StringComparer.OrdinalIgnoreCase);
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private bool _connected;

    public override async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Close();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(timer.Token)
                ?? throw new InvalidOperationException(Loc.Format(CoreStrings.Ble_NotFound, WinRtAdapter.FormatAddress(address)));
            _device = device;
            _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(timer.Token);
            _session.MaintainConnection = true;

            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(timer.Token);
            if (result.Status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException(Loc.Format(CoreStrings.Ble_RequestFailed, Describe(result.Status, result.ProtocolError)));
            }

            lock (_services)
            {
                foreach (var service in result.Services)
                {
                    _services[service.Uuid.ToString("D")] = service;
                }
            }

            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            _connected = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Close();
            throw new TimeoutException();
        }
        catch
        {
            Close();
            throw;
        }
    }

    public override Task DisconnectAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    public override Task<bool> HasServiceAsync(string service)
    {
        lock (_services)
        {
            return Task.FromResult(_services.ContainsKey(service));
        }
    }

    public override async Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic)
    {
        GattDeviceService? svc;
        lock (_services)
        {
            _services.TryGetValue(service, out svc);
        }

        if (svc is null)
        {
            return null;
        }

        var result = await svc.GetCharacteristicsForUuidAsync(Guid.Parse(characteristic), BluetoothCacheMode.Uncached);
        return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0
            ? new WinRtCharacteristic(result.Characteristics[0])
            : null;
    }

    internal static string Describe(GattCommunicationStatus status, byte? protocolError) =>
        protocolError is { } code ? $"{status} 0x{code:x2}" : status.ToString();

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _connected)
        {
            _connected = false;
            OnDisconnected();
        }
    }

    /// <summary>Windows drops the link once nothing holds the device, its services, or a session that maintains it.</summary>
    private void Close()
    {
        _connected = false;
        lock (_services)
        {
            foreach (var service in _services.Values)
            {
                service.Dispose();
            }

            _services.Clear();
        }

        _session?.Dispose();
        _session = null;
        if (_device is { } device)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            device.Dispose();
            _device = null;
        }
    }
}

internal sealed class WinRtCharacteristic(GattCharacteristic characteristic) : BleCharacteristic
{
    private GattCharacteristicProperties Properties => characteristic.CharacteristicProperties;

    public override bool CanNotify => (Properties & (GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate)) != 0;

    public override bool CanWriteWithoutResponse => (Properties & GattCharacteristicProperties.WriteWithoutResponse) != 0;

    public override async Task<byte[]> ReadAsync()
    {
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        return result.Status == GattCommunicationStatus.Success
            ? result.Value.ToArray()
            : throw new InvalidOperationException(Loc.Format(CoreStrings.Ble_RequestFailed, WinRtPeripheral.Describe(result.Status, result.ProtocolError)));
    }

    public override async Task WriteAsync(byte[] value, bool withResponse)
    {
        var option = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var result = await characteristic.WriteValueWithResultAsync(value.AsBuffer(), option);
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException(Loc.Format(CoreStrings.Ble_RequestFailed, WinRtPeripheral.Describe(result.Status, result.ProtocolError)));
        }
    }

    public override async Task<IDisposable> SubscribeAsync(Action<byte[]> onValue)
    {
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (_, e) => onValue(e.CharacteristicValue.ToArray());
        characteristic.ValueChanged += handler;
        var unsubscribe = new Unsubscriber(() => characteristic.ValueChanged -= handler);

        // Notifications when the characteristic has them; the FTMS control point only indicates.
        var mode = (Properties & GattCharacteristicProperties.Notify) != 0
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(mode);
        if (result.Status != GattCommunicationStatus.Success)
        {
            unsubscribe.Dispose();
            throw new InvalidOperationException(Loc.Format(CoreStrings.Ble_RequestFailed, WinRtPeripheral.Describe(result.Status, result.ProtocolError)));
        }

        return unsubscribe;
    }
}
#endif
