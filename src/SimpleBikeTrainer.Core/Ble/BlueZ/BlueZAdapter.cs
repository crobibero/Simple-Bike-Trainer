using AsyncAwaitBestPractices;
using SimpleBikeTrainer.Resources;
using Tmds.DBus.Protocol;

namespace SimpleBikeTrainer.Ble.BlueZ;

/// <summary>BlueZ on the system D-Bus, through the proxies generated from BlueZ.xml.</summary>
internal sealed class BlueZAdapter : BleAdapter
{
    internal const string Service = "org.bluez";
    internal const string GattService1 = "org.bluez.GattService1";

    private readonly DBusConnection _connection;
    private readonly Adapter1 _adapter;
    private readonly string _devicePrefix;
    private IDisposable? _added;
    private IDisposable? _changed;
    private bool _discovering;

    private BlueZAdapter(DBusConnection connection, string path)
    {
        _connection = connection;
        _adapter = new Adapter1(connection, Service, path);
        _devicePrefix = path + "/";
    }

    public static async Task<BleAdapter> CreateAsync(string? name)
    {
        var connection = DBusConnection.System;
        Dictionary<ObjectPath, Dictionary<string, Dictionary<string, VariantValue>>> objects;
        try
        {
            objects = await GetObjectsAsync(connection);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(Loc.Format(CoreStrings.Ble_NoBlueZ, e.Message), e);
        }

        var adapters = objects.Where(o => o.Value.ContainsKey(Adapter1.DBusInterfaceName))
            .Select(o => o.Key.ToString())
            .Order(StringComparer.Ordinal);
        var path = string.IsNullOrEmpty(name) ? adapters.FirstOrDefault() : adapters.FirstOrDefault(p => p == "/org/bluez/" + name);
        if (path is null)
        {
            throw new InvalidOperationException(CoreStrings.Ble_NoAdapter);
        }

        var adapter = new BlueZAdapter(connection, path);
        if (!await adapter._adapter.GetPoweredAsync())
        {
            await adapter._adapter.SetPoweredAsync(true);
        }

        return adapter;
    }

    internal static Task<Dictionary<ObjectPath, Dictionary<string, Dictionary<string, VariantValue>>>> GetObjectsAsync(DBusConnection connection) =>
        new ObjectManager(connection, Service, "/").GetManagedObjectsAsync();

