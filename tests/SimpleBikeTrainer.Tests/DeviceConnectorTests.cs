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
    private static readonly DiscoveredDevice Meter = new(null!, DeviceId.Bluetooth("F0:11:22:33:44:55"), "4iiii PRECISION", DeviceKind.PowerMeter, -60);

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
    public void ABluetoothDeviceBlueZOnlyRemembersIsNotInView()
    {
        var saved = Saved(Kickr, Strap);
        var asleep = Strap with { Rssi = null };

        Assert.False(DeviceConnector.AllInView(saved, [Kickr, asleep], wantHeartRate: true, wantShifters: false));
        Assert.True(DeviceConnector.AllInView(saved, [Kickr, Strap], wantHeartRate: true, wantShifters: false));

        // ANT+ sticks and Wi-Fi give no signal strength at all.
        var antStrap = new DiscoveredDevice(null!, DeviceId.AntPlus(12345), "HRM-Dual", DeviceKind.HeartRate, null);
        Assert.True(DeviceConnector.AllInView(Saved(Kickr, antStrap), [Kickr, antStrap], wantHeartRate: true, wantShifters: false));
    }

    [Fact]
    public void PairedSensorsAreWaitedForAndOnlyThePairedOnesAreWanted()
    {
        var saved = Saved(Kickr, Meter);
        DiscoveredDevice other = new(null!, DeviceId.Bluetooth("F0:00:00:00:00:09"), "Assioma", DeviceKind.PowerMeter, -50);

        Assert.False(DeviceConnector.AllInView(saved, [Kickr, other], wantHeartRate: false, wantShifters: false));
        Assert.True(DeviceConnector.AllInView(saved, [Kickr, Meter], wantHeartRate: false, wantShifters: false));
        Assert.False(DeviceConnector.IsWanted(saved, other, wantHeartRate: true, wantShifters: true));
        Assert.False(DeviceConnector.IsWanted(new DevicesConfig(), Meter, wantHeartRate: true, wantShifters: true));
    }

    [Fact]
    public void ConnectedDevicesNeedNotBeInView()
    {
        var saved = Saved(Kickr, Strap);
        var set = new DeviceSet();
        set.Put(DeviceKind.Trainer, new TestTrainer(Kickr.Id));

        Assert.True(DeviceConnector.AllInView(saved, [Strap], wantHeartRate: true, wantShifters: false, set));
    }

    [Fact]
    public async Task AMonitorThatWillNotConnectYetKeepsTryingAndAControllerIsLeftOut()
    {
        var link = new TestLink(fails: [Strap.Id, Click.Id]);
        var set = new DeviceSet();
        var warnings = new List<string>();

        var connected = await DeviceConnector.ConnectIntoAsync(
            set,
            [Kickr with { Link = link }, Strap with { Link = link }, Click with { Link = link }],
            (message, warning) =>
            {
                if (warning)
                {
                    warnings.Add(message);
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([Kickr.Id], connected.Select(d => d.Id));
        Assert.Equal(Kickr.Id, set.Trainer?.Id);
        var strap = Assert.IsType<TestStrap>(set.HeartRate);
        Assert.True(strap.InBackground);
        Assert.Empty(set.Shifters);
        Assert.True(link.Opened.Single(d => d.Id == Click.Id).Disposed);

        // The monitor is tried once, as it keeps trying in the background; the controller twice, the first failure only progress.
        Assert.Equal(1, link.Attempts[Strap.Id]);
        Assert.Equal(2, link.Attempts[Click.Id]);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("still trying", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMonitorBlueZOnlyRemembersIsLeftToConnectInTheBackground()
    {
        var link = new TestLink(fails: [Strap.Id]);
        var set = new DeviceSet();

        var connected = await DeviceConnector.ConnectIntoAsync(
            set,
            [Kickr with { Link = link }, Strap with { Link = link, Rssi = null }],
            (_, _) => { },
            TestContext.Current.CancellationToken);

        Assert.Equal([Kickr.Id], connected.Select(d => d.Id));
        Assert.True(Assert.IsType<TestStrap>(set.HeartRate).InBackground);
        Assert.False(link.Attempts.ContainsKey(Strap.Id));
    }

    [Fact]
    public async Task ATrainerThatWillNotConnectFailsTheConnectAndLeavesTheSetAsItWas()
    {
        var link = new TestLink(fails: [Kickr.Id]);
        var set = new DeviceSet();

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceConnector.ConnectIntoAsync(
            set, [Kickr with { Link = link }, Strap with { Link = link }], (_, _) => { }, TestContext.Current.CancellationToken));

        Assert.Contains(Kickr.Name, e.Message, StringComparison.Ordinal);
        Assert.Equal(0, set.Count);
        Assert.All(link.Opened, d => Assert.True(d.Disposed));
    }

    [Fact]
    public async Task AddingAHeartRateMonitorKeepsTheTrainerConnected()
    {
        var config = Config();
        var trainer = new TestTrainer(Kickr.Id);
        var set = Set(trainer);

        var added = await DeviceConnector.AddAsync(config, set, [Strap], Connect, (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.Same(trainer, set.Trainer);
        Assert.False(trainer.Disposed);
        Assert.Equal(Strap.Id, set.HeartRate?.Id);
        Assert.Equal([Strap.Id], added.Select(d => d.Id));
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
        var set = Set(trainer, strap, click);
        config.Devices.Pick(DeviceKind.HeartRate, Strap.Id, Strap.Name);
        config.Devices.Pick(DeviceKind.Shifter, Click.Id, Click.Name);
        DiscoveredDevice bike = new(null!, DeviceId.Bluetooth("D4:00:00:00:00:01"), "KICKR BIKE 0001", DeviceKind.Trainer, -60);
        DiscoveredDevice watch = new(null!, DeviceId.Bluetooth("E1:00:00:00:00:02"), "Forerunner 965", DeviceKind.HeartRate, -70);
        DiscoveredDevice right = new(null!, DeviceId.Bluetooth("C8:00:00:00:00:03"), "Zwift Click", DeviceKind.Shifter, -65);

        await DeviceConnector.AddAsync(config, set, [bike, watch, right], Connect, (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.Equal(bike.Id, set.Trainer?.Id);
        Assert.Equal(watch.Id, set.HeartRate?.Id);
        Assert.Equal([Click.Id, right.Id], set.Shifters.Select(s => s.Id));
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

        var set = Set(trainer, strap);
        var added = await DeviceConnector.AddAsync(
            config,
            set,
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

        Assert.Empty(added);
        Assert.Same(strap, set.HeartRate);
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

        var set = Set(trainer);
        await Assert.ThrowsAsync<OperationCanceledException>(() => DeviceConnector.AddAsync(
            config,
            set,
            [Strap, Click],
            (found, _, _) => found.Kind == DeviceKind.HeartRate
                ? Task.FromResult<ISensorDevice?>(added = new TestStrap(found.Id))
                : throw new OperationCanceledException(),
            (_, _) => { },
            TestContext.Current.CancellationToken));

        Assert.True(added?.Disposed);
        Assert.False(trainer.Disposed);
        Assert.Equal([Kickr.Id], set.All.Select(d => d.Id));
        Assert.Null(config.Devices.HeartRate);
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

    private static DeviceSet Set(TestTrainer trainer, TestStrap? strap = null, TestClick? click = null)
    {
        var set = new DeviceSet();
        set.Put(DeviceKind.Trainer, trainer);
        if (strap is not null)
        {
            set.Put(DeviceKind.HeartRate, strap);
        }

        if (click is not null)
        {
            set.Put(DeviceKind.Shifter, click);
        }

        return set;
    }

    private static Task<ISensorDevice?> Connect(DiscoveredDevice found, Action<string, bool> progress, CancellationToken ct) =>
        Task.FromResult<ISensorDevice?>(found.Kind switch
        {
            DeviceKind.Trainer => new TestTrainer(found.Id),
            DeviceKind.HeartRate => new TestStrap(found.Id),
            _ => new TestClick(found.Id),
        });

    private class TestDevice(DeviceId id) : IConnectableDevice
    {
        public string Name => Id.Address;

        public DeviceId Id { get; } = id;

        public bool IsConnected { get; private set; } = true;

        public string? Status => null;

        public bool Disposed { get; private set; }

        public bool InBackground { get; private set; }

        /// <summary>Set by <see cref="TestLink"/>: each connect fails.</summary>
        public Action? Connecting { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (Connecting is { } connecting)
            {
                IsConnected = false;
                connecting();
            }

            return Task.CompletedTask;
        }

        public void ConnectInBackground()
        {
            InBackground = true;
            IsConnected = Connecting is null;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Opens test devices; the ones in <paramref name="fails"/> time out on every connect.</summary>
    private sealed class TestLink(IReadOnlyList<DeviceId> fails) : IDeviceLink
    {
        public List<TestDevice> Opened { get; } = [];

        public Dictionary<DeviceId, int> Attempts { get; } = [];

        public IConnectableDevice Open(DiscoveredDevice found)
        {
            TestDevice device = found.Kind switch
            {
                DeviceKind.Trainer => new TestTrainer(found.Id),
                DeviceKind.HeartRate => new TestStrap(found.Id),
                _ => new TestClick(found.Id),
            };

            if (fails.Contains(found.Id))
            {
                device.Connecting = () =>
                {
                    Attempts[found.Id] = Attempts.GetValueOrDefault(found.Id) + 1;
                    throw new TimeoutException("The operation has timed out.");
                };
            }

            Opened.Add(device);
            return device;
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
