using System.Runtime.InteropServices;
using SimpleBikeTrainer.Resources;
using static SimpleBikeTrainer.Ble.CoreBluetooth.ObjC;

namespace SimpleBikeTrainer.Ble.CoreBluetooth;

/// <summary>Every scanner shares the one central manager.</summary>
internal sealed class CoreBluetoothAdapter : BleAdapter
{
    private readonly Central _central;
    private bool _scanning;

    private CoreBluetoothAdapter(Central central)
    {
        _central = central;
    }

    public static async Task<BleAdapter> CreateAsync()
    {
        var central = Central.Instance;
        return await central.StateAsync() switch
        {
            Central.PoweredOn => new CoreBluetoothAdapter(central),
            Central.PoweredOff => throw new InvalidOperationException(CoreStrings.Ble_RadioOff),
            Central.Unauthorized => throw new InvalidOperationException(CoreStrings.Ble_Unauthorized),
            _ => throw new InvalidOperationException(CoreStrings.Ble_NoAdapter),
        };
    }

    public override Task StartScanAsync()
    {
        if (!_scanning)
        {
            _scanning = true;
            _central.Advertised += OnAdvertised;
            _central.StartScan();
        }

        return Task.CompletedTask;
    }

    public override Task StopScanAsync()
    {
        if (_scanning)
        {
            _scanning = false;
            _central.Advertised -= OnAdvertised;
            _central.StopScan();
        }

        return Task.CompletedTask;
    }
}

/// <summary>Delegate callbacks and the app's CoreBluetooth calls all run on one private serial queue, so this state needs no locks.</summary>
internal sealed class Central
{
    // CBManagerState values.
    public const nint Unauthorized = 3;
    public const nint PoweredOff = 4;
    public const nint PoweredOn = 5;

    private const string Framework = "/System/Library/Frameworks/CoreBluetooth.framework/CoreBluetooth";

    private static readonly Lock CreateLock = new();

    /// <summary>Set before the manager exists: CoreBluetooth reports the radio's state as soon as it is created.</summary>
    private static Central? _instance;

    /// <summary>The CBCentralManagerDelegate and CBPeripheralDelegate methods, registered once per process.</summary>
    private static readonly Lazy<nint> DelegateClass = new(CreateDelegateClass);

    private readonly TaskCompletionSource<nint> _state = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<nint, MacPeripheral> _peripherals = [];
    private readonly nint _nameKey;
    private readonly nint _servicesKey;
    private readonly nint _manufacturerKey;
    private readonly nint _allowDuplicatesKey;
    private int _scanners;

    private unsafe Central()
    {
        var framework = NativeLibrary.Load(Framework);
        _nameKey = *(nint*)NativeLibrary.GetExport(framework, "CBAdvertisementDataLocalNameKey");
        _servicesKey = *(nint*)NativeLibrary.GetExport(framework, "CBAdvertisementDataServiceUUIDsKey");
        _manufacturerKey = *(nint*)NativeLibrary.GetExport(framework, "CBAdvertisementDataManufacturerDataKey");
        _allowDuplicatesKey = *(nint*)NativeLibrary.GetExport(framework, "CBCentralManagerScanOptionAllowDuplicatesKey");

        Queue = dispatch_queue_create("dev.codyrobibero.SimpleBikeTrainer.bluetooth", 0);
        Delegate = Send(Send(DelegateClass.Value, Sel("alloc")), Sel("init"));
        _instance = this;
        Manager = Send(Send(Class("CBCentralManager"), Sel("alloc")), Sel("initWithDelegate:queue:"), Delegate, Queue);
    }

    public static Central Instance
    {
        get
        {
            lock (CreateLock)
            {
                return _instance ?? new Central();
            }
        }
    }

    public nint Queue { get; }

    public nint Delegate { get; }

    public nint Manager { get; }

    public event Action<BleAdvertisement>? Advertised;

