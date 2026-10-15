using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Tests;

public sealed class DeviceConnectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-devices").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DiscoveredDevice Kickr = new(null!, DeviceId.Bluetooth("CE:D1:96:5E:7A:BB"), "KICKR CORE 6107", DeviceKind.Trainer, -58);
    private static readonly DiscoveredDevice Strap = new(null!, DeviceId.Bluetooth("E1:22:33:44:55:66"), "HRM-Pro:123456", DeviceKind.HeartRate, -71);
    private static readonly DiscoveredDevice Click = new(null!, DeviceId.Bluetooth("C8:A3:BD:AE:3B:B6"), "Zwift Click", DeviceKind.Shifter, -64);

    [Fact]
    public void PickedDevicesWithNoControllerStopTheScanOnceTheyAreInView()
    {
        var saved = Saved(Kickr, Strap);

        Assert.False(DeviceConnector.AllInView(saved, [Kickr], wantHeartRate: true, wantShifters: true));
        Assert.True(DeviceConnector.AllInView(saved, [Kickr, Strap], wantHeartRate: true, wantShifters: true));
    }

    [Fact]
    public void PickedControllersAreWaitedFor()
    {
        var saved = Saved(Kickr, Click);

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
        var trainer = new TestTrainer(Kickr.Id);
        var current = new ConnectedDevices(trainer, null, []);

        var devices = await DeviceConnector.AddAsync(config, current, [Strap], Connect, (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.Same(trainer, devices.Trainer);
        Assert.False(trainer.Disposed);
        Assert.Equal(Strap.Id, devices.HeartRate?.Id);
        var saved = AppConfig.Load(config.Path).Devices;
        Assert.Equal(Kickr.Id, saved.Trainer);
        Assert.Equal(Strap.Id, saved.HeartRate);
        Assert.Equal("HRM-Pro:123456", saved.NameOf(Strap.Id));
        Assert.True(saved.Picked);
    }

    [Fact]
    public async Task ANewTrainerOrMonitorReplacesTheOldOneAndControllersJoin()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Id);
        var strap = new TestStrap(Strap.Id);
        var click = new TestClick(Click.Id);
        var current = new ConnectedDevices(trainer, strap, [click]);
        config.Devices.Pick(DeviceKind.HeartRate, Strap.Id, Strap.Name);
        config.Devices.Pick(DeviceKind.Shifter, Click.Id, Click.Name);
        DiscoveredDevice bike = new(null!, DeviceId.Bluetooth("D4:00:00:00:00:01"), "KICKR BIKE 0001", DeviceKind.Trainer, -60);
        DiscoveredDevice watch = new(null!, DeviceId.Bluetooth("E1:00:00:00:00:02"), "Forerunner 965", DeviceKind.HeartRate, -70);
        DiscoveredDevice right = new(null!, DeviceId.Bluetooth("C8:00:00:00:00:03"), "Zwift Click", DeviceKind.Shifter, -65);

        var devices = await DeviceConnector.AddAsync(config, current, [bike, watch, right], Connect, (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.Equal(bike.Id, devices.Trainer.Id);
        Assert.Equal(watch.Id, devices.HeartRate?.Id);
        Assert.Equal([Click.Id, right.Id], devices.Shifters.Select(s => s.Id));
        Assert.True(trainer.Disposed);
        Assert.True(strap.Disposed);
        Assert.False(click.Disposed);
        Assert.Equal([Click.Id, right.Id], config.Devices.Shifters);
    }

    [Fact]
    public async Task ADeviceThatWillNotConnectLeavesTheOthersAsTheyWere()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Id);
        var strap = new TestStrap(Strap.Id);
        config.Devices.Pick(DeviceKind.HeartRate, Strap.Id, Strap.Name);
        DiscoveredDevice watch = new(null!, DeviceId.Bluetooth("E1:00:00:00:00:02"), "Forerunner 965", DeviceKind.HeartRate, -70);
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
        Assert.Equal(Strap.Id, config.Devices.HeartRate);
        Assert.Equal(["Forerunner 965 would not connect"], warnings);
    }

    [Fact]
    public async Task CancellingDisconnectsOnlyTheNewDevices()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Id);
        TestStrap? added = null;

        await Assert.ThrowsAsync<OperationCanceledException>(() => DeviceConnector.AddAsync(
            config,
            new ConnectedDevices(trainer, null, []),
            [Strap, Click],
            (found, _, _) => found.Kind == DeviceKind.HeartRate
                ? Task.FromResult<ISensorDevice?>(added = new TestStrap(found.Id))
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
        var saved = Saved(Kickr, Strap, Click);
        saved.Find(Click.Id)!.Name = string.Empty;

        Assert.Equal($"{Kickr.Name} (Bluetooth CE:D1:96:5E:7A:BB)", saved.Describe(Kickr.Id));
        Assert.Equal("Bluetooth C8:A3:BD:AE:3B:B6", saved.Describe(Click.Id));
        Assert.Equal("C8:A3:BD:AE:3B:B6", saved.NameOf(Click.Id));
        saved.Forget(Strap.Id);
        saved.Forget(Click.Id);

        Assert.Null(saved.HeartRate);
        Assert.Empty(saved.Shifters);
        Assert.Equal([Kickr.Id], saved.Saved.Select(d => d.Id));
    }

    [Fact]
    public void ARenamedDeviceKeepsItsNameUntilClearedAndForgotten()
    {
        var saved = Saved(Kickr);

        saved.Rename(Kickr.Id, "  Garage KICKR ");
        Assert.Equal("Garage KICKR", saved.NameOf(Kickr.Id));
        Assert.Equal(Kickr.Name, saved.AdvertisedName(Kickr.Id));

        saved.Rename(Kickr.Id, Kickr.Name);
        Assert.Empty(saved.Find(Kickr.Id)!.Alias);

        saved.Rename(Kickr.Id, "Garage KICKR");
        saved.Rename(Kickr.Id, "");
        Assert.Equal(Kickr.Name, saved.NameOf(Kickr.Id));

        saved.Rename(Kickr.Id, "Garage KICKR");
        saved.Forget(Kickr.Id);
        Assert.Null(saved.Find(Kickr.Id));
        Assert.Equal(Kickr.Id.Address, saved.NameOf(Kickr.Id));
    }

    private AppConfig Config()
    {
        var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
        config.Devices.Pick(DeviceKind.Trainer, Kickr.Id, Kickr.Name);
        return config;
    }

    private static DevicesConfig Saved(params DiscoveredDevice[] devices)
    {
        var saved = new DevicesConfig { Picked = true };
        foreach (var d in devices)
        {
            saved.Pick(d.Kind, d.Id, d.Name);
        }

        return saved;
    }

    private static Task<ISensorDevice?> Connect(DiscoveredDevice found, Action<string, bool> progress, CancellationToken ct) =>
        Task.FromResult<ISensorDevice?>(found.Kind switch
        {
            DeviceKind.Trainer => new TestTrainer(found.Id),
            DeviceKind.HeartRate => new TestStrap(found.Id),
            _ => new TestClick(found.Id),
        });

    private class TestDevice(DeviceId id) : ISensorDevice
    {
        public string Name => Id.Address;

        public DeviceId Id { get; } = id;

        public bool IsConnected => true;

        public string? Status => null;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTrainer(DeviceId id) : TestDevice(id), ITrainer
    {
        public event Action<IndoorBikeData>? DataReceived { add { } remove { } }

        public string? LastError => null;

        public Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestStrap(DeviceId id) : TestDevice(id), IHeartRateMonitor
    {
        public event Action<int>? HeartRateReceived { add { } remove { } }
    }

    private sealed class TestClick(DeviceId id) : TestDevice(id), IShifter
    {
        public event Action<ClickButtons>? ButtonsPressed { add { } remove { } }
    }
}
