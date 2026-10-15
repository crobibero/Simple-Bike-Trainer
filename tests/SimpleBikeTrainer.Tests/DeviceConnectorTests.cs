using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Tests;

public sealed class DeviceConnectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-devices").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DiscoveredDevice Kickr = new(null!, "CE:D1:96:5E:7A:BB", "KICKR CORE 6107", DeviceKind.Trainer, -58);
    private static readonly DiscoveredDevice Strap = new(null!, "E1:22:33:44:55:66", "HRM-Pro:123456", DeviceKind.HeartRate, -71);
    private static readonly DiscoveredDevice Click = new(null!, "C8:A3:BD:AE:3B:B6", "Zwift Click", DeviceKind.Shifter, -64);

    [Fact]
    public void PickedDevicesWithNoControllerStopTheScanOnceTheyAreInView()
    {
        var saved = new DevicesConfig { Trainer = Kickr.Address, HeartRate = Strap.Address, Picked = true };

        Assert.False(DeviceConnector.AllInView(saved, [Kickr], wantHeartRate: true, wantShifters: true));
        Assert.True(DeviceConnector.AllInView(saved, [Kickr, Strap], wantHeartRate: true, wantShifters: true));
    }

    [Fact]
    public void PickedControllersAreWaitedFor()
    {
        var saved = new DevicesConfig { Trainer = Kickr.Address, Shifters = [Click.Address], Picked = true };

        Assert.False(DeviceConnector.AllInView(saved, [Kickr], wantHeartRate: false, wantShifters: true));
        Assert.True(DeviceConnector.AllInView(saved, [Kickr, Click], wantHeartRate: false, wantShifters: true));
    }

    [Fact]
    public void WithNothingPickedAnyControllerInRangeIsWaitedFor()
    {
        var saved = new DevicesConfig();

        Assert.False(DeviceConnector.AllInView(saved, [Kickr], wantHeartRate: false, wantShifters: true));
        Assert.True(DeviceConnector.AllInView(saved, [Kickr, Click], wantHeartRate: false, wantShifters: true));
    }

    [Fact]
    public async Task AddingAHeartRateMonitorKeepsTheTrainerConnected()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Address);
        var current = new ConnectedDevices(trainer, null, []);

        var devices = await DeviceConnector.AddAsync(config, current, [Strap], Connect, (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.Same(trainer, devices.Trainer);
        Assert.False(trainer.Disposed);
        Assert.Equal(Strap.Address, devices.HeartRate?.Address);
        var saved = AppConfig.Load(config.Path).Devices;
        Assert.Equal(Kickr.Address, saved.Trainer);
        Assert.Equal(Strap.Address, saved.HeartRate);
        Assert.Equal("HRM-Pro:123456", saved.NameOf(Strap.Address));
        Assert.True(saved.Picked);
    }

    [Fact]
    public async Task ANewTrainerOrMonitorReplacesTheOldOneAndControllersJoin()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Address);
        var strap = new TestStrap(Strap.Address);
        var click = new TestClick(Click.Address);
        var current = new ConnectedDevices(trainer, strap, [click]);
        config.Devices.HeartRate = Strap.Address;
        config.Devices.Shifters = [Click.Address];
        DiscoveredDevice bike = new(null!, "D4:00:00:00:00:01", "KICKR BIKE 0001", DeviceKind.Trainer, -60);
        DiscoveredDevice watch = new(null!, "E1:00:00:00:00:02", "Forerunner 965", DeviceKind.HeartRate, -70);
        DiscoveredDevice right = new(null!, "C8:00:00:00:00:03", "Zwift Click", DeviceKind.Shifter, -65);

        var devices = await DeviceConnector.AddAsync(config, current, [bike, watch, right], Connect, (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.Equal(bike.Address, devices.Trainer.Address);
        Assert.Equal(watch.Address, devices.HeartRate?.Address);
        Assert.Equal([Click.Address, right.Address], devices.Shifters.Select(s => s.Address));
        Assert.True(trainer.Disposed);
        Assert.True(strap.Disposed);
        Assert.False(click.Disposed);
        Assert.Equal([Click.Address, right.Address], config.Devices.Shifters);
    }

    [Fact]
    public async Task ADeviceThatWillNotConnectLeavesTheOthersAsTheyWere()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Address);
        var strap = new TestStrap(Strap.Address);
        config.Devices.HeartRate = Strap.Address;
        DiscoveredDevice watch = new(null!, "E1:00:00:00:00:02", "Forerunner 965", DeviceKind.HeartRate, -70);
        var warnings = new List<string>();

        var devices = await DeviceConnector.AddAsync(
            config,
            new ConnectedDevices(trainer, strap, []),
            [watch],
            (found, progress, _) =>
            {
                progress($"{found.Name} would not connect", true);
                return Task.FromResult<ISensorDevice?>(null);
            },
            (message, warning) =>
            {
                if (warning)
                {
                    warnings.Add(message);
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Same(strap, devices.HeartRate);
        Assert.False(strap.Disposed);
        Assert.Equal(Strap.Address, config.Devices.HeartRate);
        Assert.Equal(["Forerunner 965 would not connect"], warnings);
    }

    [Fact]
    public async Task CancellingDisconnectsOnlyTheNewDevices()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Address);
        TestStrap? added = null;

        await Assert.ThrowsAsync<OperationCanceledException>(() => DeviceConnector.AddAsync(
            config,
            new ConnectedDevices(trainer, null, []),
            [Strap, Click],
            (found, _, _) => found.Kind == DeviceKind.HeartRate
                ? Task.FromResult<ISensorDevice?>(added = new TestStrap(found.Address))
                : throw new OperationCanceledException(),
            (_, _) => { },
            TestContext.Current.CancellationToken));

        Assert.True(added?.Disposed);
        Assert.False(trainer.Disposed);
        Assert.False(File.Exists(config.Path));
    }

    [Fact]
    public void ForgettingADeviceDropsItAndItsName()
    {
        var saved = new DevicesConfig
        {
            Trainer = Kickr.Address,
            HeartRate = Strap.Address,
            Shifters = [Click.Address],
            Names = new() { [Kickr.Address] = Kickr.Name, [Strap.Address] = Strap.Name },
        };

        Assert.Equal($"{Kickr.Name} ({Kickr.Address})", saved.Describe(Kickr.Address));
        Assert.Equal(Click.Address, saved.NameOf(Click.Address));
        saved.Forget(Strap.Address);
        saved.Forget(Click.Address);

        Assert.Equal(string.Empty, saved.HeartRate);
        Assert.Empty(saved.Shifters);
        Assert.Equal([Kickr.Address], saved.Names.Keys);
    }

    [Fact]
    public void ARenamedDeviceKeepsItsNameUntilClearedAndForgotten()
    {
        var saved = new DevicesConfig { Trainer = Kickr.Address, Names = new() { [Kickr.Address] = Kickr.Name } };

        saved.Rename(Kickr.Address, "  Garage KICKR ");
        Assert.Equal("Garage KICKR", saved.NameOf(Kickr.Address));
        Assert.Equal(Kickr.Name, saved.AdvertisedName(Kickr.Address));

        saved.Rename(Kickr.Address, Kickr.Name);
        Assert.Empty(saved.Aliases);

        saved.Rename(Kickr.Address, "Garage KICKR");
        saved.Rename(Kickr.Address, "");
        Assert.Equal(Kickr.Name, saved.NameOf(Kickr.Address));

        saved.Rename(Kickr.Address, "Garage KICKR");
        saved.Forget(Kickr.Address);
        Assert.Empty(saved.Aliases);
    }

    private AppConfig Config()
    {
        var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
        config.Devices.Trainer = Kickr.Address;
        return config;
    }

    private static Task<ISensorDevice?> Connect(DiscoveredDevice found, Action<string, bool> progress, CancellationToken ct) =>
        Task.FromResult<ISensorDevice?>(found.Kind switch
        {
            DeviceKind.Trainer => new TestTrainer(found.Address),
            DeviceKind.HeartRate => new TestStrap(found.Address),
            _ => new TestClick(found.Address),
        });

    private class TestDevice(string address) : ISensorDevice
    {
        public string Name => Address;

        public string Address { get; } = address;

        public bool IsConnected => true;

        public string? Status => null;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTrainer(string address) : TestDevice(address), ITrainer
    {
        public event Action<IndoorBikeData>? DataReceived { add { } remove { } }

        public string? LastError => null;

        public Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestStrap(string address) : TestDevice(address), IHeartRateMonitor
    {
        public event Action<int>? HeartRateReceived { add { } remove { } }
    }

    private sealed class TestClick(string address) : TestDevice(address), IShifter
    {
        public event Action<ClickButtons>? ButtonsPressed { add { } remove { } }
    }
}