    public override async Task StartScanAsync()
    {
        _added ??= await new ObjectManager(_connection, Service, "/").WatchInterfacesAddedAsync(OnInterfacesAdded, emitOnCapturedContext: false);

        // BlueZ keeps devices it saw earlier: one that advertises again changes its RSSI rather than being added.
        _changed ??= await _connection.WatchPropertiesChangedAsync(
            Service,
            null,
            Device1.DBusInterfaceName,
            static (m, _) => (m.PathAsString ?? string.Empty, RssiChanged(m)),
            (Notification<(string Path, bool RssiChanged)> n) =>
            {
                if (n.Value.RssiChanged)
                {
                    ReportAsync(n.Value.Path).SafeFireAndForget();
                }
            },
            ObserverFlags.None,
            emitOnCapturedContext: false);

        foreach (var (path, interfaces) in await GetObjectsAsync(_connection))
        {
            if (interfaces.ContainsKey(Device1.DBusInterfaceName))
            {
                ReportAsync(path.ToString()).SafeFireAndForget();
            }
        }

        try
        {
            await _adapter.SetDiscoveryFilterAsync(new Dictionary<string, VariantValue> { ["Transport"] = "le" });
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
        _added?.Dispose();
        _added = null;
        _changed?.Dispose();
        _changed = null;
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

    private void OnInterfacesAdded((ObjectPath Object, Dictionary<string, Dictionary<string, VariantValue>> Interfaces) added)
    {
        if (added.Interfaces.ContainsKey(Device1.DBusInterfaceName))
        {
            ReportAsync(added.Object.ToString()).SafeFireAndForget();
        }
    }

    private static bool RssiChanged(Message message)
    {
        var reader = message.GetBodyReader();
        reader.ReadString(); // The interface.
        return Device1Properties.ReadFrom(ref reader, withInvalidated: true).IsSet(Device1Property.RSSI);
    }

    private async Task ReportAsync(string path)
    {
        if (!path.StartsWith(_devicePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var device = new Device1(_connection, Service, path);
        INullableDevice1Properties p;
        try
        {
            p = await device.GetNullablePropertiesAsync();
        }
        catch (DBusExceptionBase)
        {
            // The device vanished between the signal and the property read.
            return;
        }

        var name = !string.IsNullOrEmpty(p.Name) ? p.Name : !string.IsNullOrEmpty(p.Alias) ? p.Alias : null;
        var manufacturerData = new Dictionary<ushort, byte[]>();
        foreach (var (company, value) in p.ManufacturerData ?? [])
        {
            if (value.ItemType == VariantValueType.Byte)
            {
                manufacturerData[company] = value.GetArray<byte>();
            }
        }

        // BlueZ keeps devices it saw earlier; an RSSI means it is advertising now.
        var rssi = p.RSSI is 0 ? null : p.RSSI;
        OnAdvertised(new BleAdvertisement(new BlueZPeripheral(device), p.Address ?? string.Empty, name, p.UUIDs ?? [], manufacturerData, rssi));
    }
}

internal sealed class BlueZPeripheral(Device1 device) : BlePeripheral
{
    private IDisposable? _watcher;
    private TaskCompletionSource? _resolved;

    public override async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _resolved = resolved;
        _watcher ??= await device.WatchPropertiesChangedAsync(OnPropertiesChanged, emitOnCapturedContext: false);

        await device.ConnectAsync().WaitAsync(timeout, cancellationToken);
        if (!await device.GetServicesResolvedAsync())
        {
            await resolved.Task.WaitAsync(timeout, cancellationToken);
        }
    }

    public override Task DisconnectAsync() => device.DisconnectAsync();

    public override async Task<bool> HasServiceAsync(string service) =>
        FindService(await BlueZAdapter.GetObjectsAsync(device.Connection), service) is not null;

    public override async Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic)
    {
        var objects = await BlueZAdapter.GetObjectsAsync(device.Connection);
        if (FindService(objects, service) is not { } servicePath)
        {
            return null;
        }

        foreach (var (path, interfaces) in Children(objects, servicePath, GattCharacteristic1.DBusInterfaceName))
        {
            var properties = interfaces[GattCharacteristic1.DBusInterfaceName];
            if (HasUuid(properties, characteristic))
            {
                var flags = properties.TryGetValue("Flags", out var f) ? f.GetArray<string>() : [];
                return new BlueZCharacteristic(new GattCharacteristic1(device.Connection, BlueZAdapter.Service, path), flags);
            }
        }

        return null;
    }

    private string? FindService(Dictionary<ObjectPath, Dictionary<string, Dictionary<string, VariantValue>>> objects, string service) =>
        Children(objects, device.Path.ToString(), BlueZAdapter.GattService1)
            .Where(o => HasUuid(o.Interfaces[BlueZAdapter.GattService1], service))
            .Select(o => o.Path)
            .FirstOrDefault();

    /// <summary>The objects below <paramref name="parent"/> in BlueZ's tree that have <paramref name="iface"/>.</summary>
    private static IEnumerable<(string Path, Dictionary<string, Dictionary<string, VariantValue>> Interfaces)> Children(
        Dictionary<ObjectPath, Dictionary<string, Dictionary<string, VariantValue>>> objects, string parent, string iface) =>
        objects.Select(o => (Path: o.Key.ToString(), Interfaces: o.Value))
            .Where(o => o.Path.StartsWith(parent + "/", StringComparison.Ordinal) && o.Interfaces.ContainsKey(iface));

    private static bool HasUuid(Dictionary<string, VariantValue> properties, string uuid) =>
        properties.TryGetValue("UUID", out var value) && string.Equals(value.GetString(), uuid, StringComparison.OrdinalIgnoreCase);

    private void OnPropertiesChanged(IChangedDevice1Properties changes)
    {
        if (changes.ServicesResolved == true)
        {
            _resolved?.TrySetResult();
        }

        if (changes.Connected == false)
        {
            OnDisconnected();
        }
    }
}

internal sealed class BlueZCharacteristic(GattCharacteristic1 characteristic, string[] flags) : BleCharacteristic
{
    public override bool CanNotify => flags.Contains("notify") || flags.Contains("indicate");

    public override bool CanWriteWithoutResponse => flags.Contains("write-without-response");

    public override Task<byte[]> ReadAsync() => characteristic.ReadValueAsync([]);

    public override Task WriteAsync(byte[] value, bool withResponse) =>
        characteristic.WriteValueAsync(value, new Dictionary<string, VariantValue> { ["type"] = withResponse ? "request" : "command" });

    /// <summary>Watches the Value property before starting notifications, so the first one is not missed.</summary>
    public override async Task<IDisposable> SubscribeAsync(Action<byte[]> onValue)
    {
        var watcher = await characteristic.WatchPropertiesChangedAsync(
            changes =>
            {
                if (changes.Value is { } bytes)
                {
                    onValue(bytes);
                }
            },
            emitOnCapturedContext: false);
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