    private static unsafe nint CreateDelegateClass()
    {
        var cls = objc_allocateClassPair(Class("NSObject"), "SimpleBikeTrainerBluetoothDelegate", 0);
        Add(cls, "centralManagerDidUpdateState:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&DidUpdateState, "v@:@");
        Add(cls, "centralManager:didDiscoverPeripheral:advertisementData:RSSI:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&DidDiscover, "v@:@@@@");
        Add(cls, "centralManager:didConnectPeripheral:", (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&DidConnect, "v@:@@");
        Add(cls, "centralManager:didFailToConnectPeripheral:error:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidFailToConnect, "v@:@@@");
        Add(cls, "centralManager:didDisconnectPeripheral:error:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidDisconnect, "v@:@@@");
        Add(cls, "peripheral:didDiscoverServices:", (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&DidDiscoverServices, "v@:@@");
        Add(cls, "peripheral:didDiscoverCharacteristicsForService:error:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidDiscoverCharacteristics, "v@:@@@");
        Add(cls, "peripheral:didUpdateValueForCharacteristic:error:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidUpdateValue, "v@:@@@");
        Add(cls, "peripheral:didWriteValueForCharacteristic:error:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidWriteValue, "v@:@@@");
        Add(cls, "peripheral:didUpdateNotificationStateForCharacteristic:error:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidUpdateNotificationState, "v@:@@@");
        objc_registerClassPair(cls);
        return cls;

        static void Add(nint cls, string selector, nint implementation, string types) =>
            class_addMethod(cls, Sel(selector), implementation, types);
    }

    /// <summary>The radio's state once CoreBluetooth knows it (CBManagerState), or 0 when it does not say in time.</summary>
    public async Task<nint> StateAsync()
    {
        try
        {
            return await _state.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void Run(Action action) => RunOn(Queue, action);

    public void StartScan() => Run(() =>
    {
        if (_scanners++ == 0)
        {
            // Duplicates bring the scan responses, which carry the name, and fresh signal strengths.
            var yes = SendBool(Class("NSNumber"), Sel("numberWithBool:"), true);
            var options = Send(Class("NSDictionary"), Sel("dictionaryWithObject:forKey:"), yes, _allowDuplicatesKey);
            Send(Manager, Sel("scanForPeripheralsWithServices:options:"), 0, options);
        }
    });

    public void StopScan() => Run(() =>
    {
        if (--_scanners == 0)
        {
            Send(Manager, Sel("stopScan"));
        }
    });

    /// <summary>Runs a callback's work. An exception must not reach CoreBluetooth, which would end the process.</summary>
    private static void Callback(Action<Central> work)
    {
        try
        {
            if (_instance is { } central)
            {
                work(central);
            }
        }
        catch
        {
            // Lost: the callback has nowhere to report it.
        }
    }

    private static void ForPeripheral(nint peripheral, Action<MacPeripheral> work) =>
        Callback(c =>
        {
            if (c._peripherals.TryGetValue(peripheral, out var p))
            {
                work(p);
            }
        });

    [UnmanagedCallersOnly]
    private static void DidUpdateState(nint self, nint cmd, nint manager) =>
        Callback(c =>
        {
            var state = Send(manager, Sel("state"));
            if (state > 1)
            {
                // 0 is unknown and 1 resetting; anything else is an answer.
                c._state.TrySetResult(state);
            }
        });

    [UnmanagedCallersOnly]
    private static void DidDiscover(nint self, nint cmd, nint manager, nint peripheral, nint advertisement, nint rssi) =>
        Callback(c =>
        {
            if (!c._peripherals.TryGetValue(peripheral, out var p))
            {
                // CoreBluetooth drops a peripheral nobody holds, connection and all.
                Send(peripheral, Sel("retain"));
                p = c._peripherals[peripheral] = new MacPeripheral(c, peripheral);
            }

            var address = ToManagedString(Send(Send(peripheral, Sel("identifier")), Sel("UUIDString"))) ?? string.Empty;
            var name = ToManagedString(Send(advertisement, Sel("objectForKey:"), c._nameKey))
                ?? ToManagedString(Send(peripheral, Sel("name")));
            var services = Items(Send(advertisement, Sel("objectForKey:"), c._servicesKey)).Select(Uuid).ToList();

            // CoreBluetooth gives the raw field: the company ID, little-endian, then the data.
            var manufacturerData = new Dictionary<ushort, byte[]>();
            if (ToBytes(Send(advertisement, Sel("objectForKey:"), c._manufacturerKey)) is { Length: >= 2 } raw)
            {
                manufacturerData[(ushort)(raw[0] | (raw[1] << 8))] = raw[2..];
            }

            // 127 means CoreBluetooth has no reading.
            var dbm = rssi == 0 ? 127 : Send(rssi, Sel("integerValue"));
            short? signal = dbm is 127 or 0 ? null : (short)dbm;
            c.Advertised?.Invoke(new BleAdvertisement(p, address, name, services, manufacturerData, signal));
        });

    [UnmanagedCallersOnly]
    private static void DidConnect(nint self, nint cmd, nint manager, nint peripheral) =>
        ForPeripheral(peripheral, p => p.OnConnected());

    [UnmanagedCallersOnly]
    private static void DidFailToConnect(nint self, nint cmd, nint manager, nint peripheral, nint error) =>
        ForPeripheral(peripheral, p => p.OnFailed(ErrorText(error) ?? CoreStrings.Ble_Disconnected));

    [UnmanagedCallersOnly]
    private static void DidDisconnect(nint self, nint cmd, nint manager, nint peripheral, nint error) =>
        ForPeripheral(peripheral, p => p.OnLinkLost(ErrorText(error) ?? CoreStrings.Ble_Disconnected));

    [UnmanagedCallersOnly]
    private static void DidDiscoverServices(nint self, nint cmd, nint peripheral, nint error) =>
        ForPeripheral(peripheral, p => p.OnServices(ErrorText(error)));

    [UnmanagedCallersOnly]
    private static void DidDiscoverCharacteristics(nint self, nint cmd, nint peripheral, nint service, nint error) =>
        ForPeripheral(peripheral, p => p.OnCharacteristics(service, ErrorText(error)));

    [UnmanagedCallersOnly]
    private static void DidUpdateValue(nint self, nint cmd, nint peripheral, nint characteristic, nint error) =>
        ForPeripheral(peripheral, p => p.Characteristic(characteristic)?.OnValue(ErrorText(error)));

    [UnmanagedCallersOnly]
    private static void DidWriteValue(nint self, nint cmd, nint peripheral, nint characteristic, nint error) =>
        ForPeripheral(peripheral, p => p.Characteristic(characteristic)?.OnWritten(ErrorText(error)));

    [UnmanagedCallersOnly]
    private static void DidUpdateNotificationState(nint self, nint cmd, nint peripheral, nint characteristic, nint error) =>
        ForPeripheral(peripheral, p => p.Characteristic(characteristic)?.OnNotifying(ErrorText(error)));
}

/// <summary>Apart from the constructor, its state is only touched on the central's queue.</summary>
internal sealed class MacPeripheral(Central central, nint handle) : BlePeripheral
{
    private readonly Dictionary<string, nint> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, MacCharacteristic> _characteristics = [];
    private TaskCompletionSource? _connecting;
    private int _servicesLeft;
    private bool _connected;

    public Central Central => central;

    public nint Handle => handle;

    public override async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        central.Run(() =>
        {
            _connecting = connecting;
            _connected = false;
            _services.Clear();
            _characteristics.Clear();
            Send(handle, Sel("setDelegate:"), central.Delegate);
            Send(central.Manager, Sel("connectPeripheral:options:"), handle, 0);
        });

        try
        {
            await connecting.Task.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    public override Task DisconnectAsync()
    {
        central.Run(() =>
        {
            _connected = false;
            Send(central.Manager, Sel("cancelPeripheralConnection:"), handle);
        });
        return Task.CompletedTask;
    }

    public override Task<bool> HasServiceAsync(string service)
    {
        var found = false;
        central.Run(() => found = _services.ContainsKey(service));
        return Task.FromResult(found);
    }

    public override Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic)
    {
        BleCharacteristic? found = null;
        central.Run(() =>
        {
            if (_services.TryGetValue(service, out var svc))
            {
                found = _characteristics.Values.FirstOrDefault(c => c.Service == svc && string.Equals(c.Uuid, characteristic, StringComparison.OrdinalIgnoreCase));
            }
        });
        return Task.FromResult(found);
    }

    public MacCharacteristic? Characteristic(nint characteristic) => _characteristics.GetValueOrDefault(characteristic);

    public void OnConnected() => Send(handle, Sel("discoverServices:"), 0);

    public void OnFailed(string error)
    {
        _connecting?.TrySetException(new InvalidOperationException(error));
        _connecting = null;
    }

    public void OnServices(string? error)
    {
        if (error is not null)
        {
            OnFailed(error);
            return;
        }

        var services = Items(Send(handle, Sel("services"))).ToList();
        _servicesLeft = services.Count;
        foreach (var service in services)
        {
            _services[Uuid(Send(service, Sel("UUID")))] = service;
            Send(handle, Sel("discoverCharacteristics:forService:"), 0, service);
        }

        if (services.Count == 0)
        {
            OnReady();
        }
    }

    public void OnCharacteristics(nint service, string? error)
    {
        if (error is null)
        {
            foreach (var c in Items(Send(service, Sel("characteristics"))))
            {
                _characteristics[c] = new MacCharacteristic(this, c, service, Uuid(Send(c, Sel("UUID"))), (int)Send(c, Sel("properties")));
            }
        }

        // A service whose characteristics would not load stays, with none, as in BlueZPeripheral and WinRtPeripheral.
        if (--_servicesLeft == 0)
        {
            OnReady();
        }
    }

    public void OnLinkLost(string error)
    {
        foreach (var c in _characteristics.Values)
        {
            c.OnLinkLost(error);
        }

        if (_connecting is not null)
        {
            OnFailed(error);
        }
        else if (_connected)
        {
            _connected = false;

            // Off the queue: the reconnect calls back into CoreBluetooth, which runs on it.
            Task.Run(OnDisconnected);
        }
    }

    private void OnReady()
    {
        _connected = true;
        _connecting?.TrySetResult();
        _connecting = null;
    }
}

/// <summary>Requests wait for their delegate callback, which arrives on the central's queue.</summary>
internal sealed class MacCharacteristic(MacPeripheral peripheral, nint handle, nint service, string uuid, int properties) : BleCharacteristic
{
    // CBCharacteristicProperties.
    private const int WriteWithoutResponse = 0x04;
    private const int Notify = 0x10;
    private const int Indicate = 0x20;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly List<Action<byte[]>> _listeners = [];
    private readonly Queue<TaskCompletionSource<byte[]>> _reads = new();
    private readonly Queue<TaskCompletionSource> _writes = new();
    private TaskCompletionSource? _notifying;

    public nint Service => service;

    public string Uuid => uuid;

    public override bool CanNotify => (properties & (Notify | Indicate)) != 0;

    public override bool CanWriteWithoutResponse => (properties & WriteWithoutResponse) != 0;

    public override Task<byte[]> ReadAsync()
    {
        var read = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peripheral.Central.Run(() =>
        {
            _reads.Enqueue(read);
            Send(peripheral.Handle, Sel("readValueForCharacteristic:"), handle);
        });
        return read.Task.WaitAsync(RequestTimeout);
    }

    public override Task WriteAsync(byte[] value, bool withResponse)
    {
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peripheral.Central.Run(() =>
        {
            if (withResponse)
            {
                _writes.Enqueue(write);
            }

            // CBCharacteristicWriteType: 0 with response, 1 without.
            Send(peripheral.Handle, Sel("writeValue:forCharacteristic:type:"), NSData(value), handle, withResponse ? 0 : 1);
        });

        return withResponse ? write.Task.WaitAsync(RequestTimeout) : Task.CompletedTask;
    }

    public override async Task<IDisposable> SubscribeAsync(Action<byte[]> onValue)
    {
        var notifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peripheral.Central.Run(() =>
        {
            _listeners.Add(onValue);
            _notifying = notifying;
            SendBool(peripheral.Handle, Sel("setNotifyValue:forCharacteristic:"), true, handle);
        });

        var unsubscribe = new Unsubscriber(() => peripheral.Central.Run(() => _listeners.Remove(onValue)));
        try
        {
            await notifying.Task.WaitAsync(RequestTimeout);
        }
        catch
        {
            unsubscribe.Dispose();
            throw;
        }

        return unsubscribe;
    }

    /// <summary>A read's answer, or a notification: CoreBluetooth reports both the same way.</summary>
    public void OnValue(string? error)
    {
        if (_reads.TryDequeue(out var read))
        {
            if (error is null)
            {
                read.TrySetResult(ToBytes(Send(handle, Sel("value"))));
            }
            else
            {
                read.TrySetException(new InvalidOperationException(error));
            }

            return;
        }

        if (error is null)
        {
            var value = ToBytes(Send(handle, Sel("value")));
            foreach (var listener in _listeners.ToList())
            {
                listener(value);
            }
        }
    }

    public void OnWritten(string? error)
    {
        if (_writes.TryDequeue(out var write))
        {
            if (error is null)
            {
                write.TrySetResult();
            }
            else
            {
                write.TrySetException(new InvalidOperationException(error));
            }
        }
    }

    public void OnNotifying(string? error)
    {
        if (error is null)
        {
            _notifying?.TrySetResult();
        }
        else
        {
            _notifying?.TrySetException(new InvalidOperationException(error));
        }

        _notifying = null;
    }

    public void OnLinkLost(string error)
    {
        var failure = new InvalidOperationException(error);
        while (_reads.TryDequeue(out var read))
        {
            read.TrySetException(failure);
        }

        while (_writes.TryDequeue(out var write))
        {
            write.TrySetException(failure);
        }

        _notifying?.TrySetException(failure);
        _notifying = null;
        _listeners.Clear();
    }
}
