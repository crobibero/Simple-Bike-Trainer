using SimpleBikeTrainer.AntPlus;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Tests;

public sealed class SensorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-sensors").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void PowerMeasurementsSkipTheFieldsBeforeTheCrankData()
    {
        // Flags: pedal balance, accumulated torque, wheel and crank revolutions. 250 W; 1000 revolutions at 2048/1024 s.
        byte[] full = [0x35, 0x00, 0xFA, 0x00, 0x32, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x08, 0xE8, 0x03, 0x00, 0x08];
        byte[] powerOnly = [0x00, 0x00, 0x2C, 0x01];

        Assert.Equal(new PowerMeasurement(250, new CrankData(1000, 2048)), CyclingSensors.ParsePowerMeasurement(full));
        Assert.Equal(new PowerMeasurement(300, null), CyclingSensors.ParsePowerMeasurement(powerOnly));
        Assert.Null(CyclingSensors.ParsePowerMeasurement([0x00, 0x00]));
    }

    [Fact]
    public void CscMeasurementsGiveTheCrankAfterAnyWheelData()
    {
        byte[] both = [0x03, 0x10, 0x00, 0x00, 0x00, 0x00, 0x04, 0x07, 0x00, 0x00, 0x02];
        byte[] crank = [0x02, 0x07, 0x00, 0x00, 0x02];
        byte[] speedOnly = [0x01, 0x10, 0x00, 0x00, 0x00, 0x00, 0x04];

        Assert.Equal(new CrankData(7, 512), CyclingSensors.ParseCscMeasurement(both));
        Assert.Equal(new CrankData(7, 512), CyclingSensors.ParseCscMeasurement(crank));
        Assert.Null(CyclingSensors.ParseCscMeasurement(speedOnly));
    }

    [Fact]
    public void CadenceComesFromRevolutionsOverTimeAndDropsToZeroWhenTheCrankStops()
    {
        var cadence = new CrankCadence();

        Assert.Null(cadence.Update(new CrankData(10, 1000), 0));

        // One revolution in 0.75 s is 80 rpm; the counters roll over at 65536.
        Assert.Equal(80, cadence.Update(new CrankData(11, 1768), 750)!.Value, 3);
        Assert.Equal(80, cadence.Update(new CrankData(11, 1768), 1500)!.Value, 3);
        var wrapped = new CrankCadence();
        wrapped.Update(new CrankData(65535, 65000), 0);
        Assert.Equal(90, wrapped.Update(new CrankData(1, (65000 + 1365) & 0xFFFF), 1365)!.Value, 0);

        // A jump of hundreds of revolutions is a glitch, and the last good value stays.
        Assert.Equal(80, cadence.Update(new CrankData(400, 2768), 2500)!.Value, 3);

        // The same revolution repeated for 3 s: the rider stopped.
        Assert.Equal(0, cadence.Update(new CrankData(400, 2768), 5600));
    }

    [Fact]
    public void AntPlusPowerAndCadencePagesDecode()
    {
        byte[] power = [0x10, 0x01, 0xFF, 0x58, 0x00, 0x00, 0xFA, 0x00];
        byte[] noCadence = [0x10, 0x01, 0xFF, 0xFF, 0x00, 0x00, 0x2C, 0x01];
        byte[] torque = [0x12, 0x01, 0x00, 0x58, 0x00, 0x00, 0x00, 0x00];

        Assert.Equal<(int, int?)?>((250, 88), AntPlusPages.BikePower(power));
        Assert.Equal<(int, int?)?>((300, null), AntPlusPages.BikePower(noCadence));
        Assert.Null(AntPlusPages.BikePower(torque));

        // Cadence only: page number and three page bytes, then time and count. Combined: time and count first.
        Assert.Equal(new CrankData(0x0102, 0x0304), AntPlusPages.Crank([0x80, 0, 0, 0, 0x04, 0x03, 0x02, 0x01], combined: false));
        Assert.Equal(new CrankData(0x0102, 0x0304), AntPlusPages.Crank([0x04, 0x03, 0x02, 0x01, 0, 0, 0, 0], combined: true));
    }

    [Theory]
    [InlineData(AntPlusPages.BikePowerType, DeviceKind.PowerMeter, "ANT+ power meter 4321")]
    [InlineData(AntPlusPages.BikeCadenceType, DeviceKind.CadenceSensor, "ANT+ cadence sensor 4321")]
    [InlineData((byte)(AntPlusPages.BikeSpeedCadenceType | 0x80), DeviceKind.CadenceSensor, "ANT+ cadence sensor 4321")]
    public void AntPlusScansFindPowerMetersAndCadenceSensors(byte type, DeviceKind kind, string name)
    {
        var found = AntScanner.Identify(new AntReceived(new byte[8], 4321, type, 1, -60));

        Assert.Equal(kind, found?.Kind);
        Assert.Equal(name, found?.Name);
    }

    [Fact]
    public void BluetoothPowerMetersAndCadenceSensorsAreKnownByTheirServices()
    {
        Assert.Equal(DeviceKind.PowerMeter, BleScanner.Classify("4iiii", [GattUuids.CyclingPower], null));
        Assert.Equal(DeviceKind.CadenceSensor, BleScanner.Classify("CADENCE", [GattUuids.CyclingSpeedCadence], null));

        // A KICKR offers Cycling Power too, and stays a trainer.
        Assert.Equal(DeviceKind.Trainer, BleScanner.Classify("KICKR CORE", [GattUuids.CyclingPower, GattUuids.FitnessMachine], null));
    }

    [Fact]
    public void AutoPrefersASensorAndAChoiceIsTakenAsIs()
    {
        double? Read(MetricSource s) => s is MetricSource.Trainer or MetricSource.PowerMeter ? (double)(int)s : null;

        Assert.Equal(MetricSource.PowerMeter, MetricSources.Resolve(Metric.Power, MetricSource.Auto, Read)?.Source);
        Assert.Equal(MetricSource.PowerMeter, MetricSources.Resolve(Metric.Cadence, MetricSource.Auto, Read)?.Source);
        Assert.Equal(MetricSource.Trainer, MetricSources.Resolve(Metric.Power, MetricSource.Trainer, Read)?.Source);
        Assert.Null(MetricSources.Resolve(Metric.Cadence, MetricSource.CadenceSensor, Read));
        Assert.Null(MetricSources.Resolve(Metric.HeartRate, MetricSource.Off, Read));

        // One no metric can come from, as from a config edited by hand, gives nothing.
        Assert.Empty(MetricSources.Order(Metric.Power, MetricSource.HeartRateMonitor));
    }

    [Theory]
    [InlineData(MetricSource.Auto, MetricSource.Auto, MetricSource.Auto, 240, 97, 130)]
    [InlineData(MetricSource.Trainer, MetricSource.Trainer, MetricSource.Trainer, 150, 90, 100)]
    [InlineData(MetricSource.PowerMeter, MetricSource.PowerMeter, MetricSource.Off, 240, 92, null)]
    [InlineData(MetricSource.Auto, MetricSource.Auto, MetricSource.HeartRateMonitor, 240, 97, 130)]
    public async Task ARideTakesEachReadingFromTheChosenDevice(MetricSource power, MetricSource cadence, MetricSource heartRate, int watts, int rpm, int? bpm)
    {
        await using var trainer = new ReportingTrainer();
        await using var meter = new Sensor(new SensorReading(240, 92));
        await using var pod = new Sensor(new SensorReading(null, 97));
        await using var strap = new Strap(130);
        var options = new RideOptions(200, 20000, new RiderModel(), Path.Combine(_dir, $"{power}-{cadence}-{heartRate}.fit"))
        {
            Metrics = new MetricsConfig { Power = power, Cadence = cadence, HeartRate = heartRate },
        };

        var snapshot = await RideAsync(new RideSession(Workout.FreeRide(), options, trainer, strap, [], TestClock.Fast, meter, pod));

        Assert.Equal((watts, rpm, bpm), (snapshot.Power, snapshot.Cadence, snapshot.HeartRate));
    }

    [Fact]
    public async Task AutoFallsBackToTheTrainerWhileASensorSendsNothing()
    {
        await using var trainer = new ReportingTrainer();
        await using var meter = new Sensor(null);
        var options = new RideOptions(200, 20000, new RiderModel(), Path.Combine(_dir, "fallback.fit"));

        var snapshot = await RideAsync(new RideSession(Workout.FreeRide(), options, trainer, null, [], TestClock.Fast, meter));

        Assert.Equal((150, 90, 100), (snapshot.Power, snapshot.Cadence, snapshot.HeartRate));
    }

    /// <summary>Rides a few seconds and returns the last snapshot.</summary>
    private static async Task<RideSnapshot> RideAsync(RideSession session)
    {
        RideSnapshot? last = null;
        session.Updated += s =>
        {
            if (s.Moving.TotalSeconds >= 3)
            {
                last ??= s;
                session.Send(RideCommand.Finish);
            }
        };

        await session.RunAsync(TestContext.Current.CancellationToken);
        return last!;
    }

    /// <summary>A trainer that reports 150 W, 90 rpm and a heart rate of 100 it relays, every couple of milliseconds.</summary>
    private sealed class ReportingTrainer : ITrainer, IAsyncDisposable
    {
        private readonly Timer _timer;

        public ReportingTrainer() => _timer = new Timer(_ => DataReceived?.Invoke(new IndoorBikeData(30, 90, 150, 100, null, null)), null, 0, 2);

        public event Action<IndoorBikeData>? DataReceived;

        public string Name => "trainer";

        public bool IsConnected => true;

        public string? Status => null;

        public string? LastError => null;

        public Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => _timer.DisposeAsync();
    }

    /// <summary>A power meter or cadence sensor sending the same reading every couple of milliseconds; none sends nothing.</summary>
    private sealed class Sensor : ICyclingSensor, IAsyncDisposable
    {
        private readonly Timer _timer;

        public Sensor(SensorReading? reading) => _timer = new Timer(_ =>
        {
            if (reading is { } r)
            {
                ReadingReceived?.Invoke(r);
            }
        }, null, 0, 2);

        public event Action<SensorReading>? ReadingReceived;

        public string Name => "sensor";

        public bool IsConnected => true;

        public string? Status => null;

        public ValueTask DisposeAsync() => _timer.DisposeAsync();
    }

    private sealed class Strap : IHeartRateMonitor, IAsyncDisposable
    {
        private readonly Timer _timer;

        public Strap(int bpm) => _timer = new Timer(_ => HeartRateReceived?.Invoke(bpm), null, 0, 2);

        public event Action<int>? HeartRateReceived;

        public string Name => "strap";

        public bool IsConnected => true;

        public string? Status => null;

        public ValueTask DisposeAsync() => _timer.DisposeAsync();
    }
}
